import { memo, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { IoTimeOutline } from 'react-icons/io5';
import {
  Colors,
  Font,
  Gap,
  Radius,
  Weight,
  flexRow,
  frostedCard,
} from '@festival/theme';

type CatalogUpdateBannerProps = {
  count: number;
};

const CatalogUpdateBanner = memo(function CatalogUpdateBanner({
  count,
}: CatalogUpdateBannerProps) {
  const { t } = useTranslation();

  return (
    <div
      role="status"
      aria-live="polite"
      data-testid="catalog-update-banner"
      style={styles.banner}
    >
      <IoTimeOutline
        aria-hidden="true"
        size={20}
        style={styles.icon}
      />
      <div style={styles.copy}>
        <strong style={styles.title}>
          {t('songs.catalogUpdatesPending', { count })}
        </strong>
        <span style={styles.detail}>
          {t('songs.catalogUpdatesAwaitingPublication')}
        </span>
      </div>
    </div>
  );
});

export default CatalogUpdateBanner;

const styles = {
  banner: {
    ...frostedCard,
    ...flexRow,
    alignItems: 'center',
    gap: Gap.md,
    borderRadius: Radius.lg,
    padding: `${Gap.md}px ${Gap.lg}px`,
    color: Colors.textPrimary,
  } as CSSProperties,
  icon: {
    color: Colors.accentBlueBright,
    flexShrink: 0,
  } as CSSProperties,
  copy: {
    display: 'flex',
    flexDirection: 'column',
    gap: Gap.xs,
    minWidth: 0,
  } as CSSProperties,
  title: {
    fontSize: Font.md,
    fontWeight: Weight.semibold,
  } as CSSProperties,
  detail: {
    color: Colors.textSecondary,
    fontSize: Font.sm,
  } as CSSProperties,
};
