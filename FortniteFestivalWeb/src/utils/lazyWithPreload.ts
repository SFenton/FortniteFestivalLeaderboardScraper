import { lazy, type ComponentType } from 'react';

type LazyModule<TProps> = {
  default: ComponentType<TProps>;
};

export function lazyWithPreload<TProps>(
  loader: () => Promise<LazyModule<TProps>>,
) {
  let pending: Promise<LazyModule<TProps>> | null = null;
  let resolved: LazyModule<TProps> | null = null;
  const load = () => {
    pending ??= loader().then((module) => {
      resolved = module;
      return module;
    });
    return pending;
  };
  const loadForReact = () => (
    resolved
      ? {
          then(onFulfilled: (module: LazyModule<TProps>) => void) {
            onFulfilled(resolved!);
          },
        } as unknown as Promise<LazyModule<TProps>>
      : load()
  );

  return {
    Component: lazy(loadForReact),
    preload: () => {
      if (typeof navigator !== 'undefined' && navigator.onLine === false) return;
      void load().catch(() => {});
    },
    load: () => load().then(() => {}),
    isLoaded: () => resolved !== null,
  };
}
