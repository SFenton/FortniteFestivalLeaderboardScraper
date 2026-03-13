/**
 * HTTP client for the Festival Score Tracker service's data endpoints.
 *
 * These endpoints require Bearer (JWT) authentication and are scoped
 * to the authenticated user's account/device.
 *
 * Endpoints:
 *   GET  /api/account/check?username=xxx — public, check if account exists
 *   GET  /api/me/sync                    — download personal SQLite DB
 *   GET  /api/me/sync/version            — check personal DB version/size
 *   GET  /api/me/backfill/status         — check backfill + history recon status
 */

// ── Types ───────────────────────────────────────────────────────────

export interface AccountCheckResult {
  exists: boolean;
  accountId: string | null;
  displayName: string | null;
}

export interface SyncVersionResult {
  deviceId: string;
  available: boolean;
  version: string | null;
  sizeBytes: number | null;
}

export interface ServiceVersionResult {
  version: string;
}

export interface BackfillStatusResult {
  accountId: string;
  backfill: {
    status: string;
    songsChecked: number;
    totalSongsToCheck: number;
    entriesFound: number;
    startedAt: string;
    completedAt: string | null;
  } | null;
  historyRecon: {
    status: string;
  } | null;
}

// ── Client ──────────────────────────────────────────────────────────

export class FstServiceClient {
  private readonly baseUrl: string;
  private accessToken: string;

  constructor(serviceEndpoint: string, accessToken: string) {
    this.baseUrl = serviceEndpoint.replace(/\/+$/, '');
    this.accessToken = accessToken;
  }

  /** Update the access token (e.g. after a refresh). */
  setAccessToken(token: string): void {
    this.accessToken = token;
  }

  /**
   * Check if an account exists on the service (public, no auth required).
   */
  async checkAccount(username: string): Promise<AccountCheckResult> {
    const res = await fetch(
      `${this.baseUrl}/api/account/check?username=${encodeURIComponent(username)}`,
    );

    if (!res.ok) {
      throw new FstServiceError('checkAccount', res.status, await res.text().catch(() => ''));
    }

    return res.json() as Promise<AccountCheckResult>;
  }

  /**
   * Get the version/size of the personal DB for the authenticated device.
   */
  async getSyncVersion(): Promise<SyncVersionResult> {
    const res = await fetch(`${this.baseUrl}/api/me/sync/version`, {
      headers: {Authorization: `Bearer ${this.accessToken}`},
    });

    if (!res.ok) {
      throw new FstServiceError('getSyncVersion', res.status, await res.text().catch(() => ''));
    }

    return res.json() as Promise<SyncVersionResult>;
  }

  /**
   * Download the personal SQLite DB as an ArrayBuffer.
   */
  async downloadPersonalDb(): Promise<ArrayBuffer> {
    const res = await fetch(`${this.baseUrl}/api/me/sync`, {
      headers: {Authorization: `Bearer ${this.accessToken}`},
    });

    if (!res.ok) {
      throw new FstServiceError('downloadPersonalDb', res.status, await res.text().catch(() => ''));
    }

    return res.arrayBuffer();
  }

  /**
   * Get the current backfill + history reconstruction status.
   */
  async getBackfillStatus(): Promise<BackfillStatusResult> {
    const res = await fetch(`${this.baseUrl}/api/me/backfill/status`, {
      headers: {Authorization: `Bearer ${this.accessToken}`},
    });

    if (!res.ok) {
      throw new FstServiceError('getBackfillStatus', res.status, await res.text().catch(() => ''));
    }

    return res.json() as Promise<BackfillStatusResult>;
  }

  /**
   * Get the service version (public, no auth required).
   */
  async getServiceVersion(): Promise<ServiceVersionResult> {
    const res = await fetch(`${this.baseUrl}/api/version`);

    if (!res.ok) {
      throw new FstServiceError('getServiceVersion', res.status, await res.text().catch(() => ''));
    }

    return res.json() as Promise<ServiceVersionResult>;
  }

  /**
   * Build a WebSocket URL for real-time notifications.
   * The access token is passed as a query parameter since WebSocket
   * doesn't support custom headers.
   */
  getWebSocketUrl(): string {
    const wsBase = this.baseUrl
      .replace(/^http:/, 'ws:')
      .replace(/^https:/, 'wss:');
    return `${wsBase}/api/ws?token=${encodeURIComponent(this.accessToken)}`;
  }
}

// ── Error type ──────────────────────────────────────────────────────

export class FstServiceError extends Error {
  readonly endpoint: string;
  readonly statusCode: number;
  readonly responseBody: string;

  constructor(endpoint: string, statusCode: number, responseBody: string) {
    super(`FST service error on ${endpoint}: HTTP ${statusCode}`);
    this.name = 'FstServiceError';
    this.endpoint = endpoint;
    this.statusCode = statusCode;
    this.responseBody = responseBody;
  }
}
