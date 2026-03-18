import { useCallback, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { useScrollMask } from '../../hooks/ui/useScrollMask';
import { ModalSection } from './components/ModalSection';
import ModalShell from './components/ModalShell';
import css from './Modal.module.css';

type Props = {
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
export default function Modal({ visible, title, onClose, onApply, onReset, resetLabel, resetHint, applyLabel, applyDisabled, children }: Props) {
  const { t } = useTranslation();
  const scrollRef = useRef<HTMLDivElement>(null);
  const updateScrollMask = useScrollMask(scrollRef, [visible, children]);
  const handleContentScroll = useCallback(() => { updateScrollMask(); }, [updateScrollMask]);

  return (
    <ModalShell visible={visible} title={title} onClose={onClose}>
      {/* Content */}
      <div ref={scrollRef} onScroll={handleContentScroll} className={css.contentScroll}>
        {children}
        {onReset && (
          <ModalSection title={resetLabel ?? 'Reset'} hint={resetHint}>
            <button className={css.resetBtn} onClick={onReset}>{resetLabel ?? t('common.reset')}</button>
          </ModalSection>
        )}
      </div>

      {/* Footer */}
      <div className={css.footerWrap}>
        <button
          className={`${css.applyBtn} ${applyDisabled ? css.applyBtnDisabled : ''}`}
          onClick={onApply}
          disabled={applyDisabled}
        >
          {applyLabel ?? 'Apply'}
        </button>
      </div>
    </ModalShell>
  );
}
