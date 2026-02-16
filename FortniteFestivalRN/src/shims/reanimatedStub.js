/**
 * react-native-reanimated stub for Windows.
 *
 * react-native-reanimated (and its dependency react-native-worklets) have no
 * Windows native modules.  Metro redirects all `require('react-native-reanimated')`
 * calls to this file when bundling for platform === 'windows'.
 *
 * This prevents the Worklets native init crash for ALL import paths — direct
 * imports in our code AND transitive imports inside react-native-draggable-flatlist.
 *
 * Based on the project's jest mock (jest.setup.js) with additional exports
 * required by react-native-draggable-flatlist's module-level code.
 */

'use strict';

const {View, Text, ScrollView, Image, FlatList} = require('react-native');

// ── Animated component wrappers ────────────────────────────────────
const createAnimatedComponent = (Component) => Component;

const AnimatedNamespace = {
  View,
  Text,
  ScrollView,
  Image,
  FlatList,
  createAnimatedComponent,
};

// ── Hooks ──────────────────────────────────────────────────────────
const useSharedValue = (init) => ({value: init});
const useDerivedValue = (updater) => ({value: updater()});
const useAnimatedStyle = (updater) => updater();
const useAnimatedProps = (updater) => updater();
const useAnimatedRef = () => ({current: null});
const useAnimatedReaction = () => {};
const useAnimatedGestureHandler = () => ({});
const useAnimatedScrollHandler = () => ({});

// ── Animation builders ─────────────────────────────────────────────
const withTiming = (toValue) => toValue;
const withSpring = (toValue) => toValue;
const withDecay = () => 0;
const withRepeat = (animation) => animation;
const withDelay = (_delay, animation) => animation;
const withSequence = (...animations) => animations[animations.length - 1];

// ── Threading ──────────────────────────────────────────────────────
// runOnUI is called at module-scope by react-native-draggable-flatlist's
// CellRendererComponent, so it MUST return a callable function.
const runOnUI = (_fn) => () => {};
const runOnJS = (fn) => (...args) => fn(...args);

// ── Utility ────────────────────────────────────────────────────────
const cancelAnimation = () => {};
const measure = () => ({x: 0, y: 0, width: 0, height: 0, pageX: 0, pageY: 0});
const scrollTo = () => {};
const setGestureState = () => {};
const makeMutable = (init) => ({value: init});

const interpolate = (value, inputRange, outputRange) => {
  if (!inputRange || inputRange.length < 2) return outputRange?.[0] ?? 0;
  const t = Math.max(0, Math.min(1, (value - inputRange[0]) / (inputRange[1] - inputRange[0])));
  return outputRange[0] + t * (outputRange[1] - outputRange[0]);
};

const interpolateColor = () => 'transparent';
const Extrapolation = {CLAMP: 'clamp', EXTEND: 'extend', IDENTITY: 'identity'};

// ── Easing ─────────────────────────────────────────────────────────
const identity = (t) => t;
const Easing = {
  linear: identity,
  ease: identity,
  quad: identity,
  cubic: identity,
  poly: () => identity,
  sin: identity,
  circle: identity,
  exp: identity,
  bounce: identity,
  bezier: () => identity,
  bezierFn: () => identity,
  steps: () => identity,
  in: (fn) => fn || identity,
  out: (fn) => fn || identity,
  inOut: (fn) => fn || identity,
};

// ── Layout animation classes (no-op constructors) ──────────────────
const noopLayout = {duration: () => noopLayout, delay: () => noopLayout, springify: () => noopLayout};
const FadeIn = noopLayout;
const FadeOut = noopLayout;
const Layout = noopLayout;
const SlideInRight = noopLayout;
const SlideOutLeft = noopLayout;
const SlideInUp = noopLayout;
const SlideOutDown = noopLayout;
const ZoomIn = noopLayout;
const ZoomOut = noopLayout;
const BounceIn = noopLayout;
const BounceOut = noopLayout;

// ── Global worklet init (required by Babel plugin) ─────────────────
if (typeof global !== 'undefined') {
  global.__reanimatedWorkletInit = global.__reanimatedWorkletInit || (() => {});
}

// ── Exports ────────────────────────────────────────────────────────
module.exports = {
  __esModule: true,
  default: AnimatedNamespace,
  ...AnimatedNamespace,
  createAnimatedComponent,
  useSharedValue,
  useDerivedValue,
  useAnimatedStyle,
  useAnimatedProps,
  useAnimatedRef,
  useAnimatedReaction,
  useAnimatedGestureHandler,
  useAnimatedScrollHandler,
  withTiming,
  withSpring,
  withDecay,
  withRepeat,
  withDelay,
  withSequence,
  runOnUI,
  runOnJS,
  cancelAnimation,
  measure,
  scrollTo,
  setGestureState,
  makeMutable,
  interpolate,
  interpolateColor,
  Extrapolation,
  Easing,
  FadeIn,
  FadeOut,
  Layout,
  SlideInRight,
  SlideOutLeft,
  SlideInUp,
  SlideOutDown,
  ZoomIn,
  ZoomOut,
  BounceIn,
  BounceOut,
};
