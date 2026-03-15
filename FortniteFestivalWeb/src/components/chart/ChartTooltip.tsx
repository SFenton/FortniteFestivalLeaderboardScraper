import type { ChartPoint } from './useChartData';
import { Colors, Font, Gap } from '@festival/theme';

interface Props {
  active?: boolean;
  payload?: Array<{ payload: ChartPoint }>;
}

export default function ChartTooltip({ active, payload }: Props) {
  if (!active || !payload?.length) return null;
  const first = payload[0];
  if (!first) return null;
  const d = first.payload;
  return (
    <div style={styles.tooltip}>
      <div style={styles.date}>
        {new Date(d.date).toLocaleDateString('en-US', { month: 'long', day: 'numeric', year: 'numeric' })}
        {' '}
        {new Date(d.date).toLocaleTimeString('en-US', { hour: 'numeric', minute: '2-digit' })}
        {d.season != null && <span style={styles.season}> · S{d.season}</span>}
      </div>
      <div style={styles.row}>
        <span style={{ color: Colors.accentBlueBright, fontWeight: 600 }}>Score:</span>{' '}
        {d.score.toLocaleString()}
      </div>
      <div style={styles.row}>
        <span style={{ color: Colors.accentPurple, fontWeight: 600 }}>Accuracy:</span>{' '}
        {d.accuracy % 1 === 0 ? `${d.accuracy}%` : `${d.accuracy.toFixed(1)}%`}
        {d.isFullCombo && <span style={styles.fc}>FC</span>}
      </div>
      {d.stars != null && <div style={styles.row}>{'★'.repeat(d.stars)}</div>}
    </div>
  );
}

const styles: Record<string, React.CSSProperties> = {
  tooltip: {
    backgroundColor: Colors.backgroundCard,
    border: `1px solid ${Colors.borderPrimary}`,
    borderRadius: 8,
    padding: `${Gap.md}px ${Gap.xl}px`,
    fontSize: Font.sm,
    color: Colors.textPrimary,
    lineHeight: '1.5',
    boxShadow: '0 4px 12px rgba(0,0,0,0.4)',
  },
  date: { fontWeight: 600, marginBottom: Gap.xs },
  season: { color: Colors.textTertiary, fontWeight: 400 },
  row: {},
  fc: {
    marginLeft: Gap.sm,
    color: Colors.gold,
    fontWeight: 700,
    fontSize: Font.xs,
  },
};
