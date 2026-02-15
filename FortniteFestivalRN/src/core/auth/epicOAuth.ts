/**
 * Epic Games OAuth configuration for react-native-app-auth.
 *
 * The app uses Authorization Code + PKCE flow:
 *   1. Opens Epic's login page in an in-app browser
 *   2. User authenticates (including 2FA if enabled)
 *   3. Epic redirects to FSTService's /api/auth/epiccallback
 *   4. FSTService 302-redirects to festscoretracker://auth/callback?code=XYZ
 *   5. react-native-app-auth intercepts the deep link and returns the code
 *   6. The code is sent to our FSTService (NOT exchanged on-device)
 *
 * Epic's Developer Portal rejects custom URI schemes as redirect URLs,
 * so we use FSTService as a redirect proxy.
 *
 * Epic handles 2FA on their login page — no 2FA library needed.
 */

import {Alert, Platform} from 'react-native';

// ── OAuth configuration ─────────────────────────────────────────────

/**
 * Epic Games OAuth client configuration.
 *
 * This is a PUBLIC client (no client secret on-device).
 * The actual token exchange happens server-side in FSTService,
 * which holds the confidential Switch client credentials.
 *
 * We use a dedicated registered client for the mobile app.
 * TODO: Replace with actual registered Epic client ID for this app.
 */
const EPIC_CLIENT_ID = 'PLACEHOLDER_EPIC_CLIENT_ID';

/**
 * Builds the OAuth config for react-native-app-auth.
 *
 * The redirect URL is dynamic — it points to FSTService's
 * /api/auth/epiccallback endpoint, which 302-redirects to the
 * app's custom scheme after Epic sends the auth code.
 *
 * @param serviceEndpoint  The FSTService base URL (e.g. https://fst.example.com)
 */
function buildEpicAuthConfig(serviceEndpoint: string) {
  // Strip trailing slashes from the endpoint
  const base = serviceEndpoint.replace(/\/+$/, '');

  return {
    clientId: EPIC_CLIENT_ID,
    redirectUrl: Platform.select({
      // On Windows, react-native-app-auth-windows uses a loopback listener
      // that doesn't go through a browser redirect, so the custom scheme works.
      windows: 'http://localhost:8400/auth/callback',
      // On iOS/Android, Epic redirects to FSTService, which 302s to the app.
      default: `${base}/api/auth/epiccallback`,
    }),
    scopes: ['basic_profile', 'friends_list'],
    serviceConfiguration: {
      authorizationEndpoint: 'https://www.epicgames.com/id/authorize',
      tokenEndpoint: 'https://account-public-service-prod.ol.epicgames.com/account/api/oauth/token',
    },
    // We only need the authorization code — token exchange is done server-side
    usePKCE: true,
    skipCodeExchange: true,
  };
}

// ── Authorization flow ──────────────────────────────────────────────

export interface EpicAuthResult {
  /** The authorization code to send to FSTService's /api/auth/login. */
  authorizationCode: string;
}

/**
 * Opens the Epic Games login page in an in-app browser and returns
 * the authorization code after the user completes authentication.
 *
 * Uses `react-native-app-auth` (or `react-native-app-auth-windows`
 * on Windows) to handle the native OAuth browser flow.
 *
 * On iOS/Android, Epic redirects to FSTService's /api/auth/epiccallback,
 * which 302-redirects to festscoretracker://auth/callback?code=XYZ.
 * react-native-app-auth intercepts that deep link and returns the code.
 *
 * @param serviceEndpoint  The FSTService base URL (needed to build the redirect URL)
 * @throws If the user cancels, the auth service errors, or the
 *         library is not installed.
 */
export async function authorizeWithEpic(serviceEndpoint: string): Promise<EpicAuthResult> {
  try {
    // Dynamically require to avoid hard crash if not installed yet
    // eslint-disable-next-line @typescript-eslint/no-var-requires
    const {authorize} = getAppAuthModule();

    const config = buildEpicAuthConfig(serviceEndpoint);
    const result = await authorize(config);

    if (!result.authorizationCode) {
      throw new Error('No authorization code received from Epic Games.');
    }

    return {authorizationCode: result.authorizationCode};
  } catch (error: any) {
    // User cancelled the login flow
    if (
      error?.message?.includes('cancelled') ||
      error?.message?.includes('canceled') ||
      error?.code === 'RCTAppAuth_USER_CANCELLED'
    ) {
      throw new EpicAuthCancelledError();
    }

    throw error;
  }
}

// ── Helpers ─────────────────────────────────────────────────────────

function getAppAuthModule(): {authorize: (config: any) => Promise<any>} {
  if (Platform.OS === 'windows') {
    try {
      // eslint-disable-next-line @typescript-eslint/no-var-requires
      return require('react-native-app-auth-windows');
    } catch {
      throw new EpicAuthNotInstalledError('react-native-app-auth-windows');
    }
  }

  try {
    // eslint-disable-next-line @typescript-eslint/no-var-requires
    return require('react-native-app-auth');
  } catch {
    throw new EpicAuthNotInstalledError('react-native-app-auth');
  }
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
