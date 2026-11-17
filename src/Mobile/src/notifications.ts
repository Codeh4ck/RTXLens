import Constants from 'expo-constants';
import * as Notifications from 'expo-notifications';
import { Platform } from 'react-native';

import { warningNotificationIncident, type IncidentRef } from './incidentProtocol';

const channelId = 'rtxlens-warnings';
const build = Constants.expoConfig?.extra;
const projectId = build?.eas?.projectId;

export const lockScreenSupported = typeof projectId === 'string'
  && (Platform.OS === 'ios' || Platform.OS === 'android') && build?.rtxlensPush?.[Platform.OS] === true;

export async function requestPushToken(devicePushToken?: Notifications.DevicePushToken) {
  const token = await Notifications.getExpoPushTokenAsync({ projectId, devicePushToken });
  return token.data;
}

export const onPushTokenChanged = Notifications.addPushTokenListener;

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

export function showWarningNotification(title: string, body: string, incident?: IncidentRef) {
  const data = incident ? { screen: 'warnings', incidentId: incident.id, kind: incident.kind } : { screen: 'warnings' };
  return Notifications.scheduleNotificationAsync({
    content: { title, body, sound: 'default', data },
    trigger: Platform.OS === 'android' ? { channelId } : null,
  });
}

export function onWarningNotificationOpened(open: (incident: IncidentRef | null) => void) {
  const handle = (response: Notifications.NotificationResponse | null) => {
    const data = response?.notification.request.content.data;
    if (data?.screen === 'warnings') {
      open(warningNotificationIncident(data));
      Notifications.clearLastNotificationResponse();
    }
  };

  const subscription = Notifications.addNotificationResponseReceivedListener(handle);
  handle(Notifications.getLastNotificationResponse());
  return subscription;
}
