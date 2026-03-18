import { useEffect, useLayoutEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import css from './ConfirmAlert.module.css';

const TRANSITION_MS = 250;

export default function ConfirmAlert({
  title,
  message,
  onNo,
  onYes,
}: {
  title: string;
  message: string;
  onNo: () => void;
  onYes: () => void;
}) {
  const { t } = useTranslation();
  const [animIn, setAnimIn] = useState(false);

  /* v8 ignore start — animation setup */
  useLayoutEffect(() => {
    const id = requestAnimationFrame(() => setAnimIn(true));
    return () => cancelAnimationFrame(id);
  }, []);
  /* v8 ignore stop */

  useEffect(() => {
    const handleKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onNo(); };
    document.addEventListener('keydown', handleKey);
    return () => document.removeEventListener('keydown', handleKey);
  }, [onNo]);

  return (
    /* v8 ignore start — animation ternaries */
    <div
      className={css.overlay}
      style={{ opacity: animIn ? 1 : 0, transition: `opacity ${TRANSITION_MS}ms ease` }}
      onClick={onNo}
    >
      <div
        className={css.card}
        style={{
          opacity: animIn ? 1 : 0,
          transform: animIn ? 'scale(1)' : 'scale(0.95)',
          transition: `opacity ${TRANSITION_MS}ms ease, transform ${TRANSITION_MS}ms ease`,
        }}
        onClick={e => e.stopPropagation()}
      >
        <div className={css.title} style={{ opacity: 0, animation: animIn ? 'fadeInUp 300ms ease-out 100ms forwards' : 'none' }}>{title}</div>
        <div className={css.message} style={{ opacity: 0, animation: animIn ? 'fadeInUp 300ms ease-out 200ms forwards' : 'none' }}>{message}</div>
        <div className={css.buttons} style={{ opacity: 0, animation: animIn ? 'fadeInUp 300ms ease-out 300ms forwards' : 'none' }}>
          <button className={css.btnNo} onClick={onNo}>{t('common.no')}</button>
          <button className={css.btnYes} onClick={onYes}>{t('common.yes')}</button>
        </div>
      </div>
    </div>
    /* v8 ignore stop */
  );
}
