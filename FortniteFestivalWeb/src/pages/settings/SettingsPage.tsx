/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useCallback, useEffect, useMemo, useRef, useState, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { useSettings } from '../../contexts/SettingsContext';
import { useFeatureFlags } from '../../contexts/FeatureFlagsContext';
import { useIsMobile, useIsMobileChrome } from '../../hooks/ui/useIsMobile';
import { ToggleRow } from '../../components/common/ToggleRow';
import SectionHeader from '../../components/common/SectionHeader';
import { ReorderList } from '../../components/sort/ReorderList';
import { METADATA_SORT_DISPLAY } from '../../utils/songSettings';
import ConfirmAlert from '../../components/modals/ConfirmAlert';
import { modalStyles as modalCss } from '../../components/modals/modalStyles';
import { InstrumentIcon } from '../../components/display/InstrumentIcons';
import type { ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import { Colors, Font, Gap, Weight, Radius, Layout, Display, Align, Justify, Overflow, CssValue, LineHeight, TextAlign, frostedCard, btnDanger, btnPrimary, flexColumn, flexRow, flexBetween, padding, transition, CssProp, FAST_FADE_MS, STAGGER_INTERVAL, FADE_DURATION } from '@festival/theme';
import { useRegisterFirstRun } from '../../hooks/ui/useRegisterFirstRun';
import { useFirstRunReplay } from '../../hooks/ui/useFirstRun';
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

import { APP_VERSION, CORE_VERSION, THEME_VERSION } from '../../hooks/data/useVersions';

/** Track whether settings page has rendered at least once to skip stagger on re-visit. */
let _hasRendered = false;

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

type ShowKey = 'showLead' | 'showBass' | 'showDrums' | 'showVocals' | 'showProLead' | 'showProBass';

const INSTRUMENT_SHOW_MAP: { key: InstrumentKey; showKey: ShowKey; i18nKey: string }[] = [
  { key: 'Solo_Guitar', showKey: 'showLead', i18nKey: 'instruments.lead' },
  { key: 'Solo_Bass', showKey: 'showBass', i18nKey: 'instruments.bass' },
  { key: 'Solo_Drums', showKey: 'showDrums', i18nKey: 'instruments.drums' },
  { key: 'Solo_Vocals', showKey: 'showVocals', i18nKey: 'instruments.vocals' },
  { key: 'Solo_PeripheralGuitar', showKey: 'showProLead', i18nKey: 'instruments.proLead' },
  { key: 'Solo_PeripheralBass', showKey: 'showProBass', i18nKey: 'instruments.proBass' },
];

type MetadataKey =
  | 'metadataShowScore'
  | 'metadataShowPercentage'
  | 'metadataShowPercentile'
  | 'metadataShowSeasonAchieved'
  | 'metadataShowDifficulty'
  | 'metadataShowStars';

const METADATA_TOGGLES: { key: MetadataKey; i18nKey: string }[] = [
  { key: 'metadataShowScore', i18nKey: 'metadata.score' },
  { key: 'metadataShowPercentage', i18nKey: 'metadata.percentage' },
  { key: 'metadataShowPercentile', i18nKey: 'metadata.percentile' },
  { key: 'metadataShowSeasonAchieved', i18nKey: 'metadata.seasonAchieved' },
  { key: 'metadataShowDifficulty', i18nKey: 'metadata.intensity' },
  { key: 'metadataShowStars', i18nKey: 'metadata.stars' },
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
  const [showResetConfirm, setShowResetConfirm] = useState(false);

  // Register first-run slides so replay is always available from Settings
  /* v8 ignore next */
  const songsSlidesMemo = useMemo(() => songSlides(isMobileChrome), [isMobileChrome]);
  const songInfoSlidesMemo = useMemo(() => songInfoSlides(isMobileChrome), [isMobileChrome]);
  const playerHistorySlidesMemo = useMemo(() => playerHistorySlides(isMobileChrome), [isMobileChrome]);
  useRegisterFirstRun('songs', t('nav.songs'), songsSlidesMemo);
  useRegisterFirstRun('songinfo', t('nav.songInfo', 'Song Info'), songInfoSlidesMemo);
  useRegisterFirstRun('playerhistory', t('history.title'), playerHistorySlidesMemo);
  useRegisterFirstRun('statistics', t('nav.statistics'), statisticsSlides);
  useRegisterFirstRun('suggestions', t('nav.suggestions'), suggestionsSlides);
  useRegisterFirstRun('leaderboards', t('nav.leaderboards'), leaderboardsSlides);
  useRegisterFirstRun('compete', t('nav.compete'), competeSlides);
  useRegisterFirstRun('rivals', t('rivals.title'), rivalsSlides);
  useRegisterFirstRun('shop', t('nav.shop'), shopSlides);
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

  const st = useSettingsStyles(isMobile, settings.filterInvalidScores);

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
  const visualOrderItems = useMemo(
    () =>
      settings.songRowVisualOrder.map(k => ({
        key: k,
        label: METADATA_SORT_DISPLAY[k] ?? k,
      })),
    [settings.songRowVisualOrder],
  );
  /* v8 ignore stop */

  let staggerIndex = 0;
  const stagger = (idx: number): number | undefined => skipAnimRef.current ? undefined : idx * STAGGER_INTERVAL;
  const headerStagger: CSSProperties = !skipAnimRef.current
    ? { opacity: 0, animation: `fadeInUp ${FADE_DURATION}ms ease-out forwards` }
    : {};

  return (
    <Page
      scrollRestoreKey="settings"
      containerStyle={st.container}
      before={!isMobileChrome ? <PageHeader title={t('settings.title')} style={headerStagger} /> : undefined}
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
            {settings.songRowVisualOrderEnabled && (
              <div>
                <div style={st.innerSectionTitle}>{t('settings.songRowVisualOrder')}</div>
                <div style={st.sectionHint}>
                  {t('settings.songRowVisualOrderDesc')}
                </div>
                <div style={st.reorderListWrap}>
                  <ReorderList
                    items={visualOrderItems}
                    /* v8 ignore start -- DnD reorder callback; can't fire in jsdom */
                    onReorder={items => updateSettings({ songRowVisualOrder: items.map(i => i.key) })}
                    /* v8 ignore stop */
                  />
                </div>
              </div>
            )}
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
          </Card>
          </FadeInDiv>

          {/* ── Item Shop ── */}
          {flags.shop && (
          <FadeInDiv delay={stagger(staggerIndex++)}>
          <SectionHeader title={t('settings.itemShop', 'Item Shop')} description={t('settings.itemShopHint', 'Control how Item Shop availability is displayed.')} />
          <Card>
            <ToggleRow
              label={t('settings.disableShopHighlighting', 'Disable Item Shop Highlighting')}
              description={t('settings.disableShopHighlightingDesc', 'Turn off the pulsing highlight on songs available in the Item Shop.')}
              checked={settings.hideItemShop || settings.disableShopHighlighting}
              onToggle={() => updateSettings({ disableShopHighlighting: !settings.disableShopHighlighting })}
              disabled={settings.hideItemShop}
              large={isMobile}
            />
            <ToggleRow
              label={t('settings.hideItemShop', 'Hide Item Shop')}
              description={t('settings.hideItemShopDesc', 'Hide all Item Shop UI elements including navigation, buttons, and sort options.')}
              checked={settings.hideItemShop}
              onToggle={() => updateSettings({
                hideItemShop: !settings.hideItemShop,
                ...(!settings.hideItemShop ? { disableShopHighlighting: true } : {}),
              })}
              large={isMobile}
            />
          </Card>
          </FadeInDiv>
          )}

          {/* ── Instruments ── */}
          <FadeInDiv delay={stagger(staggerIndex++)}>
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
          </FadeInDiv>

          {/* ── Instrument Metadata ── */}
          <FadeInDiv delay={stagger(staggerIndex++)}>
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
          </FadeInDiv>

          {/* ── Version ── */}
          <FadeInDiv delay={stagger(staggerIndex++)}>
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
          </FadeInDiv>

          {/* ── First Run Guides ── */}
          {flags.firstRun && (
          <FadeInDiv delay={stagger(staggerIndex++)}>
          <SectionHeader title={t('firstRun.settings.showFirstRunTitle')} description={t('firstRun.settings.showFirstRunHint')} />
          <Card>
            <button style={modalCss.toggleRow} onClick={songsReplay.open}>
              <div style={modalCss.toggleContent}>
                <div style={modalCss.toggleLabel}>{t('nav.songs')}</div>
              </div>
              <span style={st.firstRunBtn}>{t('firstRun.settings.showButton')}</span>
            </button>
            <button style={modalCss.toggleRow} onClick={songInfoReplay.open}>
              <div style={modalCss.toggleContent}>
                <div style={modalCss.toggleLabel}>{t('nav.songInfo', 'Song Info')}</div>
              </div>
              <span style={st.firstRunBtn}>{t('firstRun.settings.showButton')}</span>
            </button>
            <button style={modalCss.toggleRow} onClick={statsReplay.open}>
              <div style={modalCss.toggleContent}>
                <div style={modalCss.toggleLabel}>{t('nav.statistics')}</div>
              </div>
              <span style={st.firstRunBtn}>{t('firstRun.settings.showButton')}</span>
            </button>
            <button style={modalCss.toggleRow} onClick={suggestionsReplay.open}>
              <div style={modalCss.toggleContent}>
                <div style={modalCss.toggleLabel}>{t('nav.suggestions')}</div>
              </div>
              <span style={st.firstRunBtn}>{t('firstRun.settings.showButton')}</span>
            </button>
            <button style={modalCss.toggleRow} onClick={playerHistoryReplay.open}>
              <div style={modalCss.toggleContent}>
                <div style={modalCss.toggleLabel}>{t('history.title')}</div>
              </div>
              <span style={st.firstRunBtn}>{t('firstRun.settings.showButton')}</span>
            </button>
            {flags.leaderboards && (
            <button style={modalCss.toggleRow} onClick={leaderboardsReplay.open}>
              <div style={modalCss.toggleContent}>
                <div style={modalCss.toggleLabel}>{t('nav.leaderboards')}</div>
              </div>
              <span style={st.firstRunBtn}>{t('firstRun.settings.showButton')}</span>
            </button>
            )}
            {flags.compete && (
            <button style={modalCss.toggleRow} onClick={competeReplay.open}>
              <div style={modalCss.toggleContent}>
                <div style={modalCss.toggleLabel}>{t('nav.compete')}</div>
              </div>
              <span style={st.firstRunBtn}>{t('firstRun.settings.showButton')}</span>
            </button>
            )}
            {flags.rivals && (
            <button style={modalCss.toggleRow} onClick={rivalsReplay.open}>
              <div style={modalCss.toggleContent}>
                <div style={modalCss.toggleLabel}>{t('rivals.title')}</div>
              </div>
              <span style={st.firstRunBtn}>{t('firstRun.settings.showButton')}</span>
            </button>
            )}
            {flags.shop && (
            <button style={modalCss.toggleRow} onClick={shopReplay.open}>
              <div style={modalCss.toggleContent}>
                <div style={modalCss.toggleLabel}>{t('nav.shop')}</div>
              </div>
              <span style={st.firstRunBtn}>{t('firstRun.settings.showButton')}</span>
            </button>
            )}
          </Card>
          </FadeInDiv>
          )}

          {/* ── Reset ── */}
          <FadeInDiv delay={stagger(staggerIndex)}>
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
          </FadeInDiv>

        </div>
    </Page>
  );
}

function Card({ children }: { children: React.ReactNode }) {
  const st = useCardStyles();
  return <div style={st.card}>{children}</div>;
}

function useCardStyles() {
  return useMemo(() => ({
    card: {
      ...frostedCard,
      borderRadius: Radius.md,
      padding: padding(Layout.paddingTop),
      ...flexColumn,
      gap: Gap.md,
    } as CSSProperties,
  }), []);
}

function useSettingsStyles(isMobile: boolean, filterOpen: boolean) {
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
      paddingRight: Layout.settingsSliderPadding,
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
      padding: padding(Gap.sm, Gap.section),
      fontSize: Font.md,
      flexShrink: 0,
    } as CSSProperties,
  }), [isMobile, filterOpen]);
}