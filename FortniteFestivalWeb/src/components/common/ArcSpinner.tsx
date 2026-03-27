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

/** Duration of one spin cycle in ms, parsed once from the theme token. */
const SPIN_DURATION_MS = parseFloat(Spinner.duration) * 1000;

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
        /* Sync all spinner instances to the same rotation angle via wall-clock time.
           A negative delay fast-forwards the animation so a newly mounted spinner
           picks up at the same phase as any already-visible one. */
        animationDelay: `${-(performance.now() % SPIN_DURATION_MS)}ms`,
      } as CSSProperties,
    };
  }, [size]);
}
