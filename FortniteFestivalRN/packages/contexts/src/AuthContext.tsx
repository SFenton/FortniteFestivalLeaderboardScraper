import React, {createContext, useCallback, useContext, useEffect, useMemo, useState} from 'react';
import {Alert, Platform} from 'react-native';

import type {AuthMode, AuthSession} from '@festival/core';
import {
  AUTH_MODE_STORAGE_KEY,
  AUTH_SESSION_KEY,
  SERVICE_ENDPOINT_KEY,
} from '@festival/core';
import {FstAuthClient, FstAuthError} from '@festival/core';
import {FstServiceClient, FstServiceError} from '@festival/core';
import {authorizeWithEpic, EpicAuthCancelledError} from '@festival/core';

// ── Auth state machine ──────────────────────────────────────────────
export type AuthStatus =
  | 'loading'           // reading persisted mode on mount
  | 'choosing'          // no persisted mode — show sign-in screen
  | 'local'             // local mode chosen
  | 'authenticated';    // Service login succeeded
  // 'unauthenticated' will be added when service auth lands

type AuthState = {
  status: AuthStatus;
  mode: AuthMode | null;
  /** Persisted session (tokens + profile) when status === 'authenticated'. */
  session: AuthSession | null;
  /** FST service endpoint URL when mode === 'service'. */
  serviceEndpoint: string | null;
};

type AuthActions = {
  /** User chose "Use Locally" — persists mode, transitions to 'local'. */
  confirmLocal: () => void;

  /** Prompt the local-mode warning alert, then transition on confirm. */
  promptLocal: () => void;

  /**
   * User chose to connect to a Festival Score Tracker service.
   * Checks the service is reachable, opens the Epic Games OAuth login
   * page in an in-app browser, retrieves the authorization code, and
   * sends it to the FST service to complete registration/login.
   *
   * @param serviceEndpoint  The FST service URL entered by the user.
   */
  signInWithService: (serviceEndpoint: string) => void;

  /**
   * Refresh the access token using the stored refresh token.
   * Updates auth state + persistence. Returns the new access token.
   * Throws if refresh fails (caller should handle sign-out).
   */
  refreshAccessToken: () => Promise<string>;

  /** Reset back to the sign-in screen (wipes persisted mode + tokens). */
  signOut: () => Promise<void>;
};

type AuthContextValue = {
  auth: AuthState;
  authActions: AuthActions;
};

const AuthContext = createContext<AuthContextValue | null>(null);

// ── Device ID key ───────────────────────────────────────────────────
const DEVICE_ID_KEY = 'fnfestival:deviceId';

// ── Helpers ─────────────────────────────────────────────────────────

function getAsyncStorage(): {
  getItem: (k: string) => Promise<string | null>;
  setItem: (k: string, v: string) => Promise<void>;
  removeItem: (k: string) => Promise<void>;
} {
  if (process.env.JEST_WORKER_ID) {
    // In-memory stub for tests
    const store: Record<string, string> = {};
    return {
      getItem: async (k) => store[k] ?? null,
      setItem: async (k, v) => { store[k] = v; },
      removeItem: async (k) => { delete store[k]; },
    };
  }
  // eslint-disable-next-line @typescript-eslint/no-var-requires
  const mod = require('@react-native-async-storage/async-storage') as {default?: unknown};
  return (mod.default ?? mod) as any;
}

/**
 * Get or create a stable device identifier (UUID v4 stored in AsyncStorage).
 * This identifies the device to FSTService for registration.
 */
async function getDeviceId(): Promise<string> {
  const storage = getAsyncStorage();
  const existing = await storage.getItem(DEVICE_ID_KEY);
  if (existing) return existing;

  // Generate a UUID v4 (no crypto dependency needed)
  const id = 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(
    /[xy]/g,
    (c) => {
      const r = (Math.random() * 16) | 0;
      const v = c === 'x' ? r : (r & 0x3) | 0x8;
      return v.toString(16);
    },
  );
  await storage.setItem(DEVICE_ID_KEY, id);
  return id;
}

async function loadPersistedMode(): Promise<AuthMode | null> {
  const storage = getAsyncStorage();
  const raw = await storage.getItem(AUTH_MODE_STORAGE_KEY);
  if (raw === 'local' || raw === 'service') return raw;
  return null;
}

async function loadPersistedSession(): Promise<AuthSession | null> {
  const storage = getAsyncStorage();
  const raw = await storage.getItem(AUTH_SESSION_KEY);
  if (!raw) return null;
  try {
    return JSON.parse(raw) as AuthSession;
  } catch {
    return null;
  }
}

