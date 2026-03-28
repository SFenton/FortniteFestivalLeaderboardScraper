import { useCallback } from 'react';
import { useNavigate } from 'react-router-dom';
import HamburgerButton from '../HamburgerButton';
import PlayerSearchBar from '../../player/PlayerSearchBar';
import HeaderProfileButton from './HeaderProfileButton';
import { appStyles } from '../../../appStyles';
import { headerSearchStyles } from './HeaderSearch';

export interface DesktopNavProps {
  hasPlayer: boolean;
  onOpenSidebar: () => void;
  onProfileClick: () => void;
  isWideDesktop?: boolean;
}

export default function DesktopNav({ hasPlayer, onOpenSidebar, onProfileClick, isWideDesktop }: DesktopNavProps) {
  const navigate = useNavigate();
  /* v8 ignore start — navigation callback */
  const handleSelect = useCallback((r: { accountId: string }) => {
    navigate(`/player/${r.accountId}`);
  }, [navigate]);
  /* v8 ignore stop */

  const searchBar = (
    <PlayerSearchBar
      onSelect={handleSelect}
      style={headerSearchStyles.container}
      searchStyle={headerSearchStyles.inputWrap}
    />
  );

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
          <HeaderProfileButton hasPlayer={hasPlayer} onClick={onProfileClick} />
        </>
      )}
    </nav>
  );
}
