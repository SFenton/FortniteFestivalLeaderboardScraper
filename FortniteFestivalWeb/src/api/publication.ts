import type { PublicationResponse } from '@festival/core/api';

export const PUBLICATION_CHANGED_EVENT = 'fst:publication-changed';
const PUBLICATION_STORAGE_KEY = 'fst_publication_id';

let currentPublication: PublicationResponse | null = null;
let publicationRequest: Promise<PublicationResponse> | null = null;
let pinRequests = true;
let publicationBootstrapEnabled = false;
let revalidateHttpCache = false;

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
      publicationId: 1,
      previousPublicationId: null,
      publishedScrapeId: 1,
      publishedAt: null,
      pinningEnabled: false,
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
    if (!Number.isSafeInteger(publication.publicationId)
        || publication.publicationId <= 0
        || typeof publication.pinningEnabled !== 'boolean') {
      throw new Error('Invalid publication response');
    }

    const previousId = currentPublication?.publicationId
      ?? readStoredPublicationId();
    currentPublication = publication;
    writeStoredPublicationId(publication.publicationId);
    if (notifyEvenIfSame || previousId !== publication.publicationId) {
      if (previousId != null) revalidateHttpCache = true;
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
  const requestInit = revalidateHttpCache && init?.cache == null
    ? { ...init, cache: 'no-cache' as const }
    : init;
  let response = await fetch(
    appendPublicationId(path, publication.publicationId),
    requestInit,
  );
  if (response.status !== 409) return response;

  const body = await response.clone().json().catch(() => null) as {
    status?: string;
  } | null;
  if (body?.status !== 'publication_changed') return response;

  revalidateHttpCache = true;
  currentPublication = null;
  const refreshed = await ensurePublication(true);
  const retryInit = revalidateHttpCache && init?.cache == null
    ? { ...init, cache: 'no-cache' as const }
    : init;
  response = await fetch(
    appendPublicationId(path, refreshed.publicationId),
    retryInit,
  );
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
  revalidateHttpCache = false;
}

export function setPublicationForTests(
  publicationId: number,
  pinPublicationRequests = true,
): void {
  currentPublication = {
    publicationId,
    previousPublicationId: null,
    publishedScrapeId: publicationId,
    publishedAt: null,
    pinningEnabled: pinPublicationRequests,
  };
  publicationRequest = null;
  pinRequests = pinPublicationRequests;
  publicationBootstrapEnabled = true;
  revalidateHttpCache = false;
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
