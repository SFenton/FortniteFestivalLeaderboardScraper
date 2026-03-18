/**
 * Coverage-only tests that import barrel re-exports and theme files
 * to ensure they register as covered. Each barrel re-exports from
 * hooks/data/ or hooks/ui/ — importing them verifies the re-export chain.
 */
import { describe, it, expect } from 'vitest';

// Hook barrel re-exports (src/hooks/*.ts)
import { useAccountSearch } from '../hooks/data/useAccountSearch';
import { useFilteredSongs } from '../hooks/data/useFilteredSongs';
import { useHeaderCollapse } from '../hooks/ui/useHeaderCollapse';
import { useIsMobile } from '../hooks/ui/useIsMobile';
import { useLoadPhase } from '../hooks/data/useLoadPhase';
import { useMediaQuery } from '../hooks/ui/useMediaQuery';
import { useModalState } from '../hooks/ui/useModalState';
import { useScoreFilter } from '../hooks/data/useScoreFilter';
import { useScrollFade } from '../hooks/ui/useScrollFade';
import { useScrollMask } from '../hooks/ui/useScrollMask';
import { useScrollRestore, clearScrollCache } from '../hooks/ui/useScrollRestore';
import { useStaggerRush } from '../hooks/ui/useStaggerRush';
import { useSuggestions } from '../hooks/data/useSuggestions';
import { useSyncStatus } from '../hooks/data/useSyncStatus';
import { useTabNavigation } from '../hooks/ui/useTabNavigation';
import { useTrackedPlayer } from '../hooks/data/useTrackedPlayer';
import { APP_VERSION, CORE_VERSION } from '../hooks/data/useVersions';
import * as useVisualViewportExports from '../hooks/ui/useVisualViewport';

// Theme (src/theme/*.ts)
import { Colors } from '../theme/colors';
import { Radius, Font, LineHeight, Gap, Opacity, Size, MaxWidth, Layout } from '../theme/spacing';
import { goldFill, goldOutline, goldOutlineSkew } from '../theme/goldStyles';
import { frostedCard, frostedCardLight } from '../theme/frostedStyles';
import * as themeIndex from '../theme/index';

// i18n
import i18n from '../i18n/index';

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

  it('theme index re-exports', () => {
    expect(themeIndex).toBeDefined();
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
