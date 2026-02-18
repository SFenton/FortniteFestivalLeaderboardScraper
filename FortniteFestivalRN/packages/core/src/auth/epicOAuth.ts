/**
 * Epic Games OAuth — Authorization Code flow.
 *
 * On **all platforms**, `redirect_uri` points to FSTService's
 * `/api/auth/epiccallback` — the only URI registered in Epic's Developer Portal.
 *
 * **Windows** — The native C++ `OAuthLoopback` module starts a loopback HTTP
 * listener on 127.0.0.1:8400 and opens Epic's login page in the system browser.
 * The loopback return URL is encoded in the OAuth `state` parameter, so when
 * Epic redirects to FSTService's epiccallback, the server 302-redirects to
 * `http://localhost:8400/auth/callback?code=XYZ` and the native module captures it.
 *
 * **iOS / Android** — Uses `react-native-app-auth` to handle the native
 * OAuth browser flow.  FSTService's epiccallback 302-redirects to the app's
 * custom deep-link scheme `festscoretracker://auth/callback?code=XYZ`.
 *
 * In both cases the authorization code is sent to FSTService's
 * POST /api/auth/login — the token exchange is always server-side.
 *
 * Epic handles 2FA on their login page — no 2FA library needed.
 */

import {NativeModules, Platform} from 'react-native';

// ── OAuth configuration ─────────────────────────────────────────────

/**
 * Epic Games OAuth client configuration.
 *
 * This is a PUBLIC client (no client secret on-device).
 * The actual token exchange happens server-side in FSTService,
 * which holds the confidential Switch client credentials.
 *
 * We use a dedicated registered client for the mobile app.
 */
const EPIC_CLIENT_ID = 'xyza7891QHDwTpqKnAkLEQU3nC1dmEI4';

/** Port used by the native OAuthLoopback listener on Windows. */
const LOOPBACK_PORT = 8400;

/** Timeout (seconds) for the loopback listener to receive the callback. */
const LOOPBACK_TIMEOUT_SECONDS = 120;

/** Epic Games OAuth endpoints. */
const EPIC_AUTH_ENDPOINT = 'https://www.epicgames.com/id/authorize';
const EPIC_TOKEN_ENDPOINT =
  'https://account-public-service-prod.ol.epicgames.com/account/api/oauth/token';

/**
 * Builds the OAuth config object.
 *
 * On **all platforms**, the `redirect_uri` sent to Epic points to FSTService's
 * `/api/auth/epiccallback` — that is the only URI registered in Epic's
 * Developer Portal.
 *
 * On iOS/Android, FSTService 302-redirects to the app's custom deep-link scheme.
 * On Windows, FSTService 302-redirects to a localhost loopback URL (encoded in
 * the OAuth `state` parameter) where the native OAuthLoopback module is listening.
 *
 * @param serviceEndpoint  The FSTService base URL (e.g. https://fst.example.com)
 */
function buildEpicAuthConfig(serviceEndpoint: string) {
  const base = serviceEndpoint.replace(/\/+$/, '');

  return {
    clientId: EPIC_CLIENT_ID,
    // Always use the registered FSTService callback — Epic rejects anything else.
    redirectUrl: `${base}/api/auth/epiccallback`,
    scopes: ['basic_profile', 'friends_list'],
    serviceConfiguration: {
      authorizationEndpoint: EPIC_AUTH_ENDPOINT,
      tokenEndpoint: EPIC_TOKEN_ENDPOINT,
    },
    // We only need the authorization code — token exchange is done server-side.
    // PKCE is disabled because the server exchanges the code using the
    // confidential client_secret, which is sufficient protection.
    usePKCE: false,
    skipCodeExchange: true,
  };
}

// ── Authorization flow ──────────────────────────────────────────────

export interface EpicAuthResult {
  /** The authorization code to send to FSTService's /api/auth/login. */
  authorizationCode: string;
}

