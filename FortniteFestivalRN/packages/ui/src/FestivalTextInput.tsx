import React, {useCallback, useMemo, useState} from 'react';
import {
  Platform,
  StyleSheet,
  TextInput as RNTextInput,
  type TextInputProps,
} from 'react-native';

const WIN_MIN_HEIGHT = 38;

/**
 * Drop-in replacement for React Native's `TextInput` that automatically
 * suppresses the XAML focus rectangle on Windows, hides the placeholder
 * text when the input is focused, and enforces a minimum height so the
 * XAML TextBox doesn't collapse when the placeholder is cleared.
 *
 * On all other platforms it simply forwards every prop to `TextInput`.
 */
export const FestivalTextInput = React.forwardRef<RNTextInput, TextInputProps>(
  (props, ref) => {
    if (Platform.OS !== 'windows') {
      return <RNTextInput ref={ref} {...props} />;
    }

    const {placeholder, style, onFocus, onBlur, ...rest} = props;

    const [focused, setFocused] = useState(false);

    const handleFocus = useCallback(
      (e: any) => {
        setFocused(true);
        onFocus?.(e);
      },
      [onFocus],
    );

    const handleBlur = useCallback(
      (e: any) => {
        setFocused(false);
        onBlur?.(e);
      },
      [onBlur],
    );

    // Ensure a minHeight is always present so the TextBox doesn't collapse
    // when the placeholder is cleared on focus.
    const mergedStyle = useMemo(() => {
      const flat = StyleSheet.flatten(style) || {};
      if ((flat as any).minHeight == null && (flat as any).height == null) {
        return [style, {minHeight: WIN_MIN_HEIGHT}];
      }
      return style;
    }, [style]);

    return (
      <RNTextInput
        ref={ref}
        {...{enableFocusRing: false}}
        placeholder={focused ? '' : placeholder}
        style={mergedStyle}
        onFocus={handleFocus}
        onBlur={handleBlur}
        {...rest}
      />
    );
  },
);

FestivalTextInput.displayName = 'FestivalTextInput';
