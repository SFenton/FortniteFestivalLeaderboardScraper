module.exports = {
  preset: 'react-native',
  watchman: false,
  setupFilesAfterEnv: ['<rootDir>/jest.setup.js'],
  moduleNameMapper: {
    '^@festival/core$': '<rootDir>/packages/core/src',
    '^@festival/core/(.*)$': '<rootDir>/packages/core/src/$1',
    '^@festival/ui$': '<rootDir>/packages/ui/src',
    '^@festival/ui/(.*)$': '<rootDir>/packages/ui/src/$1',
    '^@festival/contexts$': '<rootDir>/packages/contexts/src',
    '^@festival/contexts/(.*)$': '<rootDir>/packages/contexts/src/$1',
    '^@festival/local-app$': '<rootDir>/packages/local-app/src',
    '^@festival/local-app/(.*)$': '<rootDir>/packages/local-app/src/$1',
    '^@festival/server-app$': '<rootDir>/packages/server-app/src',
    '^@festival/server-app/(.*)$': '<rootDir>/packages/server-app/src/$1',
  },
  transformIgnorePatterns: [
    'node_modules/(?!((jest-)?react-native|@react-native|@react-native-masked-view|@react-navigation|@bottom-tabs|react-native-gesture-handler|react-native-reanimated|react-native-screens|react-native-safe-area-context|react-native-drawer-layout|react-native-vector-icons|react-native-linear-gradient|react-native-draggable-flatlist)/)',
  ],
  collectCoverage: true,
  collectCoverageFrom: [
    'packages/core/src/**/*.{ts,tsx}',
    '!packages/core/src/**/__tests__/**',
    '!packages/core/src/**/index.{ts,tsx}',
    '!packages/core/src/**/types.{ts,tsx}',
    '!packages/core/src/**/*.types.{ts,tsx}',
    '!packages/core/src/**/*.d.ts',
  ],
  coveragePathIgnorePatterns: ['/node_modules/', '/android/', '/ios/', '/windows/', '/macos/'],
  coverageThreshold: {
    global: {
      branches: 95,
      functions: 95,
      lines: 95,
      statements: 95,
    },
  },
};
