import { useMemo, type CSSProperties } from 'react';
import MarqueeText from './MarqueeText';
import { Colors, Font, Gap, Radius, padding } from '@festival/theme';

export function LongText(props?: Record<string, unknown>) {
  const width = typeof props?.width === 'number' ? props.width : 220;
  const styles = useMemo<Record<string, CSSProperties>>(() => ({
    frame: {
      width,
      padding: padding(Gap.md),
      border: `1px solid ${Colors.borderSubtle}`,
      borderRadius: Radius.sm,
      background: Colors.surfaceFrosted,
      color: Colors.textPrimary,
      fontSize: Font.md,
    },
  }), [width]);

  return (
    <div style={styles.frame}>
      <MarqueeText
        text="A deliberately long Festival title that must activate marquee overflow"
        style={{ display: 'block', width: '100%' }}
      />
    </div>
  );
}
