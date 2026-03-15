import { useEffect, useLayoutEffect, useState, useRef, useCallback } from 'react';
import { IoClose } from 'react-icons/io5';
import { APP_VERSION } from '../hooks/useVersions';
import { changelog, type ChangelogEntry } from '../changelog';
import { useScrollMask } from '../hooks/useScrollMask';
import css from './ChangelogModal.module.css';

const TRANSITION_MS = 300;

export default function ChangelogModal({ onDismiss }: { onDismiss: () => void }) {
  const [animIn, setAnimIn] = useState(false);
  const scrollRef = useRef<HTMLDivElement>(null);

  useLayoutEffect(() => {
    const id = requestAnimationFrame(() => setAnimIn(true));
    return () => cancelAnimationFrame(id);
  }, []);

  useEffect(() => {
    const handleKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onDismiss(); };
    document.addEventListener('keydown', handleKey);
    return () => document.removeEventListener('keydown', handleKey);
  }, [onDismiss]);

  const updateMask = useScrollMask(scrollRef, [animIn]);
  const handleScroll = useCallback(() => updateMask(), [updateMask]);

  return (
    <div
      className={css.overlay}
      style={{ opacity: animIn ? 1 : 0, transition: `opacity ${TRANSITION_MS}ms ease` }}
      onClick={onDismiss}
    >
      <div
        className={css.card}
        style={{
          opacity: animIn ? 1 : 0,
          transform: animIn ? 'scale(1) translateY(0)' : 'scale(0.95) translateY(10px)',
          transition: `opacity ${TRANSITION_MS}ms ease, transform ${TRANSITION_MS}ms ease`,
        }}
        onClick={e => e.stopPropagation()}
      >
        <div className={css.header}>
          <h2 className={css.title}>Changelog <span className={css.dot}>·</span> {APP_VERSION}</h2>
          <button className={css.closeBtn} onClick={onDismiss} aria-label="Close">
            <IoClose size={18} />
          </button>
        </div>

        <div ref={scrollRef} onScroll={handleScroll} className={css.content}>
          {changelog.map((entry: ChangelogEntry, ei) => (
            <div key={ei} className={css.entry}>
              {entry.sections.map((section, si) => (
                <div key={si} style={si > 0 ? { marginTop: 24 } : undefined}>
                  <div className={css.sectionTitle}>{section.title}</div>
                  <ul className={css.changeList}>
                    {section.items.map((item, i) => (
                      <li key={i} className={css.changeItem}>{item}</li>
                    ))}
                  </ul>
                </div>
              ))}
            </div>
          ))}
        </div>

        <div className={css.footer}>
          <button className={css.dismissBtn} onClick={onDismiss}>
            Dismiss
          </button>
        </div>
      </div>
    </div>
  );
}
