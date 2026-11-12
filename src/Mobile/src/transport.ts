import { NativeModule, requireOptionalNativeModule } from 'expo';

import type { PairingReply } from './protocol';

export type StreamUpdate = {
  id: string;
  state: 'connected' | 'closed' | 'error';
  message?: string;
};

declare class RtxLensTransport extends NativeModule<{
  onStreamState: (event: StreamUpdate) => void;
  onFrame: (event: { id: string; json: string }) => void;
}> {
  post(address: string, pin: string, path: string, json: string): Promise<PairingReply>;
  openStream(id: string, address: string, pin: string, token: string): void;
  closeStream(id: string): void;
}

export const transport = requireOptionalNativeModule<RtxLensTransport>('RtxLensTransport');
