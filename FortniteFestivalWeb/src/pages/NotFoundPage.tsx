import { useMemo, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import {
  Colors, Font, Gap, Radius, Border, Weight, CssValue,
  flexColumn, padding, border,
} from '@festival/theme';
import Page from './Page';
import { PageMessage } from './PageMessage';
import PageHeader from '../components/common/PageHeader';
import { Routes } from '../routes';

export default function NotFoundPage() {
  const { t } = useTranslation();
  const styles = useStyles();

  return (
    <Page before={<PageHeader title={t('apiError.notFound')} />}>
      <PageMessage>
        <div style={styles.content}>
          <p style={styles.message}>{t('apiError.notFoundSubtitle')}</p>
          <Link to={Routes.songs} style={styles.link}>
            {t('error.goToSongs')}
          </Link>
        </div>
      </PageMessage>
    </Page>
  );
}

function useStyles() {
  return useMemo(() => ({
    content: {
      ...flexColumn,
      alignItems: 'center',
      gap: Gap.xl,
      textAlign: 'center',
    } as CSSProperties,
    message: {
      margin: Gap.none,
    } as CSSProperties,
    link: {
      padding: padding(Gap.lg, Gap.section),
      borderRadius: Radius.xs,
      border: border(Border.thin, Colors.accentBlue),
      backgroundColor: Colors.chipSelectedBg,
      color: Colors.textPrimary,
      fontSize: Font.md,
      fontWeight: Weight.semibold,
      textDecoration: CssValue.none,
    } as CSSProperties,
  }), []);
}
