import settingsEnglish from '../../i18n/settings.en.json';
import i18n from '../../i18n';
import serviceInfoEnglish from './serviceInfo.en.json';

i18n.addResourceBundle('en', 'settings', {
  settings: {
    ...settingsEnglish.settings,
    serviceInfo: serviceInfoEnglish,
  },
}, true, true);
