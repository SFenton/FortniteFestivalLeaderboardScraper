/**
 * gestureHandlerStub – Windows fallback for react-native-gesture-handler.
 *
 * react-native-gesture-handler has no Windows native implementation.
 * Its module-level code calls getViewManagerConfig('getConstants') which
 * triggers a LogBox error on Windows.  This stub provides the minimal
 * API surface the codebase uses (GestureHandlerRootView) plus common
 * exports that transitive dependencies (e.g. react-native-draggable-flatlist)
 * may reference.
 */
import React from 'react';
import { View, FlatList, ScrollView } from 'react-native';

// GestureHandlerRootView – just a plain View on Windows
export const GestureHandlerRootView = View;

// Gesture detector / handler stubs
export const GestureDetector = ({ children }) => children;
export class Gesture {
  static Tap() { return new Gesture(); }
  static Pan() { return new Gesture(); }
  static Pinch() { return new Gesture(); }
  static Rotation() { return new Gesture(); }
  static Fling() { return new Gesture(); }
  static LongPress() { return new Gesture(); }
  static ForceTouch() { return new Gesture(); }
  static Native() { return new Gesture(); }
  static Manual() { return new Gesture(); }
  static Race(...gestures) { return new Gesture(); }
  static Simultaneous(...gestures) { return new Gesture(); }
  static Exclusive(...gestures) { return new Gesture(); }

  // Chainable config methods – return `this` for fluent API
  onStart() { return this; }
  onUpdate() { return this; }
  onEnd() { return this; }
  onFinalize() { return this; }
  onBegin() { return this; }
  onChange() { return this; }
  onTouchesDown() { return this; }
  onTouchesMove() { return this; }
  onTouchesUp() { return this; }
  onTouchesCancelled() { return this; }
  enabled() { return this; }
  shouldCancelWhenOutside() { return this; }
  hitSlop() { return this; }
  minPointers() { return this; }
  maxPointers() { return this; }
  minDistance() { return this; }
  minVelocity() { return this; }
  numberOfTaps() { return this; }
  maxDuration() { return this; }
  maxDelay() { return this; }
  maxDist() { return this; }
  minDist() { return this; }
  withRef() { return this; }
  withTestId() { return this; }
  runOnJS() { return this; }
  simultaneousWithExternalGesture() { return this; }
  requireExternalGestureToFail() { return this; }
  blocksExternalGesture() { return this; }
  activateAfterLongPress() { return this; }
}

// Handler components – render children directly (no gesture recognition)
const noopHandler = React.forwardRef(({ children, ...rest }, ref) => (
  <View ref={ref} {...rest}>{children}</View>
));
noopHandler.displayName = 'NoopGestureHandler';

export const PanGestureHandler = noopHandler;
export const TapGestureHandler = noopHandler;
export const LongPressGestureHandler = noopHandler;
export const PinchGestureHandler = noopHandler;
export const RotationGestureHandler = noopHandler;
export const FlingGestureHandler = noopHandler;
export const ForceTouchGestureHandler = noopHandler;
export const NativeViewGestureHandler = noopHandler;

// Wrapped RN components – just re-export from RN
export { ScrollView, FlatList } from 'react-native';

// createNativeWrapper – returns the component unchanged
export function createNativeWrapper(Component) { return Component; }

// State enum
export const State = {
  UNDETERMINED: 0,
  FAILED: 1,
  BEGAN: 2,
  CANCELLED: 3,
  ACTIVE: 4,
  END: 5,
};

// Directions enum
export const Directions = {
  RIGHT: 1,
  LEFT: 2,
  UP: 4,
  DOWN: 8,
};

// Swipeable component
export const Swipeable = noopHandler;

// DrawerLayout
export const DrawerLayout = noopHandler;

// TouchableHighlight / TouchableOpacity / etc. – re-export from RN
export { TouchableHighlight, TouchableOpacity, TouchableWithoutFeedback, TouchableNativeFeedback } from 'react-native';

// gestureHandlerRootHOC – identity HOC
export function gestureHandlerRootHOC(Component) { return Component; }

// Default export (side-effect module)
export default {};
