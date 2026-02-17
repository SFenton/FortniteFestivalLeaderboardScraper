import React from 'react';
import {useEffect, useRef} from 'react';
import {Animated} from 'react-native';
import Ionicons from 'react-native-vector-icons/Ionicons';

import type {AccordionAnimationResult} from './useAccordionAnimation';

const ANIMATION_DURATION = 250;

/**
 * Accordion animation hook — Windows variant.
 * Uses React Native's built-in Animated API since react-native-reanimated
 * doesn't have Windows native modules.
 */
export function useAccordionAnimation(
  open: boolean,
  contentHeight: number,
): AccordionAnimationResult {
  const animatedHeight = useRef(new Animated.Value(open ? 1 : 0)).current;

  useEffect(() => {
    Animated.timing(animatedHeight, {
      toValue: open ? 1 : 0,
      duration: ANIMATION_DURATION,
      useNativeDriver: false,
    }).start();
  }, [open, animatedHeight]);

  const bodyHeight =
    contentHeight > 0
      ? animatedHeight.interpolate({inputRange: [0, 1], outputRange: [0, contentHeight]})
      : undefined;

  const bodyStyle = {height: bodyHeight, opacity: animatedHeight, overflow: 'hidden' as const};

  const chevronElement = (
    <Ionicons name={open ? 'chevron-up' : 'chevron-down'} size={20} color="#8899AA" />
  );

  return {
    AnimatedView: Animated.View as any,
    bodyStyle,
    chevronElement,
  };
}
