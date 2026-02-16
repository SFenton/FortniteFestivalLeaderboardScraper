/**
 * MaskedViewStub – Windows fallback for @react-native-masked-view/masked-view.
 *
 * The native RNCMaskedView component doesn't exist on Windows.
 * This stub simply renders the children without any masking, which is an
 * acceptable visual degradation (the fade-scroll gradient won't appear,
 * but all content remains fully visible and interactive).
 */
import React from 'react';
import { View } from 'react-native';

function MaskedView({ maskElement, children, ...rest }) {
  // Ignore maskElement; render children directly in a View.
  return <View {...rest}>{children}</View>;
}

export default MaskedView;
