import type { SensorFrame } from './protocol';

type WarningEvent = { title: string; detail: string; simulation: boolean };

export class WarningEvents {
  private instance: string | null = null;
  private seen = new Map<string, number>();
  private simulations = new Set<string>();

  receive(frame: SensorFrame): WarningEvent[] {
    const events: WarningEvent[] = [];
    if (frame.warningInstance !== this.instance) {
      this.instance = frame.warningInstance;
      this.seen.clear();
    }

    const activeEpisodes = new Set(frame.warnings.map(warning => warning.episodeId));
    for (const episode of this.seen.keys()) {
      if (!activeEpisodes.has(episode)) {
        this.seen.delete(episode);
      }
    }

    for (const warning of frame.warnings) {
      if (warning.phase === 'Active' && warning.notificationSequence > (this.seen.get(warning.episodeId) ?? 0)) {
        this.seen.set(warning.episodeId, warning.notificationSequence);
        if (frame.notificationsEnabled) {
          events.push({ title: warning.title, detail: warning.detail, simulation: false });
        }
      }
    }

    const currentSimulations = new Set(frame.simulationWarnings.map(warning => warning.id));
    for (const warning of frame.simulationWarnings) {
      if (!this.simulations.has(warning.id)) {
        events.push({ title: warning.title, detail: warning.detail, simulation: true });
      }
    }
    this.simulations = currentSimulations;

    return events;
  }
}
