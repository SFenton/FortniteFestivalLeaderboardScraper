import {
  createContext,
  useContext,
  useCallback,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from 'react';
import {
  type FirstRunSlideDef,
  type FirstRunGateContext,
  type FirstRunStorage,
  loadSeenSlides,
  saveSeenSlides,
  isSlideUnseen,
  contentHash,
} from '../firstRun/types';

type PageRegistration = {
  label: string;
  slides: FirstRunSlideDef[];
};

type FirstRunContextValue = {
  /** Register a page's slides. Called by useRegisterFirstRun. */
  register: (pageKey: string, label: string, slides: FirstRunSlideDef[]) => void;
  /** Unregister a page (on unmount). */
  unregister: (pageKey: string) => void;
  /** Get unseen slides for a page, filtered by gate predicates. */
  getUnseenSlides: (pageKey: string, ctx: FirstRunGateContext) => FirstRunSlideDef[];
  /** Get ALL slides for a page (ignoring seen state + gates). For Settings replay. */
  getAllSlides: (pageKey: string) => FirstRunSlideDef[];
  /** Mark slide IDs as seen in localStorage. */
  markSeen: (slides: FirstRunSlideDef[]) => void;
  /** Reset seen state for all slides of a page. */
  resetPage: (pageKey: string) => void;
  /** Reset all seen state. */
  resetAll: () => void;
  /** List of registered pages (for Settings "Show First Run" section). */
  registeredPages: { pageKey: string; label: string }[];
};

const FirstRunContext = createContext<FirstRunContextValue | null>(null);

export function FirstRunProvider({ children }: { children: ReactNode }) {
  const registryRef = useRef<Map<string, PageRegistration>>(new Map());
  // Bump to force re-renders when registry or seen state changes
  const [tick, setTick] = useState(0);
  const seenRef = useRef<FirstRunStorage>(loadSeenSlides());

  const register = useCallback((pageKey: string, label: string, slides: FirstRunSlideDef[]) => {
    registryRef.current.set(pageKey, { label, slides });
    setTick(t => t + 1);
  }, []);

  const unregister = useCallback((pageKey: string) => {
    registryRef.current.delete(pageKey);
    setTick(t => t + 1);
  }, []);

  const getUnseenSlides = useCallback((pageKey: string, ctx: FirstRunGateContext): FirstRunSlideDef[] => {
    const page = registryRef.current.get(pageKey);
    if (!page) return [];
    return page.slides.filter(slide => {
      if (slide.gate && !slide.gate(ctx)) return false;
      return isSlideUnseen(slide, seenRef.current);
    });
  }, []);

  const getAllSlides = useCallback((pageKey: string): FirstRunSlideDef[] => {
    const page = registryRef.current.get(pageKey);
    return page?.slides ?? [];
  }, []);

  const markSeen = useCallback((slides: FirstRunSlideDef[]) => {
    const seen = { ...seenRef.current };
    const now = new Date().toISOString();
    for (const slide of slides) {
      seen[slide.id] = {
        version: slide.version,
        hash: contentHash(slide.title + slide.description),
        seenAt: now,
      };
    }
    seenRef.current = seen;
    saveSeenSlides(seen);
    setTick(t => t + 1);
  }, []);

  const resetPage = useCallback((pageKey: string) => {
    const page = registryRef.current.get(pageKey);
    if (!page) return;
    const seen = { ...seenRef.current };
    for (const slide of page.slides) {
      delete seen[slide.id];
    }
    seenRef.current = seen;
    saveSeenSlides(seen);
    setTick(t => t + 1);
  }, []);

  const resetAll = useCallback(() => {
    seenRef.current = {};
    saveSeenSlides({});
    setTick(t => t + 1);
  }, []);

  const registeredPages = useMemo(() => {
    const pages: { pageKey: string; label: string }[] = [];
    for (const [pageKey, reg] of registryRef.current) {
      pages.push({ pageKey, label: reg.label });
    }
    return pages;
    // eslint-disable-next-line react-hooks/exhaustive-deps -- tick forces recompute when registrations change
  }, [tick]);

  const value = useMemo<FirstRunContextValue>(() => ({
    register,
    unregister,
    getUnseenSlides,
    getAllSlides,
    markSeen,
    resetPage,
    resetAll,
    registeredPages,
  }), [register, unregister, getUnseenSlides, getAllSlides, markSeen, resetPage, resetAll, registeredPages]);

  return (
    <FirstRunContext.Provider value={value}>
      {children}
    </FirstRunContext.Provider>
  );
}

export function useFirstRunContext(): FirstRunContextValue {
  const ctx = useContext(FirstRunContext);
  if (!ctx) throw new Error('useFirstRunContext must be used within a FirstRunProvider');
  return ctx;
}
