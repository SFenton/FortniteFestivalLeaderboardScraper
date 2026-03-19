import { useNavigate } from 'react-router-dom';
import { IoChevronBack } from 'react-icons/io5';
import { InstrumentIcon } from '../../display/InstrumentIcons';
import BackLink from './BackLink';
import css from './MobileHeader.module.css';
import { type ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import { Size, TRANSITION_MS } from '@festival/theme';

export interface MobileHeaderProps {
  navTitle: string | null;
  backFallback: string | null;
  shouldAnimate: boolean;
  locationKey: string;
  /** Current instrument filter (shown as icon on /songs). */
  songInstrument: InstrumentKey | null;
  /** Whether we're on the /songs route. */
  isSongsRoute: boolean;
}

export default function MobileHeader({
  navTitle,
  backFallback,
  shouldAnimate,
  locationKey,
  songInstrument,
  isSongsRoute,
}: MobileHeaderProps) {
  const navigate = useNavigate();

  /* v8 ignore start — conditional rendering tested via AppMobile integration */
  if (navTitle) {
    return (
      <div key={locationKey} className={`sa-top ${css.header}`} style={shouldAnimate ? { animation: `fadeIn ${TRANSITION_MS}ms ease-out` } : undefined}>
        {backFallback ? (
          <a
            href="#"
            /* v8 ignore start */
            onClick={(e) => { e.preventDefault(); navigate(-1); }}
            /* v8 ignore stop */
            className={css.titleBack}
          >
            <IoChevronBack size={Size.iconNav} />
            <span>{navTitle}</span>
          </a>
        ) : (
          <span className={css.title}>{navTitle}</span>
        )}
        {isSongsRoute && songInstrument && (
          <InstrumentIcon instrument={songInstrument} size={Size.iconInstrumentSm} style={{ marginLeft: 'auto' }} />
        )}
      </div>
    );
  }

  if (backFallback) {
    return <BackLink key={locationKey} fallback={backFallback} animate={shouldAnimate} />;
  }

  return null;
  /* v8 ignore stop */
}
