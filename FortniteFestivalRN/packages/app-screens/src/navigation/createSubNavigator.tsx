import React from 'react';
import {Platform, Pressable, StyleSheet, Text} from 'react-native';
import {createNativeStackNavigator} from '@react-navigation/native-stack';
import type {ParamListBase} from '@react-navigation/native';
import {Colors} from '@festival/ui';

import {SongDetailsScreen} from '../screens/SongDetailsScreen';

const Stack = createNativeStackNavigator<ParamListBase>();

function BackChevron(props: {onPress: () => void}) {
  return (
    <Pressable
      onPress={props.onPress}
      hitSlop={12}
      style={({pressed}) => [styles.backBtn, pressed && styles.backBtnPressed]}
      accessibilityRole="button"
      accessibilityLabel="Go back"
    >
      <Text style={styles.backText}>‹</Text>
    </Pressable>
  );
}

/**
 * Factory that produces a Stack.Navigator pairing a list screen with
 * the shared SongDetails screen.  Each call returns a stable component
 * that can be rendered directly by a tab / drawer navigator.
 */
export function createSubNavigator(
  listScreenName: string,
  ListScreen: React.ComponentType<{onOpenSong: (id: string, title: string) => void}>,
) {
  function ListWrapper({navigation}: any) {
    return (
      <ListScreen
        onOpenSong={(songId: string, _title: string) => {
          navigation.navigate('SongDetails', {songId});
        }}
      />
    );
  }
  ListWrapper.displayName = `${listScreenName}Wrapper`;

  return function SubNavigator() {
    return (
      <Stack.Navigator
        screenOptions={({navigation}) => ({
          headerStyle: styles.header,
          headerTitleStyle: styles.headerTitle,
          headerTintColor: Colors.textPrimary,
          headerBackTitleVisible: false,
          headerLeft: () => <BackChevron onPress={() => navigation.goBack()} />,
        })}
      >
        <Stack.Screen
          name={listScreenName}
          component={ListWrapper}
          options={{headerShown: false}}
        />
        <Stack.Screen
          name="SongDetails"
          component={SongDetailsScreen as any}
          options={{
            title: '',
            headerShown: false,
            animation: 'none',
            presentation: Platform.OS === 'ios' ? 'fullScreenModal' : 'card',
            contentStyle: {backgroundColor: Colors.backgroundBlack},
          }}
        />
      </Stack.Navigator>
    );
  };
}

const styles = StyleSheet.create({
  header: {backgroundColor: Colors.backgroundApp},
  headerTitle: {color: Colors.textPrimary, fontWeight: '700'},
  backBtn: {paddingHorizontal: 6, paddingVertical: 2},
  backBtnPressed: {opacity: 0.75},
  backText: {color: Colors.textPrimary, fontSize: 28, fontWeight: '700', marginTop: Platform.OS === 'ios' ? -2 : 0},
});
