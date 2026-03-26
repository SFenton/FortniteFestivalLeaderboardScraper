import { useCallback } from 'react';
import { useNavigate } from 'react-router-dom';
import HamburgerButton from './HamburgerButton';
import PlayerSearchBar from '../../player/PlayerSearchBar';
import HeaderProfileButton from './HeaderProfileButton';
import appCss from '../../../App.module.css';
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
    <nav className={`sa-top ${appCss.nav}${isWideDesktop ? ` ${appCss.navWide}` : ''}`}>
      {isWideDesktop ? (
        <>
          <div className={appCss.sidebarSpacer} />
          <div className={appCss.navWideInner}>
            <div className={appCss.spacer} />
            {searchBar}
          </div>
          <div className={appCss.rightSpacer} />
        </>
      ) : (
        <>
          <HamburgerButton onClick={onOpenSidebar} />
          <div className={appCss.spacer} />
          {searchBar}
          <HeaderProfileButton hasPlayer={hasPlayer} onClick={onProfileClick} />
        </>
      )}
    </nav>
  );
}
