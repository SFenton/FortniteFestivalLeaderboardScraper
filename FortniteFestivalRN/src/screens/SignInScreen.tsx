import React, {useCallback, useRef, useState} from 'react';
import {
  Platform,
  Pressable,
  StyleSheet,
  Text,
  TextInput,
  View,
} from 'react-native';
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
};

/**
 * Full-screen sign-in screen shown after the intro carousel (or on
 * re-launch when no persisted auth mode exists).
 *
 * Two options:
 *   • Enter Epic Games Username  (primary, purple)
 *   • Use Locally              (secondary, dark — shows warning alert)
 *
 * The transparent background lets the SlidingRowsBackground shine through.
 */
export function SignInScreen({onContinue}: {onContinue: () => void}) {
  const {authActions} = useAuth();
  const [serviceEndpoint, setServiceEndpoint] = useState('');
  const [epicUsername, setEpicUsername] = useState('');
  const inputRef = useRef<TextInput>(null);

  const handleSignIn = useCallback(() => {
    authActions.signInWithService(serviceEndpoint, epicUsername);
  }, [authActions, serviceEndpoint, epicUsername]);

  const handleLocal = () => {
    // Show the warning alert; if the user confirms, persist local mode
    // and then proceed to the spinner → main app transition.
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
        <View style={styles.exchangeCodeContainer}>
          <Text style={styles.cardBody}>
            Enter the endpoint of the Festival Score Tracker service you want
            to connect to.
          </Text>
          <TextInput
            ref={inputRef}
            style={styles.exchangeCodeInput}
            placeholder="https://example.com"
            placeholderTextColor="rgba(255,255,255,0.4)"
            value={serviceEndpoint}
            onChangeText={setServiceEndpoint}
            autoCapitalize="none"
            autoCorrect={false}
            selectionColor={COLORS.epicButtonBorder}
          />
        </View>
        <View style={styles.exchangeCodeContainer}>
          <Text style={styles.cardBody}>
            Enter your Epic Games username here.
          </Text>
          <TextInput
            style={styles.exchangeCodeInput}
            placeholder="Username"
            placeholderTextColor="rgba(255,255,255,0.4)"
            value={epicUsername}
            onChangeText={setEpicUsername}
            autoCapitalize="none"
            autoCorrect={false}
            selectionColor={COLORS.epicButtonBorder}
          />
        </View>
        <Pressable onPress={handleSignIn} style={({pressed}) => pressed && styles.pressed}>
          <View style={styles.epicButton}>
            <Text style={styles.epicButtonText}>Sign In</Text>
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
  epicButton: {
    backgroundColor: COLORS.epicButton,
    borderColor: COLORS.epicButtonBorder,
    borderWidth: 1,
    borderRadius: 12,
    paddingVertical: 12,
    alignItems: 'center',
    marginTop: 4,
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
