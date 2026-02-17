import React from 'react';

type WindowsFlyoutUiContextValue = {
  chromeHidden: boolean;
  setChromeHidden: (hidden: boolean) => void;
  openFlyout: () => void;
};

const WindowsFlyoutUiContext = React.createContext<WindowsFlyoutUiContextValue | undefined>(
  undefined,
);

export function WindowsFlyoutUiProvider(props: {children: React.ReactNode}) {
  const [chromeHidden, setChromeHidden] = React.useState(false);
  const openFlyoutRef = React.useRef<() => void>(() => {});

  const setOpenFlyout = React.useCallback((fn: () => void) => {
    openFlyoutRef.current = fn;
  }, []);

  const openFlyout = React.useCallback(() => {
    openFlyoutRef.current();
  }, []);

  const value = React.useMemo(
    () => ({chromeHidden, setChromeHidden, openFlyout}),
    [chromeHidden, openFlyout],
  );

  return (
    <WindowsFlyoutUiContext.Provider value={{...value, _setOpenFlyout: setOpenFlyout} as any}>
      {props.children}
    </WindowsFlyoutUiContext.Provider>
  );
}

/** @internal – only consumed by WindowsFlyout to register its open callback. */
export function useRegisterOpenFlyout(fn: () => void) {
  const ctx = React.useContext(WindowsFlyoutUiContext) as any;
  const setOpenFlyout = ctx?._setOpenFlyout;
  React.useEffect(() => {
    setOpenFlyout?.(fn);
  }, [fn, setOpenFlyout]);
}

export function useWindowsFlyoutUi(): WindowsFlyoutUiContextValue {
  const ctx = React.useContext(WindowsFlyoutUiContext);

  // Safe no-op fallback when used outside the Windows flyout tree.
  return (
    ctx ?? {
      chromeHidden: false,
      setChromeHidden: () => {},
      openFlyout: () => {},
    }
  );
}
