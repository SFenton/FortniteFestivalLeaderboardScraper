import { readFileSync, readdirSync, statSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import ts from 'typescript';
import { describe, expect, it } from 'vitest';

const webRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const repoRoot = path.resolve(webRoot, '..');
const coreRoot = path.join(repoRoot, 'packages/core');
const corePackageJsonPath = path.join(coreRoot, 'package.json');
const corePackage = JSON.parse(readFileSync(corePackageJsonPath, 'utf8')) as {
  sideEffects?: boolean;
  exports?: Record<string, string | { types?: string; default?: string }>;
};
const themeRoot = path.join(repoRoot, 'packages/theme');
const uiUtilsRoot = path.join(repoRoot, 'packages/ui-utils');
const themePackage = readPackageManifest(path.join(themeRoot, 'package.json'));
const uiUtilsPackage = readPackageManifest(path.join(uiUtilsRoot, 'package.json'));
const webPackage = JSON.parse(
  readFileSync(path.join(webRoot, 'package.json'), 'utf8'),
) as {
  dependencies?: Record<string, string>;
};

describe('@festival/core package boundaries', () => {
  it('publishes explicit feature entry points and declares audited side effects', () => {
    expect(corePackage.sideEffects).toBe(false);
    expect(corePackage.exports).toMatchObject({
      '.': { types: './src/index.ts', default: './src/index.ts' },
      './api': { types: './src/api/index.ts', default: './src/api/index.ts' },
      './app': { types: './src/app/index.ts', default: './src/app/index.ts' },
      './config': { types: './src/config.ts', default: './src/config.ts' },
      './runtime': { types: './src/runtime.ts', default: './src/runtime.ts' },
      './suggestions': { types: './src/suggestions/index.ts', default: './src/suggestions/index.ts' },
      './types': { types: './src/types.ts', default: './src/types.ts' },
    });
  });

  it('resolves every feature entry through package exports during the web build', () => {
    const configFile = ts.readConfigFile(
      path.join(webRoot, 'tsconfig.json'),
      fileName => readFileSync(fileName, 'utf8'),
    );
    const parsed = ts.parseJsonConfigFileContent(configFile.config, ts.sys, webRoot);
    const containingFile = path.join(webRoot, 'src/main.tsx');
    const expected = new Map([
      ['@festival/core', 'src/index.ts'],
      ['@festival/core/api', 'src/api/index.ts'],
      ['@festival/core/app', 'src/app/index.ts'],
      ['@festival/core/config', 'src/config.ts'],
      ['@festival/core/runtime', 'src/runtime.ts'],
      ['@festival/core/suggestions', 'src/suggestions/index.ts'],
      ['@festival/core/types', 'src/types.ts'],
    ]);

    for (const [specifier, relativeTarget] of expected) {
      const resolved = ts.resolveModuleName(
        specifier,
        containingFile,
        parsed.options,
        ts.sys,
      ).resolvedModule?.resolvedFileName;
      expect(resolved && path.normalize(resolved)).toBe(path.join(coreRoot, relativeTarget));
    }
  });

  it('keeps bare root imports out of web and shared-package runtime sources', () => {
    const sourceRoots = [path.join(webRoot, 'src')];
    const bareImports = sourceRoots.flatMap(sourceRoot =>
      sourceFiles(sourceRoot).flatMap(fileName =>
        moduleSpecifiers(fileName)
          .filter(specifier => specifier === '@festival/core')
          .map(() => path.relative(repoRoot, fileName)),
      ),
    );
    expect(bareImports).toEqual([]);
  });

  it('keeps suggestion generation outside main/Songs and inside lazy Suggestions', () => {
    const mainGraph = collectStaticGraph(path.join(webRoot, 'src/main.tsx'));
    const suggestionsGraph = collectStaticGraph(
      path.join(webRoot, 'src/pages/suggestions/SuggestionsPage.tsx'),
    );
    const generator = path.join(coreRoot, 'src/suggestions/suggestionGenerator.ts');
    const rootBarrel = path.join(coreRoot, 'src/index.ts');

    expect(mainGraph.has(rootBarrel)).toBe(false);
    expect(mainGraph.has(generator)).toBe(false);
    expect(suggestionsGraph.has(generator)).toBe(true);
  });
});

describe('theme and UI utility package boundaries', () => {
  it('publishes root-only entry points with audited side effects', () => {
    for (const packageManifest of [themePackage, uiUtilsPackage]) {
      expect(packageManifest.sideEffects).toBe(false);
      expect(packageManifest.exports).toEqual({
        '.': {
          types: './src/index.ts',
          default: './src/index.ts',
        },
        './package.json': './package.json',
      });
    }
  });

  it('resolves package roots through Yarn portals and rejects deep imports', () => {
    const configFile = ts.readConfigFile(
      path.join(webRoot, 'tsconfig.json'),
      fileName => readFileSync(fileName, 'utf8'),
    );
    const parsed = ts.parseJsonConfigFileContent(configFile.config, ts.sys, webRoot);
    const containingFile = path.join(webRoot, 'src/main.tsx');

    for (const [specifier, expected] of [
      ['@festival/theme', path.join(themeRoot, 'src/index.ts')],
      ['@festival/ui-utils', path.join(uiUtilsRoot, 'src/index.ts')],
    ] as const) {
      const resolved = ts.resolveModuleName(
        specifier,
        containingFile,
        parsed.options,
        ts.sys,
      ).resolvedModule?.resolvedFileName;
      expect(resolved && path.normalize(resolved)).toBe(expected);
    }

    for (const specifier of [
      '@festival/theme/colors',
      '@festival/ui-utils/platform',
    ]) {
      expect(ts.resolveModuleName(
        specifier,
        containingFile,
        parsed.options,
        ts.sys,
      ).resolvedModule).toBeUndefined();
    }
  });

  it('keeps all consumers on package roots rather than deep or source imports', () => {
    const roots = [
      path.join(webRoot, 'src'),
      path.join(webRoot, '__test__'),
      path.join(webRoot, 'component-tests'),
      path.join(webRoot, 'e2e'),
    ];
    const violations = roots.flatMap(root => sourceFiles(root).flatMap(fileName =>
      moduleSpecifiers(fileName)
        .filter(specifier => (
          specifier.startsWith('@festival/theme/')
          || specifier.startsWith('@festival/ui-utils/')
          || /packages\/(?:theme|ui-utils)\/src/.test(specifier.replace(/\\/g, '/'))
        ))
        .map(specifier => `${path.relative(repoRoot, fileName)} -> ${specifier}`),
    ));
    expect(violations).toEqual([]);
  });

  it('keeps web dependencies on the repository portal packages', () => {
    expect(webPackage.dependencies?.['@festival/theme']).toBe('portal:../packages/theme');
    expect(webPackage.dependencies?.['@festival/ui-utils']).toBe('portal:../packages/ui-utils');
  });
});

function readPackageManifest(fileName: string): {
  sideEffects?: boolean;
  exports?: Record<string, string | { types?: string; default?: string }>;
} {
  return JSON.parse(readFileSync(fileName, 'utf8'));
}

function collectStaticGraph(entryFile: string): Set<string> {
  const visited = new Set<string>();
  const pending = [entryFile];
  while (pending.length > 0) {
    const fileName = pending.pop();
    if (!fileName || visited.has(fileName)) continue;
    visited.add(fileName);
    for (const specifier of moduleSpecifiers(fileName)) {
      const resolved = resolveSourceModule(fileName, specifier);
      if (resolved && !visited.has(resolved)) pending.push(resolved);
    }
  }
  return visited;
}

function moduleSpecifiers(fileName: string): string[] {
  const source = ts.createSourceFile(
    fileName,
    readFileSync(fileName, 'utf8'),
    ts.ScriptTarget.Latest,
    true,
  );
  return source.statements.flatMap(statement => {
    if (
      (ts.isImportDeclaration(statement) || ts.isExportDeclaration(statement))
      && statement.moduleSpecifier
      && ts.isStringLiteral(statement.moduleSpecifier)
    ) {
      return [statement.moduleSpecifier.text];
    }
    return [];
  });
}

function resolveSourceModule(fromFile: string, specifier: string): string | null {
  if (specifier.startsWith('.')) {
    return resolveTypeScriptFile(path.resolve(path.dirname(fromFile), specifier));
  }
  if (specifier === '@festival/core' || specifier.startsWith('@festival/core/')) {
    const subpath = specifier === '@festival/core'
      ? '.'
      : `./${specifier.slice('@festival/core/'.length)}`;
    const target = resolveExportTarget(subpath);
    return target ? path.join(coreRoot, target.slice(2)) : null;
  }
  return null;
}

function resolveExportTarget(subpath: string): string | null {
  const exports = corePackage.exports ?? {};
  const exact = exports[subpath];
  if (exact) return exportTarget(exact);
  for (const [pattern, value] of Object.entries(exports)) {
    if (!pattern.endsWith('/*')) continue;
    const prefix = pattern.slice(0, -1);
    if (!subpath.startsWith(prefix)) continue;
    const match = subpath.slice(prefix.length);
    return exportTarget(value)?.replace('*', match) ?? null;
  }
  return null;
}

function exportTarget(value: string | { types?: string; default?: string }): string | null {
  return typeof value === 'string' ? value : value.types ?? value.default ?? null;
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

function sourceFiles(root: string): string[] {
  return readdirSync(root, { withFileTypes: true }).flatMap(entry => {
    const fullPath = path.join(root, entry.name);
    if (entry.isDirectory()) return sourceFiles(fullPath);
    if (!entry.isFile() || !/\.(?:ts|tsx)$/.test(entry.name)) return [];
    return statSync(fullPath).isFile() ? [fullPath] : [];
  });
}
