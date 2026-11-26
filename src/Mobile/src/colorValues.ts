export type HsvColor = { hue: number; saturation: number; brightness: number };

export function hexToHsv(hex: string): HsvColor {
  const [red, green, blue] = [1, 3, 5].map(index => parseInt(hex.slice(index, index + 2), 16) / 255);
  const brightness = Math.max(red, green, blue);
  const delta = brightness - Math.min(red, green, blue);
  const saturation = brightness === 0 ? 0 : delta / brightness;

  if (delta === 0) return { hue: 0, saturation, brightness };

  const channel = brightness === red ? (green - blue) / delta : brightness === green ? 2 + (blue - red) / delta : 4 + (red - green) / delta;
  return { hue: (channel * 60 + 360) % 360, saturation, brightness };
}

export function hsvToHex({ hue, saturation, brightness }: HsvColor): string {
  const chroma = brightness * saturation;
  const sector = (hue % 360) / 60;
  const middle = chroma * (1 - Math.abs(sector % 2 - 1));
  const base = brightness - chroma;
  
  const values = [
    [chroma, middle, 0], [middle, chroma, 0], [0, chroma, middle],
    [0, middle, chroma], [middle, 0, chroma], [chroma, 0, middle],
  ][Math.floor(sector)];

  return '#' + values.map(value => Math.round((value + base) * 255).toString(16).padStart(2, '0')).join('').toUpperCase();
}
