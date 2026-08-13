import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import ts from 'typescript';
import { describe, expect, it } from 'vitest';

const webRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const mainEntry = path.join(webRoot, 'src/main.tsx');
const loaderFile = path.join(webRoot, 'src/components/lazy/secondaryControls.ts');
const targetFiles = [
  'src/components/search/SearchModal.tsx',
  'src/components/notifications/MobileNotificationsModal.tsx',
  'src/components/notifications/notificationMocks.ts',
  'src/components/notifications/notificationText.ts',
  'src/components/modals/ChangelogModal.tsx',
  'src/components/modals/ConfirmAlert.tsx',
  'src/components/icons/PwaIconCapture.tsx',
  'src/components/firstRun/FirstRunCarousel.tsx',
  'src/diagnostics/ModalAccessibilityFixture.tsx',
  'src/diagnostics/scrollFadeTestMode.ts',
  'src/diagnostics/tapDiagnostics.ts',
  'src/pages/band/modals/BandInstrumentFilterModal.tsx',
  'src/pages/songs/modals/SortModal.tsx',
  'src/pages/songs/modals/FilterModal.tsx',
  'src/components/sort/ReorderList.tsx',
  'src/components/sort/SortableRow.tsx',
  'src/pages/songinfo/components/path/PathDataTable.tsx',
  'src/pages/leaderboards/modals/RankByModal.tsx',
  'src/pages/leaderboards/firstRun/metricInfo/MetricInfoCarousel.tsx',
  'src/pages/leaderboards/firstRun/metricInfo/index.ts',
  'src/components/common/Math.tsx',
].map(fileName => path.join(webRoot, fileName));

describe('secondary control import boundaries', () => {
  it('keeps diagnostic and interaction-only implementations outside the initial Songs graph', () => {
    const mainGraph = collectStaticGraph(mainEntry);

    for (const target of targetFiles) {
      expect(mainGraph.has(target), path.relative(webRoot, target)).toBe(false);
    }

    const packageImports = [...mainGraph].flatMap(moduleSpecifiers);
    expect(packageImports.some(specifier => specifier.startsWith('@dnd-kit/'))).toBe(false);
    expect(packageImports.some(specifier => specifier === 'katex')).toBe(false);
  });

  it('declares each modal implementation as a dynamic interaction import', () => {
    expect(dynamicImportSpecifiers(loaderFile).sort()).toEqual([
      '../../pages/band/modals/BandInstrumentFilterModal',
      '../../pages/songs/modals/FilterModal',
      '../../pages/songs/modals/SortModal',
      '../modals/ChangelogModal',
      '../modals/ConfirmAlert',
      '../notifications/MobileNotificationsModal',
      '../search/SearchModal',
    ]);
  });

  it('keeps metric help and KaTeX behind the Rank By info action', () => {
    const rankByFile = path.join(webRoot, 'src/pages/leaderboards/modals/RankByModal.tsx');
    const metricInfoLoader = path.join(webRoot, 'src/pages/leaderboards/firstRun/metricInfo/lazyMetricInfo.ts');
    const rankByGraph = collectStaticGraph(rankByFile);

    for (const target of [
      'src/pages/leaderboards/firstRun/metricInfo/MetricInfoCarousel.tsx',
      'src/pages/leaderboards/firstRun/metricInfo/index.ts',
      'src/components/firstRun/FirstRunCarousel.tsx',
      'src/components/common/Math.tsx',
    ].map(fileName => path.join(webRoot, fileName))) {
      expect(rankByGraph.has(target), path.relative(webRoot, target)).toBe(false);
    }
    expect([...rankByGraph].flatMap(moduleSpecifiers).some(specifier => specifier === 'katex')).toBe(false);
    expect(dynamicImportSpecifiers(metricInfoLoader)).toEqual(['./MetricInfoCarousel']);
  });

  it('keeps the path column settings contract independent of the DnD table implementation', () => {
    const settingsContext = path.join(webRoot, 'src/contexts/SettingsContext.tsx');
    const specifiers = moduleSpecifiers(settingsContext);

    expect(specifiers).toContain('../pages/songinfo/components/path/pathTableColumns');
    expect(specifiers).not.toContain('../pages/songinfo/components/path/PathDataTable');
  });
});

function collectStaticGraph(entryFile: string): Set<string> {
  const visited = new Set<string>();
  const pending = [entryFile];
  while (pending.length > 0) {
    const fileName = pending.pop();
    if (!fileName || visited.has(fileName)) continue;
    visited.add(fileName);
    for (const specifier of moduleSpecifiers(fileName)) {
      const resolved = specifier.startsWith('.')
        ? resolveTypeScriptFile(path.resolve(path.dirname(fileName), specifier))
        : null;
      if (resolved && !visited.has(resolved)) pending.push(resolved);
    }
  }
  return visited;
}

function moduleSpecifiers(fileName: string): string[] {
  const source = sourceFile(fileName);
  return source.statements.flatMap((statement) => {
    if (ts.isImportDeclaration(statement)) {
      if (statement.importClause?.isTypeOnly) return [];
      if (ts.isStringLiteral(statement.moduleSpecifier)) return [statement.moduleSpecifier.text];
    }
    if (ts.isExportDeclaration(statement)) {
      if (statement.isTypeOnly) return [];
      if (statement.moduleSpecifier && ts.isStringLiteral(statement.moduleSpecifier)) {
        return [statement.moduleSpecifier.text];
      }
    }
    return [];
  });
}

function dynamicImportSpecifiers(fileName: string): string[] {
  const values: string[] = [];
  const visit = (node: ts.Node) => {
    const [argument] = ts.isCallExpression(node) ? node.arguments : [];
    if (
      ts.isCallExpression(node)
      && node.expression.kind === ts.SyntaxKind.ImportKeyword
      && argument
      && ts.isStringLiteral(argument)
    ) {
      values.push(argument.text);
    }
    ts.forEachChild(node, visit);
  };
  visit(sourceFile(fileName));
  return values;
}

function sourceFile(fileName: string) {
  return ts.createSourceFile(
    fileName,
    readFileSync(fileName, 'utf8'),
    ts.ScriptTarget.Latest,
    true,
  );
}

function resolveTypeScriptFile(basePath: string): string | null {
  for (const candidate of [
    basePath,
    `${basePath}.ts`,
    `${basePath}.tsx`,
    path.join(basePath, 'index.ts'),
    path.join(basePath, 'index.tsx'),
  ]) {
    if (ts.sys.fileExists(candidate)) return path.normalize(candidate);
  }
  return null;
}
