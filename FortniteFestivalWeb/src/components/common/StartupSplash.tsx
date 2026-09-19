import { useTranslation } from 'react-i18next';
import { TRANSITION_MS, ZIndex } from '@festival/theme';
import type { TransitionEvent } from 'react';
import ArcSpinner from './ArcSpinner';
import styles from './StartupSplash.module.css';

export type StartupSplashPhase = 'covered' | 'revealing';

type StartupSplashProps = {
  announce?: boolean;
  phase?: StartupSplashPhase;
  onExitComplete?: () => void;
};

export default function StartupSplash({
  announce = false,
  phase = 'covered',
  onExitComplete,
}: StartupSplashProps) {
  const { t } = useTranslation();

  const handleTransitionEnd = (event: TransitionEvent<HTMLDivElement>) => {
    if (
      event.target === event.currentTarget
      && event.propertyName === 'opacity'
    ) {
      onExitComplete?.();
    }
  };

  return (
    <div
      data-testid="startup-splash"
      data-phase={phase}
      className={`${styles.splash} ${phase === 'revealing' ? styles.revealing : ''}`}
      style={{ transitionDuration: `${TRANSITION_MS}ms`, zIndex: ZIndex.changelogOverlay + 1 }}
      role={announce ? 'status' : undefined}
      aria-live={announce ? 'polite' : undefined}
      aria-atomic={announce ? 'true' : undefined}
      aria-busy={announce ? 'true' : undefined}
      aria-hidden={announce ? undefined : 'true'}
      onTransitionEnd={handleTransitionEnd}
    >
      <div aria-hidden="true">
        <ArcSpinner className={styles.spinner} />
      </div>
      {announce && (
        <span data-testid="startup-splash-status" className={styles.visuallyHidden}>
          {t('common.loading')}
        </span>
      )}
    </div>
  );
}
