/**
 * HTTP client for the Festival Score Tracker service's auth endpoints.
 *
 * Endpoints (designed in EpicLoginDesign.md, implemented in FSTService):
 *   POST /api/auth/login   — exchange Epic auth code for FST tokens
 *   POST /api/auth/refresh — refresh an expired access token
 *   POST /api/auth/logout  — revoke a refresh token session
 *   GET  /api/auth/me      — get authenticated user profile
 */

import type {AuthLoginResponse, AuthRefreshResponse} from './authTypes';

// ── Client ──────────────────────────────────────────────────────────

export class FstAuthClient {
  private readonly baseUrl: string;

  constructor(serviceEndpoint: string) {
    // Strip trailing slash for consistent path joining
    this.baseUrl = serviceEndpoint.replace(/\/+$/, '');
  }

  /**
   * Exchange an Epic authorization code for FST access + refresh tokens.
   *
   * The service swaps the code with Epic server-side (keeping the client
   * secret off-device), fetches friends, auto-registers the user, and
   * returns its own JWT + opaque refresh token.
   */
  async login(
    authorizationCode: string,
    deviceId: string,
    platform: 'ios' | 'android' | 'windows',
  ): Promise<AuthLoginResponse> {
    const res = await fetch(`${this.baseUrl}/api/auth/login`, {
      method: 'POST',
      headers: {'Content-Type': 'application/json'},
      body: JSON.stringify({code: authorizationCode, deviceId, platform}),
    });

    if (!res.ok) {
      const body = await res.text().catch(() => '');
      throw new FstAuthError('login', res.status, body);
    }

    return res.json() as Promise<AuthLoginResponse>;
  }

  /**
   * Refresh an expired access token using a valid refresh token.
   * The refresh token is rotated — the old one becomes invalid.
   */
  async refresh(refreshToken: string): Promise<AuthRefreshResponse> {
    const res = await fetch(`${this.baseUrl}/api/auth/refresh`, {
      method: 'POST',
      headers: {'Content-Type': 'application/json'},
      body: JSON.stringify({refreshToken}),
    });

    if (!res.ok) {
      const body = await res.text().catch(() => '');
      throw new FstAuthError('refresh', res.status, body);
    }

    return res.json() as Promise<AuthRefreshResponse>;
  }

  /**
   * Revoke the current session (invalidates the refresh token server-side).
   */
  async logout(refreshToken: string): Promise<void> {
    const res = await fetch(`${this.baseUrl}/api/auth/logout`, {
      method: 'POST',
      headers: {'Content-Type': 'application/json'},
      body: JSON.stringify({refreshToken}),
    });

    // Best-effort — ignore 4xx/5xx on logout
    if (!res.ok) {
      console.warn(
        `[FstAuthClient] logout returned ${res.status}`,
        await res.text().catch(() => ''),
      );
    }
  }
}

// ── Error type ──────────────────────────────────────────────────────

export class FstAuthError extends Error {
  readonly endpoint: string;
  readonly statusCode: number;
  readonly responseBody: string;

  constructor(endpoint: string, statusCode: number, responseBody: string) {
    super(`FST auth error on ${endpoint}: HTTP ${statusCode}`);
    this.name = 'FstAuthError';
    this.endpoint = endpoint;
    this.statusCode = statusCode;
    this.responseBody = responseBody;
  }
}
