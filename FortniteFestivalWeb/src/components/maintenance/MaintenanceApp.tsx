import EmptyState from '../common/EmptyState';
import { buildStaggerStyle, clearStaggerStyle } from '../../hooks/ui/useStaggerStyle';
import styles from './MaintenanceApp.module.css';

type MaintenanceAppProps = {
  checking?: boolean;
};

const TITLE = 'Festival Score Tracker Status';
const MAINTENANCE_MESSAGE = 'Festival Score Tracker is currently down for maintenance. Please check again shortly. For questions, feel free to reach out to SFenton on Discord or Twitter.';
const CHECKING_MESSAGE = 'Checking Festival Score Tracker status...';
const MAINTENANCE_TITLE_STYLE = { fontSize: 'calc(var(--font-xl) * 1.5)' };
const MAINTENANCE_SUBTITLE_STYLE = { fontSize: 'calc(var(--font-md) * 1.5)' };

export default function MaintenanceApp({ checking = false }: MaintenanceAppProps) {
  return (
    <main className={styles.page} aria-label={TITLE} aria-live="polite">
      <EmptyState
        className={styles.emptyState}
        title={TITLE}
        subtitle={checking ? CHECKING_MESSAGE : MAINTENANCE_MESSAGE}
        style={buildStaggerStyle(200)}
        titleStyle={MAINTENANCE_TITLE_STYLE}
        subtitleStyle={MAINTENANCE_SUBTITLE_STYLE}
        onAnimationEnd={clearStaggerStyle}
      />
    </main>
  );
}
