export interface PreserveShellScrollState {
  preserveShellScrollKey?: string;
}

export function createPreserveShellScrollState(scope: string): PreserveShellScrollState {
  return {
    preserveShellScrollKey: `${scope}:${Date.now()}:${Math.random().toString(36).slice(2)}`,
  };
}