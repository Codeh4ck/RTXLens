import { createContext, useContext, useMemo } from 'react';

export type Palette = { primary: string; secondary: string; tertiary: string; background: string };
export const schemes = [
  ['Default', '#39EED1 #7DDCC7 #79D5DE #090E17', '#007F76 #0C665B #236872 #F3F6FA'],
  ['Ultraviolet', '#BF9AFF #DAB9FF #F3ACED #110D1D', '#6743A9 #8056B7 #9C387D #F6F1FC'],
  ['Emerald Circuit', '#61E995 #9ADDB6 #C1E77E #0B1611', '#12713C #2E7955 #4B7317 #EFF8F2'],
  ['Crimson Pulse', '#FF7888 #F4A5AE #FFBCA1 #1B0E15', '#A92D46 #963A5E #973F2F #FCF0F3'],
  ['Amber Signal', '#FFD26C #F3C196 #EBE18B #1A130A', '#775007 #87501D #6B6313 #FBF5E8'],
  ['Cobalt Surge', '#78B2FF #9BCDFF #A8B6FF #0A1220', '#235DAA #246A91 #4B50A8 #EEF4FC'],
] as const;

export type Appearance = { scheme: string; light: boolean; custom: Palette; panelGlow: boolean };
function unpack(hex: string): Palette {
  const [primary, secondary, tertiary, background] = hex.split(' ');
  return { primary, secondary, tertiary, background };
}
export const defaultAppearance: Appearance = { scheme: 'Default', light: false, custom: unpack(schemes[0][1]), panelGlow: false };
export function validColor(value: unknown): value is string {
  return typeof value === 'string' && /^#[0-9a-f]{6}$/i.test(value);
}
export function decodeAppearance(raw: string): Appearance {
  const value = JSON.parse(raw);
  if (typeof value?.light !== 'boolean' || !(value.scheme === 'Custom' || schemes.some(([name]) => name === value.scheme))
    || !Object.keys(defaultAppearance.custom).every(key => validColor(value.custom?.[key]))) return defaultAppearance;
  return { scheme: value.scheme, light: value.light, custom: value.custom, panelGlow: value.panelGlow === true };
}
export function selectedPalette(value: Appearance) {
  const preset = schemes.find(([name]) => name === value.scheme);
  return preset ? unpack(preset[value.light ? 2 : 1]) : value.custom;
}

function channels(hex: string) {
  return [1, 3, 5].map(index => parseInt(hex.slice(index, index + 2), 16));
}
function mix(a: string, b: string, weight: number) {
  const target = channels(b);
  return '#' + channels(a).map((v, i) => Math.round(v + (target[i] - v) * weight).toString(16).padStart(2, '0')).join('');
}
function luminance(hex: string) {
  const [r, g, b] = channels(hex).map(channel => {
    const v = channel / 255;
    return v <= 0.04045 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}
export function isLight(hex: string) { return luminance(hex) > 0.179; }

export function themeColors(value: Appearance) {
  const { background, primary, secondary, tertiary } = selectedPalette(value);
  const light = isLight(background);
  const text = light ? '#000000' : '#FFFFFF';
  const surface = mix(background, text, 0.05);
  function readable(color: string) {
    const base = luminance(surface) + 0.05;
    for (let step = 0; step < 20; step++) {
      const candidate = mix(color, text, step / 20);
      const l = luminance(candidate) + 0.05;
      if (Math.max(l, base) / Math.min(l, base) >= 4.5) return candidate;
    }
    return text;
  }
  const accent = readable(primary);
  return { background, surface, text, accent, panelGlow: value.panelGlow, panelHeader: mix(surface, accent, 0.075), secondary: readable(secondary), tertiary: readable(tertiary),
    onAccent: isLight(accent) ? '#000000' : '#FFFFFF', raised: mix(background, text, 0.09), line: mix(background, text, 0.24),
    muted: mix(surface, text, 0.7), accentSurface: mix(background, accent, 0.14),
    warning: light ? '#794500' : '#FFD08B', warningSurface: light ? '#FFF0D6' : '#392C20',
    danger: light ? '#981E42' : '#FF9AB0', dangerSurface: light ? '#FCE4EB' : '#391F2C' };
}
export type Colors = ReturnType<typeof themeColors>;
export const ThemeContext = createContext(themeColors(defaultAppearance));
export function useColors() { return useContext(ThemeContext); }
export function useThemeStyles<T>(create: (colors: Colors) => T) {
  const colors = useColors();
  return useMemo(() => create(colors), [create, colors]);
}
