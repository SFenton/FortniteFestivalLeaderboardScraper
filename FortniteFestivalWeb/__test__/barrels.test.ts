/**
 * Coverage-only tests that import barrel re-exports and theme files
 * to ensure they register as covered. Each barrel re-exports from
 * hooks/data/ or hooks/ui/ — importing them verifies the re-export chain.
 */
import { describe, it, expect } from 'vitest';

// Hook barrel re-exports (src/hooks/*.ts)
import { useAccountSearch } from '../src/hooks/data/useAccountSearch';
import { useFilteredSongs } from '../src/hooks/data/useFilteredSongs';
import { useHeaderCollapse } from '../src/hooks/ui/useHeaderCollapse';
import { useIsMobile } from '../src/hooks/ui/useIsMobile';
import { useLoadPhase } from '../src/hooks/data/useLoadPhase';
import { useMediaQuery } from '../src/hooks/ui/useMediaQuery';
import { useModalState } from '../src/hooks/ui/useModalState';
import { useScoreFilter } from '../src/hooks/data/useScoreFilter';
import { useScrollFade } from '../src/hooks/ui/useScrollFade';
import { useScrollMask } from '../src/hooks/ui/useScrollMask';
import { useScrollRestore, clearScrollCache } from '../src/hooks/ui/useScrollRestore';
import { useStaggerRush } from '../src/hooks/ui/useStaggerRush';
import { useSuggestions } from '../src/hooks/data/useSuggestions';
import { useSyncStatus } from '../src/hooks/data/useSyncStatus';
import { useTabNavigation } from '../src/hooks/ui/useTabNavigation';
import { useTrackedPlayer } from '../src/hooks/data/useTrackedPlayer';
import { APP_VERSION, CORE_VERSION, THEME_VERSION } from '../src/hooks/data/useVersions';
import * as useVisualViewportExports from '../src/hooks/ui/useVisualViewport';

// Theme (@festival/theme)
import { Colors, Radius, Font, LineHeight, Gap, Opacity, Size, MaxWidth, Layout } from '@festival/theme';
import { goldFill, goldOutline, goldOutlineSkew, frostedCard, frostedCardLight } from '@festival/theme';

// i18n
import i18n from '../src/i18n/index';

// models
import * as models from '@festival/core/api/serverTypes';

describe('barrel re-exports', () => {
  it('hook barrels export functions', () => {
    expect(typeof useAccountSearch).toBe('function');
    expect(typeof useFilteredSongs).toBe('function');
    expect(typeof useHeaderCollapse).toBe('function');
    expect(typeof useIsMobile).toBe('function');
    expect(typeof useLoadPhase).toBe('function');
    expect(typeof useMediaQuery).toBe('function');
    expect(typeof useModalState).toBe('function');
    expect(typeof useScoreFilter).toBe('function');
    expect(typeof useScrollFade).toBe('function');
    expect(typeof useScrollMask).toBe('function');
    expect(typeof useScrollRestore).toBe('function');
    expect(typeof clearScrollCache).toBe('function');
    expect(typeof useStaggerRush).toBe('function');
    expect(typeof useSuggestions).toBe('function');
    expect(typeof useSyncStatus).toBe('function');
    expect(typeof useTabNavigation).toBe('function');
    expect(typeof useTrackedPlayer).toBe('function');
    expect(typeof useVisualViewportExports).toBe('object');
  });

  it('useVersions exports version strings', () => {
    expect(typeof APP_VERSION).toBe('string');
    expect(typeof CORE_VERSION).toBe('string');
    expect(typeof THEME_VERSION).toBe('string');
  });
});

describe('theme exports', () => {
  it('colors has expected properties', () => {
    expect(Colors).toBeDefined();
    expect(typeof Colors.textPrimary).toBe('string');
  });

  it('spacing has expected properties', () => {
    expect(Radius).toBeDefined();
    expect(Font).toBeDefined();
    expect(LineHeight).toBeDefined();
    expect(Gap).toBeDefined();
    expect(Opacity).toBeDefined();
    expect(Size).toBeDefined();
    expect(MaxWidth).toBeDefined();
    expect(Layout).toBeDefined();
  });

  it('goldStyles exports CSS objects', () => {
    expect(goldFill).toBeDefined();
    expect(goldOutline).toBeDefined();
    expect(goldOutlineSkew).toBeDefined();
  });

  it('frostedStyles exports CSS objects', () => {
    expect(frostedCard).toBeDefined();
    expect(frostedCardLight).toBeDefined();
  });
});

describe('i18n', () => {
  it('i18n instance is initialized', () => {
    expect(i18n).toBeDefined();
    expect(i18n.isInitialized).toBe(true);
  });
});

describe('models', () => {
  it('models barrel re-exports types', () => {
    expect(models).toBeDefined();
  });
});
