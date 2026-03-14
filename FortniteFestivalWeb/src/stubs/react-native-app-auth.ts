// Stub for react-native-app-auth when building for web via Vite.
// The epicOAuth.ts module dynamically requires this on mobile platforms only.
export function authorize(): Promise<never> {
  throw new Error('react-native-app-auth is not available on web.');
}
