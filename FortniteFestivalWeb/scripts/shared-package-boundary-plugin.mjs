import { mkdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';

const CORE_GENERATOR_SUFFIX = '/packages/core/src/suggestions/suggestionGenerator.ts';
const SUGGESTIONS_PAGE_SUFFIX = '/FortniteFestivalWeb/src/pages/suggestions/SuggestionsPage.tsx';

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

      if (initialModules.some(id => normalizeId(id).endsWith(CORE_GENERATOR_SUFFIX))) {
        this.error('SuggestionGenerator must not be reachable from the initial Songs chunk graph.');
      }
      if (!suggestionsChunk) {
        this.error('Unable to locate the lazy SuggestionsPage chunk.');
      }
      if (!suggestionsModules.some(id => normalizeId(id).endsWith(CORE_GENERATOR_SUFFIX))) {
        this.error('SuggestionGenerator must remain reachable from the lazy SuggestionsPage chunk graph.');
      }

      if (graphOutput) {
        const outputPath = path.isAbsolute(graphOutput)
          ? graphOutput
          : path.resolve(webRoot, graphOutput);
        const report = {
          capturedAtUtc: new Date().toISOString(),
          entryFiles: [...initialFiles],
          suggestionsFiles: [...suggestionsFiles],
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
