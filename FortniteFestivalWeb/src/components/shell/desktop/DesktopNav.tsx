import { useTranslation } from 'react-i18next';
import HamburgerButton from '../HamburgerButton';
import HeaderProfileButton, { type HeaderProfileType } from './HeaderProfileButton';
import { appStyles } from '../../../appStyles';
import SearchPill from '../../search/SearchPill';

export interface DesktopNavProps {
  hasPlayer: boolean;
  profileType?: HeaderProfileType;
  onOpenSidebar: () => void;
  onProfileClick: () => void;
  onOpenSearch?: () => void;
  isWideDesktop?: boolean;
}

export default function DesktopNav({ hasPlayer, profileType, onOpenSidebar, onProfileClick, onOpenSearch = () => {}, isWideDesktop }: DesktopNavProps) {
  const { t } = useTranslation();
  const searchBar = <SearchPill label={t('common.searchAction')} onClick={onOpenSearch} />;

  return (
    <nav className="sa-top" style={isWideDesktop ? { ...appStyles.nav, ...appStyles.navWide } : appStyles.nav}>
      {isWideDesktop ? (
        <>
          <div style={appStyles.sidebarSpacer} />
          <div style={appStyles.navWideInner}>
            <div style={appStyles.spacer} />
            {searchBar}
          </div>
          <div style={appStyles.rightSpacer} />
        </>
      ) : (
        <>
          <HamburgerButton onClick={onOpenSidebar} />
          <div style={appStyles.spacer} />
          {searchBar}
          <HeaderProfileButton hasPlayer={hasPlayer} profileType={profileType} onClick={onProfileClick} />
        </>
      )}
    </nav>
  );
}
