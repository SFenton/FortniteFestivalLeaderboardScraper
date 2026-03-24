import { IoBagHandle } from 'react-icons/io5';
import { Gap } from '@festival/theme';
import { useSettings } from '../../../../contexts/SettingsContext';
import css from './ShopButtonDemo.module.css';

/**
 * Demo component for the SongInfo FRE slide showing the Item Shop button.
 * Matches the production SongDetailHeader pill/circle exactly.
 * Pulse is only active when shop highlighting is enabled in settings.
 */
/* v8 ignore start -- demo component uses SettingsContext */
export default function ShopButtonDemo({ mobile }: { mobile?: boolean }) {
  const { settings } = useSettings();
  const pulse = !settings.disableShopHighlighting && !settings.hideItemShop;

  if (mobile) {
    return (
      <div className={css.wrap}>
        <div className={pulse ? `${css.shopCircle} ${css.pulse}` : css.shopCircle}>
          <IoBagHandle size={72} />
        </div>
      </div>
    );
  }

  return (
    <div className={css.wrap}>
      <div className={pulse ? `${css.shopButton} ${css.shopPulse}` : css.shopButton}>
        <IoBagHandle size={28} style={{ marginRight: Gap.lg }} />
        Item Shop
      </div>
    </div>
  );
}
/* v8 ignore stop */
