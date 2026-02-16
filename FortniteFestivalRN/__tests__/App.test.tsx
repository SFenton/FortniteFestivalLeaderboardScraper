/**
 * @format
 */

import React from 'react';
import ReactTestRenderer from 'react-test-renderer';

// Mock the heavy app modules so the smoke test doesn't traverse the full
// navigation tree (which pulls in ESM-only native dependencies like
// @bottom-tabs, react-native-draggable-flatlist, etc.).
jest.mock('@festival/local-app', () => {
  const RN = require('react-native');
  const R = require('react');
  return {__esModule: true, LocalApp: () => R.createElement(RN.View)};
});
jest.mock('@festival/server-app', () => {
  const RN = require('react-native');
  const R = require('react');
  return {__esModule: true, ServerApp: () => R.createElement(RN.View)};
});
jest.mock('@festival/contexts', () => {
  const R = require('react');
  const mockCtx = {mode: 'local', isReady: true, isAuthenticated: false, login: jest.fn(), logout: jest.fn()};
  const mockFestival = {songs: [], scores: {}, settings: {}, updateSettings: jest.fn()};
  return {
    __esModule: true,
    AuthProvider: ({children}: any) => children,
    useAuth: () => mockCtx,
    FestivalProvider: ({children}: any) => children,
    useFestival: () => mockFestival,
  };
});
jest.mock('@festival/ui', () => {
  const RN = require('react-native');
  const R = require('react');
  const actual = jest.requireActual('@festival/ui');
  return {...actual, SlidingRowsBackground: () => R.createElement(RN.View)};
});
jest.mock('../src/screens/IntroScreen', () => {
  const RN = require('react-native');
  const R = require('react');
  return {__esModule: true, IntroScreen: () => R.createElement(RN.View)};
});
jest.mock('../src/screens/SignInScreen', () => {
  const RN = require('react-native');
  const R = require('react');
  return {__esModule: true, SignInScreen: () => R.createElement(RN.View)};
});

import App from '../App';

test('renders correctly', async () => {
  await ReactTestRenderer.act(() => {
    ReactTestRenderer.create(<App />);
  });
});
