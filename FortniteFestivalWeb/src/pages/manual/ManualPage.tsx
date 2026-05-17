/* eslint-disable react/forbid-dom-props -- page follows the app's inline theme style pattern */
import { useCallback, useMemo, useState, type CSSProperties, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { IoBagHandle, IoChevronBack, IoChevronForward, IoCompass, IoMusicalNotes, IoPeople, IoPhonePortrait, IoSettings, IoSparkles, IoStatsChart, IoSync, IoTrophy } from 'react-icons/io5';
import {
  Align, Border, BoxSizing, Colors, CssValue, Display, Font, Gap, Layout, LineHeight,
  MaxWidth, ObjectFit, Overflow, Radius, Size, TextAlign, Weight, border, flexBetween,
  flexCenter, flexColumn, flexRow, padding,
} from '@festival/theme';
import Page from '../Page';
import PageHeader from '../../components/common/PageHeader';
import { ActionPill } from '../../components/common/ActionPill';
import { useScrollContainer } from '../../contexts/ScrollContainerContext';
import { useIsMobileChrome, useIsWideDesktop } from '../../hooks/ui/useIsMobile';
import { usePageQuickLinks, type PageQuickLinkItem } from '../../hooks/ui/usePageQuickLinks';
import { usePressAction } from '../../hooks/ui/usePressAction';
import { useSwipeNavigation } from '../../hooks/ui/useSwipeNavigation';

type ManualSectionId =
  | 'navigation'
  | 'songs'
  | 'profiles'
  | 'player-details'
  | 'band-details'
  | 'song-detail'
  | 'sync-history'
  | 'suggestions'
  | 'compete'
  | 'leaderboards-rivals'
  | 'shop'
  | 'settings';

type ManualIcon =
  | 'navigation'
  | 'songs'
  | 'profiles'
  | 'player'
  | 'band'
  | 'detail'
  | 'sync'
  | 'suggestions'
  | 'compete'
  | 'leaderboards'
  | 'shop'
  | 'settings';

type ViewportId = 'mobile' | 'compact' | 'wide';

type ManualCarousel = {
  id: string;
  slug: string;
  titleKey: string;
};

type ManualSubsection = {
  id: string;
  translationKey: string;
  copyKeys: string[];
  carousels: ManualCarousel[];
};

type ManualSection = {
  id: ManualSectionId;
  translationKey: string;
  icon: ManualIcon;
  copyKeys: string[];
  carousels: ManualCarousel[];
  subsections: ManualSubsection[];
};

type ScreenshotSlide = {
  viewport: ViewportId;
  src: string;
};

const VIEWPORTS: ViewportId[] = ['mobile', 'compact', 'wide'];
const SECTION_ICON_SLOT_SIZE = 32;
const SECTION_ICON_SIZE = Size.iconSm;

export const MANUAL_SECTIONS: ManualSection[] = [
  {
    id: 'navigation', translationKey: 'navigation', icon: 'navigation', copyKeys: ['intro', 'context'], carousels: [{ id: 'navigation-overview', slug: 'navigation-overview', titleKey: 'overview' }],
    subsections: [
      { id: 'navigation-sidebar', translationKey: 'sidebar', copyKeys: ['body'], carousels: [{ id: 'navigation-sidebar', slug: 'navigation-sidebar', titleKey: 'sidebar' }] },
      { id: 'navigation-mobile-actions', translationKey: 'mobileActions', copyKeys: ['body'], carousels: [{ id: 'navigation-mobile-actions', slug: 'navigation-mobile-actions', titleKey: 'mobileActions' }] },
      { id: 'navigation-quick-links', translationKey: 'quickLinks', copyKeys: ['body'], carousels: [{ id: 'navigation-quick-links', slug: 'navigation-quick-links', titleKey: 'quickLinks' }] },
    ],
  },
  {
    id: 'songs', translationKey: 'songs', icon: 'songs', copyKeys: ['intro', 'context'], carousels: [{ id: 'songs-overview', slug: 'songs-overview', titleKey: 'overview' }],
    subsections: [
      { id: 'songs-rows', translationKey: 'rows', copyKeys: ['body'], carousels: [{ id: 'songs-rows', slug: 'songs-rows', titleKey: 'rows' }] },
      { id: 'songs-profile-sorts', translationKey: 'profileSorts', copyKeys: ['body'], carousels: [{ id: 'songs-profile-sorts', slug: 'songs-profile-sorts', titleKey: 'profileSorts' }] },
      { id: 'songs-search-sort-filter', translationKey: 'searchSortFilter', copyKeys: ['body'], carousels: [{ id: 'songs-search-sort-filter', slug: 'songs-search-sort-filter', titleKey: 'searchSortFilter' }] },
    ],
  },
  {
    id: 'profiles', translationKey: 'profiles', icon: 'profiles', copyKeys: ['intro', 'context'], carousels: [{ id: 'profiles-overview', slug: 'profiles-overview', titleKey: 'overview' }],
    subsections: [
      { id: 'profiles-player', translationKey: 'player', copyKeys: ['body'], carousels: [{ id: 'profiles-player', slug: 'profiles-player', titleKey: 'player' }] },
      { id: 'profiles-band', translationKey: 'band', copyKeys: ['body'], carousels: [{ id: 'profiles-band', slug: 'profiles-band', titleKey: 'band' }] },
      { id: 'profiles-context', translationKey: 'context', copyKeys: ['body'], carousels: [{ id: 'profiles-context', slug: 'profiles-context', titleKey: 'context' }] },
    ],
  },
  {
    id: 'player-details', translationKey: 'playerDetails', icon: 'player', copyKeys: ['intro', 'context'], carousels: [{ id: 'player-details-overview', slug: 'player-details-overview', titleKey: 'overview' }],
    subsections: [
      { id: 'player-summary', translationKey: 'summary', copyKeys: ['body'], carousels: [{ id: 'player-summary', slug: 'player-summary', titleKey: 'summary' }] },
      { id: 'player-instruments', translationKey: 'instruments', copyKeys: ['body'], carousels: [{ id: 'player-instruments', slug: 'player-instruments', titleKey: 'instruments' }] },
      { id: 'player-navigation', translationKey: 'navigation', copyKeys: ['body'], carousels: [{ id: 'player-navigation', slug: 'player-navigation', titleKey: 'navigation' }] },
    ],
  },
  {
    id: 'band-details', translationKey: 'bandDetails', icon: 'band', copyKeys: ['intro', 'context'], carousels: [{ id: 'band-details-overview', slug: 'band-details-overview', titleKey: 'overview' }],
    subsections: [
      { id: 'band-members', translationKey: 'members', copyKeys: ['body'], carousels: [{ id: 'band-members', slug: 'band-members', titleKey: 'members' }] },
      { id: 'band-rank-history', translationKey: 'rankHistory', copyKeys: ['body'], carousels: [{ id: 'band-rank-history', slug: 'band-rank-history', titleKey: 'rankHistory' }] },
      { id: 'band-songs', translationKey: 'songs', copyKeys: ['body'], carousels: [{ id: 'band-songs', slug: 'band-songs', titleKey: 'songs' }] },
    ],
  },
  {
    id: 'song-detail', translationKey: 'songDetail', icon: 'detail', copyKeys: ['intro', 'context'], carousels: [{ id: 'song-detail-overview', slug: 'song-detail-overview', titleKey: 'overview' }],
    subsections: [
      { id: 'song-detail-cards', translationKey: 'cards', copyKeys: ['body'], carousels: [{ id: 'song-detail-cards', slug: 'song-detail-cards', titleKey: 'cards' }] },
      { id: 'song-detail-leaderboards', translationKey: 'leaderboards', copyKeys: ['body'], carousels: [{ id: 'song-detail-leaderboards', slug: 'song-detail-leaderboards', titleKey: 'leaderboards' }] },
      { id: 'song-detail-paths-history', translationKey: 'pathsHistory', copyKeys: ['body'], carousels: [{ id: 'song-detail-paths-history', slug: 'song-detail-paths-history', titleKey: 'pathsHistory' }] },
    ],
  },
  {
    id: 'sync-history', translationKey: 'syncHistory', icon: 'sync', copyKeys: ['intro', 'context'], carousels: [{ id: 'sync-overview', slug: 'sync-overview', titleKey: 'overview' }],
    subsections: [
      { id: 'sync-card', translationKey: 'card', copyKeys: ['body'], carousels: [{ id: 'sync-card', slug: 'sync-card', titleKey: 'card' }] },
      { id: 'sync-after', translationKey: 'after', copyKeys: ['body'], carousels: [{ id: 'sync-after', slug: 'sync-after', titleKey: 'after' }] },
      { id: 'sync-graphs', translationKey: 'graphs', copyKeys: ['body'], carousels: [{ id: 'sync-graphs', slug: 'sync-graphs', titleKey: 'graphs' }] },
    ],
  },
  {
    id: 'suggestions', translationKey: 'suggestions', icon: 'suggestions', copyKeys: ['intro', 'context'], carousels: [{ id: 'suggestions-overview', slug: 'suggestions-overview', titleKey: 'overview' }],
    subsections: [
      { id: 'suggestions-solo', translationKey: 'solo', copyKeys: ['body'], carousels: [{ id: 'suggestions-solo', slug: 'suggestions-solo', titleKey: 'solo' }] },
      { id: 'suggestions-band', translationKey: 'band', copyKeys: ['body'], carousels: [{ id: 'suggestions-band', slug: 'suggestions-band', titleKey: 'band' }] },
      { id: 'suggestions-filters', translationKey: 'filters', copyKeys: ['body'], carousels: [{ id: 'suggestions-filters', slug: 'suggestions-filters', titleKey: 'filters' }] },
    ],
  },
  {
    id: 'compete', translationKey: 'compete', icon: 'compete', copyKeys: ['intro', 'context'], carousels: [{ id: 'compete-overview', slug: 'compete-overview', titleKey: 'overview' }],
    subsections: [
      { id: 'compete-mobile-hub', translationKey: 'mobileHub', copyKeys: ['body'], carousels: [{ id: 'compete-mobile-hub', slug: 'compete-mobile-hub', titleKey: 'mobileHub' }] },
      { id: 'compete-leaderboards', translationKey: 'leaderboards', copyKeys: ['body'], carousels: [{ id: 'compete-leaderboards', slug: 'compete-leaderboards', titleKey: 'leaderboards' }] },
      { id: 'compete-rivals', translationKey: 'rivals', copyKeys: ['body'], carousels: [{ id: 'compete-rivals', slug: 'compete-rivals', titleKey: 'rivals' }] },
    ],
  },
  {
    id: 'leaderboards-rivals', translationKey: 'leaderboardsRivals', icon: 'leaderboards', copyKeys: ['intro', 'context'], carousels: [{ id: 'leaderboards-rivals-overview', slug: 'leaderboards-rivals-overview', titleKey: 'overview' }],
    subsections: [
      { id: 'leaderboards-full', translationKey: 'fullRankings', copyKeys: ['body'], carousels: [{ id: 'leaderboards-full', slug: 'leaderboards-full', titleKey: 'fullRankings' }] },
      { id: 'leaderboards-metrics', translationKey: 'metrics', copyKeys: ['body'], carousels: [{ id: 'leaderboards-metrics', slug: 'leaderboards-metrics', titleKey: 'metrics' }] },
      { id: 'rivals-solo', translationKey: 'rivalsSolo', copyKeys: ['body'], carousels: [{ id: 'rivals-solo', slug: 'rivals-solo', titleKey: 'rivalsSolo' }] },
    ],
  },
  {
    id: 'shop', translationKey: 'shop', icon: 'shop', copyKeys: ['intro', 'context'], carousels: [{ id: 'shop-overview', slug: 'shop-overview', titleKey: 'overview' }],
    subsections: [
      { id: 'shop-grid-list', translationKey: 'gridList', copyKeys: ['body'], carousels: [{ id: 'shop-grid-list', slug: 'shop-grid-list', titleKey: 'gridList' }] },
      { id: 'shop-badges', translationKey: 'badges', copyKeys: ['body'], carousels: [{ id: 'shop-badges', slug: 'shop-badges', titleKey: 'badges' }] },
      { id: 'shop-settings', translationKey: 'settings', copyKeys: ['body'], carousels: [{ id: 'shop-settings', slug: 'shop-settings', titleKey: 'settings' }] },
    ],
  },
  {
    id: 'settings', translationKey: 'settings', icon: 'settings', copyKeys: ['intro', 'context'], carousels: [{ id: 'settings-overview', slug: 'settings-overview', titleKey: 'overview' }],
    subsections: [
      { id: 'settings-instruments', translationKey: 'instruments', copyKeys: ['body'], carousels: [{ id: 'settings-instruments', slug: 'settings-instruments', titleKey: 'instruments' }] },
      { id: 'settings-paths', translationKey: 'paths', copyKeys: ['body'], carousels: [{ id: 'settings-paths', slug: 'settings-paths', titleKey: 'paths' }] },
      { id: 'settings-preferences', translationKey: 'preferences', copyKeys: ['body'], carousels: [{ id: 'settings-preferences', slug: 'settings-preferences', titleKey: 'preferences' }] },
    ],
  },
];

function buildSlides(slug: string): ScreenshotSlide[] {
  const baseUrl = `${import.meta.env.BASE_URL}manual/screenshots`;
  return VIEWPORTS.map(viewport => ({ viewport, src: `${baseUrl}/${slug}-${viewport}.png` }));
}

function sectionTitleKey(section: ManualSection) {
  return `appManual.sections.${section.translationKey}.title`;
}

function sectionCopyKey(section: ManualSection, copyKey: string) {
  return `appManual.sections.${section.translationKey}.copy.${copyKey}`;
}

function subsectionTitleKey(section: ManualSection, subsection: ManualSubsection) {
  return `appManual.sections.${section.translationKey}.subsections.${subsection.translationKey}.title`;
}

function subsectionCopyKey(section: ManualSection, subsection: ManualSubsection, copyKey: string) {
  return `appManual.sections.${section.translationKey}.subsections.${subsection.translationKey}.copy.${copyKey}`;
}

function carouselTitleKey(section: ManualSection, titleKey: string) {
  return `appManual.sections.${section.translationKey}.carousels.${titleKey}`;
}

function subsectionCarouselTitleKey(section: ManualSection, subsection: ManualSubsection, titleKey: string) {
  return `appManual.sections.${section.translationKey}.subsections.${subsection.translationKey}.carousels.${titleKey}`;
}

export default function ManualPage() {
  const { t } = useTranslation();
  const styles = useStyles();
  const scrollContainerRef = useScrollContainer();
  const isWideDesktop = useIsWideDesktop();
  const isMobileChrome = useIsMobileChrome();

  const quickLinkItems = useMemo<PageQuickLinkItem[]>(() => MANUAL_SECTIONS.flatMap(section => {
    const sectionTitle = t(sectionTitleKey(section));
    return [
      {
        id: section.id,
        label: sectionTitle,
        landmarkLabel: sectionTitle,
        icon: renderManualIcon(section.icon, Size.iconSm),
      },
      ...section.subsections.map(subsection => {
        const subsectionTitle = t(subsectionTitleKey(section, subsection));
        return {
          id: subsection.id,
          label: subsectionTitle,
          landmarkLabel: `${sectionTitle}: ${subsectionTitle}`,
          depth: 1,
        } satisfies PageQuickLinkItem;
      }),
    ];
  }), [t]);

  const quickLinksState = usePageQuickLinks({
    items: quickLinkItems,
    scrollContainerRef,
    isDesktopRailEnabled: isWideDesktop,
  });

  const quickLinks = {
    title: t('appManual.quickLinks'),
    items: quickLinkItems,
    activeItemId: quickLinksState.activeItemId,
    visible: quickLinksState.quickLinksOpen,
    onOpen: quickLinksState.openQuickLinks,
    onClose: quickLinksState.closeQuickLinks,
    onSelect: quickLinksState.handleQuickLinkSelect,
    testIdPrefix: 'manual',
  };

  const compactQuickLinksAction = !isWideDesktop && !isMobileChrome
    ? <ActionPill icon={<IoCompass size={Size.iconAction} />} label={t('appManual.quickLinks')} onClick={quickLinksState.openQuickLinks} />
    : undefined;
  const pageHeader = !isMobileChrome
    ? <PageHeader title={t('appManual.title')} actions={compactQuickLinksAction} />
    : undefined;

  return (
    <Page
      scrollRestoreKey="manual"
      containerStyle={styles.container}
      before={pageHeader}
      quickLinks={quickLinks}
    >
      <div style={styles.sectionStack}>
        {MANUAL_SECTIONS.map(section => (
          <ManualSectionBlock
            key={section.id}
            section={section}
            registerSectionRef={quickLinksState.registerSectionRef}
          />
        ))}
      </div>
    </Page>
  );
}

function ManualSectionBlock({ section, registerSectionRef }: { section: ManualSection; registerSectionRef: (id: string, element: HTMLElement | null) => void }) {
  const { t } = useTranslation();
  const styles = useStyles();
  const title = t(sectionTitleKey(section));

  return (
    <section ref={(element) => registerSectionRef(section.id, element)} data-testid={`manual-section-${section.id}`} aria-labelledby={`manual-heading-${section.id}`} style={styles.section}>
      <div style={styles.sectionHeaderBlock}>
        <div style={styles.sectionTitleRow}>
          <span data-testid={`manual-section-icon-${section.id}`} style={styles.sectionIcon} aria-hidden="true">{renderManualIcon(section.icon, SECTION_ICON_SIZE)}</span>
          <h2 id={`manual-heading-${section.id}`} style={styles.sectionTitle}>{title}</h2>
        </div>
        <ManualParagraphs keysList={section.copyKeys} resolveKey={(copyKey) => sectionCopyKey(section, copyKey)} style={styles.sectionParagraph} />
      </div>
      <ManualCarouselGroup section={section} carousels={section.carousels} titleResolver={(carousel) => carouselTitleKey(section, carousel.titleKey)} />
      <div style={styles.subsectionStack}>
        {section.subsections.map(subsection => (
          <ManualSubsectionBlock
            key={subsection.id}
            section={section}
            subsection={subsection}
            registerSectionRef={registerSectionRef}
          />
        ))}
      </div>
    </section>
  );
}

function ManualSubsectionBlock({ section, subsection, registerSectionRef }: { section: ManualSection; subsection: ManualSubsection; registerSectionRef: (id: string, element: HTMLElement | null) => void }) {
  const { t } = useTranslation();
  const styles = useStyles();
  const title = t(subsectionTitleKey(section, subsection));

  return (
    <section ref={(element) => registerSectionRef(subsection.id, element)} data-testid={`manual-subsection-${subsection.id}`} aria-labelledby={`manual-heading-${subsection.id}`} style={styles.subsection}>
      <h3 id={`manual-heading-${subsection.id}`} style={styles.subsectionTitle}>{title}</h3>
      <ManualParagraphs keysList={subsection.copyKeys} resolveKey={(copyKey) => subsectionCopyKey(section, subsection, copyKey)} style={styles.subsectionParagraph} />
      <ManualCarouselGroup section={section} carousels={subsection.carousels} titleResolver={(carousel) => subsectionCarouselTitleKey(section, subsection, carousel.titleKey)} />
    </section>
  );
}

function ManualParagraphs({ keysList, resolveKey, style }: { keysList: string[]; resolveKey: (key: string) => string; style: CSSProperties }) {
  const { t } = useTranslation();
  return (
    <>
      {keysList.map(copyKey => <p key={copyKey} style={style}>{t(resolveKey(copyKey))}</p>)}
    </>
  );
}

function ManualCarouselGroup({ section, carousels, titleResolver }: { section: ManualSection; carousels: ManualCarousel[]; titleResolver: (carousel: ManualCarousel) => string }) {
  const styles = useStyles();
  return (
    <div style={styles.carouselGroup}>
      {carousels.map(carousel => (
        <ScreenshotCarousel
          key={carousel.id}
          carouselId={carousel.id}
          titleKey={titleResolver(carousel)}
          slides={buildSlides(carousel.slug)}
          sectionTitleKey={sectionTitleKey(section)}
        />
      ))}
    </div>
  );
}

function ScreenshotCarousel({ carouselId, titleKey, slides, sectionTitleKey }: { carouselId: string; titleKey: string; slides: ScreenshotSlide[]; sectionTitleKey: string }) {
  const { t } = useTranslation();
  const styles = useStyles();
  const [index, setIndex] = useState(0);
  const [loadedSrc, setLoadedSrc] = useState<string | null>(null);
  const slide = slides[index] ?? slides[0]!;
  const viewportLabel = t(`appManual.viewports.${slide.viewport}`);
  const title = t(titleKey);
  const altText = t('appManual.screenshotAlt', { topic: title, viewport: viewportLabel });

  const goPrevious = useCallback(() => {
    setLoadedSrc(null);
    setIndex(current => (current === 0 ? slides.length - 1 : current - 1));
  }, [slides.length]);
  const goNext = useCallback(() => {
    setLoadedSrc(null);
    setIndex(current => (current + 1) % slides.length);
  }, [slides.length]);
  const previousPressHandlers = usePressAction<HTMLButtonElement>({ onPress: goPrevious });
  const nextPressHandlers = usePressAction<HTMLButtonElement>({ onPress: goNext });
  const { handleTouchStart, handleTouchEnd } = useSwipeNavigation({ onBack: goPrevious, onForward: goNext });

  return (
    <div style={styles.carousel} data-testid={`manual-carousel-${carouselId}`} data-section={sectionTitleKey}>
      <div style={styles.carouselHeader}>
        <span style={styles.carouselTitle}>{title}</span>
        <span style={styles.viewportPill}>{viewportLabel}</span>
      </div>
      <div style={styles.screenshotFrame} onTouchStart={handleTouchStart} onTouchEnd={handleTouchEnd} data-testid={`manual-carousel-frame-${carouselId}`}>
        <div style={loadedSrc === slide.src ? styles.hiddenScreenshotPlaceholder : styles.screenshotPlaceholder} aria-hidden={loadedSrc === slide.src ? 'true' : undefined}>
          <span style={styles.placeholderTitle}>{title}</span>
          <span style={styles.placeholderHint}>{t('appManual.screenshotPending')}</span>
        </div>
        <img
          src={slide.src}
          alt={altText}
          onLoad={() => setLoadedSrc(slide.src)}
          onError={() => setLoadedSrc(null)}
          style={loadedSrc === slide.src ? styles.screenshotImage : styles.hiddenScreenshotImage}
        />
      </div>
      <div style={styles.carouselControls}>
        <button type="button" style={styles.carouselButton} {...previousPressHandlers} aria-label={t('appManual.previousScreenshot')}>
          <IoChevronBack size={Size.iconSm} />
        </button>
        <span style={styles.carouselLabel}>{t('appManual.screenshotCaption', { topic: title, viewport: viewportLabel })}</span>
        <button type="button" style={styles.carouselButton} {...nextPressHandlers} aria-label={t('appManual.nextScreenshot')}>
          <IoChevronForward size={Size.iconSm} />
        </button>
      </div>
    </div>
  );
}

function renderManualIcon(icon: ManualIcon, size: number): ReactNode {
  switch (icon) {
    case 'navigation': return <IoCompass size={size} />;
    case 'songs': return <IoMusicalNotes size={size} />;
    case 'profiles': return <IoPeople size={size} />;
    case 'player': return <IoStatsChart size={size} />;
    case 'band': return <IoPeople size={size} />;
    case 'detail': return <IoMusicalNotes size={size} />;
    case 'sync': return <IoSync size={size} />;
    case 'suggestions': return <IoSparkles size={size} />;
    case 'compete': return <IoPhonePortrait size={size} />;
    case 'leaderboards': return <IoTrophy size={size} />;
    case 'shop': return <IoBagHandle size={size} />;
    case 'settings': return <IoSettings size={size} />;
  }
}

function useStyles() {
  return useMemo(() => ({
    container: {
      maxWidth: MaxWidth.card,
      paddingBottom: Layout.paddingTop,
    } as CSSProperties,
    sectionStack: {
      ...flexColumn,
      gap: Gap.section * 2,
    } as CSSProperties,
    section: {
      ...flexColumn,
      gap: Gap.section,
      paddingTop: Gap.lg,
      scrollMarginTop: Gap.section,
    } as CSSProperties,
    sectionHeaderBlock: {
      ...flexColumn,
      gap: Gap.md,
      minWidth: 0,
      maxWidth: 980,
    } as CSSProperties,
    sectionTitleRow: {
      ...flexRow,
      alignItems: Align.center,
      gap: Gap.lg,
      minWidth: 0,
    } as CSSProperties,
    sectionIcon: {
      ...flexCenter,
      width: SECTION_ICON_SLOT_SIZE,
      minWidth: SECTION_ICON_SLOT_SIZE,
      height: SECTION_ICON_SLOT_SIZE,
      color: Colors.textPrimary,
      lineHeight: 0,
    } as CSSProperties,
    sectionTitle: {
      margin: Gap.none,
      color: Colors.textPrimary,
      fontSize: Font.xl,
      fontWeight: Weight.bold,
      lineHeight: LineHeight.snug,
      overflowWrap: 'anywhere',
    } as CSSProperties,
    sectionParagraph: {
      margin: Gap.none,
      color: Colors.textPrimary,
      fontSize: Font.md,
      lineHeight: LineHeight.relaxed,
      overflowWrap: 'anywhere',
    } as CSSProperties,
    subsectionStack: {
      ...flexColumn,
      gap: Gap.section,
    } as CSSProperties,
    subsection: {
      ...flexColumn,
      gap: Gap.md,
      paddingLeft: Gap.none,
      scrollMarginTop: Gap.section,
    } as CSSProperties,
    subsectionTitle: {
      margin: Gap.none,
      color: Colors.textPrimary,
      fontSize: Font.lg,
      fontWeight: Weight.semibold,
      lineHeight: LineHeight.snug,
      overflowWrap: 'anywhere',
    } as CSSProperties,
    subsectionParagraph: {
      maxWidth: 920,
      margin: Gap.none,
      color: Colors.textPrimary,
      fontSize: Font.md,
      lineHeight: LineHeight.relaxed,
      overflowWrap: 'anywhere',
    } as CSSProperties,
    carouselGroup: {
      display: Display.grid,
      gridTemplateColumns: 'repeat(auto-fit, minmax(min(100%, 320px), 1fr))',
      gap: Gap.lg,
      alignItems: Align.center,
      width: CssValue.full,
      maxWidth: 1080,
      margin: CssValue.marginCenter,
    } as CSSProperties,
    carousel: {
      ...flexColumn,
      gap: Gap.md,
      minWidth: 0,
      width: CssValue.full,
      maxWidth: 780,
      margin: CssValue.marginCenter,
    } as CSSProperties,
    carouselHeader: {
      ...flexBetween,
      gap: Gap.md,
      minWidth: 0,
    } as CSSProperties,
    carouselTitle: {
      minWidth: 0,
      color: Colors.textPrimary,
      fontSize: Font.sm,
      fontWeight: Weight.semibold,
      lineHeight: LineHeight.snug,
      overflow: Overflow.hidden,
      textOverflow: 'ellipsis',
      whiteSpace: 'nowrap',
    } as CSSProperties,
    screenshotFrame: {
      position: 'relative',
      width: CssValue.full,
      aspectRatio: '16 / 10',
      minHeight: 180,
      maxHeight: 540,
      overflow: Overflow.hidden,
      borderRadius: Radius.sm,
      border: border(Border.thin, Colors.borderSubtle),
      backgroundColor: Colors.backgroundApp,
      boxSizing: BoxSizing.borderBox,
      touchAction: 'pan-y',
    } as CSSProperties,
    screenshotPlaceholder: {
      position: 'absolute',
      inset: 0,
      ...flexColumn,
      alignItems: Align.center,
      justifyContent: 'center',
      gap: Gap.sm,
      padding: padding(Gap.xl),
      textAlign: TextAlign.center,
      color: Colors.textPrimary,
      backgroundColor: Colors.surfaceMuted,
    } as CSSProperties,
    hiddenScreenshotPlaceholder: {
      display: 'none',
    } as CSSProperties,
    viewportPill: {
      flexShrink: 0,
      padding: padding(Gap.xs, Gap.md),
      borderRadius: Radius.xs,
      color: Colors.textPrimary,
      backgroundColor: Colors.surfaceMuted,
      border: border(Border.thin, Colors.borderSubtle),
      fontSize: Font.xs,
      fontWeight: Weight.bold,
      lineHeight: LineHeight.snug,
    } as CSSProperties,
    placeholderTitle: {
      maxWidth: CssValue.full,
      color: Colors.textPrimary,
      fontSize: Font.lg,
      fontWeight: Weight.semibold,
      lineHeight: LineHeight.snug,
      overflowWrap: 'anywhere',
    } as CSSProperties,
    placeholderHint: {
      color: Colors.textPrimary,
      fontSize: Font.sm,
      lineHeight: LineHeight.snug,
    } as CSSProperties,
    screenshotImage: {
      position: 'absolute',
      inset: 0,
      width: CssValue.full,
      height: CssValue.full,
      objectFit: ObjectFit.contain,
      display: 'block',
    } as CSSProperties,
    hiddenScreenshotImage: {
      display: 'none',
    } as CSSProperties,
    carouselControls: {
      ...flexBetween,
      gap: Gap.md,
    } as CSSProperties,
    carouselButton: {
      ...flexCenter,
      width: Layout.pillButtonHeight,
      height: Layout.pillButtonHeight,
      borderRadius: Radius.full,
      flexShrink: 0,
      border: border(Border.thin, Colors.borderPrimary),
      backgroundColor: Colors.surfaceElevated,
      color: Colors.textPrimary,
      cursor: 'pointer',
    } as CSSProperties,
    carouselLabel: {
      minWidth: 0,
      flex: '1 1 auto',
      color: Colors.textPrimary,
      fontSize: Font.sm,
      lineHeight: LineHeight.snug,
      textAlign: TextAlign.center,
      overflow: Overflow.hidden,
      textOverflow: 'ellipsis',
      whiteSpace: 'nowrap',
    } as CSSProperties,
  }), []);
}