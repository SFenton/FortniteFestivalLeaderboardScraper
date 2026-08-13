import { mkdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';

const CORE_GENERATOR_SUFFIX = '/packages/core/src/suggestions/suggestionGenerator.ts';
const SUGGESTIONS_PAGE_SUFFIX = '/FortniteFestivalWeb/src/pages/suggestions/SuggestionsPage.tsx';
const SECONDARY_CONTROL_BOUNDARIES = [
  {
    label: 'SearchModal',
    suffix: '/FortniteFestivalWeb/src/components/search/SearchModal.tsx',
  },
  {
    label: 'MobileNotificationsModal',
    suffix: '/FortniteFestivalWeb/src/components/notifications/MobileNotificationsModal.tsx',
  },
  {
    label: 'BandInstrumentFilterModal',
    suffix: '/FortniteFestivalWeb/src/pages/band/modals/BandInstrumentFilterModal.tsx',
  },
  {
    label: 'Songs SortModal',
    suffix: '/FortniteFestivalWeb/src/pages/songs/modals/SortModal.tsx',
  },
  {
    label: 'Songs FilterModal',
    suffix: '/FortniteFestivalWeb/src/pages/songs/modals/FilterModal.tsx',
  },
  {
    label: 'ChangelogModal',
    suffix: '/FortniteFestivalWeb/src/components/modals/ChangelogModal.tsx',
  },
  {
    label: 'ConfirmAlert',
    suffix: '/FortniteFestivalWeb/src/components/modals/ConfirmAlert.tsx',
  },
];
const INITIAL_FORBIDDEN_MODULE_SUFFIXES = [
  ...SECONDARY_CONTROL_BOUNDARIES.map(boundary => boundary.suffix),
  '/FortniteFestivalWeb/src/components/notifications/notificationText.ts',
  '/FortniteFestivalWeb/src/components/notifications/notificationMocks.ts',
  '/FortniteFestivalWeb/src/diagnostics/ModalAccessibilityFixture.tsx',
  '/FortniteFestivalWeb/src/diagnostics/scrollFadeTestMode.ts',
  '/FortniteFestivalWeb/src/diagnostics/tapDiagnostics.ts',
  '/FortniteFestivalWeb/src/components/icons/PwaIconCapture.tsx',
  '/FortniteFestivalWeb/src/components/firstRun/FirstRunCarousel.tsx',
  '/FortniteFestivalWeb/src/components/sort/ReorderList.tsx',
  '/FortniteFestivalWeb/src/components/sort/SortableRow.tsx',
  '/FortniteFestivalWeb/src/pages/songinfo/components/path/PathDataTable.tsx',
  '/FortniteFestivalWeb/src/pages/leaderboards/modals/RankByModal.tsx',
  '/FortniteFestivalWeb/src/components/common/Math.tsx',
];
const INITIAL_FORBIDDEN_MODULE_MARKERS = [
  '/node_modules/@dnd-kit/',
  '/node_modules/katex/',
];

export function sharedPackageBoundaryPlugin({ webRoot, graphOutput }) {
  return {
    name: 'fst-shared-package-boundary',
    generateBundle(_options, bundle) {
      const chunks = Object.values(bundle).filter(output => output.type === 'chunk');
      const chunksByFile = new Map(chunks.map(chunk => [chunk.fileName, chunk]));
      const entryFiles = chunks.filter(chunk => chunk.isEntry).map(chunk => chunk.fileName);
      const initialFiles = staticClosure(entryFiles, chunksByFile);
      const suggestionsChunk = chunks.find(chunk =>
        normalizeId(chunk.facadeModuleId).endsWith(SUGGESTIONS_PAGE_SUFFIX),
      );
      const suggestionsFiles = suggestionsChunk
        ? staticClosure([suggestionsChunk.fileName], chunksByFile)
        : new Set();

      const initialModules = modulesForFiles(initialFiles, chunksByFile);
      const suggestionsModules = modulesForFiles(suggestionsFiles, chunksByFile);
      const generatorChunks = chunks.filter(chunk =>
        Object.keys(chunk.modules).some(id => normalizeId(id).endsWith(CORE_GENERATOR_SUFFIX)),
      );
      const secondaryControlFiles = Object.fromEntries(SECONDARY_CONTROL_BOUNDARIES.map((boundary) => {
        const chunk = chunks.find(candidate =>
          normalizeId(candidate.facadeModuleId).endsWith(boundary.suffix),
        );
        return [boundary.label, chunk ? [...staticClosure([chunk.fileName], chunksByFile)] : []];
      }));

      if (initialModules.some(id => normalizeId(id).endsWith(CORE_GENERATOR_SUFFIX))) {
        this.error('SuggestionGenerator must not be reachable from the initial Songs chunk graph.');
      }
      if (!suggestionsChunk) {
        this.error('Unable to locate the lazy SuggestionsPage chunk.');
      }
      if (!suggestionsModules.some(id => normalizeId(id).endsWith(CORE_GENERATOR_SUFFIX))) {
        this.error('SuggestionGenerator must remain reachable from the lazy SuggestionsPage chunk graph.');
      }
      for (const suffix of INITIAL_FORBIDDEN_MODULE_SUFFIXES) {
        if (initialModules.some(id => normalizeId(id).endsWith(suffix))) {
          this.error(`${path.basename(suffix)} must not be reachable from the initial Songs chunk graph.`);
        }
      }
      for (const marker of INITIAL_FORBIDDEN_MODULE_MARKERS) {
        if (initialModules.some(id => normalizeId(id).includes(marker))) {
          this.error(`${marker} must not be reachable from the initial Songs chunk graph.`);
        }
      }
      for (const boundary of SECONDARY_CONTROL_BOUNDARIES) {
        const files = secondaryControlFiles[boundary.label];
        if (files.length === 0) {
          this.error(`Unable to locate the lazy ${boundary.label} chunk.`);
        }
        const modules = modulesForFiles(files, chunksByFile);
        if (!modules.some(id => normalizeId(id).endsWith(boundary.suffix))) {
          this.error(`${boundary.label} must remain reachable from its lazy interaction chunk graph.`);
        }
      }
      const sortModules = modulesForFiles(secondaryControlFiles['Songs SortModal'], chunksByFile);
      if (!sortModules.some(id => normalizeId(id).includes('/node_modules/@dnd-kit/'))) {
        this.error('DnD Kit must remain reachable from the lazy Songs SortModal chunk graph.');
      }
      const notificationModules = modulesForFiles(secondaryControlFiles.MobileNotificationsModal, chunksByFile);
      if (notificationModules.some(id => normalizeId(id).endsWith('/FortniteFestivalWeb/src/components/notifications/notificationMocks.ts'))) {
        this.error('Notification mock data must load only through its explicit validation boundary.');
      }

      if (graphOutput) {
        const outputPath = path.isAbsolute(graphOutput)
          ? graphOutput
          : path.resolve(webRoot, graphOutput);
        const report = {
          capturedAtUtc: new Date().toISOString(),
          entryFiles: [...initialFiles],
          suggestionsFiles: [...suggestionsFiles],
          secondaryControlFiles,
          generatorChunks: generatorChunks.map(chunk => chunk.fileName),
          chunks: chunks.map(chunk => ({
            fileName: chunk.fileName,
            name: chunk.name,
            isEntry: chunk.isEntry,
            isDynamicEntry: chunk.isDynamicEntry,
            facadeModuleId: chunk.facadeModuleId,
            imports: chunk.imports,
            dynamicImports: chunk.dynamicImports,
            modules: Object.entries(chunk.modules)
              .map(([id, info]) => ({
                id,
                renderedLength: info.renderedLength,
                originalLength: info.originalLength,
                removedExports: info.removedExports,
                renderedExports: info.renderedExports,
              }))
              .sort((left, right) => right.renderedLength - left.renderedLength),
          })),
        };
        mkdirSync(path.dirname(outputPath), { recursive: true });
        writeFileSync(outputPath, `${JSON.stringify(report, null, 2)}\n`);
      }
    },
  };
}

function staticClosure(startFiles, chunksByFile) {
  const visited = new Set();
  const pending = [...startFiles];
  while (pending.length > 0) {
    const fileName = pending.pop();
    if (!fileName || visited.has(fileName)) continue;
    visited.add(fileName);
    const chunk = chunksByFile.get(fileName);
    if (chunk) pending.push(...chunk.imports);
  }
  return visited;
}

function modulesForFiles(files, chunksByFile) {
  return [...files].flatMap(fileName => Object.keys(chunksByFile.get(fileName)?.modules ?? {}));
}

function normalizeId(id) {
  return (id ?? '').replaceAll('\\', '/');
}
