import React from 'react';
import {useEffect} from 'react';
import Animated, {useAnimatedStyle, useSharedValue, withTiming} from 'react-native-reanimated';
import Ionicons from 'react-native-vector-icons/Ionicons';

const ANIMATION_DURATION = 250;

export interface AccordionAnimationResult {
  /** The Animated View component to wrap collapsible body content. */
  AnimatedView: typeof Animated.View;
  /** Style to apply to the body wrapper. */
  bodyStyle: any;
  /** The chevron element to render in the header. */
  chevronElement: React.ReactNode;
}

/**
 * Accordion animation hook — Reanimated variant (iOS / Android).
 * Uses `useSharedValue` and `useAnimatedStyle` for smooth native-driven animations.
 */
export function useAccordionAnimation(
  open: boolean,
  contentHeight: number,
): AccordionAnimationResult {
  const animatedHeight = useSharedValue(open ? 1 : 0);
  const chevronRotation = useSharedValue(open ? 1 : 0);

  useEffect(() => {
    animatedHeight.value = withTiming(open ? 1 : 0, {duration: ANIMATION_DURATION});
    chevronRotation.value = withTiming(open ? 1 : 0, {duration: ANIMATION_DURATION});
  }, [open, animatedHeight, chevronRotation]);

  const bodyStyle = useAnimatedStyle(() => ({
    height: contentHeight > 0 ? animatedHeight.value * contentHeight : undefined,
    opacity: animatedHeight.value,
    overflow: 'hidden' as const,
  }));

  const chevronStyle = useAnimatedStyle(() => ({
    transform: [{rotate: `${chevronRotation.value * 180}deg`}],
  }));

  const chevronElement = (
    <Animated.View style={chevronStyle}>
      <Ionicons name="chevron-down" size={20} color="#8899AA" />
    </Animated.View>
  );

  return {
    AnimatedView: Animated.View,
    bodyStyle,
    chevronElement,
  };
}