/**
 * Opens the Epic Games login page and returns the authorization code
 * after the user completes authentication.
 *
 * - **Windows**: Uses the native `OAuthLoopback` C++ module (loopback
 *   HTTP listener on localhost).
 * - **iOS / Android**: Uses `react-native-app-auth` (native OAuth browser).
 *
 * @param serviceEndpoint  The FSTService base URL (needed to build the redirect URL)
 * @throws {EpicAuthCancelledError} If the user cancels the login flow.
 */
export async function authorizeWithEpic(serviceEndpoint: string): Promise<EpicAuthResult> {
  try {
    const config = buildEpicAuthConfig(serviceEndpoint);

    let authorizationCode: string;

    if (Platform.OS === 'windows') {
      authorizationCode = await windowsLoopbackAuthorize(config);
    } else {
      authorizationCode = await mobileAuthorize(config);
    }

    if (!authorizationCode) {
      throw new Error('No authorization code received from Epic Games.');
    }

    return {authorizationCode};
  } catch (error: any) {
    // User cancelled the login flow
    if (
      error?.message?.includes('cancelled') ||
      error?.message?.includes('canceled') ||
      error?.message?.includes('timed out') ||
      error?.code === 'RCTAppAuth_USER_CANCELLED'
    ) {
      throw new EpicAuthCancelledError();
    }

    throw error;
  }
}

// ── Platform-specific authorize implementations ─────────────────────

/**
 * Windows: Uses the native OAuthLoopback C++ module.
 *
 * Flow:
 *   1. Start loopback listener on localhost:8400
 *   2. Open Epic login with redirect_uri → FSTService's /api/auth/epiccallback
 *   3. Pass loopback URL in the OAuth `state` param (base64 JSON)
 *   4. Epic → FSTService → 302 to localhost:8400 with ?code=
 *   5. Loopback listener captures the code
 */
async function windowsLoopbackAuthorize(config: any): Promise<string> {
  const {OAuthLoopback} = NativeModules;

  if (!OAuthLoopback) {
    throw new Error(
      'OAuthLoopback native module not found. ' +
        'Rebuild the Windows app (yarn windows).',
    );
  }

  const loopbackUrl = `http://localhost:${LOOPBACK_PORT}/auth/callback`;

  // Encode the loopback return URL in the state parameter so FSTService
  // knows to redirect there instead of the mobile deep link.
  const statePayload = JSON.stringify({return_to: loopbackUrl});
  const stateB64 = btoa(statePayload);

  // Build the full authorization URL with the registered redirect_uri.
  const params = [
    `client_id=${encodeURIComponent(config.clientId)}`,
    `redirect_uri=${encodeURIComponent(config.redirectUrl)}`,
    `response_type=code`,
    `scope=${encodeURIComponent(config.scopes.join(' '))}`,
    `state=${encodeURIComponent(stateB64)}`,
  ].join('&');

  const authUrl = `${config.serviceConfiguration.authorizationEndpoint}?${params}`;

  return OAuthLoopback.authorize(authUrl, LOOPBACK_PORT, LOOPBACK_TIMEOUT_SECONDS);
}

/**
 * iOS / Android: Uses react-native-app-auth.
 */
async function mobileAuthorize(config: any): Promise<string> {
  let authorize: (cfg: any) => Promise<any>;
  try {
    // eslint-disable-next-line @typescript-eslint/no-var-requires
    ({authorize} = require('react-native-app-auth'));
  } catch {
    throw new EpicAuthNotInstalledError('react-native-app-auth');
  }

  const result = await authorize(config);
  return result.authorizationCode;
}

// ── Error types ─────────────────────────────────────────────────────

export class EpicAuthCancelledError extends Error {
  constructor() {
    super('User cancelled Epic Games login.');
    this.name = 'EpicAuthCancelledError';
  }
}

export class EpicAuthNotInstalledError extends Error {
  readonly packageName: string;

  constructor(packageName: string) {
    super(
      `${packageName} is not installed. ` +
        'Run `npm install ${packageName}` and rebuild.',
    );
    this.name = 'EpicAuthNotInstalledError';
    this.packageName = packageName;
  }
}
