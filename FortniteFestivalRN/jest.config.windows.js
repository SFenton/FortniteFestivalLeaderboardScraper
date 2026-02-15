const config = {
	setupFilesAfterEnv: ['<rootDir>/jest.setup.js'],
	testPathIgnorePatterns: ['<rootDir>/__tests__/App.test.tsx'],
};

module.exports = require('@rnx-kit/jest-preset')('windows', config);
