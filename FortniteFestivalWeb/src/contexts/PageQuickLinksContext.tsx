import { createContext, useCallback, useContext, useMemo, useRef, useState, type ReactNode } from 'react';

export type RegisteredPageQuickLinks = {
  title: string;
  openQuickLinks: () => void;
};

type PageQuickLinksContextType = {
  registerPageQuickLinks: (registration: RegisteredPageQuickLinks | null) => void;
  pageQuickLinks: RegisteredPageQuickLinks | null;
  hasPageQuickLinks: boolean;
  openPageQuickLinks: () => void;
};

const noop = () => {};

const PageQuickLinksContext = createContext<PageQuickLinksContextType>({
  registerPageQuickLinks: noop,
  pageQuickLinks: null,
  hasPageQuickLinks: false,
  openPageQuickLinks: noop,
});

export function PageQuickLinksProvider({ children }: { children: ReactNode }) {
  const registrationRef = useRef<RegisteredPageQuickLinks | null>(null);
  const [pageQuickLinks, setPageQuickLinks] = useState<RegisteredPageQuickLinks | null>(null);

  const registerPageQuickLinks = useCallback((registration: RegisteredPageQuickLinks | null) => {
    const currentRegistration = registrationRef.current;
    if (
      currentRegistration?.title === registration?.title
      && currentRegistration?.openQuickLinks === registration?.openQuickLinks
    ) {
      return;
    }

    registrationRef.current = registration;
    setPageQuickLinks(registration);
  }, []);

  const openPageQuickLinks = useCallback(() => {
    registrationRef.current?.openQuickLinks();
  }, []);

  const value = useMemo<PageQuickLinksContextType>(() => ({
    registerPageQuickLinks,
    pageQuickLinks,
    hasPageQuickLinks: pageQuickLinks !== null,
    openPageQuickLinks,
  }), [openPageQuickLinks, pageQuickLinks, registerPageQuickLinks]);

  return (
    <PageQuickLinksContext.Provider value={value}>
      {children}
    </PageQuickLinksContext.Provider>
  );
}

export function usePageQuickLinksController() {
  return useContext(PageQuickLinksContext);
}