import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';

const webRoot = path.resolve(import.meta.dirname, '../../..');

describe('Suggestions pagination architecture', () => {
  it('uses the internal IntersectionObserver sentinel without the legacy package', () => {
    const packageJson = JSON.parse(readFileSync(path.join(webRoot, 'package.json'), 'utf8')) as {
      dependencies?: Record<string, string>;
    };
    const pageSource = readFileSync(
      path.join(webRoot, 'src/pages/suggestions/SuggestionsPage.tsx'),
      'utf8',
    );
    const sentinelSource = readFileSync(
      path.join(webRoot, 'src/pages/suggestions/components/SuggestionsLoadSentinel.tsx'),
      'utf8',
    );

    expect(packageJson.dependencies).not.toHaveProperty('react-infinite-scroll-component');
    expect(pageSource).toContain('SuggestionsLoadSentinel');
    expect(pageSource).not.toContain('InfiniteScroll');
    expect(sentinelSource).toContain('new IntersectionObserver');
    expect(sentinelSource).toContain('rootMargin: `0px 0px ${prefetchPx}px 0px`');
  });

  it('virtualizes measured category cards and bounds the cached mix', () => {
    const hookSource = readFileSync(
      path.join(webRoot, 'src/hooks/data/useSuggestions.ts'),
      'utf8',
    );
    const virtualListSource = readFileSync(
      path.join(webRoot, 'src/pages/suggestions/components/VirtualizedSuggestionsList.tsx'),
      'utf8',
    );

    expect(hookSource).toContain('SUGGESTIONS_CATEGORY_LIMIT = 1_000');
    expect(hookSource).toContain('startNewMix');
    expect(virtualListSource).toContain('useVirtualizer');
    expect(virtualListSource).toContain('virtualizer.measureElement');
    expect(virtualListSource).toContain('dynamicChildren: true');
    expect(virtualListSource).toContain('focusedRowIndex');
  });
});
