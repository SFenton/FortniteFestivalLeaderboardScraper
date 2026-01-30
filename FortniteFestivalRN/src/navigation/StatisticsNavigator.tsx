import React from 'react';
import {Platform, Pressable, StyleSheet, Text} from 'react-native';
import {createNativeStackNavigator} from '@react-navigation/native-stack';

import {StatisticsScreen} from '../screens/StatisticsScreen';
import {SongDetailsScreen} from '../screens/SongDetailsScreen';

export type StatisticsStackParamList = {
  StatisticsHome: undefined;
  SongDetails: {songId: string};
};

const Stack = createNativeStackNavigator<StatisticsStackParamList>();

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

function StatisticsHomeWrapper({navigation}: any) {
  return (
    <StatisticsScreen
      onOpenSong={(songId, _title) => {
        navigation.navigate('SongDetails', {songId});
      }}
    />
  );
}

export function StatisticsNavigator() {
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
      <Stack.Screen name="StatisticsHome" component={StatisticsHomeWrapper} options={{headerShown: false}} />
      <Stack.Screen
        name="SongDetails"
        component={SongDetailsScreen as any}
        options={{
          title: '',
          headerTransparent: true,
          headerStyle: {backgroundColor: 'transparent'},
          headerShadowVisible: false,
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
