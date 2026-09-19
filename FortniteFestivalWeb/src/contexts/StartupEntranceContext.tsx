import { createContext, useContext, type ReactNode } from 'react';

const StartupEntranceContext = createContext(true);

export function StartupEntranceProvider({
  complete,
  children,
}: {
  complete: boolean;
  children: ReactNode;
}) {
  return (
    <StartupEntranceContext.Provider value={complete}>
      {children}
    </StartupEntranceContext.Provider>
  );
}

export function useStartupEntranceComplete(): boolean {
  return useContext(StartupEntranceContext);
}
