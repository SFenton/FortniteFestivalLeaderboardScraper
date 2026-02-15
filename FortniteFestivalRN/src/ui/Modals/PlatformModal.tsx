import React from 'react';
import {Platform, Pressable, StyleSheet, View} from 'react-native';

import RNModal from 'react-native-modal';

type Variant = 'center' | 'bottom';

export function PlatformModal(props: {
  visible: boolean;
  onRequestClose: () => void;
  variant: Variant;
  /** When true, removes horizontal padding so content can span full device width. */
  fullWidth?: boolean;
  children: React.ReactNode;
}) {
  const {visible, onRequestClose, variant, fullWidth, children} = props;

  // RNW's built-in Modal can pop as a separate window/dialog.
  // For Windows we render an in-tree overlay instead.
  if (Platform.OS === 'windows') {
    if (!visible) return null;
    return (
      <View style={styles.winOverlay} pointerEvents="box-none">
        <Pressable style={styles.winScrim} onPress={onRequestClose} />
        <View style={[styles.winContent, variant === 'bottom' ? styles.winContentBottom : styles.winContentCenter]} pointerEvents="box-none">
          {children}
        </View>
      </View>
    );
  }

  return (
    <RNModal
      isVisible={visible}
      onBackdropPress={onRequestClose}
      onBackButtonPress={onRequestClose}
      backdropOpacity={0.55}
      useNativeDriver
      style={variant === 'bottom' ? (fullWidth ? styles.mobileBottomFull : styles.mobileBottom) : styles.mobileCenter}
      animationIn={variant === 'bottom' ? 'slideInUp' : 'fadeIn'}
      animationOut={variant === 'bottom' ? 'slideOutDown' : 'fadeOut'}
      hideModalContentWhileAnimating
      propagateSwipe
    >
      {children}
    </RNModal>
  );
}

const styles = StyleSheet.create({
  winOverlay: {
    ...StyleSheet.absoluteFillObject,
    zIndex: 1000,
  },
  winScrim: {
    ...StyleSheet.absoluteFillObject,
    backgroundColor: 'rgba(0,0,0,0.55)',
  },
  winContent: {
    ...StyleSheet.absoluteFillObject,
    padding: 18,
  },
  winContentCenter: {
    justifyContent: 'center',
  },
  winContentBottom: {
    justifyContent: 'flex-end',
  },

  mobileCenter: {
    margin: 0,
    justifyContent: 'center',
    padding: 18,
  },
  mobileBottom: {
    margin: 0,
    justifyContent: 'flex-end',
    padding: 12,
  },
  mobileBottomFull: {
    margin: 0,
    justifyContent: 'flex-end',
    padding: 0,
  },
});
