/* eslint-env jest */

import 'react-native-gesture-handler/jestSetup';

jest.mock('react-native-reanimated', () => {
  global.__reanimatedWorkletInit = () => {};

  const Reanimated = {
    createAnimatedComponent: (Component) => Component,
    Easing: {
      linear: (t) => t,
    },
    runOnJS: (fn) => fn,
    useSharedValue: (value) => ({ value }),
    useAnimatedStyle: (updater) => updater(),
    useAnimatedProps: (updater) => updater(),
    useDerivedValue: (updater) => ({ value: updater() }),
    withTiming: (value) => value,
    withSpring: (value) => value,
  };

  return {
    __esModule: true,
    default: Reanimated,
    ...Reanimated,
  };
});

jest.mock('react-native-screens', () => {
  const actual = jest.requireActual('react-native-screens');

  return {
    ...actual,
    enableScreens: jest.fn(),
  };
});

jest.mock('@react-navigation/bottom-tabs/unstable', () => {
  const actual = jest.requireActual('@react-navigation/bottom-tabs');

  return {
    ...actual,
    createNativeBottomTabNavigator: actual.createBottomTabNavigator,
  };
});

jest.mock('@callstack/liquid-glass', () => {
  const {View} = require('react-native');

  return {
    __esModule: true,
    LiquidGlassView: View,
    LiquidGlassContainerView: View,
    isLiquidGlassSupported: false,
  };
});