async function loadPersistedEndpoint(): Promise<string | null> {
  const storage = getAsyncStorage();
  return storage.getItem(SERVICE_ENDPOINT_KEY);
}

async function persistMode(mode: AuthMode): Promise<void> {
  const storage = getAsyncStorage();
  await storage.setItem(AUTH_MODE_STORAGE_KEY, mode);
}

async function persistSession(session: AuthSession): Promise<void> {
  const storage = getAsyncStorage();
  await storage.setItem(AUTH_SESSION_KEY, JSON.stringify(session));
}

async function persistEndpoint(endpoint: string): Promise<void> {
  const storage = getAsyncStorage();
  await storage.setItem(SERVICE_ENDPOINT_KEY, endpoint);
}

async function clearPersistedAuth(): Promise<void> {
  const storage = getAsyncStorage();
  await Promise.all([
    storage.removeItem(AUTH_MODE_STORAGE_KEY),
    storage.removeItem(AUTH_SESSION_KEY),
    storage.removeItem(SERVICE_ENDPOINT_KEY),
  ]);
}

// ── Provider ────────────────────────────────────────────────────────

export function AuthProvider({children}: {children: React.ReactNode}) {
  const [auth, setAuth] = useState<AuthState>({
    status: 'loading',
    mode: null,
    session: null,
    serviceEndpoint: null,
  });

  // Load persisted mode + session on mount
  useEffect(() => {
    if (auth.status !== 'loading') return;
    (async () => {
      try {
        const [mode, session, endpoint] = await Promise.all([
          loadPersistedMode(),
          loadPersistedSession(),
          loadPersistedEndpoint(),
        ]);

        if (mode === 'local') {
          setAuth({status: 'local', mode: 'local', session: null, serviceEndpoint: null});
        } else if (mode === 'service' && session && endpoint) {
          // Check if access token is still valid
          const expiresAt = new Date(session.expiresAt);
          if (expiresAt > new Date()) {
            setAuth({status: 'authenticated', mode: 'service', session, serviceEndpoint: endpoint});
          } else {
            // Token expired — try to refresh
            try {
              const client = new FstAuthClient(endpoint);
              const refreshResult = await client.refresh(session.refreshToken);
              const newSession: AuthSession = {
                ...session,
                accessToken: refreshResult.accessToken,
                refreshToken: refreshResult.refreshToken,
                expiresAt: new Date(
                  Date.now() + refreshResult.expiresIn * 1000,
                ).toISOString(),
              };
              await persistSession(newSession);
              setAuth({status: 'authenticated', mode: 'service', session: newSession, serviceEndpoint: endpoint});
            } catch {
              // Refresh failed — force re-login
              setAuth({status: 'choosing', mode: null, session: null, serviceEndpoint: null});
            }
          }
        } else {
          setAuth({status: 'choosing', mode: null, session: null, serviceEndpoint: null});
        }
      } catch {
        setAuth({status: 'choosing', mode: null, session: null, serviceEndpoint: null});
      }
    })();
  }, [auth.status]);

  const confirmLocal = useCallback(() => {
    (async () => {
      try { await persistMode('local'); } catch { /* best effort */ }
      setAuth({status: 'local', mode: 'local', session: null, serviceEndpoint: null});
    })();
  }, []);

  const promptLocal = useCallback(() => {
    const message =
      'Selecting local allows the app to be used locally, and make all leaderboard requests itself. ' +
      'However, it removes certain features from the app, and requires the user to copy and paste a sensitive code from Epic Games code endpoints.\n\n' +
      'We recommend connecting to a Festival Score Tracker service if you have an endpoint available to you. ' +
      'Continue with local mode at your own risk.';

    if (Platform.OS === 'ios') {
      Alert.alert('Warning', message, [
        {text: 'Cancel', style: 'cancel'},
        {text: 'Use Local', style: 'destructive', onPress: confirmLocal},
      ]);
    } else {
      // Android / Windows — no destructive styling
      Alert.alert('Warning', message, [
        {text: 'Cancel', style: 'cancel'},
        {text: 'Use Local', onPress: confirmLocal},
      ]);
    }
  }, [confirmLocal]);

  const signInWithService = useCallback((serviceEndpoint: string) => {
    const endpoint = serviceEndpoint.trim();
    if (!endpoint) {
      Alert.alert('Missing Endpoint', 'Please enter the Festival Score Tracker service endpoint.');
      return;
    }

    (async () => {
      try {
        // 1. Health check — verify the service is reachable
        try {
          const controller = new AbortController();
          const timeoutId = setTimeout(() => controller.abort(), 10_000);
          const healthRes = await fetch(`${endpoint.replace(/\/+$/, '')}/healthz`, {
            method: 'GET',
            signal: controller.signal,
          });
          clearTimeout(timeoutId);
          if (!healthRes.ok) {
            Alert.alert(
              'Service Unavailable',
              `Festival Score Tracker at ${endpoint} returned HTTP ${healthRes.status}. ` +
                'Please check the URL and try again.',
            );
            return;
          }
        } catch (err: any) {
          const detail = err?.message ?? String(err);
          console.error('[AuthContext] Health check failed:', detail, err);
          Alert.alert(
            'Connection Failed',
            `Could not reach the Festival Score Tracker service at ${endpoint}.\n\n${detail}`,
          );
          return;
        }

        // 2. Open Epic Games sign-in in the in-app browser
        let authorizationCode: string;
        try {
          const epicResult = await authorizeWithEpic(endpoint);
          authorizationCode = epicResult.authorizationCode;
        } catch (err: any) {
          if (err instanceof EpicAuthCancelledError) {
            // User cancelled — just return silently
            return;
          }
          Alert.alert(
            'Epic Login Failed',
            err?.message ?? 'An error occurred during Epic Games sign-in.',
          );
          return;
        }

        // 3. Generate a device identifier
        const deviceId = await getDeviceId();

        // 4. Send the authorization code to the FST service
        const client = new FstAuthClient(endpoint);
        const loginResult = await client.login(
          authorizationCode,
          deviceId,
          Platform.OS as 'ios' | 'android' | 'windows',
        );

        // 5. Build session and persist everything
        const session: AuthSession = {
          accessToken: loginResult.accessToken,
          refreshToken: loginResult.refreshToken,
          expiresAt: new Date(
            Date.now() + loginResult.expiresIn * 1000,
          ).toISOString(),
          accountId: loginResult.accountId,
          displayName: loginResult.displayName,
        };

        await Promise.all([
          persistMode('service'),
          persistSession(session),
          persistEndpoint(endpoint),
        ]);

        // 6. Transition to authenticated
        setAuth({
          status: 'authenticated',
          mode: 'service',
          session,
          serviceEndpoint: endpoint,
        });
      } catch (error: any) {
        if (error instanceof FstAuthError) {
          Alert.alert(
            'Sign-In Failed',
            `The Festival Score Tracker service returned an error (HTTP ${error.statusCode}). ` +
              'Please try again.',
          );
          return;
        }

        Alert.alert(
          'Sign-In Failed',
          error?.message ?? 'An unexpected error occurred during sign-in.',
        );
      }
    })();
  }, []);

  const refreshAccessToken = useCallback(async (): Promise<string> => {
    if (!auth.session || !auth.serviceEndpoint) {
      throw new Error('Cannot refresh: no active session');
    }

    const client = new FstAuthClient(auth.serviceEndpoint);
    const result = await client.refresh(auth.session.refreshToken);

    const newSession: AuthSession = {
      ...auth.session,
      accessToken: result.accessToken,
      refreshToken: result.refreshToken,
      expiresAt: new Date(
        Date.now() + result.expiresIn * 1000,
      ).toISOString(),
    };

    await persistSession(newSession);
    setAuth(prev => ({...prev, session: newSession}));

    return result.accessToken;
  }, [auth.session, auth.serviceEndpoint]);

  const signOut = useCallback(async () => {
    // Best-effort: revoke the session on the server
    if (auth.session && auth.serviceEndpoint) {
      try {
        const client = new FstAuthClient(auth.serviceEndpoint);
        await client.logout(auth.session.refreshToken);
      } catch {
        // Don't block sign-out on network errors
      }
    }

    try { await clearPersistedAuth(); } catch { /* best effort */ }
    setAuth({status: 'choosing', mode: null, session: null, serviceEndpoint: null});
  }, [auth.session, auth.serviceEndpoint]);

  const value = useMemo<AuthContextValue>(
    () => ({
      auth,
      authActions: {confirmLocal, promptLocal, signInWithService, refreshAccessToken, signOut},
    }),
    [auth, confirmLocal, promptLocal, signInWithService, refreshAccessToken, signOut],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

// ── Hook ────────────────────────────────────────────────────────────

export function useAuth(): AuthContextValue {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('useAuth must be used within <AuthProvider>');
  return ctx;
}
