import type { ConfigContext, ExpoConfig } from 'expo/config';

export default function configure({ config }: ConfigContext): Partial<ExpoConfig> {
  const projectId = process.env.RTXLENS_EXPO_PROJECT_ID;
  const ios = process.env.RTXLENS_IOS_PUSH === '1';
  const iosEnvironment = process.env.RTXLENS_IOS_PUSH_ENVIRONMENT ?? 'development';
  const googleServicesFile = process.env.RTXLENS_GOOGLE_SERVICES_FILE;
  if ((ios || googleServicesFile) && !projectId) {
    throw new Error('Set RTXLENS_EXPO_PROJECT_ID to enable push notifications.');
  }
  if (!projectId) {
    return config;
  }
  if (!/^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/i.test(projectId)) {
    throw new Error('RTXLENS_EXPO_PROJECT_ID must be the UUID of the Expo project.');
  }
  if (ios && iosEnvironment !== 'development' && iosEnvironment !== 'production') {
    throw new Error('RTXLENS_IOS_PUSH_ENVIRONMENT must be development or production.');
  }

  return {
    ...config,
    extra: { ...config.extra, eas: { projectId }, rtxlensPush: { ios, android: Boolean(googleServicesFile) } },
    android: googleServicesFile ? { ...config.android, googleServicesFile } : config.android,
    plugins: config.plugins?.map(plugin => Array.isArray(plugin) && plugin[0] === 'expo-notifications'
      ? ['expo-notifications', { ...plugin[1], enableRemoteNotifications: ios, mode: iosEnvironment }]
      : plugin),
  };
}
