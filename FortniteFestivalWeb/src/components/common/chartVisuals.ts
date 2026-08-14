import { Colors, Font, Layout } from '@festival/theme';

export const CHART_AXIS_TICK = {
  fill: Colors.textPrimary,
  fontSize: Font.md,
};

export const CHART_X_AXIS_TICK = {
  ...CHART_AXIS_TICK,
  dy: Layout.chartTickOffset,
};

export const CHART_X_AXIS_ANGLE = Layout.chartXAxisAngle;
