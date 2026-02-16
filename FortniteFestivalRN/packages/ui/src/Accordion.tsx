import React, {useCallback, useEffect, useState} from 'react';
import {LayoutChangeEvent, Pressable, StyleSheet, Text, View} from 'react-native';
import Ionicons from 'react-native-vector-icons/Ionicons';
import Animated, {useAnimatedStyle, useSharedValue, withTiming} from 'react-native-reanimated';

// ── Types ───────────────────────────────────────────────────────────

export type AccordionProps = {
  /** Section title displayed in the header. */
  title: string;
  /** Optional hint text shown below the title. */
  hint?: string;
  /** Whether the accordion starts expanded. Default: false. */
  initiallyOpen?: boolean;
  /** Content rendered inside the collapsible body. */
  children: React.ReactNode;
};

// ── Component ───────────────────────────────────────────────────────

const ANIMATION_DURATION = 250;

export function Accordion({title, hint, initiallyOpen = false, children}: AccordionProps) {
  const [open, setOpen] = useState(initiallyOpen);
  const [contentHeight, setContentHeight] = useState(0);

  const animatedHeight = useSharedValue(initiallyOpen ? 1 : 0);
  const chevronRotation = useSharedValue(initiallyOpen ? 1 : 0);

  useEffect(() => {
    animatedHeight.value = withTiming(open ? 1 : 0, {duration: ANIMATION_DURATION});
    chevronRotation.value = withTiming(open ? 1 : 0, {duration: ANIMATION_DURATION});
  }, [open, animatedHeight, chevronRotation]);

  const toggle = useCallback(() => setOpen(prev => !prev), []);

  const onContentLayout = useCallback((e: LayoutChangeEvent) => {
    const h = e.nativeEvent.layout.height;
    if (h > 0) setContentHeight(h);
  }, []);

  const bodyStyle = useAnimatedStyle(() => ({
    height: contentHeight > 0 ? animatedHeight.value * contentHeight : undefined,
    opacity: animatedHeight.value,
    overflow: 'hidden' as const,
  }));

  const chevronStyle = useAnimatedStyle(() => ({
    transform: [{rotate: `${chevronRotation.value * 180}deg`}],
  }));

  return (
    <View style={styles.container}>
      {/* Header */}
      <Pressable
        onPress={toggle}
        style={({pressed}) => [styles.header, pressed && styles.headerPressed]}
        accessibilityRole="button"
        accessibilityState={{expanded: open}}
      >
        <View style={styles.headerText}>
          <Text style={styles.title}>{title}</Text>
          {hint ? <Text style={styles.hint}>{hint}</Text> : null}
        </View>
        <Animated.View style={chevronStyle}>
          <Ionicons name="chevron-down" size={20} color="#8899AA" />
        </Animated.View>
      </Pressable>

      {/* Collapsible body */}
      <Animated.View style={bodyStyle}>
        <View onLayout={onContentLayout} style={styles.body}>
          {children}
        </View>
      </Animated.View>
    </View>
  );
}

// ── Styles ──────────────────────────────────────────────────────────

const styles = StyleSheet.create({
  container: {
    // No extra wrapper styling – the accordion inherits from its parent section.
  },
  header: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    paddingVertical: 4,
  },
  headerPressed: {
    opacity: 0.85,
  },
  headerText: {
    flex: 1,
    marginRight: 12,
    gap: 4,
  },
  title: {
    color: '#FFFFFF',
    fontSize: 16,
    fontWeight: '800',
  },
  hint: {
    color: '#D7DEE8',
    opacity: 0.85,
    fontSize: 12,
    lineHeight: 16,
  },
  body: {
    paddingTop: 8,
  },
});
