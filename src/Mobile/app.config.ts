import type { ConfigContext, ExpoConfig } from 'expo/config';
import { withAppBuildGradle } from 'expo/config-plugins';

export default function configure({ config }: ConfigContext): Partial<ExpoConfig> {
  config = withStoreBuildNumber(config);
  if (process.env.RTXLENS_PLAY_SIGNING === '1') {
    config = withPlaySigning(config);
  }

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

function withStoreBuildNumber(config: Partial<ExpoConfig>): Partial<ExpoConfig> {
  const buildNumber = process.env.RTXLENS_STORE_BUILD_NUMBER;
  if (buildNumber === undefined) {
    return config;
  }

  if (!/^[1-9]\d*$/.test(buildNumber) || Number(buildNumber) > 2_100_000_000) {
    throw new Error('Set RTXLENS_STORE_BUILD_NUMBER to a positive integer no greater than 2100000000.');
  }

  return {
    ...config,
    ios: { ...config.ios, buildNumber },
    android: { ...config.android, versionCode: Number(buildNumber) },
  };
}

function withPlaySigning(config: Partial<ExpoConfig>): ExpoConfig {
  const { name, slug } = config;
  if (!name || !slug) {
    throw new Error('The Play build needs an Expo app name and slug.');
  }

  return withAppBuildGradle({ ...config, name, slug }, config => {
    if (config.modResults.language !== 'groovy') {
      throw new Error('The Play signing configuration expects the Expo Groovy build.gradle template.');
    }

    config.modResults.contents += `
android {
    signingConfigs {
        playUpload {
            storeFile file(System.getenv('RTXLENS_PLAY_KEYSTORE'))
            storePassword System.getenv('RTXLENS_PLAY_STORE_PASSWORD')
            keyAlias System.getenv('RTXLENS_PLAY_KEY_ALIAS')
            keyPassword System.getenv('RTXLENS_PLAY_KEY_PASSWORD')
        }
    }

    buildTypes {
        release {
            signingConfig signingConfigs.playUpload
        }
    }
}
`;
    return config;
  });
}
