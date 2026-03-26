/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { memo, useMemo } from 'react';
import { Colors, Font, Gap, Weight, LineHeight } from '@festival/theme';

interface SectionHeaderProps {
  title: string;
  description?: string;
  flush?: boolean;
}

const SectionHeader = memo(function SectionHeader({ title, description, flush }: SectionHeaderProps) {
  const s = useStyles();
  return (
    <>
      <div style={s.title}>{title}</div>
      {description && <div style={flush ? s.descriptionFlush : s.description}>{description}</div>}
    </>
  );
});

export default SectionHeader;

function useStyles() {
  return useMemo(() => {
    const description = {
      fontSize: Font.md,
      color: Colors.textSecondary,
      lineHeight: LineHeight.relaxed,
      marginBottom: Gap.md,
    };
    return {
      title: {
        fontSize: Font.xl,
        fontWeight: Weight.bold,
        color: Colors.textPrimary,
      },
      description,
      descriptionFlush: { ...description, marginBottom: Gap.none },
    };
  }, []);
}
