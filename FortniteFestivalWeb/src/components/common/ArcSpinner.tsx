/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { memo, useMemo, type CSSProperties } from 'react';
import { Colors, SpinnerSize, Spinner } from '@festival/theme';

export { SpinnerSize };

interface ArcSpinnerProps {
  size?: SpinnerSize;
  className?: string;
  style?: CSSProperties;
}

const ArcSpinner = memo(function ArcSpinner({ size = SpinnerSize.LG, className, style }: ArcSpinnerProps) {
  const s = useStyles(size);
  return <div data-testid="arc-spinner" className={className} style={{ ...s.spinner, ...style }} />;
});

export default ArcSpinner;

function useStyles(size: SpinnerSize) {
  return useMemo(() => {
    const config = Spinner[size];
    return {
      spinner: {
        width: config.size,
        height: config.size,
        border: `${config.border}px solid ${Spinner.trackColor}`,
        borderTopColor: Colors.accentPurple,
        borderRadius: '50%',
        animation: `spin ${Spinner.duration} linear infinite`,
      } as CSSProperties,
    };
  }, [size]);
}
