import type { PublicationResponse } from '@festival/core/api';

export const PUBLICATION_CHANGED_EVENT = 'fst:publication-changed';
const PUBLICATION_STORAGE_KEY = 'fst_publication_id';
const HTTP_CONFLICT = 409;
const HTTP_NOT_MODIFIED = 304;

type ResourceRevalidationState = {
  completion: Promise<void>;
  epoch: number;
  pending: boolean;
  resolve: () => void;
};

type ResourceRevalidationTicket = {
  resourceKey: string;
  state: ResourceRevalidationState;
};

let currentPublication: PublicationResponse | null = null;
let publicationRequest: Promise<PublicationResponse> | null = null;
let pinRequests = true;
let publicationBootstrapEnabled = false;
let httpCacheRevalidationEpoch = 0;
const resourceRevalidations = new Map<string, ResourceRevalidationState>();

export function activatePublicationBootstrap(): void {
  publicationBootstrapEnabled = true;
}

export function getCurrentPublicationId(): number | null {
  return currentPublication?.publicationId ?? null;
}

export function isPublicationPinningEnabled(): boolean {
  return currentPublication?.pinningEnabled === true;
}

export async function ensurePublication(
  force = false,
  notifyEvenIfSame = false,
): Promise<PublicationResponse> {
  if (import.meta.env.MODE === 'e2e') {
    currentPublication ??= {
      contractVersion: 1,
      publicationId: 1,
      previousPublicationId: null,
      publishedScrapeId: 1,
      publishedAt: null,
      readyForPinning: false,
      pinningEnabled: false,
      unreadySurfaces: [],
    };
    return currentPublication;
  }

  if (!force && currentPublication) return currentPublication;
  if (!force && publicationRequest) return publicationRequest;

  publicationRequest = fetch('/api/publication', {
    cache: 'no-store',
  }).then(async response => {
    if (!response.ok) {
      throw new Error(`API ${response.status}: ${response.statusText}`);
    }

    const publication = await response.json() as PublicationResponse;
    if (!Number.isSafeInteger(publication.contractVersion)
        || publication.contractVersion <= 0
        || !Number.isSafeInteger(publication.publicationId)
        || publication.publicationId <= 0
        || !Number.isSafeInteger(publication.publishedScrapeId)
        || publication.publishedScrapeId <= 0
        || typeof publication.readyForPinning !== 'boolean'
        || !Array.isArray(publication.unreadySurfaces)
        || typeof publication.pinningEnabled !== 'boolean') {
      throw new Error('Invalid publication response');
    }

    const previousId = currentPublication?.publicationId
      ?? readStoredPublicationId();
    currentPublication = publication;
    writeStoredPublicationId(publication.publicationId);
    if (previousId != null) advanceHttpCacheRevalidationEpoch();
    if (notifyEvenIfSame || previousId !== publication.publicationId) {
      dispatchPublicationChanged(publication);
    }
    return publication;
  }).finally(() => {
    publicationRequest = null;
  });

  return publicationRequest;
}

export async function fetchWithPublication(
  path: string,
  init?: RequestInit,
): Promise<Response> {
  if (!publicationBootstrapEnabled) {
    return fetch(path, init);
  }

  const publication = await ensurePublication();
  let response = await fetchPublishedResource(path, publication, init);
  if (response.status !== HTTP_CONFLICT) return response;

  const body = await response.clone().json().catch(() => null) as {
    status?: string;
  } | null;
  if (body?.status !== 'publication_changed') return response;

  const refreshed = await ensurePublication(true);
  response = await fetchPublishedResource(path, refreshed, init);
  return response;
}

export function withCurrentPublicationId(path: string): string {
  if (!publicationBootstrapEnabled) return path;
  const publicationId = getCurrentPublicationId();
  return publicationId && isPublicationPinningEnabled()
    ? appendPublicationId(path, publicationId)
    : path;
}

export function resetPublicationForTests(): void {
  currentPublication = null;
  publicationRequest = null;
  pinRequests = true;
  publicationBootstrapEnabled = false;
  resetHttpCacheRevalidation();
}

export function setPublicationForTests(
  publicationId: number,
  pinPublicationRequests = true,
): void {
  currentPublication = {
    contractVersion: 1,
    publicationId,
    previousPublicationId: null,
    publishedScrapeId: publicationId,
    publishedAt: null,
    readyForPinning: pinPublicationRequests,
    pinningEnabled: pinPublicationRequests,
    unreadySurfaces: [],
  };
  publicationRequest = null;
  pinRequests = pinPublicationRequests;
  publicationBootstrapEnabled = true;
  resetHttpCacheRevalidation();
}

