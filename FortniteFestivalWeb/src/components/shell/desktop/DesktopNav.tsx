import HamburgerButton from '../HamburgerButton';
import HeaderActions, { type HeaderActionProfileType } from '../HeaderActions';
import { appStyles } from '../../../appStyles';

export type HeaderProfileType = HeaderActionProfileType;

export interface DesktopNavProps {
  hasPlayer: boolean;
  profileType?: HeaderProfileType;
  profileLabel?: string;
  onOpenSidebar: () => void;
  onProfileClick: () => void;
  onOpenSearch?: () => void;
  onOpenNotifications?: () => void;
  notificationCount?: number;
  isWideDesktop?: boolean;
}

export default function DesktopNav({
  hasPlayer,
  profileType,
  profileLabel,
  onOpenSidebar,
  onProfileClick,
  onOpenSearch,
  onOpenNotifications,
  notificationCount,
  isWideDesktop,
}: DesktopNavProps) {
  const resolvedProfileType = profileType ?? (hasPlayer ? 'player' : 'none');
  const actions = <HeaderActions
    testIdPrefix="desktop-header"
    profileType={resolvedProfileType}
    profileLabel={profileLabel}
    onProfileAction={onProfileClick}
    onOpenSearch={onOpenSearch}
    onOpenNotifications={onOpenNotifications}
    notificationCount={notificationCount}
  />;

  return (
    <nav className="sa-top" style={isWideDesktop ? { ...appStyles.nav, ...appStyles.navWide } : appStyles.nav}>
      {isWideDesktop ? (
        <>
          <div style={appStyles.sidebarSpacer} />
          <div style={appStyles.navWideInner}>
            <div style={appStyles.spacer} />
            {actions}
          </div>
          <div style={appStyles.rightSpacer} />
        </>
      ) : (
        <>
          <HamburgerButton onClick={onOpenSidebar} />
          <div style={appStyles.spacer} />
          {actions}
        </>
      )}
    </nav>
  );
}
