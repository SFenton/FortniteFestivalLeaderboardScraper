/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { useEffect, useMemo, useRef, useState, type CSSProperties, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { IoNotifications, IoNotificationsOffOutline, IoPeople, IoPerson, IoPersonAdd, IoSearch } from 'react-icons/io5';
import {
  Align, BoxSizing, Colors, CssValue, Gap, IconSize, Position, Radius, Size, Weight, SpinnerSize,
  flexCenter, flexRow,
} from '@festival/theme';
import { IS_IOS } from '@festival/ui-utils';
import ArcSpinner from '../common/ArcSpinner';

const HEADER_ACTION_TRANSITION_MS = 180;
export const HEADER_NOTIFICATION_SWAP_FADE_MS = 280;

export type HeaderActionProfileType = 'none' | 'player' | 'band';
export type HeaderNotificationVisualState = 'icon' | 'iconOut' | 'spinnerIn' | 'spinner' | 'spinnerOut';

export interface HeaderActionsProps {
  profileType?: HeaderActionProfileType;
  profileLabel?: string;
  onProfileAction?: () => void;
  onOpenSearch?: () => void;
  onOpenNotifications?: () => void;
  hasNotifications?: boolean;
  notificationCount?: number;
  notificationVisualState?: HeaderNotificationVisualState;
  leadingSlot?: ReactNode;
  testIdPrefix: string;
  style?: CSSProperties;
}

export default function HeaderActions({
  profileType = 'none',
  profileLabel,
  onProfileAction,
  onOpenSearch,
  onOpenNotifications,
  notificationCount = 0,
  hasNotifications = notificationCount > 0,
  notificationVisualState = 'icon',
  leadingSlot,
  testIdPrefix,
  style,
}: HeaderActionsProps) {
  const { t } = useTranslation();
  const s = useStyles();
  const notificationLoading = notificationVisualState !== 'icon';
  const notificationVisible = Boolean(onOpenNotifications) || notificationLoading;
  const notificationInteractive = Boolean(onOpenNotifications) && !notificationLoading;
  const notificationVisualRef = useRef({ hasNotifications, notificationCount });

  if (notificationVisible && !notificationLoading) {
    notificationVisualRef.current = { hasNotifications, notificationCount };
  }

  const notificationVisual = notificationVisible
    ? { hasNotifications, notificationCount }
    : notificationVisualRef.current;
  const notificationBadgeLabel = notificationVisual.notificationCount > 9 ? '9+' : String(notificationVisual.notificationCount);
  const ProfileIcon = profileType === 'band' ? IoPeople : profileType === 'player' ? IoPerson : IoPersonAdd;
  const NotificationIcon = notificationVisual.hasNotifications ? IoNotifications : IoNotificationsOffOutline;
  const notificationState = notificationLoading ? 'loading' : notificationVisual.hasNotifications ? 'populated' : 'empty';
  const iconOpacity = notificationVisualState === 'icon' || notificationVisualState === 'spinnerOut' ? 1 : 0;
  const spinnerOpacity = notificationVisualState === 'spinnerIn' || notificationVisualState === 'spinner' ? 1 : 0;
  const showNotificationBadge = (notificationVisualState === 'icon' || notificationVisualState === 'spinnerOut') && notificationVisual.notificationCount > 0;
  const notificationGlyphStyle = IS_IOS ? { ...s.notificationGlyph, ...s.notificationGlyphIos } : s.notificationGlyph;

  return (
    <div style={{ ...s.actions, ...style }} data-testid={`${testIdPrefix}-actions`}>
      {leadingSlot}
      {onProfileAction && (
        <button
          type="button"
          style={s.profileButton}
          onClick={onProfileAction}
          aria-label={profileLabel ?? t('aria.profile')}
          data-testid={`${testIdPrefix}-profile`}
          data-profile-type={profileType}
        >
          <ProfileIcon size={IconSize.md} />
        </button>
      )}
      <button
        type="button"
        style={s.iconButton}
        onClick={onOpenSearch}
        aria-label={t('common.searchAction')}
        data-testid={`${testIdPrefix}-search`}
      >
        <IoSearch size={IconSize.md} />
      </button>
      <HeaderActionPresence visible={notificationVisible} testId={`${testIdPrefix}-notifications-presence`} styles={s}>
        <button
          type="button"
          style={{ ...s.iconButton, ...(notificationInteractive ? undefined : s.iconButtonInert) }}
          onClick={notificationInteractive ? onOpenNotifications : undefined}
          aria-label={t('common.notifications')}
          aria-busy={notificationLoading ? 'true' : undefined}
          aria-disabled={notificationInteractive ? undefined : 'true'}
          data-testid={`${testIdPrefix}-notifications`}
          data-notification-visual-state={notificationVisualState}
          data-notification-state={notificationState}
          tabIndex={notificationInteractive ? 0 : -1}
        >
          <span style={{ ...s.notificationIconLayer, opacity: iconOpacity }} data-testid={`${testIdPrefix}-notifications-icon-layer`}>
            <span style={notificationGlyphStyle} data-testid={`${testIdPrefix}-notifications-glyph`}>
              <NotificationIcon size={IconSize.md} />
            </span>
            {showNotificationBadge && <span style={s.notificationBadge}>{notificationBadgeLabel}</span>}
          </span>
          {notificationLoading && (
            <span style={{ ...s.notificationSpinnerLayer, opacity: spinnerOpacity }} data-testid={`${testIdPrefix}-notifications-spinner`} aria-hidden="true">
              <ArcSpinner size={SpinnerSize.SM} />
            </span>
          )}
        </button>
      </HeaderActionPresence>
    </div>
  );
}

function HeaderActionPresence({ visible, testId, styles, children }: { visible: boolean; testId: string; styles: ReturnType<typeof useStyles>; children: ReactNode }) {
  const [shouldRender, setShouldRender] = useState(visible);

  useEffect(() => {
    if (visible) {
      setShouldRender(true);
      return;
    }

    const timer = window.setTimeout(() => setShouldRender(false), HEADER_ACTION_TRANSITION_MS);
    return () => window.clearTimeout(timer);
  }, [visible]);

  if (!shouldRender) return null;

  return (
    <span
      style={visible ? styles.actionPresenceVisible : styles.actionPresenceHidden}
      data-testid={testId}
      data-visible={visible ? 'true' : 'false'}
      aria-hidden={visible ? undefined : 'true'}
    >
      <span style={styles.actionPresenceInner}>{children}</span>
    </span>
  );
}

function useStyles() {
  return useMemo(() => ({
    actions: {
      ...flexRow,
      alignItems: Align.center,
      gap: Gap.md,
      marginLeft: CssValue.auto,
      flexShrink: 0,
    } as CSSProperties,
    iconButton: {
      ...flexCenter,
      position: Position.relative,
      width: Size.iconLg,
      height: Size.iconLg,
      background: 'none',
      border: 'none',
      cursor: 'pointer',
      borderRadius: Radius.xs,
      color: Colors.textPrimary,
      padding: 0,
      flexShrink: 0,
      marginTop: -Gap.xs,
      marginBottom: -Gap.xs,
    } as CSSProperties,
    iconButtonInert: {
      cursor: 'default',
    } as CSSProperties,
    notificationBadge: {
      position: Position.absolute,
      top: 2,
      right: 1,
      minWidth: 16,
      height: 16,
      padding: '0 4px',
      borderRadius: CssValue.circle,
      background: Colors.statusRed,
      color: Colors.textPrimary,
      fontSize: 10,
      fontWeight: Weight.bold,
      lineHeight: '16px',
      textAlign: 'center',
      boxSizing: BoxSizing.borderBox,
      pointerEvents: 'none',
    } as CSSProperties,
    notificationIconLayer: {
      ...flexCenter,
      position: Position.absolute,
      inset: 0,
      transition: `opacity ${HEADER_NOTIFICATION_SWAP_FADE_MS}ms cubic-bezier(0.22, 1, 0.36, 1)`,
      willChange: 'opacity',
      pointerEvents: 'none',
    } as CSSProperties,
    notificationSpinnerLayer: {
      ...flexCenter,
      position: Position.absolute,
      inset: 0,
      transition: `opacity ${HEADER_NOTIFICATION_SWAP_FADE_MS}ms cubic-bezier(0.22, 1, 0.36, 1)`,
      willChange: 'opacity',
      pointerEvents: 'none',
    } as CSSProperties,
    notificationGlyph: {
      ...flexCenter,
      width: IconSize.md,
      height: IconSize.md,
      lineHeight: 0,
    } as CSSProperties,
    notificationGlyphIos: {
      transform: 'translateY(4px)',
    } as CSSProperties,
    actionPresenceVisible: {
      width: Size.iconLg,
      minWidth: 0,
      opacity: 1,
      overflow: 'hidden',
      pointerEvents: 'auto',
      transition: `width ${HEADER_ACTION_TRANSITION_MS}ms ease, opacity ${HEADER_ACTION_TRANSITION_MS}ms ease, transform ${HEADER_ACTION_TRANSITION_MS}ms ease`,
      transform: 'translateX(0)',
      flexShrink: 0,
    } as CSSProperties,
    actionPresenceHidden: {
      width: 0,
      minWidth: 0,
      opacity: 0,
      overflow: 'hidden',
      pointerEvents: 'none',
      transition: `width ${HEADER_ACTION_TRANSITION_MS}ms ease, opacity ${HEADER_ACTION_TRANSITION_MS}ms ease, transform ${HEADER_ACTION_TRANSITION_MS}ms ease`,
      transform: 'translateX(6px)',
      flexShrink: 0,
    } as CSSProperties,
    actionPresenceInner: {
      display: 'inline-flex',
      width: Size.iconLg,
      minWidth: Size.iconLg,
    } as CSSProperties,
    profileButton: {
      ...flexCenter,
      width: Size.iconLg,
      height: Size.iconLg,
      background: 'none',
      border: 'none',
      cursor: 'pointer',
      borderRadius: Radius.xs,
      color: Colors.textPrimary,
      padding: 0,
      flexShrink: 0,
      marginTop: -Gap.xs,
      marginBottom: -Gap.xs,
    } as CSSProperties,
  }), []);
}
