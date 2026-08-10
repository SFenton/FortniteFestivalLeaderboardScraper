import { Component, Suspense, useEffect, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Colors, Font, Gap, flexCenter, flexColumn, padding } from '@festival/theme';
import ArcSpinner, { SpinnerSize } from './ArcSpinner';
import PressableButton from './PressableButton';
import ModalShell from '../modals/components/ModalShell';

type LazyModalBoundaryProps = {
  visible: boolean;
  title: string;
  boundaryName: string;
  onClose: () => void;
  load?: () => Promise<void>;
  isLoaded?: () => boolean;
  children: ReactNode;
};

type LazyImportErrorBoundaryProps = {
  fallback: ReactNode;
  children: ReactNode;
};

type LazyImportErrorBoundaryState = {
  failed: boolean;
};

class LazyImportErrorBoundary extends Component<LazyImportErrorBoundaryProps, LazyImportErrorBoundaryState> {
  state: LazyImportErrorBoundaryState = { failed: false };

  static getDerivedStateFromError(): LazyImportErrorBoundaryState {
    return { failed: true };
  }

  render() {
    return this.state.failed ? this.props.fallback : this.props.children;
  }
}

export default function LazyModalBoundary({
  visible,
  title,
  boundaryName,
  onClose,
  load,
  isLoaded,
  children,
}: LazyModalBoundaryProps) {
  const [requested, setRequested] = useState(visible);
  const [loadState, setLoadState] = useState<'idle' | 'loading' | 'ready' | 'failed'>(() => (
    !load || isLoaded?.() ? 'ready' : 'idle'
  ));

  useEffect(() => {
    if (visible) setRequested(true);
  }, [visible]);

  useEffect(() => {
    if (!visible || !load) return;
    if (isLoaded?.()) {
      setLoadState('ready');
      return;
    }

    let active = true;
    setLoadState('loading');
    void load().then(
      () => { if (active) setLoadState('ready'); },
      () => { if (active) setLoadState('failed'); },
    );
    return () => { active = false; };
  }, [isLoaded, load, visible]);

  if (!visible && !requested) return null;

  if (loadState === 'failed') {
    return (
      <LazyModalFailure
        visible={visible}
        title={title}
        boundaryName={boundaryName}
        onClose={onClose}
      />
    );
  }

  if (load && loadState !== 'ready' && !isLoaded?.()) {
    return (
      <LazyModalLoading
        visible={visible}
        title={title}
        boundaryName={boundaryName}
        onClose={onClose}
      />
    );
  }

  return (
    <LazyImportErrorBoundary
      fallback={(
        <LazyModalFailure
          visible={visible}
          title={title}
          boundaryName={boundaryName}
          onClose={onClose}
        />
      )}
    >
      <Suspense
        fallback={(
          <LazyModalLoading
            visible={visible}
            title={title}
            boundaryName={boundaryName}
            onClose={onClose}
          />
        )}
      >
        {children}
      </Suspense>
    </LazyImportErrorBoundary>
  );
}

function LazyModalLoading({
  visible,
  title,
  boundaryName,
  onClose,
}: Omit<LazyModalBoundaryProps, 'children'>) {
  const { t } = useTranslation();
  return (
    <ModalShell
      visible={visible}
      title={title}
      onClose={onClose}
      panelTestId={`${boundaryName}-lazy-loading`}
    >
      <div role="status" aria-live="polite" style={styles.content}>
        <ArcSpinner size={SpinnerSize.MD} />
        <span>{t('common.loading')}</span>
      </div>
    </ModalShell>
  );
}

function LazyModalFailure({
  visible,
  title,
  boundaryName,
  onClose,
}: Omit<LazyModalBoundaryProps, 'children'>) {
  const { t } = useTranslation();
  return (
    <ModalShell
      visible={visible}
      title={title}
      onClose={onClose}
      panelTestId={`${boundaryName}-lazy-error`}
    >
      <div role="alert" style={styles.content}>
        <p style={styles.message}>{t('error.controlLoadFailed')}</p>
        <PressableButton style={styles.reloadButton} onPress={() => window.location.reload()}>
          {t('common.reload')}
        </PressableButton>
      </div>
    </ModalShell>
  );
}

const styles = {
  content: {
    ...flexColumn,
    ...flexCenter,
    gap: Gap.xl,
    minHeight: 180,
    padding: Gap.section,
    color: Colors.textSecondary,
    textAlign: 'center' as const,
  },
  message: {
    margin: 0,
    fontSize: Font.md,
  },
  reloadButton: {
    border: 0,
    borderRadius: 999,
    padding: padding(Gap.md, Gap.xl),
    background: Colors.accentPurple,
    color: Colors.textPrimary,
    fontSize: Font.md,
    cursor: 'pointer',
  },
};
