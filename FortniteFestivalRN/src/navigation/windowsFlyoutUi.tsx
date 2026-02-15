import React from 'react';

type WindowsFlyoutUiContextValue = {
  chromeHidden: boolean;
  setChromeHidden: (hidden: boolean) => void;
};

const WindowsFlyoutUiContext = React.createContext<WindowsFlyoutUiContextValue | undefined>(
  undefined,
);

export function WindowsFlyoutUiProvider(props: {children: React.ReactNode}) {
  const [chromeHidden, setChromeHidden] = React.useState(false);

  const value = React.useMemo(
    () => ({chromeHidden, setChromeHidden}),
    [chromeHidden],
  );

  return (
    <WindowsFlyoutUiContext.Provider value={value}>
      {props.children}
    </WindowsFlyoutUiContext.Provider>
  );
}

export function useWindowsFlyoutUi(): WindowsFlyoutUiContextValue {
  const ctx = React.useContext(WindowsFlyoutUiContext);

  // Safe no-op fallback when used outside the Windows flyout tree.
  return (
    ctx ?? {
      chromeHidden: false,
      setChromeHidden: () => {},
    }
  );
}
