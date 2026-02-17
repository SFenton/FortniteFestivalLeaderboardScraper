// UI Component Library — @festival/ui
// Reusable React Native components shared across local-app and server-app.

export {Accordion} from './Accordion';
export {AnimatedBackground} from './AnimatedBackground';
export {CenteredEmptyStateCard} from './CenteredEmptyStateCard';
export {FadeScrollView} from './FadeScrollView';
export {FestivalTextInput} from './FestivalTextInput';
export {FrostedSurface} from './FrostedSurface';
export {HamburgerButton} from './HamburgerButton';
export {IntSlider} from './IntSlider';
export {MarqueeText} from './MarqueeText';
export {PageHeader} from './PageHeader';
export {Screen} from './Screen';
export {SlidingRowsBackground} from './SlidingRowsBackground';
export {useCardGrid} from './useCardGrid';

// Instruments
export {InstrumentCard, MetricPill, StarsVisual} from './instruments/InstrumentCard';
export type {InstrumentCardData} from './instruments/InstrumentCard';
export {DifficultyBars} from './instruments/DifficultyBars';
export type {DifficultyBarsProps} from './instruments/DifficultyBars';
export {StatisticsInstrumentCard} from './instruments/StatisticsInstrumentCard';
export type {StatisticsCardData} from './instruments/StatisticsInstrumentCard';
export {getInstrumentIconSource, getInstrumentStatusVisual, MAUI_STATUS_COLORS} from './instruments/instrumentVisuals';

// Songs
export {SongRowShell} from './songs/SongRowShell';
export type {SongRowShellProps} from './songs/SongRowShell';
export {SongRow} from './songs/SongRow';
export type {SongRowDisplayData, InstrumentChipVisual, InstrumentDetailData} from './songs/SongRow';
export {TopSongRow} from './songs/TopSongRow';
export type {TopSongRowProps, TopSongRowItem} from './songs/TopSongRow';

// Suggestions
export {SuggestionCard} from './suggestions/SuggestionCard';
export {SuggestionSongRow} from './suggestions/SuggestionSongRow';

// Cards
export {CategoryCard} from './cards/CategoryCard';
export type {CategoryCardProps} from './cards/CategoryCard';

// Controls
export {ToggleRow} from './controls/ToggleRow';
export type {ToggleRowProps} from './controls/ToggleRow';
export {ChoiceButton} from './controls/ChoiceButton';
export type {ChoiceButtonProps} from './controls/ChoiceButton';

// Theme
export {Colors} from './theme';
export type {ColorKey} from './theme';
export {Radius, Font, LineHeight, Gap, Opacity, Size, MaxWidth, Layout} from './theme';

// Shared Styles
export {pillStyles} from './styles';
export {songRowStyles} from './styles';
export {cardStyles} from './styles';
export {gridStyles} from './styles';
export {buttonStyles} from './styles';

// Platform helpers
export {WIN_SCROLLBAR_INSET} from './platformConstants';

// Modals
export {PlatformModal} from './Modals/PlatformModal';
export {SortModal} from './Modals/SortModal';
export {FilterModal} from './Modals/FilterModal';
export {SuggestionsFilterModal, defaultSuggestionsInstrumentFilters} from './Modals/SuggestionsFilterModal';
export type {SuggestionsInstrumentFilters} from './Modals/SuggestionsFilterModal';
export {modalStyles} from './Modals/modalStyles';
