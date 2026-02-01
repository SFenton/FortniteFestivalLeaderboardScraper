import React from 'react';
import {Platform, Pressable, StyleSheet, Text} from 'react-native';
import {createNativeStackNavigator} from '@react-navigation/native-stack';

import {SongsScreen} from '../screens/SongsScreen';
import {SongDetailsScreen, type SongsStackParamList} from '../screens/SongDetailsScreen';

const Stack = createNativeStackNavigator<SongsStackParamList>();

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

function SongsListWrapper({navigation}: any) {
  return (
    <SongsScreen
      onOpenSong={(songId, _title) => {
        navigation.navigate('SongDetails', {songId});
        // SongsScreen already logs; keep wrapper lean.
      }}
    />
  );
}

export function SongsNavigator() {
  return (
    <Stack.Navigator
      screenOptions={({navigation}) => ({
        headerStyle: styles.header,
        headerTitleStyle: styles.headerTitle,
        headerTintColor: '#FFFFFF',
        headerBackTitleVisible: false,
        headerLeft: () => <BackChevron onPress={() => navigation.goBack()} />,
      })}
    >
      <Stack.Screen name="SongsList" component={SongsListWrapper} options={{headerShown: false}} />
      <Stack.Screen
        name="SongDetails"
        component={SongDetailsScreen}
        options={{
          title: '',
          headerShown: false,
          animation: 'none',
          presentation: 'card',
          contentStyle: {backgroundColor: '#000000'},
        }}
      />
    </Stack.Navigator>
  );
}

const styles = StyleSheet.create({
  header: {
    backgroundColor: '#1A0830',
  },
  headerTitle: {
    color: '#FFFFFF',
    fontWeight: '700',
  },
  backBtn: {
    paddingHorizontal: 6,
    paddingVertical: 2,
  },
  backBtnPressed: {
    opacity: 0.75,
  },
  backText: {
    color: '#FFFFFF',
    fontSize: 28,
    fontWeight: '700',
    marginTop: Platform.OS === 'ios' ? -2 : 0,
  },
});
