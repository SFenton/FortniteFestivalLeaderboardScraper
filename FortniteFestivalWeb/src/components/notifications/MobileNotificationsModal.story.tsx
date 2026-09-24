import { useState } from 'react';
import MobileNotificationsModal from './MobileNotificationsModal';
import { mockMobileNotifications } from './notificationMocks';
import type { MobileNotification } from './notificationTypes';

const LOCAL_ART = '/icons/fst-icon.svg';
const previewNotifications: readonly MobileNotification[] = mockMobileNotifications.map(notification => {
  const { media } = notification;
  if (media.kind === 'song' || media.kind === 'songInstrumentGrid') {
    return { ...notification, media: { ...media, albumArt: LOCAL_ART } };
  }
  if (media.kind === 'instrumentCombo' && media.cycleAlbumArt) {
    return {
      ...notification,
      media: { ...media, cycleAlbumArt: { ...media.cycleAlbumArt, albumArt: LOCAL_ART } },
    };
  }
  return notification;
});
const previewNotificationIds = new Set(previewNotifications.map(notification => notification.notificationGuid));

export function RotationPreview() {
  const [visible, setVisible] = useState(false);

  return (
    <main style={{ padding: 24 }}>
      <h1>Notification rotation preview</h1>
      <button type="button" onClick={() => setVisible(true)}>Open notifications</button>
      <MobileNotificationsModal
        visible={visible}
        onClose={() => setVisible(false)}
        notifications={previewNotifications}
        unreadNotificationIds={previewNotificationIds}
        newNotificationIds={previewNotificationIds}
      />
    </main>
  );
}
