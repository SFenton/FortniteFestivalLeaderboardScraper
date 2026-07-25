import { lazy, type ComponentType } from 'react';

type LazyModule<TProps> = {
  default: ComponentType<TProps>;
};

export function lazyWithPreload<TProps>(
  loader: () => Promise<LazyModule<TProps>>,
) {
  let pending: Promise<LazyModule<TProps>> | null = null;
  const load = () => {
    pending ??= loader();
    return pending;
  };

  return {
    Component: lazy(load),
    preload: () => {
      if (typeof navigator !== 'undefined' && navigator.onLine === false) return;
      void load().catch(() => {});
    },
  };
}
