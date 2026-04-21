/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useCallback, useEffect, useMemo, useRef, useState, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { useSettings } from '../../contexts/SettingsContext';
import { useFeatureFlags } from '../../contexts/FeatureFlagsContext';
import { useScrollContainer } from '../../contexts/ScrollContainerContext';
import { useIsMobile, useIsMobileChrome, useIsWideDesktop } from '../../hooks/ui/useIsMobile';
import { useMediaQuery } from '../../hooks/ui/useMediaQuery';
import { ToggleRow } from '../../components/common/ToggleRow';
import { RadioRow } from '../../components/common/RadioRow';
import SectionHeader from '../../components/common/SectionHeader';
import { ReorderList } from '../../components/sort/ReorderList';
import { METADATA_SORT_DISPLAY } from '../../utils/songSettings';
import { type ColumnKey } from '../songinfo/components/path/PathDataTable';
import ConfirmAlert from '../../components/modals/ConfirmAlert';
import { modalStyles as modalCss } from '../../components/modals/modalStyles';
import { InstrumentIcon } from '../../components/display/InstrumentIcons';
import { ActionPill } from '../../components/common/ActionPill';
import type { ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import { Colors, Font, Gap, Weight, Radius, Layout, Size, Display, Align, Overflow, CssValue, LineHeight, TextAlign, btnDanger, btnPrimary, flexColumn, flexBetween, padding, transition, CssProp, FAST_FADE_MS, STAGGER_INTERVAL, FADE_DURATION, QUERY_NARROW_GRID } from '@festival/theme';
import { useRegisterFirstRun } from '../../hooks/ui/useRegisterFirstRun';
import { useFirstRunReplay } from '../../hooks/ui/useFirstRun';
import { FrostedCard } from '../../components/common/FrostedCard';
import FirstRunCarousel from '../../components/firstRun/FirstRunCarousel';
import { statisticsSlides } from '../player/firstRun';
import { suggestionsSlides } from '../suggestions/firstRun';
import { songSlides } from '../songs/firstRun';
import { songInfoSlides } from '../songinfo/firstRun';
import { playerHistorySlides } from '../leaderboard/player/firstRun';
import { leaderboardsSlides } from '../leaderboards/firstRun';
import { competeSlides } from '../compete/firstRun';
import { rivalsSlides } from '../rivals/firstRun';
import { shopSlides } from '../shop/firstRun';
import { api } from '../../api/client';
import Page from '../Page';
import PageHeader from '../../components/common/PageHeader';
import PageHeaderTransition from '../../components/common/PageHeaderTransition';
import type { PageQuickLinksConfig } from '../../components/page/PageQuickLinks';
import { usePageQuickLinks, type PageQuickLinkItem } from '../../hooks/ui/usePageQuickLinks';
import { IoCompass } from 'react-icons/io5';

import { APP_VERSION, CORE_VERSION, THEME_VERSION } from '../../hooks/data/useVersions';

/** Track whether settings page has rendered at least once to skip stagger on re-visit. */
let _hasRendered = false;

type SettingsQuickLinkId = 'app-settings' | 'item-shop' | 'show-instruments' | 'show-metadata' | 'version' | 'first-run' | 'reset';

type SettingsQuickLink = PageQuickLinkItem & {
  id: SettingsQuickLinkId;
};

function FadeInDiv({ delay, children, style }: { delay?: number; children: React.ReactNode; style?: CSSProperties }) {
  const ref = useRef<HTMLDivElement>(null);
  /* v8 ignore start — animation cleanup */
  const handleEnd = useCallback(() => {
    const el = ref.current;
    if (!el) return;
    el.style.opacity = '';
    el.style.animation = '';
  }, []);
  /* v8 ignore stop */
  if (delay == null) return <div style={style}>{children}</div>;
  return (
    <div
      ref={ref}
      style={{ opacity: 0, animation: `fadeInUp 400ms ease-out ${delay}ms forwards`, ...style }}
      onAnimationEnd={handleEnd}
    >
      {children}
    </div>
  );
}

/* -- Instrument toggle helpers -- */

type ShowKey = 'showLead' | 'showBass' | 'showDrums' | 'showVocals' | 'showProLead' | 'showProBass' | 'showPeripheralVocals' | 'showPeripheralCymbals' | 'showPeripheralDrums';

const INSTRUMENT_SHOW_MAP: { key: InstrumentKey; showKey: ShowKey; i18nKey: string }[] = [
  { key: 'Solo_Guitar', showKey: 'showLead', i18nKey: 'instruments.lead' },
  { key: 'Solo_Bass', showKey: 'showBass', i18nKey: 'instruments.bass' },
  { key: 'Solo_Drums', showKey: 'showDrums', i18nKey: 'instruments.drums' },
  { key: 'Solo_Vocals', showKey: 'showVocals', i18nKey: 'instruments.vocals' },
  { key: 'Solo_PeripheralGuitar', showKey: 'showProLead', i18nKey: 'instruments.proLead' },
  { key: 'Solo_PeripheralBass', showKey: 'showProBass', i18nKey: 'instruments.proBass' },
  { key: 'Solo_PeripheralVocals', showKey: 'showPeripheralVocals', i18nKey: 'instruments.peripheralVocals' },
  { key: 'Solo_PeripheralCymbals', showKey: 'showPeripheralCymbals', i18nKey: 'instruments.peripheralCymbals' },
  { key: 'Solo_PeripheralDrums', showKey: 'showPeripheralDrums', i18nKey: 'instruments.peripheralDrums' },
];

type MetadataKey =
  | 'metadataShowScore'
  | 'metadataShowPercentage'
  | 'metadataShowPercentile'
  | 'metadataShowSeasonAchieved'
  | 'metadataShowIntensity'
  | 'metadataShowGameDifficulty'
  | 'metadataShowStars'
  | 'metadataShowLastPlayed';

const METADATA_TOGGLES: { key: MetadataKey; i18nKey: string }[] = [
  { key: 'metadataShowScore', i18nKey: 'metadata.score' },
  { key: 'metadataShowPercentage', i18nKey: 'metadata.percentage' },
  { key: 'metadataShowPercentile', i18nKey: 'metadata.percentile' },
  { key: 'metadataShowSeasonAchieved', i18nKey: 'metadata.seasonAchieved' },
  { key: 'metadataShowIntensity', i18nKey: 'metadata.intensity' },
  { key: 'metadataShowGameDifficulty', i18nKey: 'metadata.difficulty' },
  { key: 'metadataShowStars', i18nKey: 'metadata.stars' },
  { key: 'metadataShowLastPlayed', i18nKey: 'metadata.lastPlayed' },
];

const LEEWAY_SLIDER_ID = 'fst-leeway-slider';
const leewaySliderCss = `
#${LEEWAY_SLIDER_ID} {
  -webkit-appearance: none;
  appearance: none;
  width: 100%;
  height: 6px;
  border-radius: 3px;
  outline: none;
  cursor: pointer;
}
#${LEEWAY_SLIDER_ID}::-webkit-slider-thumb {
  -webkit-appearance: none;
  appearance: none;
  width: 20px;
  height: 20px;
  border-radius: 50%;
  background: ${Colors.textPrimary};
  border: 2px solid ${Colors.accentBlue};
  cursor: pointer;
  box-shadow: 0 0 4px rgba(0,0,0,0.3);
}
#${LEEWAY_SLIDER_ID}::-moz-range-thumb {
  width: 20px;
  height: 20px;
  border-radius: 50%;
  background: ${Colors.textPrimary};
  border: 2px solid ${Colors.accentBlue};
  cursor: pointer;
  box-shadow: 0 0 4px rgba(0,0,0,0.3);
}
`;

function leewayTrackBackground(value: number): string {
  const pct = ((value - (-5)) / (5 - (-5))) * 100;
  return `linear-gradient(90deg, ${Colors.accentPurple} 0%, ${Colors.accentBlue} ${pct}%, ${Colors.surfaceMuted} ${pct}%)`;
}

/* v8 ignore start — LeewaySlider: DOM style injection not testable in jsdom */
function LeewaySlider({ value, onChange }: { value: number; onChange: (v: number) => void }) {
  const styleRef = useRef<HTMLStyleElement | null>(null);

  useEffect(() => {
    if (!styleRef.current) {
      const el = document.createElement('style');
      el.textContent = leewaySliderCss;
      document.head.appendChild(el);
      styleRef.current = el;
    }
    return () => {
      if (styleRef.current) {
        styleRef.current.remove();
        styleRef.current = null;
      }
    };
  }, []);

  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: Gap.md }}>
      <input
        id={LEEWAY_SLIDER_ID}
        type="range"
        min={-5}
        max={5}
        step={0.1}
        value={value}
        onChange={e => onChange(Math.round(parseFloat(e.target.value) * 10) / 10)}
        style={{ flex: 1, background: leewayTrackBackground(value) }}
      />
      <span style={{ minWidth: 48, textAlign: 'right', fontSize: Font.md, color: Colors.textSecondary, fontVariantNumeric: 'tabular-nums' }}>
        {value > 0 ? '+' : ''}{value.toFixed(1)}%
      </span>
    </div>
  );
}
/* v8 ignore stop */

