import { useCallback } from 'react';
import { useNavigate } from 'react-router-dom';
import HamburgerButton from './HamburgerButton';
import PlayerSearchBar from '../../player/PlayerSearchBar';
import HeaderProfileButton from './HeaderProfileButton';
import appCss from '../../../App.module.css';
import headerCss from './HeaderSearch.module.css';

export interface DesktopNavProps {
  hasPlayer: boolean;
  onOpenSidebar: () => void;
  onProfileClick: () => void;
}

export default function DesktopNav({ hasPlayer, onOpenSidebar, onProfileClick }: DesktopNavProps) {
  const navigate = useNavigate();
  const handleSelect = useCallback((r: { accountId: string }) => {
    navigate(`/player/${r.accountId}`);
  }, [navigate]);

  return (
    <nav className={`sa-top ${appCss.nav}`}>
      <HamburgerButton onClick={onOpenSidebar} />
      <div className={appCss.spacer} />
      <PlayerSearchBar
        onSelect={handleSelect}
        className={headerCss.container}
        searchClassName={headerCss.inputWrap}
        inputClassName={headerCss.input}
      />
      <HeaderProfileButton hasPlayer={hasPlayer} onClick={onProfileClick} />
    </nav>
  );
}
