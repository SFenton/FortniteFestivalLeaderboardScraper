import React from 'react';
import {Platform, StyleProp, StyleSheet, Text, View, ViewStyle} from 'react-native';
import {Colors, Font, Gap} from './theme';

export function PageHeader(props: {
  title?: string;
  left?: React.ReactNode;
  right?: React.ReactNode;
  style?: StyleProp<ViewStyle>;
}) {
  return (
    <View style={[styles.row, props.style]}>
      <View style={styles.left}>
        {props.left}
        {props.title ? <Text style={styles.title}>{props.title}</Text> : null}
      </View>
      {props.right ? <View style={styles.right}>{props.right}</View> : null}
    </View>
  );
}

const styles = StyleSheet.create({
  row: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    minHeight: Platform.OS === 'ios' ? 52 : 44,
  },
  left: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: Gap.lg,
    flexShrink: 1,
  },
  title: {
    color: Colors.textPrimary,
    fontSize: Platform.OS === 'ios' ? 34 : Font.title,
    fontWeight: '700',
    lineHeight: Platform.OS === 'ios' ? 41 : 28,
    includeFontPadding: false,
  },
  right: {
    marginLeft: Gap.xl,
  },
});
