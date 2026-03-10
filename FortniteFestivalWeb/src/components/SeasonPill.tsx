import { Colors, Font, Gap, Radius } from '../theme';

const style: React.CSSProperties = {
  flexShrink: 0,
  width: 48,
  textAlign: 'center',
  padding: `${Gap.xs}px ${Gap.sm}px`,
  borderRadius: Radius.xs,
  backgroundColor: Colors.surfaceSubtle,
  color: Colors.textSecondary,
  fontSize: Font.lg,
  fontWeight: 600,
  border: `2px solid ${Colors.borderSubtle}`,
  display: 'inline-block',
};

export default function SeasonPill({ season }: { season: number }) {
  return <span style={style}>S{season}</span>;
}
