import React, {useCallback, useRef, useState} from 'react';
import {
  ActivityIndicator,
  Platform,
  Pressable,
  StyleSheet,
  Text,
  TextInput,
  View,
} from 'react-native';
import {FestivalTextInput} from '@festival/ui';
import {useSafeAreaInsets} from 'react-native-safe-area-context';

import {FrostedSurface} from '@festival/ui';
import {useAuth} from '@festival/contexts';

// ── Colors — matches the intro carousel's Fortnite Festival palette ──
const COLORS = {
  textPrimary: '#FFFFFF',
  textSecondary: 'rgba(255,255,255,0.7)',
  epicButton: '#7B2FBE',
  epicButtonBorder: '#9D4EDD',
  localButton: '#223047',
  localButtonBorder: '#2B3B55',
  disabledButton: 'rgba(123,47,190,0.4)',
};

const DEFAULT_ENDPOINT = 'https://festivalscoretracker.com';

/**
 * Full-screen sign-in screen shown after the intro carousel (or on
 * re-launch when no persisted auth mode exists).
 *
 * Two options:
 *   • Sign In with Epic Games  (primary, purple — health check → OAuth → login)
 *   • Use Locally              (secondary, dark — shows warning alert)
 *
 * The transparent background lets the SlidingRowsBackground shine through.
 */
export function SignInScreen({onContinue}: {onContinue: () => void}) {
  const {authActions} = useAuth();
  const [serviceEndpoint, setServiceEndpoint] = useState(DEFAULT_ENDPOINT);
  const [isSigningIn, setIsSigningIn] = useState(false);
  const inputRef = useRef<TextInput>(null);

  const handleSignIn = useCallback(() => {
    setIsSigningIn(true);
    // signInWithService is fire-and-forget (async inside); we reset state
    // when the flow completes or errors (auth state change or alert dismiss).
    // Use a microtask to ensure the spinner renders before the OAuth browser opens.
    setTimeout(() => {
      authActions.signInWithService(serviceEndpoint);
      // Reset after a short delay — if the user cancels the browser or an
      // alert fires, we want the button re-enabled.
      setTimeout(() => setIsSigningIn(false), 2000);
    }, 100);
  }, [authActions, serviceEndpoint]);

  const handleLocal = () => {
    authActions.promptLocal();
  };

  const insets = useSafeAreaInsets();

  return (
    <View style={[styles.root, {paddingTop: insets.top + 24, paddingBottom: insets.bottom + 24}]}>
      {/* Title area — plain header anchored at the top, like tab screens */}
      <View style={styles.header}>
        <Text style={styles.title}>Sign In</Text>
      </View>

      <View style={styles.cardWrapper}>

      {/* Option cards — single blurred surface with both choices */}
      <FrostedSurface style={styles.card} tint="dark" intensity={18}>
        {/* ── Service login ──────────────────────────────────── */}
        <Text style={styles.cardTitle}>Connect to Festival Score Tracker</Text>
        <Text style={styles.cardBody}>
          Sync your scores automatically, see friends, rankings, score history, and more.
        </Text>
        <View style={styles.inputContainer}>
          <Text style={styles.cardBody}>
            Service endpoint (change only for local testing):
          </Text>
          <FestivalTextInput
            ref={inputRef}
            style={styles.endpointInput}
            placeholder="https://festivalscoretracker.com"
            placeholderTextColor="rgba(255,255,255,0.4)"
            value={serviceEndpoint}
            onChangeText={setServiceEndpoint}
            autoCapitalize="none"
            autoCorrect={false}
            selectionColor={COLORS.epicButtonBorder}
          />
        </View>
        <Pressable
          onPress={handleSignIn}
          disabled={isSigningIn}
          style={({pressed}) => pressed && !isSigningIn && styles.pressed}>
          <View style={[styles.epicButton, isSigningIn && styles.epicButtonDisabled]}>
            {isSigningIn ? (
              <ActivityIndicator color={COLORS.textPrimary} size="small" />
            ) : (
              <Text style={styles.epicButtonText}>Sign In with Epic Games</Text>
            )}
          </View>
        </Pressable>

        {/* Divider */}
        <View style={styles.divider} />

        {/* ── Local mode ────────────────────────────────────────── */}
        <Text style={styles.cardTitle}>Use Locally</Text>
        <Text style={styles.cardBody}>
          Fetch scores directly from Epic with an exchange code. No account required.
        </Text>
        <Pressable onPress={handleLocal} style={({pressed}) => pressed && styles.pressed}>
          <View style={styles.localButton}>
            <Text style={styles.localButtonText}>Use Local</Text>
          </View>
        </Pressable>
      </FrostedSurface>
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  root: {
    flex: 1,
    paddingHorizontal: 28,
  },
  header: {
    marginBottom: 16,
  },
  cardWrapper: {
    flex: 1,
    justifyContent: 'center',
  },
  title: {
    fontSize: Platform.OS === 'ios' ? 34 : 22,
    fontWeight: '700',
    color: COLORS.textPrimary,
    lineHeight: Platform.OS === 'ios' ? 41 : 28,
  },
  subtitle: {
    fontSize: 15,
    color: COLORS.textSecondary,
    marginTop: 6,
  },
  card: {
    borderRadius: 16,
    padding: 20,
    gap: 10,
  },
  divider: {
    height: StyleSheet.hairlineWidth,
    backgroundColor: 'rgba(255,255,255,0.15)',
    marginVertical: 14,
  },
  cardTitle: {
    fontSize: 17,
    fontWeight: '700',
    color: COLORS.textPrimary,
  },
  cardBody: {
    fontSize: 14,
    lineHeight: 20,
    color: COLORS.textSecondary,
  },
  exchangeCodeContainer: {
    marginTop: 12,
  },
  inputContainer: {
    marginTop: 12,
  },
  exchangeCodeInput: {
    backgroundColor: 'rgba(255,255,255,0.08)',
    borderColor: COLORS.epicButtonBorder,
    borderWidth: 1,
    borderRadius: 12,
    paddingVertical: 12,
    paddingHorizontal: 14,
    fontSize: 16,
    color: COLORS.textPrimary,
  },
  endpointInput: {
    backgroundColor: 'rgba(255,255,255,0.08)',
    borderColor: COLORS.epicButtonBorder,
    borderWidth: 1,
    borderRadius: 12,
    paddingVertical: 12,
    paddingHorizontal: 14,
    fontSize: 16,
    color: COLORS.textPrimary,
  },
  epicButton: {
    backgroundColor: COLORS.epicButton,
    borderColor: COLORS.epicButtonBorder,
    borderWidth: 1,
    borderRadius: 12,
    paddingVertical: 12,
    alignItems: 'center',
    marginTop: 4,
  },
  epicButtonDisabled: {
    backgroundColor: COLORS.disabledButton,
  },
  epicButtonText: {
    fontSize: 16,
    fontWeight: '800',
    color: COLORS.textPrimary,
  },
  localButton: {
    backgroundColor: COLORS.localButton,
    borderColor: COLORS.localButtonBorder,
    borderWidth: 1,
    borderRadius: 12,
    paddingVertical: 12,
    alignItems: 'center',
    marginTop: 4,
  },
  localButtonText: {
    fontSize: 16,
    fontWeight: '800',
    color: COLORS.textPrimary,
  },
  pressed: {
    opacity: 0.85,
  },
});
