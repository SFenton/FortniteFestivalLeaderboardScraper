import { Fragment, useCallback, useMemo, useRef, type CSSProperties, type ReactNode } from 'react';
import { Colors, Font, Gap, LineHeight, Weight, padding } from '@festival/theme';
import ModalShell from '../../components/modals/components/ModalShell';
import { modalStyles } from '../../components/modals/modalStyles';
import { useScrollMask } from '../../hooks/ui/useScrollMask';
import { SAFE_AREA_BOTTOM_VAR } from '../../utils/safeAreaStyles';
import { privacyPolicy, type PrivacyPolicyItem } from './privacyPolicy';

const URL_PATTERN = /(https:\/\/[^\s]+?)(?=[.,;:]?(?:\s|$))/g;
const PRIVACY_POLICY_DESKTOP_MAX_WIDTH = 720;

export type PrivacyPolicyModalProps = {
  visible: boolean;
  onClose: () => void;
  onCloseComplete?: () => void;
};

export default function PrivacyPolicyModal({ visible, onClose, onCloseComplete }: PrivacyPolicyModalProps) {
  const styles = useStyles();
  const scrollRef = useRef<HTMLDivElement>(null);
  const updateScrollMask = useScrollMask(scrollRef, [visible], { selfScroll: true });
  const handleScroll = useCallback(() => { updateScrollMask(); }, [updateScrollMask]);

  return (
    <ModalShell
      visible={visible}
      title={privacyPolicy.title}
      onClose={onClose}
      onCloseComplete={onCloseComplete}
      desktopStyle={styles.desktopPanel}
      panelTestId="privacy-policy-modal"
    >
      <div
        ref={scrollRef}
        onScroll={handleScroll}
        style={styles.scroll}
        data-testid="privacy-policy-content"
      >
        <article style={styles.article} aria-label={privacyPolicy.title}>
          <p style={styles.effectiveDate}>Effective date: {privacyPolicy.effectiveDate}</p>
          {privacyPolicy.intro.map(text => <p key={text} style={styles.paragraph}>{linkify(text, styles.link)}</p>)}
          {privacyPolicy.sections.map(section => (
            <section key={section.id} aria-labelledby={`privacy-policy-${section.id}`} style={styles.section}>
              <h3 id={`privacy-policy-${section.id}`} style={styles.heading}>{section.heading}</h3>
              {section.paragraphs?.map(text => <p key={text} style={styles.paragraph}>{linkify(text, styles.link)}</p>)}
              {section.items && (
                <ul style={styles.list}>
                  {section.items.map(item => <PolicyItem key={item.text} item={item} styles={styles} />)}
                </ul>
              )}
              {section.closing?.map(text => <p key={text} style={styles.paragraph}>{linkify(text, styles.link)}</p>)}
            </section>
          ))}
        </article>
      </div>
    </ModalShell>
  );
}

function PolicyItem({ item, styles }: { item: PrivacyPolicyItem; styles: ReturnType<typeof useStyles> }) {
  return (
    <li style={styles.listItem}>
      {item.label && <strong style={styles.itemLabel}>{item.label}: </strong>}
      {linkify(item.text, styles.link)}
    </li>
  );
}

function linkify(text: string, linkStyle: CSSProperties): ReactNode {
  const parts = text.split(URL_PATTERN);
  if (parts.length === 1) return text;
  return parts.map((part, index) => (
    index % 2 === 1
      ? <a key={part} href={part} target="_blank" rel="noopener noreferrer" style={linkStyle}>{part}</a>
      : <Fragment key={`text-${index}`}>{part}</Fragment>
  ));
}

function useStyles() {
  return useMemo(() => ({
    desktopPanel: {
      width: `min(${PRIVACY_POLICY_DESKTOP_MAX_WIDTH}px, 90vw)`,
    } as CSSProperties,
    scroll: {
      ...modalStyles.contentScroll,
      paddingBottom: `calc(${Gap.section}px + ${SAFE_AREA_BOTTOM_VAR})`,
    } as CSSProperties,
    article: {
      display: 'flex',
      flexDirection: 'column',
      gap: Gap.xl,
      color: Colors.textSecondary,
      fontSize: Font.md,
      lineHeight: LineHeight.relaxed,
      overflowWrap: 'anywhere',
    } as CSSProperties,
    effectiveDate: {
      margin: Gap.none,
      color: Colors.textMuted,
      fontSize: Font.sm,
      fontWeight: Weight.semibold,
    } as CSSProperties,
    section: {
      display: 'flex',
      flexDirection: 'column',
      gap: Gap.md,
      paddingTop: Gap.md,
    } as CSSProperties,
    heading: {
      margin: Gap.none,
      color: Colors.textPrimary,
      fontSize: Font.lg,
      fontWeight: Weight.bold,
      lineHeight: LineHeight.snug,
    } as CSSProperties,
    paragraph: {
      margin: Gap.none,
    } as CSSProperties,
    list: {
      margin: Gap.none,
      padding: padding(Gap.none, Gap.none, Gap.none, Gap.section),
      display: 'flex',
      flexDirection: 'column',
      gap: Gap.md,
    } as CSSProperties,
    listItem: {
      paddingLeft: Gap.xs,
    } as CSSProperties,
    itemLabel: {
      color: Colors.textPrimary,
      fontWeight: Weight.semibold,
    } as CSSProperties,
    link: {
      color: Colors.textPrimary,
      textDecoration: 'underline',
    } as CSSProperties,
  }), []);
}