async function fetchPublishedResource(
  path: string,
  publication: PublicationResponse,
  init?: RequestInit,
): Promise<Response> {
  const resourceKey = getResourceKey(path, init);
  let decision = await acquireResourceRevalidation(resourceKey, init);
  while (decision.epoch !== httpCacheRevalidationEpoch) {
    decision = await acquireResourceRevalidation(resourceKey, init);
  }

  const requestInit = decision.ticket == null
    ? init
    : { ...init, cache: 'no-cache' as const };
  const activePublication = currentPublication ?? publication;

  try {
    const response = await fetch(
      appendPublicationId(path, activePublication.publicationId),
      requestInit,
    );
    const epochChanged = (
      init?.cache == null
      && decision.epoch !== httpCacheRevalidationEpoch
    );
    completeResourceRevalidation(
      decision.ticket,
      !epochChanged
        && (response.ok || response.status === HTTP_NOT_MODIFIED),
    );
    if (epochChanged) {
      return fetchPublishedResource(
        path,
        currentPublication ?? publication,
        init,
      );
    }
    return response;
  } catch (error) {
    completeResourceRevalidation(decision.ticket, false);
    if (
      init?.cache == null
      && decision.epoch !== httpCacheRevalidationEpoch
    ) {
      return fetchPublishedResource(
        path,
        currentPublication ?? publication,
        init,
      );
    }
    throw error;
  }
}

function advanceHttpCacheRevalidationEpoch(): void {
  releasePendingResourceRevalidations();
  httpCacheRevalidationEpoch += 1;
  resourceRevalidations.clear();
}

function resetHttpCacheRevalidation(): void {
  releasePendingResourceRevalidations();
  httpCacheRevalidationEpoch = 0;
  resourceRevalidations.clear();
}

async function acquireResourceRevalidation(
  resourceKey: string,
  init?: RequestInit,
): Promise<{
  epoch: number;
  ticket: ResourceRevalidationTicket | null;
}> {
  if (init?.cache != null) {
    return { epoch: httpCacheRevalidationEpoch, ticket: null };
  }

  while (httpCacheRevalidationEpoch > 0) {
    const epoch = httpCacheRevalidationEpoch;
    const existing = resourceRevalidations.get(resourceKey);
    if (!existing || existing.epoch !== epoch) {
      let resolve!: () => void;
      const state: ResourceRevalidationState = {
        completion: new Promise<void>(complete => {
          resolve = complete;
        }),
        epoch,
        pending: true,
        resolve: () => resolve(),
      };
      resourceRevalidations.set(resourceKey, state);
      return {
        epoch,
        ticket: { resourceKey, state },
      };
    }
    if (!existing.pending) return { epoch, ticket: null };
    await existing.completion;
  }

  return { epoch: httpCacheRevalidationEpoch, ticket: null };
}

function completeResourceRevalidation(
  ticket: ResourceRevalidationTicket | null,
  succeeded: boolean,
): void {
  if (!ticket) return;

  const { resourceKey, state } = ticket;
  if (resourceRevalidations.get(resourceKey) === state) {
    if (succeeded && state.epoch === httpCacheRevalidationEpoch) {
      state.pending = false;
    } else {
      resourceRevalidations.delete(resourceKey);
    }
  }
  state.resolve();
}

function releasePendingResourceRevalidations(): void {
  for (const state of resourceRevalidations.values()) {
    if (state.pending) state.resolve();
  }
}

function getResourceKey(path: string, init?: RequestInit): string {
  return `${(init?.method ?? 'GET').toUpperCase()} ${path}`;
}

function appendPublicationId(path: string, publicationId: number): string {
  if (!pinRequests || !isPublicationPinningEnabled()) return path;
  const url = new URL(path, 'https://fst.invalid');
  url.searchParams.set('publicationId', String(publicationId));
  return `${url.pathname}${url.search}`;
}

function readStoredPublicationId(): number | null {
  try {
    const raw = localStorage.getItem(PUBLICATION_STORAGE_KEY);
    if (!raw) return null;
    const value = Number(raw);
    return Number.isSafeInteger(value) && value > 0 ? value : null;
  } catch {
    return null;
  }
}

function writeStoredPublicationId(publicationId: number): void {
  try {
    localStorage.setItem(PUBLICATION_STORAGE_KEY, String(publicationId));
  } catch {
    // Storage can be unavailable; the in-memory publication remains authoritative.
  }
}

function dispatchPublicationChanged(publication: PublicationResponse): void {
  if (typeof window === 'undefined') return;
  window.dispatchEvent(new CustomEvent<PublicationResponse>(
    PUBLICATION_CHANGED_EVENT,
    { detail: publication },
  ));
}
