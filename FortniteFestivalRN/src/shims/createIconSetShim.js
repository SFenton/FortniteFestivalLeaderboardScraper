/**
 * Windows-only replacement for react-native-vector-icons/lib/create-icon-set.
 *
 * The original module produces a XAML-style font URI for Windows:
 *   fontFamily: "/Assets/Ionicons.ttf#Ionicons"
 *
 * RN-Windows 0.81 Composition renderer uses DirectWrite (not XAML) for text.
 * DirectWrite's CreateTextFormat expects a bare font family name (e.g. "Ionicons"),
 * not a XAML URI.  This shim reproduces the Icon rendering logic but passes the
 * bare fontFamily to DirectWrite.
 */
import React, {PureComponent} from 'react';
import {Text} from 'react-native';

export const DEFAULT_ICON_SIZE = 12;
export const DEFAULT_ICON_COLOR = 'black';

export default function createIconSet(glyphMap, fontFamily, _fontFile, fontStyle) {
  // DirectWrite needs the bare family name — not the XAML URI format
  const fontReference = fontFamily;

  class Icon extends PureComponent {
    static defaultProps = {
      size: DEFAULT_ICON_SIZE,
      allowFontScaling: false,
    };

    render() {
      const {name, size, color, style, children, ...props} = this.props;

      let glyph = name ? glyphMap[name] || '?' : '';
      if (typeof glyph === 'number') {
        glyph = String.fromCodePoint(glyph);
      }

      const styleDefaults = {
        fontSize: size,
        color,
      };

      const styleOverrides = {
        fontFamily: fontReference,
        fontWeight: 'normal',
        fontStyle: 'normal',
      };

      props.style = [styleDefaults, style, styleOverrides, fontStyle || {}];

      return (
        <Text selectable={false} {...props}>
          {glyph}
          {children}
        </Text>
      );
    }
  }

  // Static helpers — native-module-based APIs are no-ops on Windows
  Icon.hasIcon = name =>
    Object.prototype.hasOwnProperty.call(glyphMap, name);
  Icon.getRawGlyphMap = () => glyphMap;
  Icon.getFontFamily = () => fontReference;
  Icon.getImageSourceSync = () => null;
  Icon.getImageSource = () => Promise.resolve(null);
  Icon.loadFont = () => Promise.resolve();
  // Simplified Button fallback (no native image source available)
  Icon.Button = Icon;

  return Icon;
}
