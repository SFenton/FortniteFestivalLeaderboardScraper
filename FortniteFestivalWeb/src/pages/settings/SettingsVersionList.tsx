import { memo } from 'react';
import { useTranslation } from 'react-i18next';
import styles from './SettingsVersionList.module.css';

export type SettingsVersionRow = {
  id: string;
  label: string;
  value: string;
  commit?: string;
  testId?: string;
};

interface SettingsVersionListProps {
  rows: readonly SettingsVersionRow[];
}

/**
 * Label/value pairs as a description list so assistive technology ties each
 * value to its label. The visible "·" before a commit is decorative; screen
 * readers hear "<version>, commit <sha>" instead.
 */
const SettingsVersionList = memo(function SettingsVersionList({ rows }: SettingsVersionListProps) {
  const { t } = useTranslation(['translation', 'settings'], { nsMode: 'fallback' });
  return (
    <dl className={styles.list} data-testid="settings-version-list">
      {rows.map(row => (
        <div key={row.id} className={styles.row}>
          <dt className={styles.term}>{row.label}</dt>
          <dd className={styles.value} data-testid={row.testId}>
            {row.value}
            {row.commit ? (
              <>
                <span aria-hidden="true"> · </span>
                <span className={styles.visuallyHidden}>, {t('settings.appCommit')} </span>
                {row.commit}
              </>
            ) : null}
          </dd>
        </div>
      ))}
    </dl>
  );
});

export default SettingsVersionList;
