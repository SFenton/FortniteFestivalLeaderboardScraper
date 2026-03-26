import { useCallback, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { useScrollMask } from '../../hooks/ui/useScrollMask';
import { ModalSection } from './components/ModalSection';
import ModalShell from './components/ModalShell';
import { modalStyles } from './modalStyles';

type ModalProps = {
  visible: boolean;
  title: string;
  onClose: () => void;
  onApply: () => void;
  onReset?: () => void;
  resetLabel?: string;
  resetHint?: string;
  applyLabel?: string;
  applyDisabled?: boolean;
  children: React.ReactNode;
};

/**
 * Adaptive modal: bottom sheet on mobile (≤768px), side flyout on desktop.
 * Uses a draft pattern — the parent controls open/close & apply/cancel.
 */
export default function Modal({ visible, title, onClose, onApply, onReset, resetLabel, resetHint, applyLabel, applyDisabled, children }: ModalProps) {
  const { t } = useTranslation();
  const scrollRef = useRef<HTMLDivElement>(null);
  const updateScrollMask = useScrollMask(scrollRef, [visible, children]);
  const handleContentScroll = useCallback(() => { updateScrollMask(); }, [updateScrollMask]);

  return (
    <ModalShell visible={visible} title={title} onClose={onClose}>
      {/* Content */}
      <div ref={scrollRef} onScroll={handleContentScroll} style={modalStyles.contentScroll}>
        {children}
        {onReset && (
          <ModalSection title={resetLabel ?? t('common.reset')} hint={resetHint}>
            <button style={modalStyles.resetBtn} onClick={onReset}>{resetLabel ?? t('common.reset')}</button>
          </ModalSection>
        )}
      </div>

      {/* Footer */}
      <div style={modalStyles.footerWrap}>
        <button
          style={applyDisabled ? { ...modalStyles.applyBtn, ...modalStyles.applyBtnDisabled } : modalStyles.applyBtn}
          onClick={onApply}
          disabled={applyDisabled}
        >
          {applyLabel ?? t('common.apply')}
        </button>
      </div>
    </ModalShell>
  );
}
