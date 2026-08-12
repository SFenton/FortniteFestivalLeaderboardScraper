import { useState } from 'react';
import { TabKey } from '@festival/core/runtime';
import BottomNav from './BottomNav';

export function PlayerNavigation() {
  const [activeTab, setActiveTab] = useState<TabKey>(TabKey.Settings);
  return (
    <>
      <BottomNav
        player={{ accountId: 'component-player', displayName: 'Component Player' }}
        selectedProfile={{
          type: 'player',
          accountId: 'component-player',
          displayName: 'Component Player',
        }}
        activeTab={activeTab}
        onTabClick={setActiveTab}
      />
      <input data-testid="active-tab" readOnly value={activeTab} hidden />
    </>
  );
}
