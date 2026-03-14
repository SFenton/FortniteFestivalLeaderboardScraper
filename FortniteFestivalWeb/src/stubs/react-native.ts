// Stub for react-native imports when building for web via Vite.
// The @festival/core barrel re-exports epicOAuth.ts which imports
// react-native — this stub satisfies the import without pulling in
// the full React Native package.

export const Platform = { OS: 'web' as const };
export const NativeModules = {} as Record<string, any>;