export default function SettingsPage() {
  const { t } = useTranslation();
  const { settings, updateSettings, resetSettings } = useSettings();
  const flags = useFeatureFlags();
  const isMobile = useIsMobile();
  const isMobileChrome = useIsMobileChrome();
  const isWideDesktop = useIsWideDesktop();
  const scrollContainerRef = useScrollContainer();
  const isNarrowGrid = useMediaQuery(QUERY_NARROW_GRID);
  const [showResetConfirm, setShowResetConfirm] = useState(false);

  // Register first-run slides so replay is always available from Settings
  /* v8 ignore next */
  const songsSlidesMemo = useMemo(() => songSlides(isMobileChrome), [isMobileChrome]);
  const songInfoSlidesMemo = useMemo(() => songInfoSlides(isMobileChrome), [isMobileChrome]);
  const playerHistorySlidesMemo = useMemo(() => playerHistorySlides(isMobileChrome), [isMobileChrome]);
  const statisticsSlidesMemo = useMemo(() => statisticsSlides(isMobileChrome), [isMobileChrome]);
  const shopSlidesMemo = useMemo(() => shopSlides({ viewToggleAvailable: !isNarrowGrid }), [isNarrowGrid]);
  useRegisterFirstRun('songs', t('nav.songs'), songsSlidesMemo);
  useRegisterFirstRun('songinfo', t('nav.songInfo', 'Song Info'), songInfoSlidesMemo);
  useRegisterFirstRun('playerhistory', t('history.title'), playerHistorySlidesMemo);
  useRegisterFirstRun('statistics', t('nav.statistics'), statisticsSlidesMemo);
  useRegisterFirstRun('suggestions', t('nav.suggestions'), suggestionsSlides);
  useRegisterFirstRun('leaderboards', t('nav.leaderboards'), leaderboardsSlides);
  useRegisterFirstRun('compete', t('nav.compete'), competeSlides);
  useRegisterFirstRun('rivals', t('rivals.title'), rivalsSlides);
  useRegisterFirstRun('shop', t('nav.shop'), shopSlidesMemo);
  const songsReplay = useFirstRunReplay('songs');
  const songInfoReplay = useFirstRunReplay('songinfo');
  const statsReplay = useFirstRunReplay('statistics');
  const suggestionsReplay = useFirstRunReplay('suggestions');
  const playerHistoryReplay = useFirstRunReplay('playerhistory');
  const leaderboardsReplay = useFirstRunReplay('leaderboards');
  const competeReplay = useFirstRunReplay('compete');
  const rivalsReplay = useFirstRunReplay('rivals');
  const shopReplay = useFirstRunReplay('shop');
  const [serviceVersion, setServiceVersion] = useState<string | null>(null);
  // Skip stagger on revisit
  const skipAnimRef = useRef(_hasRendered);
  _hasRendered = true;

  const st = useSettingsStyles(isMobile, settings.filterInvalidScores, settings.songRowVisualOrderEnabled);

  /* v8 ignore start — version fetch + settings callbacks */
  useEffect(() => {
    let cancelled = false;
    api.getVersion()
      .then(data => { if (!cancelled) setServiceVersion(data.version); })
      .catch(() => { /* service unreachable */ });
    return () => { cancelled = true; };
  }, []);
  /* v8 ignore stop */

  const showActiveCount = INSTRUMENT_SHOW_MAP.filter(i => settings[i.showKey]).length;

  const toggleShow = useCallback(
    (showKey: ShowKey) => {
      updateSettings({ [showKey]: !settings[showKey] });
    },
    [settings, updateSettings],
  );

  const toggleMetadata = useCallback(
    (key: MetadataKey) => {
      updateSettings({ [key]: !settings[key] });
    },
    [settings, updateSettings],
  );

  /* v8 ignore start — presentation-only metadata display mapping */
  const hiddenMetadataKeys = useMemo(() => {
    const hidden = new Set<string>();
    if (!settings.metadataShowScore) hidden.add('score');
    if (!settings.metadataShowPercentage) hidden.add('percentage');
    if (!settings.metadataShowPercentile) hidden.add('percentile');
    if (!settings.metadataShowSeasonAchieved) hidden.add('seasonachieved');
    if (!settings.metadataShowIntensity) hidden.add('intensity');
    if (!settings.metadataShowGameDifficulty) hidden.add('difficulty');
    if (!settings.metadataShowStars) hidden.add('stars');
    return hidden;
  }, [settings.metadataShowScore, settings.metadataShowPercentage, settings.metadataShowPercentile, settings.metadataShowSeasonAchieved, settings.metadataShowIntensity, settings.metadataShowGameDifficulty, settings.metadataShowStars]);

  const visualOrderItems = useMemo(
    () =>
      settings.songRowVisualOrder
        .filter(k => !hiddenMetadataKeys.has(k))
        .map(k => ({
          key: k,
          label: METADATA_SORT_DISPLAY[k] ?? k,
        })),
    [settings.songRowVisualOrder, hiddenMetadataKeys],
  );
  /* v8 ignore stop */

  const PATH_COLUMN_LABELS: Record<ColumnKey, string> = {
    note: t('paths.colNote'),
    beat: t('paths.colBeat'),
    time: t('paths.colTime'),
    od: t('paths.colOd'),
    score: t('paths.colScore'),
  };

  const pathColumnOrderItems = useMemo(
    () =>
      settings.pathColumnOrder.map(k => ({
        key: k,
        label: PATH_COLUMN_LABELS[k] ?? k,
      })),
    // eslint-disable-next-line react-hooks/exhaustive-deps -- PATH_COLUMN_LABELS is stable per render
    [settings.pathColumnOrder],
  );

  const settingsQuickLinksTitle = t('settings.quickLinks');
  const quickLinkItems = useMemo<SettingsQuickLink[]>(() => ([
    { id: 'app-settings', label: t('settings.appSettings'), landmarkLabel: t('settings.appSettings') },
    { id: 'item-shop', label: t('settings.itemShop', 'Item Shop'), landmarkLabel: t('settings.itemShop', 'Item Shop') },
    { id: 'show-instruments', label: t('settings.showInstruments'), landmarkLabel: t('settings.showInstruments') },
    { id: 'show-metadata', label: t('settings.showMetadata'), landmarkLabel: t('settings.showMetadata') },
    { id: 'version', label: t('settings.versionTitle'), landmarkLabel: t('settings.versionTitle') },
    { id: 'first-run', label: t('firstRun.settings.showFirstRunTitle'), landmarkLabel: t('firstRun.settings.showFirstRunTitle') },
    { id: 'reset', label: t('settings.resetSection'), landmarkLabel: t('settings.resetSection') },
  ]), [settingsQuickLinksTitle, t]);

  const {
    activeItemId,
    quickLinksOpen,
    openQuickLinks,
    closeQuickLinks,
    handleQuickLinkSelect,
    registerSectionRef,
  } = usePageQuickLinks<SettingsQuickLink>({
    items: quickLinkItems,
    scrollContainerRef,
    isDesktopRailEnabled: isWideDesktop,
    scrollOffset: Gap.md,
  });

  const handleModalQuickLinkSelect = useCallback((link: SettingsQuickLink) => {
    closeQuickLinks();
    handleQuickLinkSelect(link);
  }, [closeQuickLinks, handleQuickLinkSelect]);

  const pageQuickLinks = useMemo<PageQuickLinksConfig | undefined>(() => {
    if (quickLinkItems.length < 2) {
      return undefined;
    }

    return {
      title: settingsQuickLinksTitle,
      items: quickLinkItems,
      activeItemId,
      visible: quickLinksOpen,
      onOpen: openQuickLinks,
      onClose: closeQuickLinks,
      onSelect: (item) => {
        const nextItem = item as SettingsQuickLink;
        if (isWideDesktop) {
          handleQuickLinkSelect(nextItem);
          return;
        }
        handleModalQuickLinkSelect(nextItem);
      },
      testIdPrefix: 'settings',
    };
  }, [activeItemId, closeQuickLinks, handleModalQuickLinkSelect, handleQuickLinkSelect, isWideDesktop, openQuickLinks, quickLinkItems, quickLinksOpen, settingsQuickLinksTitle]);

  const compactQuickLinksAction = !isWideDesktop && pageQuickLinks
    ? (
      <ActionPill
        icon={<IoCompass size={Size.iconAction} />}
        label={settingsQuickLinksTitle}
        onClick={openQuickLinks}
      />
    )
    : undefined;

  let staggerIndex = 0;
  const stagger = (idx: number): number | undefined => skipAnimRef.current ? undefined : idx * STAGGER_INTERVAL;
  const headerStagger: CSSProperties = !skipAnimRef.current
    ? { opacity: 0, animation: `fadeInUp ${FADE_DURATION}ms ease-out forwards` }
    : {};
  const showMobilePageHeader = !isMobile || settings.showButtonsInHeaderMobile;
  const settingsHeader = <PageHeader title={isMobile ? undefined : t('settings.title')} style={isMobile ? undefined : headerStagger} actions={compactQuickLinksAction} />;

  return (
    <Page
      scrollRestoreKey="settings"
      containerStyle={st.container}
      quickLinks={pageQuickLinks}
      before={isMobile
        ? <PageHeaderTransition visible={showMobilePageHeader}>{settingsHeader}</PageHeaderTransition>
        : settingsHeader}
      after={<>
        {showResetConfirm && (
          /* v8 ignore start — confirm dialog callbacks */
          <ConfirmAlert
            title={t('settings.resetConfirmTitle')}
            message={t('settings.resetConfirmMessage')}
            onNo={() => setShowResetConfirm(false)}
            onYes={() => resetSettings()}
            onExitComplete={() => setShowResetConfirm(false)}
          />
          /* v8 ignore stop */
        )}
        {songsReplay.show && <FirstRunCarousel slides={songsReplay.slides} onDismiss={songsReplay.dismiss} onExitComplete={songsReplay.onExitComplete} />}
        {songInfoReplay.show && <FirstRunCarousel slides={songInfoReplay.slides} onDismiss={songInfoReplay.dismiss} onExitComplete={songInfoReplay.onExitComplete} />}
        {statsReplay.show && <FirstRunCarousel slides={statsReplay.slides} onDismiss={statsReplay.dismiss} onExitComplete={statsReplay.onExitComplete} />}
        {suggestionsReplay.show && <FirstRunCarousel slides={suggestionsReplay.slides} onDismiss={suggestionsReplay.dismiss} onExitComplete={suggestionsReplay.onExitComplete} />}
        {playerHistoryReplay.show && <FirstRunCarousel slides={playerHistoryReplay.slides} onDismiss={playerHistoryReplay.dismiss} onExitComplete={playerHistoryReplay.onExitComplete} />}
        {leaderboardsReplay.show && <FirstRunCarousel slides={leaderboardsReplay.slides} onDismiss={leaderboardsReplay.dismiss} onExitComplete={leaderboardsReplay.onExitComplete} />}
        {competeReplay.show && <FirstRunCarousel slides={competeReplay.slides} onDismiss={competeReplay.dismiss} onExitComplete={competeReplay.onExitComplete} />}
        {rivalsReplay.show && <FirstRunCarousel slides={rivalsReplay.slides} onDismiss={rivalsReplay.dismiss} onExitComplete={rivalsReplay.onExitComplete} />}
        {shopReplay.show && <FirstRunCarousel slides={shopReplay.slides} onDismiss={shopReplay.dismiss} onExitComplete={shopReplay.onExitComplete} />}
      </>}
    >
      <div style={st.cardColumn}>

          {/* ── App Settings ── */}
          <FadeInDiv delay={stagger(staggerIndex++)}>
            <div ref={(element) => registerSectionRef('app-settings', element)}>
              <SectionHeader title={t('settings.appSettings')} description={t('settings.appSettingsHint')} />
              <Card>
                <ToggleRow
                  label={t('settings.showInstrumentIcons')}
                  description={t('settings.showInstrumentIconsDesc')}
                  checked={!settings.songsHideInstrumentIcons}
                  onToggle={() => updateSettings({ songsHideInstrumentIcons: !settings.songsHideInstrumentIcons })}
                  large={isMobile}
                />
                <ToggleRow
                  label={t('settings.enableVisualOrder')}
                  description={t('settings.enableVisualOrderDesc')}
                  checked={settings.songRowVisualOrderEnabled}
                  onToggle={() => updateSettings({ songRowVisualOrderEnabled: !settings.songRowVisualOrderEnabled })}
                  large={isMobile}
                />
                <div data-testid="visual-order-collapse" style={st.visualOrderCollapseGrid}>
                  <div style={st.visualOrderCollapseInner}>
                    <div style={st.innerSectionTitle}>{t('settings.songRowVisualOrder')}</div>
                    <div style={st.sectionHint}>
                      {t('settings.songRowVisualOrderDesc')}
                    </div>
                    <div style={st.reorderListWrap}>
                      <ReorderList
                        items={visualOrderItems}
                        /* v8 ignore start -- DnD reorder callback; can't fire in jsdom */
                        onReorder={items => {
                          const visibleSet = new Set(items.map(i => i.key));
                          const hiddenKeys = settings.songRowVisualOrder.filter(k => !visibleSet.has(k));
                          updateSettings({ songRowVisualOrder: [...items.map(i => i.key), ...hiddenKeys] });
                        }}
                        /* v8 ignore stop */
                      />
                    </div>
                  </div>
                </div>
                <div style={st.standaloneRow}>
                  <div style={st.standaloneLabel}>{t('settings.pathDefaultView')}</div>
                  <div style={st.standaloneDesc}>
                    {t('settings.pathDefaultViewDesc')}
                  </div>
                  <RadioRow
                    label={t('settings.pathDefaultViewImage')}
                    selected={settings.pathDefaultView === 'image'}
                    onSelect={() => updateSettings({ pathDefaultView: 'image' })}
                  />
                  <RadioRow
                    label={t('settings.pathDefaultViewText')}
                    selected={settings.pathDefaultView === 'text'}
                    onSelect={() => updateSettings({ pathDefaultView: 'text' })}
                  />
                </div>
                <div style={st.standaloneRow}>
                  <div style={st.standaloneLabel}>{t('settings.pathColumnOrder')}</div>
                  <div style={st.standaloneDesc}>
                    {t('settings.pathColumnOrderDesc')}
                  </div>
                  <div style={st.reorderListWrap}>
                    <ReorderList
                      items={pathColumnOrderItems}
                      /* v8 ignore start -- DnD reorder callback; can't fire in jsdom */
                      onReorder={items => {
                        updateSettings({ pathColumnOrder: items.map(i => i.key) as ColumnKey[] });
                      }}
                      /* v8 ignore stop */
                    />
                  </div>
                </div>
                <ToggleRow
                  label={t('settings.filterInvalidScores')}
                  description={t('settings.filterInvalidScoresToggleDesc')}
                  checked={settings.filterInvalidScores}
                  onToggle={() => updateSettings({ filterInvalidScores: !settings.filterInvalidScores })}
                  large={isMobile}
                />
                <div style={st.collapseGrid}>
                  <div style={st.collapseInner}>
                    <div style={st.leewayContent}>
                      <div style={st.innerSectionTitle}>{t('settings.maxScoreLeeway')}</div>
                      <div style={st.leewayDesc}>
                        {t('settings.maxScoreLeewayDesc', { leeway: settings.filterInvalidScoresLeeway, maxScore: (100000 * (1 + settings.filterInvalidScoresLeeway / 100)).toLocaleString() })}
                      </div>
                      <LeewaySlider
                        value={settings.filterInvalidScoresLeeway}
                        onChange={v => updateSettings({ filterInvalidScoresLeeway: v })}
                      />
                    </div>
                  </div>
                </div>
                {flags.leaderboards && (
                <ToggleRow
                  label={t('settings.experimentalRanks')}
                  description={t('settings.experimentalRanksDesc')}
                  checked={settings.enableExperimentalRanks}
                  onToggle={() => updateSettings({ enableExperimentalRanks: !settings.enableExperimentalRanks })}
                  large={isMobile}
                />
                )}
                <ToggleRow
                  label={t('settings.lightTrails', 'Light Trails')}
                  description={t('settings.lightTrailsDesc', 'Show a soft glow that follows your cursor across cards. Only visible with a mouse — disabling may improve performance.')}
                  checked={!settings.disableLightTrails}
                  onToggle={() => updateSettings({ disableLightTrails: !settings.disableLightTrails })}
                  large={isMobile}
                />
                <ToggleRow
                  label={t('settings.showButtonsInHeaderMobile', 'Show Buttons In Header (Mobile)')}
                  description={t('settings.showButtonsInHeaderMobileDesc', 'Shows the Quick Links, Select Player Profile, Song Rivals, and similar header text/buttons on mobile. Turn this off to hide them and free up screen space; these actions will still be available from the floating action button on supported pages.')}
                  checked={settings.showButtonsInHeaderMobile}
                  onToggle={() => updateSettings({ showButtonsInHeaderMobile: !settings.showButtonsInHeaderMobile })}
                  large={isMobile}
                />
              </Card>
            </div>
          </FadeInDiv>

          {/* ── Item Shop ── */}
          <FadeInDiv delay={stagger(staggerIndex++)}>
            <div ref={(element) => registerSectionRef('item-shop', element)}>
              <SectionHeader title={t('settings.itemShop', 'Item Shop')} description={t('settings.itemShopHint', 'Control how Item Shop availability is displayed.')} />
              <Card>
                <ToggleRow
                  label={t('settings.disableShopHighlighting', 'Disable Item Shop Highlighting')}
                  description={t('settings.disableShopHighlightingDesc', 'Turn off the pulsing highlight on songs available in the Item Shop.')}
                  checked={settings.disableShopHighlighting}
                  onToggle={() => updateSettings({ disableShopHighlighting: !settings.disableShopHighlighting })}
                  disabled={settings.hideItemShop}
                  large={isMobile}
                />
                <ToggleRow
                  label={t('settings.hideItemShop', 'Hide Item Shop')}
                  description={t('settings.hideItemShopDesc', 'Hide all Item Shop UI elements including navigation, buttons, and sort options.')}
                  checked={settings.hideItemShop}
                  onToggle={() => updateSettings({ hideItemShop: !settings.hideItemShop })}
                  large={isMobile}
                />
              </Card>
            </div>
          </FadeInDiv>

          {/* ── Instruments ── */}
          <FadeInDiv delay={stagger(staggerIndex++)}>
            <div ref={(element) => registerSectionRef('show-instruments', element)}>
              <SectionHeader title={t('settings.showInstruments')} description={t('settings.showInstrumentsHint')} />
              <Card>
                {INSTRUMENT_SHOW_MAP.map(inst => (
                  <ToggleRow
                    key={inst.showKey}
                    label={t(inst.i18nKey)}
                    icon={<InstrumentIcon instrument={inst.key} size={isMobile ? 28 : 24} />}
                    checked={settings[inst.showKey]}
                    onToggle={() => toggleShow(inst.showKey)}
                    disabled={settings[inst.showKey] && showActiveCount <= 1}
                    large={isMobile}
                  />
                ))}
              </Card>
            </div>
          </FadeInDiv>

          {/* ── Instrument Metadata ── */}
          <FadeInDiv delay={stagger(staggerIndex++)}>
            <div ref={(element) => registerSectionRef('show-metadata', element)}>
              <SectionHeader title={t('settings.showMetadata')} description={t('settings.showMetadataHint')} />
              <Card>
                {METADATA_TOGGLES.map(m => (
                  <ToggleRow
                    key={m.key}
                    label={t(m.i18nKey)}
                    checked={settings[m.key]}
                    onToggle={() => toggleMetadata(m.key)}
                    large={isMobile}
                  />
                ))}
              </Card>
            </div>
          </FadeInDiv>

          {/* ── Version ── */}
          <FadeInDiv delay={stagger(staggerIndex++)}>
            <div ref={(element) => registerSectionRef('version', element)}>
              <SectionHeader title={t('settings.versionTitle')} description={t('settings.versionHint')} />
              <Card>
                <div style={st.versionRow}>
                  <span>{t('settings.appVersion')}</span>
                  <span style={st.versionValue}>{APP_VERSION}</span>
                </div>
                <div style={st.versionRow}>
                  <span>{t('settings.serviceVersion')}</span>
                  <span style={st.versionValue}>{serviceVersion ?? t('common.loading')}</span>
                </div>
                <div style={st.versionRow}>
                  <span>{t('settings.coreVersion')}</span>
                  <span style={st.versionValue}>{CORE_VERSION}</span>
                </div>
                <div style={st.versionRow}>
                  <span>{t('settings.themeVersion')}</span>
                  <span style={st.versionValue}>{THEME_VERSION}</span>
                </div>
              </Card>
            </div>
          </FadeInDiv>

          {/* ── First Run Guides ── */}
          <FadeInDiv delay={stagger(staggerIndex++)}>
            <div ref={(element) => registerSectionRef('first-run', element)}>
              <SectionHeader title={t('firstRun.settings.showFirstRunTitle')} description={t('firstRun.settings.showFirstRunHint')} />
              <Card>
                <button style={modalCss.toggleRowSmallerGap} onClick={songsReplay.open}>
                  <div style={modalCss.toggleContent}>
                    <div style={modalCss.toggleLabel}>{t('nav.songs')}</div>
                  </div>
                  <span style={st.firstRunBtn}>{t('firstRun.settings.showButton')}</span>
                </button>
                <button style={modalCss.toggleRowSmallerGap} onClick={songInfoReplay.open}>
                  <div style={modalCss.toggleContent}>
                    <div style={modalCss.toggleLabel}>{t('nav.songInfo', 'Song Info')}</div>
                  </div>
                  <span style={st.firstRunBtn}>{t('firstRun.settings.showButton')}</span>
                </button>
                <button style={modalCss.toggleRowSmallerGap} onClick={statsReplay.open}>
                  <div style={modalCss.toggleContent}>
                    <div style={modalCss.toggleLabel}>{t('nav.statistics')}</div>
                  </div>
                  <span style={st.firstRunBtn}>{t('firstRun.settings.showButton')}</span>
                </button>
                <button style={modalCss.toggleRowSmallerGap} onClick={suggestionsReplay.open}>
                  <div style={modalCss.toggleContent}>
                    <div style={modalCss.toggleLabel}>{t('nav.suggestions')}</div>
                  </div>
                  <span style={st.firstRunBtn}>{t('firstRun.settings.showButton')}</span>
                </button>
                <button style={modalCss.toggleRowSmallerGap} onClick={playerHistoryReplay.open}>
                  <div style={modalCss.toggleContent}>
                    <div style={modalCss.toggleLabel}>{t('history.title')}</div>
                  </div>
                  <span style={st.firstRunBtn}>{t('firstRun.settings.showButton')}</span>
                </button>
                {flags.leaderboards && (
                <button style={modalCss.toggleRowSmallerGap} onClick={leaderboardsReplay.open}>
                  <div style={modalCss.toggleContent}>
                    <div style={modalCss.toggleLabel}>{t('nav.leaderboards')}</div>
                  </div>
                  <span style={st.firstRunBtn}>{t('firstRun.settings.showButton')}</span>
                </button>
                )}
                {flags.compete && (
                <button style={modalCss.toggleRowSmallerGap} onClick={competeReplay.open}>
                  <div style={modalCss.toggleContent}>
                    <div style={modalCss.toggleLabel}>{t('nav.compete')}</div>
                  </div>
                  <span style={st.firstRunBtn}>{t('firstRun.settings.showButton')}</span>
                </button>
                )}
                <button style={modalCss.toggleRowSmallerGap} onClick={rivalsReplay.open}>
                  <div style={modalCss.toggleContent}>
                    <div style={modalCss.toggleLabel}>{t('rivals.title')}</div>
                  </div>
                  <span style={st.firstRunBtn}>{t('firstRun.settings.showButton')}</span>
                </button>
                <button style={modalCss.toggleRowSmallerGap} onClick={shopReplay.open}>
                  <div style={modalCss.toggleContent}>
                    <div style={modalCss.toggleLabel}>{t('nav.shop')}</div>
                  </div>
                  <span style={st.firstRunBtn}>{t('firstRun.settings.showButton')}</span>
                </button>
              </Card>
            </div>
          </FadeInDiv>

          {/* ── Reset ── */}
          <FadeInDiv delay={stagger(staggerIndex)}>
            <div ref={(element) => registerSectionRef('reset', element)}>
              <div style={st.resetRow}>
                <div>
                  <SectionHeader title={t('settings.resetSection')} description={t('settings.resetDescription')} flush />
                </div>
                <button
                  style={st.resetButton}
                  onClick={() => setShowResetConfirm(true)}
                >
                  {t('settings.resetAll')}
                </button>
              </div>
            </div>
          </FadeInDiv>

        </div>
    </Page>
  );
}

