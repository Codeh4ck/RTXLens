import * as Notifications from 'expo-notifications';
import { Platform } from 'react-native';

const channelId = 'rtxlens-warnings';

Notifications.setNotificationHandler({
  handleNotification: async () => ({
    shouldShowBanner: true,
    shouldShowList: true,
    shouldPlaySound: true,
    shouldSetBadge: false,
  }),
});

export async function notificationPermission(request = false): Promise<'granted' | 'available' | 'blocked'> {
  if (Platform.OS === 'android') {
    await Notifications.setNotificationChannelAsync(channelId, {
      name: 'RTX Lens warnings', importance: Notifications.AndroidImportance.HIGH, sound: 'default',
    });
  }

  let permission = await Notifications.getPermissionsAsync();
  if (request && !permission.granted && permission.canAskAgain) {
    permission = await Notifications.requestPermissionsAsync();
  }

  return permission.granted ? 'granted' : permission.canAskAgain ? 'available' : 'blocked';
}

export function showWarningNotification(title: string, body: string) {
  return Notifications.scheduleNotificationAsync({
    content: { title, body, sound: 'default', data: { screen: 'warnings' } },
    trigger: Platform.OS === 'android' ? { channelId } : null,
  });
}

export function onWarningNotificationOpened(open: () => void) {
  const handle = (response: Notifications.NotificationResponse | null) => {
    if (response?.notification.request.content.data?.screen === 'warnings') {
      open();
      Notifications.clearLastNotificationResponse();
    }
  };

  const subscription = Notifications.addNotificationResponseReceivedListener(handle);
  handle(Notifications.getLastNotificationResponse());
  return subscription;
}
