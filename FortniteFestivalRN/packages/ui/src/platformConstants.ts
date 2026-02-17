import {Platform} from 'react-native';

/**
 * Extra right-side padding applied to scrollable content containers on Windows
 * so that the native scrollbar doesn't overlay content.
 * Evaluates to 0 on all other platforms.
 */
export const WIN_SCROLLBAR_INSET = Platform.OS === 'windows' ? 16 : 0;