function Card({ children }: { children: React.ReactNode }) {
  const st = useCardStyles();
  return <FrostedCard style={st.card}>{children}</FrostedCard>;
}

function useCardStyles() {
  return useMemo(() => ({
    card: {
      borderRadius: Radius.md,
      padding: padding(Layout.paddingTop),
      ...flexColumn,
      gap: Gap.md,
    } as CSSProperties,
  }), []);
}

function useSettingsStyles(isMobile: boolean, filterOpen: boolean, visualOrderOpen: boolean) {
  return useMemo(() => ({
    container: {
      paddingBottom: Layout.paddingTop,
    } as CSSProperties,
    cardColumn: {
      ...flexColumn,
      gap: Gap.section,
    } as CSSProperties,
    innerSectionTitle: {
      fontSize: Font.md,
      fontWeight: Weight.bold,
      color: Colors.textPrimary,
      marginBottom: Gap.sm,
    } as CSSProperties,
    sectionHint: {
      fontSize: Font.md,
      color: Colors.textSecondary,
      lineHeight: LineHeight.relaxed,
      marginBottom: Gap.md,
    } as CSSProperties,
    reorderListWrap: {
      marginTop: Gap.md,
    } as CSSProperties,
    standaloneRow: {
    } as CSSProperties,
    standaloneLabel: {
      fontSize: isMobile ? Font.lg : Font.md,
      fontWeight: Weight.semibold,
      color: Colors.textPrimary,
    } as CSSProperties,
    standaloneDesc: {
      fontSize: isMobile ? Font.md : Font.sm,
      color: Colors.textMuted,
      marginTop: Gap.xs,
    } as CSSProperties,
    visualOrderCollapseGrid: {
      display: Display.grid,
      gridTemplateRows: visualOrderOpen ? '1fr' : '0fr',
      transition: transition(CssProp.gridTemplateRows, FAST_FADE_MS),
      ...(!visualOrderOpen && { marginTop: -Gap.md }),
    } as CSSProperties,
    visualOrderCollapseInner: {
      overflow: Overflow.hidden,
      minHeight: 0,
    } as CSSProperties,
    collapseGrid: {
      display: Display.grid,
      gridTemplateRows: filterOpen ? '1fr' : '0fr',
      transition: transition(CssProp.gridTemplateRows, FAST_FADE_MS),
    } as CSSProperties,
    collapseInner: {
      overflow: Overflow.hidden,
      minHeight: 0,
    } as CSSProperties,
    leewayContent: {
      paddingLeft: Gap.xl,
      paddingRight: Size.settingsSliderPadding,
      paddingBottom: Gap.md,
    } as CSSProperties,
    leewayDesc: {
      fontSize: isMobile ? Font.md : Font.sm,
      color: Colors.textMuted,
      lineHeight: LineHeight.relaxed,
      marginBottom: Gap.md,
    } as CSSProperties,
    versionRow: {
      ...flexBetween,
      padding: padding(Gap.sm, Gap.none),
      fontSize: Font.md,
    } as CSSProperties,
    versionValue: {
      color: Colors.textSecondary,
    } as CSSProperties,
    resetRow: {
      ...flexBetween,
      gap: Gap.xl,
      ...(isMobile ? { ...flexColumn, alignItems: Align.stretch } : {}),
    } as CSSProperties,
    resetButton: {
      ...btnDanger,
      padding: isMobile ? padding(Gap.xl) : padding(Gap.md, Gap.xl),
      fontSize: isMobile ? Font.md : Font.sm,
      flexShrink: 0,
      ...(isMobile ? { width: CssValue.full, textAlign: TextAlign.center } : {}),
    } as CSSProperties,
    firstRunBtn: {
      ...btnPrimary,
      padding: padding(Gap.sm, Gap.xl),
      fontSize: Font.md,
      flexShrink: 0,
      display: Display.inlineFlex,
      alignItems: Align.center,
    } as CSSProperties,
  }), [isMobile, filterOpen, visualOrderOpen]);
}