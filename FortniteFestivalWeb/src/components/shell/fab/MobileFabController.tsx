import { FabMode } from '@festival/core';
import { useLocation, useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { IoPerson, IoPersonAdd, IoSearch, IoSwapVerticalSharp, IoFunnel, IoFlash } from 'react-icons/io5';
import { useFabSearch, usePlayerPageSelect } from '../../../contexts/FabSearchContext';
import { useIsMobile } from '../../../hooks/ui/useIsMobile';
import FloatingActionButton from './FloatingActionButton';
import type { TrackedPlayer } from '../../../hooks/data/useTrackedPlayer';
import { Size } from '@festival/theme';

export interface MobileFabControllerProps {
  player: TrackedPlayer | null;
  onFindPlayer: () => void;
  onOpenPlayerModal: () => void;
}

export default function MobileFabController({ player, onFindPlayer, onOpenPlayerModal }: MobileFabControllerProps) {
  const location = useLocation();
  const navigate = useNavigate();
  const { t } = useTranslation();
  const isNarrow = useIsMobile();
  const fabSearch = useFabSearch();
  const { playerPageSelect } = usePlayerPageSelect();
  const path = location.pathname;

  /* v8 ignore start — route-specific FAB callbacks tested by MobileFabController.test */
  const playerActions = [
    { label: t('common.findPlayer'), icon: <IoSearch size={Size.iconDefault} />, onPress: onFindPlayer },
    player
      ? { label: player.displayName, icon: <IoPerson size={Size.iconDefault} />, onPress: () => navigate('/statistics') }
      : { label: t('common.selectPlayerProfile'), icon: <IoPerson size={Size.iconDefault} />, onPress: onOpenPlayerModal },
  ];

  if (path === '/songs') {
    return (
      <FloatingActionButton
        mode={FabMode.Songs}
        defaultOpen
        placeholder={t('songs.searchPlaceholder')}
        actionGroups={[
          [
            { label: t('common.sortSongs'), icon: <IoSwapVerticalSharp size={Size.iconDefault} />, onPress: () => fabSearch.openSort() },
            ...(player ? [{ label: t('common.filterSongs'), icon: <IoFunnel size={Size.iconDefault} />, onPress: () => fabSearch.openFilter() }] : []),
          ],
          playerActions,
        ]}
        onPress={() => {}}
      />
    );
  }

  if (path === '/suggestions') {
    return (
      <FloatingActionButton
        mode={FabMode.Players}
        actionGroups={[
          [{ label: t('common.filterSuggestions'), icon: <IoFunnel size={Size.iconDefault} />, onPress: () => fabSearch.openSuggestionsFilter() }],
          playerActions,
        ]}
        onPress={() => {}}
      />
    );
  }

  if (path.endsWith('/history')) {
    return (
      <FloatingActionButton
        mode={FabMode.Players}
        actionGroups={[
          [{ label: t('common.sortPlayerScores'), icon: <IoSwapVerticalSharp size={Size.iconDefault} />, onPress: () => fabSearch.openPlayerHistorySort() }],
          playerActions,
        ]}
        onPress={() => {}}
      />
    );
  }

  if (/^\/songs\/[^/]+$/.test(path)) {
    return (
      <FloatingActionButton
        mode={FabMode.Players}
        actionGroups={[
          ...(isNarrow ? [[{ label: t('common.viewPaths'), icon: <IoFlash size={Size.iconDefault} />, onPress: () => fabSearch.openPaths() }]] : []),
          playerActions,
        ]}
        onPress={() => {}}
      />
    );
  }

  // Default: generic player FAB for all other pages
  return (
    <FloatingActionButton
      mode={FabMode.Players}
      actionGroups={[
        ...(playerPageSelect ? [[
          { label: t('common.selectAsProfile', { name: playerPageSelect.displayName }), icon: <IoPersonAdd size={Size.iconDefault} />, onPress: playerPageSelect.onSelect },
        ]] : []),
        playerActions,
      ]}
      onPress={() => {}}
    />
  );
  /* v8 ignore stop */
}
