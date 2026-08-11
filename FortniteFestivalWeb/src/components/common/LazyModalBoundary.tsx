import { Component, Suspense, useCallback, useEffect, useRef, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Colors, Font, Gap, flexCenter, flexColumn, padding } from '@festival/theme';
import ArcSpinner, { SpinnerSize } from './ArcSpinner';
import PressableButton from './PressableButton';
import ModalShell, { ModalReturnFocusTargetContext } from '../modals/components/ModalShell';

type LazyModalBoundaryProps = {
  visible: boolean;
  title: string;
  boundaryName: string;
  onClose: () => void;
  load?: () => Promise<void>;
  isLoaded?: () => boolean;
  mobileEnterOffset?: number | string;
  initialFocus?: 'first' | 'panel';
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
  mobileEnterOffset,
  initialFocus,
  children,
}: LazyModalBoundaryProps) {
  const [requested, setRequested] = useState(visible);
  const [loadState, setLoadState] = useState<'idle' | 'loading' | 'ready' | 'failed'>(() => (
    !load || isLoaded?.() ? 'ready' : 'idle'
  ));
  const returnFocusTargetRef = useRef<HTMLElement | null>(null);
  const captureReturnFocusTarget = useCallback((target: HTMLElement | null) => {
    if (target?.isConnected) returnFocusTargetRef.current = target;
  }, []);

  useEffect(() => {
    if (visible) {
      setRequested(true);
    } else {
      returnFocusTargetRef.current = null;
    }
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

  let content: ReactNode;
  if (loadState === 'failed') {
    content = (
      <LazyModalFailure
        visible={visible}
        title={title}
        boundaryName={boundaryName}
        onClose={onClose}
        mobileEnterOffset={mobileEnterOffset}
        initialFocus={initialFocus}
        onReturnFocusTargetCapture={captureReturnFocusTarget}
      />
    );
  } else if (load && loadState !== 'ready' && !isLoaded?.()) {
    content = (
      <LazyModalLoading
        visible={visible}
        title={title}
        boundaryName={boundaryName}
        onClose={onClose}
        mobileEnterOffset={mobileEnterOffset}
        initialFocus={initialFocus}
        onReturnFocusTargetCapture={captureReturnFocusTarget}
      />
    );
  } else {
    content = (
      <LazyImportErrorBoundary
        fallback={(
          <LazyModalFailure
            visible={visible}
            title={title}
            boundaryName={boundaryName}
            onClose={onClose}
            mobileEnterOffset={mobileEnterOffset}
            initialFocus={initialFocus}
            onReturnFocusTargetCapture={captureReturnFocusTarget}
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
              mobileEnterOffset={mobileEnterOffset}
              initialFocus={initialFocus}
              onReturnFocusTargetCapture={captureReturnFocusTarget}
            />
          )}
        >
          {children}
        </Suspense>
      </LazyImportErrorBoundary>
    );
  }

  return (
    <ModalReturnFocusTargetContext.Provider value={returnFocusTargetRef.current}>
      {content}
    </ModalReturnFocusTargetContext.Provider>
  );
}

function LazyModalLoading({
  visible,
  title,
  boundaryName,
  onClose,
  mobileEnterOffset,
  initialFocus,
  onReturnFocusTargetCapture,
}: Omit<LazyModalBoundaryProps, 'children'> & {
  onReturnFocusTargetCapture: (target: HTMLElement | null) => void;
}) {
  const { t } = useTranslation();
  return (
    <ModalShell
      visible={visible}
      title={title}
      onClose={onClose}
      mobileEnterOffset={mobileEnterOffset}
      initialFocus={initialFocus}
      onReturnFocusTargetCapture={onReturnFocusTargetCapture}
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
  mobileEnterOffset,
  initialFocus,
  onReturnFocusTargetCapture,
}: Omit<LazyModalBoundaryProps, 'children'> & {
  onReturnFocusTargetCapture: (target: HTMLElement | null) => void;
}) {
  const { t } = useTranslation();
  return (
    <ModalShell
      visible={visible}
      title={title}
      onClose={onClose}
      mobileEnterOffset={mobileEnterOffset}
      initialFocus={initialFocus}
      onReturnFocusTargetCapture={onReturnFocusTargetCapture}
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
