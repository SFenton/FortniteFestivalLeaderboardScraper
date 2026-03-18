import type { ChartPoint } from '../../../../hooks/chart/useChartData';
import css from './ChartTooltip.module.css';

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
    <div className={css.tooltip}>
      <div className={css.date}>
        {new Date(d.date).toLocaleDateString('en-US', { month: 'long', day: 'numeric', year: 'numeric' })}
        {' '}
        {new Date(d.date).toLocaleTimeString('en-US', { hour: 'numeric', minute: '2-digit' })}
        {d.season != null && <span className={css.season}> · S{d.season}</span>}
      </div>
      <div>
        <span className={css.scoreLabel}>Score:</span>{' '}
        {d.score.toLocaleString()}
      </div>
      <div>
        <span className={css.accLabel}>Accuracy:</span>{' '}
        {d.accuracy % 1 === 0 ? `${d.accuracy}%` : `${d.accuracy.toFixed(1)}%`}
        {d.isFullCombo && <span className={css.fc}>FC</span>}
      </div>
      {d.stars != null && <div>{'★'.repeat(d.stars)}</div>}
    </div>
  );
}
