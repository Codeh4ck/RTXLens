import type { DevicePushToken } from 'expo-notifications';
import * as SecureStore from 'expo-secure-store';
import { useCallback, useEffect, useRef, useState, type RefObject } from 'react';

import type { NotificationPreferences } from './mobilePreferences';
import { lockScreenSupported, notificationPermission, onPushTokenChanged, requestPushToken } from './notifications';
import type { PairedComputer } from './protocol';
import { transport } from './transport';

const preferenceKey = 'rtxlens.lock-screen-pairing';

export function useLockScreenNotifications(computer: PairedComputer | null, address: string | undefined, live: boolean,
  preferences: RefObject<NotificationPreferences>, preferencesLoaded: boolean) {
  const pairingToken = computer?.token;
  const fingerprint = computer?.publicKeyFingerprint;
  const [preference, setPreference] = useState<string | null>();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const registered = useRef(false);
  const [ready, setReady] = useState(false);
  const operation = useRef<Promise<boolean> | null>(null);
  const latestNativeToken = useRef<DevicePushToken | undefined>(undefined);
  const registeredToken = useRef<string | null>(null);
  const enabled = pairingToken !== undefined && preference === pairingToken;
  const available = lockScreenSupported && typeof transport?.registerNotifications === 'function';

  useEffect(() => {
    if (!available) return;
    
    let current = true;

    void SecureStore.getItemAsync(preferenceKey).then(value => {
      if (current) setPreference(previous => previous === undefined ? value : previous);
    }).catch(() => {
      if (current) setError('Could not read lock-screen notification settings.');
    });

    return () => { current = false; };
  }, [available]);

  const saveEnabledPairing = useCallback(async (token: string) => {
    await SecureStore.setItemAsync(preferenceKey, token, {
      keychainAccessible: SecureStore.WHEN_UNLOCKED_THIS_DEVICE_ONLY,
    });

    setPreference(token);
  }, []);

  const register = useCallback(async (token: string | null, options: NotificationPreferences) => {
    if (!transport || !pairingToken || !fingerprint || !address) {
      throw new Error('Connect to your PC to change lock-screen notifications.');
    }

    const result = await transport.registerNotifications(address, fingerprint, pairingToken, JSON.stringify({ token, preferences: options }));

    if (result.status === 404) throw new Error('Update the RTX Lens service to use lock-screen notifications.');
    if (result.status === 401) throw new Error('Pair with your PC again to use lock-screen notifications.');
    if (result.status !== 204) throw new Error('Could not update the notification registration on your PC.');
  }, [address, fingerprint, pairingToken]);

  const update = useCallback((enable: boolean, requestPermission = false, nextPreferences?: NotificationPreferences) => {
    if (operation.current) return nextPreferences ? Promise.resolve(false) : operation.current;

    setBusy(true);
    setError(null);

    const pending = (async () => {
      if (enable) {
        if (await notificationPermission(requestPermission) !== 'granted') {
          throw new Error('Allow RTX Lens notifications in your phone’s settings.');
        }

        if (requestPermission && pairingToken) {
          await saveEnabledPairing(pairingToken);
        }

        let nativeToken: DevicePushToken | undefined;
        do {
          nativeToken = latestNativeToken.current;
          let token = nextPreferences ? registeredToken.current : null;

          if (!token) {
            try {
              token = await requestPushToken(nativeToken);
            } catch {
              throw new Error('Could not register this phone with the notification service.');
            }
          }

          await register(token, nextPreferences ?? preferences.current);

          registeredToken.current = latestNativeToken.current === nativeToken ? token : null;
        } while (latestNativeToken.current !== nativeToken);
      } else {
        await register(null, preferences.current);
        registeredToken.current = null;

        await SecureStore.deleteItemAsync(preferenceKey);
        setPreference(null);
      }

      registered.current = enable;

      setReady(enable);
      return true;
    })().catch(failure => {
      if (enable && !nextPreferences) {
        registered.current = false;
        setReady(false);
      }

      setError(failure instanceof Error ? failure.message : 'Could not change lock-screen notifications.');
      return false;
    }).finally(() => {
      operation.current = null;
      setBusy(false);
    });

    operation.current = pending;
    return pending;
  }, [pairingToken, preferences, register, saveEnabledPairing]);

  useEffect(() => {
    if (!available || !enabled || !live || !preferencesLoaded) return;

    void update(true);

    const subscription = onPushTokenChanged(token => {
      latestNativeToken.current = token;
      registeredToken.current = null;

      void update(true);
    });

    return () => subscription.remove();
  }, [available, enabled, live, preferencesLoaded, update]);

  async function forgetRegistration() {
    await operation.current;

    const removed = !enabled && !registered.current || !!address && await update(false);
    await SecureStore.deleteItemAsync(preferenceKey);

    setPreference(null);

    registeredToken.current = null;
    registered.current = false;

    setReady(false);
    setError(null);
    
    return removed;
  }

  return { available, enabled, ready, busy, error, registered, set: update,
    applyPreferences: (options: NotificationPreferences) => update(true, false, options), forgetRegistration, saveEnabledPairing };
}
