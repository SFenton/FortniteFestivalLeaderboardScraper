/**
 * Persisted authentication mode.
 *
 * - `'local'`  — app runs stand-alone, user pastes exchange codes manually.
 * - `'epic'`   — user signed in via Epic Games; service-mediated sync.
 *
 * `null` / missing means the user has never chosen — show the sign-in screen.
 */
export type AuthMode = 'local' | 'epic';

/**
 * AsyncStorage key for the persisted auth mode.
 */
export const AUTH_MODE_STORAGE_KEY = 'fnfestival:authMode';

/**
 * AsyncStorage key for the FST service endpoint URL.
 */
export const SERVICE_ENDPOINT_KEY = 'fnfestival:serviceEndpoint';

/**
 * AsyncStorage key for the persisted auth session (tokens + profile).
 */
export const AUTH_SESSION_KEY = 'fnfestival:authSession';

// ── Token / session types ───────────────────────────────────────────

/** Response from `POST /api/auth/login`. */
export interface AuthLoginResponse {
  accessToken: string;
  refreshToken: string;
  expiresIn: number;       // seconds until accessToken expires
  accountId: string;
  displayName: string;
  friends: {accountId: string; displayName: string}[];
}

/** Response from `POST /api/auth/refresh`. */
export interface AuthRefreshResponse {
  accessToken: string;
  refreshToken: string;
  expiresIn: number;
}

/** Locally-persisted session (stored in AsyncStorage). */
export interface AuthSession {
  accessToken: string;
  refreshToken: string;
  /** ISO timestamp when accessToken expires. */
  expiresAt: string;
  accountId: string;
  displayName: string;
}
