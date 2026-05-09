/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { useMemo, type CSSProperties, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { IoNotificationsOutline, IoPeople, IoPerson, IoPersonAdd, IoSearch } from 'react-icons/io5';
import {
  Align, BoxSizing, Colors, CssValue, Gap, IconSize, Position, Radius, Size, Weight,
  flexCenter, flexRow,
} from '@festival/theme';

export type HeaderActionProfileType = 'none' | 'player' | 'band';

export interface HeaderActionsProps {
  profileType?: HeaderActionProfileType;
  profileLabel?: string;
  onProfileAction?: () => void;
  onOpenSearch?: () => void;
  onOpenNotifications?: () => void;
  notificationCount?: number;
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
  leadingSlot,
  testIdPrefix,
  style,
}: HeaderActionsProps) {
  const { t } = useTranslation();
  const s = useStyles();
  const notificationBadgeLabel = notificationCount > 9 ? '9+' : String(notificationCount);
  const ProfileIcon = profileType === 'band' ? IoPeople : profileType === 'player' ? IoPerson : IoPersonAdd;

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
      {onOpenNotifications && (
        <button
          type="button"
          style={s.iconButton}
          onClick={onOpenNotifications}
          aria-label={t('common.notifications')}
          data-testid={`${testIdPrefix}-notifications`}
        >
          <IoNotificationsOutline size={IconSize.md} />
          {notificationCount > 0 && <span style={s.notificationBadge}>{notificationBadgeLabel}</span>}
        </button>
      )}
    </div>
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
