/**
 * @format
 */

import { Platform } from 'react-native';

if (Platform.OS !== 'windows') {
	// Must be loaded before navigation on platforms where it's supported.
	// eslint-disable-next-line @typescript-eslint/no-var-requires
	require('react-native-gesture-handler');
}

import { AppRegistry } from 'react-native';
import App from './App';
import { name as appName } from './app.json';

AppRegistry.registerComponent(appName, () => App);
