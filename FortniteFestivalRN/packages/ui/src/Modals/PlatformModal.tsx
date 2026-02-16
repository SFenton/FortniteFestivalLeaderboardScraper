import React, {useEffect, useRef} from 'react';
import {Animated, Platform, Pressable, StyleSheet, View} from 'react-native';

import RNModal from 'react-native-modal';

type Variant = 'center' | 'bottom';

const WIN_FLYOUT_WIDTH = 600;

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
  // For Windows we render an in-tree overlay with a right-side flyout.
  if (Platform.OS === 'windows') {
    return <WindowsFlyoutModal visible={visible} onRequestClose={onRequestClose}>{children}</WindowsFlyoutModal>;
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

/** Right-side flyout panel for Windows, matching the left-side nav flyout pattern. */
function WindowsFlyoutModal(props: {visible: boolean; onRequestClose: () => void; children: React.ReactNode}) {
  const {visible, onRequestClose, children} = props;
  const translateX = useRef(new Animated.Value(WIN_FLYOUT_WIDTH)).current;
  const scrimOpacity = useRef(new Animated.Value(0)).current;
  const prevVisibleRef = useRef(visible);

  useEffect(() => {
    const wasVisible = prevVisibleRef.current;
    prevVisibleRef.current = visible;

    if (visible && !wasVisible) {
      // Stop any in-flight close animation.
      translateX.stopAnimation();
      scrimOpacity.stopAnimation();

      // Reset to closed position, then animate open.
      translateX.setValue(WIN_FLYOUT_WIDTH);
      scrimOpacity.setValue(0);

      Animated.parallel([
        Animated.timing(translateX, {
          toValue: 0,
          duration: 200,
          useNativeDriver: false,
        }),
        Animated.timing(scrimOpacity, {
          toValue: 1,
          duration: 200,
          useNativeDriver: false,
        }),
      ]).start();
    } else if (!visible && wasVisible) {
      Animated.parallel([
        Animated.timing(translateX, {
          toValue: WIN_FLYOUT_WIDTH,
          duration: 160,
          useNativeDriver: false,
        }),
        Animated.timing(scrimOpacity, {
          toValue: 0,
          duration: 160,
          useNativeDriver: false,
        }),
      ]).start();
    }
  }, [visible, translateX, scrimOpacity]);

  if (!visible) return null;

  return (
    <View style={styles.winOverlay} pointerEvents="box-none">
      <Animated.View style={[styles.winScrim, {opacity: scrimOpacity}]}>
        <Pressable style={StyleSheet.absoluteFill} onPress={onRequestClose} />
      </Animated.View>
      <Animated.View
        style={[styles.winFlyout, {transform: [{translateX}]}]}
        pointerEvents="auto"
      >
        {children}
      </Animated.View>
    </View>
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
  winFlyout: {
    position: 'absolute',
    top: 0,
    bottom: 0,
    right: 0,
    width: WIN_FLYOUT_WIDTH,
    zIndex: 10,
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
