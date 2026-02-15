import React, {useEffect, useMemo, useRef, useState} from 'react';
import {
  Animated,
  Easing,
  LayoutChangeEvent,
  StyleProp,
  Text,
  TextStyle,
  TextProps,
  View,
  ViewStyle,
} from 'react-native';

export function MarqueeText(props: {
  text: string;
  textStyle?: StyleProp<TextStyle>;
  containerStyle?: StyleProp<ViewStyle>;
  speedPxPerSec?: number;
  gapPx?: number;
  startDelayMs?: number;
  endDelayMs?: number;
  textProps?: Omit<TextProps, 'style' | 'children'>;
}) {
  const {
    text,
    textStyle,
    containerStyle,
    speedPxPerSec = 38,
    gapPx = 28,
    startDelayMs = 700,
    endDelayMs = 700,
    textProps,
  } = props;

  const [containerWidth, setContainerWidth] = useState<number | null>(null);
  const [textWidth, setTextWidth] = useState<number | null>(null);

  const translateX = useRef(new Animated.Value(0)).current;
  const loopRef = useRef<Animated.CompositeAnimation | null>(null);

  const shouldScroll = useMemo(() => {
    if (!text) return false;
    if (containerWidth == null || textWidth == null) return false;
    return textWidth > containerWidth + 1;
  }, [containerWidth, text, textWidth]);

  useEffect(() => {
    loopRef.current?.stop();
    loopRef.current = null;
    translateX.setValue(0);

    if (!shouldScroll || containerWidth == null || textWidth == null) return;

    const distance = textWidth + gapPx;
    const durationMs = Math.max(250, Math.round((distance / Math.max(10, speedPxPerSec)) * 1000));

    const anim = Animated.loop(
      Animated.sequence([
        Animated.delay(startDelayMs),
        Animated.timing(translateX, {
          toValue: -distance,
          duration: durationMs,
          easing: Easing.linear,
          useNativeDriver: true,
        }),
        Animated.delay(endDelayMs),
        Animated.timing(translateX, {
          toValue: 0,
          duration: 0,
          useNativeDriver: true,
        }),
      ]),
    );

    loopRef.current = anim;
    anim.start();

    return () => {
      anim.stop();
    };
  }, [containerWidth, endDelayMs, gapPx, shouldScroll, speedPxPerSec, startDelayMs, textWidth, translateX]);

  const onLayout = (e: LayoutChangeEvent) => {
    const next = e.nativeEvent.layout.width;
    setContainerWidth(cur => {
      if (cur == null) return next;
      return Math.abs(cur - next) >= 1 ? next : cur;
    });
  };

  return (
    <View
      onLayout={onLayout}
      style={[{overflow: 'hidden', flexShrink: 1, minWidth: 0}, containerStyle]}
    >
      <Animated.View
        style={{
          transform: [{translateX}],
          flexDirection: 'row',
          alignItems: 'center',
        }}
      >
        <Text
          {...textProps}
          numberOfLines={1}
          ellipsizeMode="clip"
          onTextLayout={e => {
            const w = (e.nativeEvent as any)?.lines?.[0]?.width;
            if (typeof w === 'number' && Number.isFinite(w) && w > 0) {
              setTextWidth(cur => {
                if (cur == null) return w;
                return Math.abs(cur - w) >= 1 ? w : cur;
              });
            }
          }}
          style={textStyle}
        >
          {text}
        </Text>

        {shouldScroll ? (
          <>
            <View style={{width: gapPx}} />
            <Text
              {...textProps}
              numberOfLines={1}
              ellipsizeMode="clip"
              accessibilityElementsHidden
              importantForAccessibility="no"
              style={textStyle}
            >
              {text}
            </Text>
          </>
        ) : null}
      </Animated.View>
    </View>
  );
}
