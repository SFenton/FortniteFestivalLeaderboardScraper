import React, {useCallback, useState} from 'react';
import {LayoutChangeEvent, Pressable, StyleSheet, Text, View} from 'react-native';
import {useAccordionAnimation} from './useAccordionAnimation';
import {Colors, Font, LineHeight, Gap, Opacity} from './theme';

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

export function Accordion({title, hint, initiallyOpen = false, children}: AccordionProps) {
  const [open, setOpen] = useState(initiallyOpen);
  const [contentHeight, setContentHeight] = useState(0);

  const {AnimatedView, bodyStyle, chevronElement} = useAccordionAnimation(open, contentHeight);

  const toggle = useCallback(() => setOpen(prev => !prev), []);

  const onContentLayout = useCallback((e: LayoutChangeEvent) => {
    const h = e.nativeEvent.layout.height;
    if (h > 0) setContentHeight(h);
  }, []);

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
        {chevronElement}
      </Pressable>

      {/* Collapsible body */}
      <AnimatedView style={bodyStyle}>
        <View onLayout={onContentLayout} style={styles.body}>
          {children}
        </View>
      </AnimatedView>
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
    paddingVertical: Gap.sm,
  },
  headerPressed: {
    opacity: Opacity.pressed,
  },
  headerText: {
    flex: 1,
    marginRight: Gap.xl,
    gap: Gap.sm,
  },
  title: {
    color: Colors.textPrimary,
    fontSize: Font.lg,
    fontWeight: '800',
  },
  hint: {
    color: Colors.textSecondary,
    opacity: Opacity.pressed,
    fontSize: Font.sm,
    lineHeight: LineHeight.sm,
  },
  body: {
    paddingTop: Gap.md,
  },
});
