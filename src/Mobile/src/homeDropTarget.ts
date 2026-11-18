export type PanelBounds = { x: number; y: number; width: number; height: number };

// Use the pre-drag slots as hit targets. Moving the placeholder must not move the hit targets under a stationary finger.
export function homeDropIndex(slots: readonly (PanelBounds | undefined)[], x: number, y: number, current: number): number {
  let nearest = current;
  let distance = Infinity;
  slots.forEach((bounds, index) => {
    if (!bounds) return;

    const dx = Math.max(bounds.x - x, x - bounds.x - bounds.width, 0);
    const dy = Math.max(bounds.y - y, y - bounds.y - bounds.height, 0);
    const candidate = dx * dx + dy * dy;
    if (candidate < distance || (candidate === distance && index === current)) {
      nearest = index;
      distance = candidate;
    }
  });
  return nearest;
}
