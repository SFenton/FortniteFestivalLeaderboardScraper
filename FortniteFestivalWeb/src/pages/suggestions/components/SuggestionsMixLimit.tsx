import { useId, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import {
  Colors, Font, Gap, Radius, Weight,
  flexColumn, padding,
} from '@festival/theme';
import PressableButton from '../../../components/common/PressableButton';

type SuggestionsMixLimitProps = {
  onStartNewMix: () => void;
};

export function SuggestionsMixLimit({ onStartNewMix }: SuggestionsMixLimitProps) {
  const { t } = useTranslation();
  const messageId = useId();

  return (
    <div data-testid="suggestions-mix-limit" style={styles.mixLimit}>
      <div id={messageId} style={styles.mixLimitMessage}>
        {t('suggestions.mixLimitReached')}
      </div>
      <PressableButton
        data-testid="suggestions-start-new-mix"
        aria-describedby={messageId}
        style={styles.mixLimitButton}
        onPress={onStartNewMix}
      >
        {t('suggestions.startNewMix')}
      </PressableButton>
    </div>
  );
}

const styles = {
  mixLimit: {
    ...flexColumn,
    alignItems: 'center',
    gap: Gap.lg,
    padding: padding(Gap.section, Gap.xl),
    textAlign: 'center',
  } as CSSProperties,
  mixLimitMessage: {
    color: Colors.textSecondary,
    fontSize: Font.md,
    fontWeight: Weight.semibold,
  } as CSSProperties,
  mixLimitButton: {
    border: 0,
    borderRadius: Radius.full,
    padding: padding(Gap.md, Gap.xl),
    backgroundColor: Colors.accentPurple,
    color: Colors.textPrimary,
    fontSize: Font.md,
    fontWeight: Weight.bold,
    cursor: 'pointer',
  } as CSSProperties,
};
