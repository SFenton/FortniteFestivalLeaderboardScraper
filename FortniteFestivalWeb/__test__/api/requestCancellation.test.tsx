import { readFileSync, readdirSync } from 'node:fs';
import { resolve } from 'node:path';
import { QueryClient, QueryClientProvider, useQuery } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import ts from 'typescript';
import { describe, expect, it, vi } from 'vitest';

function sourceFiles(directory: string): string[] {
  return readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
    const path = resolve(directory, entry.name);
    return entry.isDirectory() ? sourceFiles(path) : /\.tsx?$/.test(entry.name) ? [path] : [];
  });
}

function createClient() {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: 2 },
      mutations: { retry: false },
    },
  });
}

function wrapper(client: QueryClient) {
  return function Wrapper({ children }: { children: ReactNode }) {
    return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
  };
}

describe('request cancellation coverage', () => {
  it('keeps every GET queryFn signal-aware', () => {
    const root = resolve(process.cwd(), 'src');
    const queryFunctions: { file: string; expression: string }[] = [];

    for (const file of sourceFiles(root)) {
      const source = readFileSync(file, 'utf8');
      const sourceFile = ts.createSourceFile(
        file,
        source,
        ts.ScriptTarget.Latest,
        true,
        file.endsWith('.tsx') ? ts.ScriptKind.TSX : ts.ScriptKind.TS,
      );
      const visit = (node: ts.Node) => {
        if (ts.isPropertyAssignment(node) && node.name.getText(sourceFile) === 'queryFn') {
          queryFunctions.push({ file, expression: node.initializer.getText(sourceFile) });
        }
        ts.forEachChild(node, visit);
      };
      visit(sourceFile);
    }

    expect(queryFunctions.length).toBeGreaterThan(0);
    for (const queryFn of queryFunctions) {
      if (queryFn.expression === 'fetchServiceInfo') continue;
      expect(queryFn.expression, queryFn.file).toMatch(/\bsignal\b/);
    }

    const serviceInfoSource = readFileSync(resolve(root, 'hooks/data/useServiceInfo.ts'), 'utf8');
    expect(serviceInfoSource).toMatch(/fetchServiceInfo\(\{\s*signal\s*\}/);

    const clientPath = resolve(root, 'api/client.ts');
    const clientSource = readFileSync(clientPath, 'utf8');
    const clientFile = ts.createSourceFile(clientPath, clientSource, ts.ScriptTarget.Latest, true, ts.ScriptKind.TS);
    const getMethods: { name: string; source: string }[] = [];
    const visitClient = (node: ts.Node) => {
      if (
        ts.isVariableDeclaration(node)
        && node.name.getText(clientFile) === 'api'
        && node.initializer
        && ts.isObjectLiteralExpression(node.initializer)
      ) {
        for (const property of node.initializer.properties) {
          const name = property.name?.getText(clientFile).replace(/["']/g, '') ?? '';
          if (/^(get|search)/.test(name)) {
            getMethods.push({ name, source: property.getText(clientFile) });
          }
        }
      }
      ts.forEachChild(node, visitClient);
    };
    visitClient(clientFile);

    for (const method of getMethods) {
      if (method.name === 'getPublication') {
        expect(method.source).toContain('ensurePublication()');
        continue;
      }
      expect(method.source, method.name).toMatch(/ApiRequestOptions|AbortSignal/);
    }
    expect(clientSource).not.toContain('shopEtagCache');

    const booleanCancellationFiles = sourceFiles(root)
      .filter(file => /\blet\s+cancelled\s*=\s*false\b/.test(readFileSync(file, 'utf8')))
      .map(file => file.replace(`${root}/`, ''));
    expect(booleanCancellationFiles).toEqual([]);

    for (const relativePath of [
      'hooks/data/useAccountSearch.ts',
      'hooks/data/useUnifiedSearch.ts',
      'hooks/data/useSuggestions.ts',
      'hooks/data/useSyncStatus.ts',
      'pages/leaderboard/player/PlayerHistoryPage.tsx',
      'pages/settings/SettingsPage.tsx',
      'pages/songinfo/components/path/PathsModal.tsx',
    ]) {
      const source = readFileSync(resolve(root, relativePath), 'utf8');
      expect(source, relativePath).toContain('AbortController');
      expect(source, relativePath).toMatch(/\bsignal\b/);
    }
  });

  it('aborts an obsolete key without retrying or rendering stale data', async () => {
    type PendingRequest = {
      signal: AbortSignal;
      resolve: (value: string) => void;
    };
    const pending = new Map<string, PendingRequest>();
    const request = vi.fn((scope: string, signal: AbortSignal) => new Promise<string>((resolvePromise, reject) => {
      const abort = () => reject(signal.reason ?? new DOMException('Aborted', 'AbortError'));
      signal.addEventListener('abort', abort, { once: true });
      pending.set(scope, {
        signal,
        resolve: value => {
          signal.removeEventListener('abort', abort);
          resolvePromise(value);
        },
      });
    }));
    const client = createClient();

    function Consumer({ scope }: { scope: string }) {
      const query = useQuery({
        queryKey: ['cancellation-test', scope],
        queryFn: ({ signal }) => request(scope, signal),
      });
      return <div>{query.isError ? 'error' : query.data ?? 'loading'}</div>;
    }

    const view = render(<Consumer scope="old" />, { wrapper: wrapper(client) });
    await waitFor(() => expect(pending.has('old')).toBe(true));

    view.rerender(<Consumer scope="new" />);
    await waitFor(() => expect(pending.get('old')?.signal.aborted).toBe(true));
    await waitFor(() => expect(pending.has('new')).toBe(true));

    pending.get('new')?.resolve('latest response');
    await screen.findByText('latest response');

    expect(screen.queryByText('error')).not.toBeInTheDocument();
    expect(screen.queryByText('stale response')).not.toBeInTheDocument();
    expect(request.mock.calls.filter(([scope]) => scope === 'old')).toHaveLength(1);
    expect(request.mock.calls.filter(([scope]) => scope === 'new')).toHaveLength(1);
    client.clear();
  });

  it('preserves React Query dedupe while both consumers share one signal', async () => {
    let resolveRequest!: (value: string) => void;
    const signals: AbortSignal[] = [];
    const request = vi.fn((signal: AbortSignal) => {
      signals.push(signal);
      return new Promise<string>(resolve => {
        resolveRequest = resolve;
      });
    });
    const client = createClient();

    function Consumer({ label }: { label: string }) {
      const query = useQuery({
        queryKey: ['cancellation-dedupe'],
        queryFn: ({ signal }) => request(signal),
      });
      return <div>{label}:{query.data ?? 'loading'}</div>;
    }

    render(
      <>
        <Consumer label="first" />
        <Consumer label="second" />
      </>,
      { wrapper: wrapper(client) },
    );

    expect(request).toHaveBeenCalledTimes(1);
    expect(signals).toHaveLength(1);
    resolveRequest('shared');
    await screen.findByText('first:shared');
    expect(screen.getByText('second:shared')).toBeInTheDocument();
    expect(request).toHaveBeenCalledTimes(1);
    client.clear();
  });
});
