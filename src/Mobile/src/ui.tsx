import { useColors, useThemeStyles, type Colors } from './appearance';
import { createContext, useContext, type ReactNode } from 'react';
import { Pressable, ScrollView, StyleSheet, Text, View } from 'react-native';
import Svg, { Path } from 'react-native-svg';

export const HomeInteractionContext = createContext(false);
export function useHomeLongPress() {
  return useContext(HomeInteractionContext) ? consumeHomeLongPress : undefined;
}
// Home handles the gesture on its container. Suppress the child control's tap after a hold.
function consumeHomeLongPress() {}

export function useStyles() { return useThemeStyles(createStyles); }

const paths = {
  palette: 'M12 3a9 9 0 1 0 0 18h1a2 2 0 0 0 1-4c-1-1 0-3 2-3h2c5 0 3-11-6-11ZM7 10h1m3-3h1m4 2h1',
  home: 'm3 10 9-7 9 7v10H3zm6 10v-6h6v6',
  power: 'm13 2-9 12h7l-1 8 10-12h-7l1-8Z',
  gpu: 'M7 7h10v10H7zM9 1v3m6-3v3M9 20v3m6-3v3M1 9h3m-3 6h3m16-6h3m-3 6h3M4 4h16v16H4z',
  warnings: 'M18 8a6 6 0 0 0-12 0c0 7-3 7-3 9h18c0-2-3-2-3-9m-8 12a2 2 0 0 0 4 0',
  settings: 'M10 3h4l1 3 3 1 3 3v4l-3 3-3 1-1 3h-4l-1-3-3-1-3-3v-4l3-3 3-1zm5 9a3 3 0 1 0-6 0 3 3 0 0 0 6 0',
  scan: 'M4 8V4h4m8 0h4v4m0 8v4h-4m-8 0H4v-4m1-4h14',
  close: 'm6 6 12 12M18 6 6 18',
  add: 'M12 5v14M5 12h14',
  up: 'm5 14 7-7 7 7',
  down: 'm5 10 7 7 7-7',
  away: 'M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18ZM3 12h18m-9-9a14 14 0 0 1 0 18m0-18a14 14 0 0 0 0 18',
  refresh: 'M20 7v5h-5m5-1a8 8 0 1 0-2 7',
  link: 'M8 13a4 4 0 0 0 6 0l4-4a4 4 0 1 0-6-6l-2 2m6 6a4 4 0 0 0-6 0l-4 4a4 4 0 1 0 6 6l2-2',
  check: 'm5 12 4 4L19 6',
  next: 'm9 6 6 6-6 6',
  back: 'm15 6-6 6 6 6',
  temp: 'M9 14V5a3 3 0 0 1 6 0v9a5 5 0 1 1-6 0Zm3-6v9',
  activity: 'M2 12h5l3-8 4 16 3-8h5',
  current: 'M4 16c4 0 4-8 8-8s4 8 8 8M4 8h2m12 0h2',
  monitor: 'M3 4h18v13H3zm5 17h8m-4-4v4',
};

export type IconName = keyof typeof paths;
export type Tab = 'home' | 'power' | 'gpu' | 'warnings' | 'settings';
const tabs: { id: Tab; label: string }[] = [
  { id: 'home', label: 'Home' }, { id: 'power', label: 'Power' }, { id: 'gpu', label: 'GPU' },
  { id: 'warnings', label: 'Warnings' }, { id: 'settings', label: 'Settings' },
];

export function Icon({ name, color, size = 20 }: { name: IconName; color?: string; size?: number }) {
  const colors = useColors();
  return (
    <Svg width={size} height={size} viewBox="0 0 24 24" fill="none">
      <Path d={paths[name]} stroke={color ?? colors.accent} strokeWidth={1.8} strokeLinecap="round" strokeLinejoin="round" />
    </Svg>
  );
}

export function BottomNavigation({ selected, warningCount, onSelect }: {
  selected: Tab; warningCount: number; onSelect: (tab: Tab) => void;
}) {
  const styles = useStyles();
  const colors = useColors();
  return (
    <View accessibilityRole="tablist" style={styles.navigation}>
      {tabs.map(({ id, label }) => {
        const active = id === selected;
        const badge = id === 'warnings' && warningCount > 0;
        return (
          <Pressable key={id} accessibilityRole="tab" accessibilityState={{ selected: active }}
            accessibilityLabel={badge ? `${label}, ${warningCount} active` : label}
            onPress={() => onSelect(id)} style={({ pressed }) => [styles.tab, pressed && styles.pressed]}>
            <View style={[styles.tabIcon, active && styles.activeTabIcon]}>
              <Icon name={id} color={active ? colors.accent : colors.muted} />
              {badge && <View style={styles.dot} />}
            </View>
            <Text style={[styles.tabLabel, active && styles.activeTabLabel]}>{label}</Text>
          </Pressable>
        );
      })}
    </View>
  );
}

export function Page({ title, description, children }: { title: string; description?: string; children: ReactNode }) {
  const styles = useStyles();
  return (
    <ScrollView contentContainerStyle={styles.page}>
      <View style={styles.pageHeader}>
        <Text accessibilityRole="header" style={styles.title}>{title}</Text>
        {description && <Text style={styles.caption}>{description}</Text>}
      </View>
      {children}
    </ScrollView>
  );
}

export function Card({ title, icon, children }: { title?: string; icon?: IconName; children: ReactNode }) {
  const styles = useStyles();
  return (
    <View style={styles.card}>
      {title && <View style={styles.row}>{icon && <Icon name={icon} />}<Text accessibilityRole="header" style={styles.heading}>{title}</Text></View>}
      {children}
    </View>
  );
}

export function Button({ icon, label, variant = 'secondary', disabled = false, onPress }: {
  icon: IconName; label: string; variant?: 'secondary' | 'primary' | 'danger'; disabled?: boolean; onPress: () => void;
}) {
  const styles = useStyles();
  const colors = useColors();
  const onLongPress = useHomeLongPress();
  const color = variant === 'primary' ? colors.onAccent : variant === 'danger' ? colors.danger : colors.text;
  return (
    <Pressable accessibilityRole="button" accessibilityState={{ disabled }} disabled={disabled} onLongPress={onLongPress} delayLongPress={420} onPress={onPress}
      style={({ pressed }) => [styles.button, variant === 'primary' && styles.primaryButton, (pressed || disabled) && styles.pressed]}>
      <Icon name={icon} color={color} size={17} />
      <Text style={[styles.buttonLabel, { color }]}>{label}</Text>
    </Pressable>
  );
}

export function Selection<T extends string>({ options, selected, onSelect }: {
  options: { id: T; label: string }[]; selected: T; onSelect: (id: T) => void;
}) {
  const styles = useStyles();
  const onLongPress = useHomeLongPress();
  return (
    <View style={styles.selection}>
      {options.map(option => (
        <Pressable key={option.id} accessibilityRole="button" accessibilityState={{ selected: option.id === selected }}
          onLongPress={onLongPress} delayLongPress={420} onPress={() => onSelect(option.id)} style={[styles.option, option.id === selected && styles.activeOption]}>
          <Text style={[styles.optionText, option.id === selected && styles.activeTabLabel]}>{option.label}</Text>
        </Pressable>
      ))}
    </View>
  );
}

export function Notice({ children }: { children: string }) {
  const styles = useStyles();
  return <Text accessibilityRole="alert" style={styles.notice}>{children}</Text>;
}

const createStyles = (colors: Colors) => StyleSheet.create({
  screen: { flex: 1, backgroundColor: colors.background },
  bottom: { backgroundColor: colors.surface, borderTopWidth: 1, borderColor: colors.line },
  page: { paddingHorizontal: 20, paddingTop: 15, paddingBottom: 24, gap: 16, alignSelf: 'center', width: '100%', maxWidth: 680 },
  pageHeader: { gap: 4, marginBottom: 4 },
  title: { color: colors.text, fontSize: 36, letterSpacing: -1.4, fontWeight: '700' },
  heading: { color: colors.text, fontSize: 17, fontWeight: '700', flexShrink: 1 },
  text: { color: colors.text, fontSize: 15 },
  caption: { color: colors.muted, fontSize: 14, lineHeight: 20 },
  card: { borderRadius: 21, backgroundColor: colors.surface, borderWidth: 1, borderColor: colors.line, padding: 16, gap: 12 },
  row: { flexDirection: 'row', alignItems: 'center', gap: 9 },
  actions: { flexDirection: 'row', flexWrap: 'wrap', gap: 8 },
  button: { flexDirection: 'row', gap: 8, alignItems: 'center', justifyContent: 'center', minHeight: 46, paddingHorizontal: 14,
    borderRadius: 12, borderWidth: 1, borderColor: colors.line, backgroundColor: colors.raised },
  primaryButton: { backgroundColor: colors.accent, borderColor: colors.accent },
  buttonLabel: { fontSize: 14, fontWeight: '700', flexShrink: 1 },
  pressed: { opacity: 0.72 },
  navigation: { flexDirection: 'row', paddingHorizontal: 8, paddingVertical: 7, gap: 2, alignSelf: 'center', maxWidth: 680, width: '100%' },
  tab: { flex: 1, alignItems: 'center', justifyContent: 'center', minHeight: 51, gap: 4 },
  tabIcon: { alignItems: 'center', justifyContent: 'center', width: 40, height: 30, borderRadius: 11 },
  activeTabIcon: { backgroundColor: colors.accentSurface },
  tabLabel: { color: colors.muted, fontSize: 11, fontWeight: '600' },
  activeTabLabel: { color: colors.tertiary },
  dot: { position: 'absolute', top: 4, right: 7, width: 8, height: 8, borderRadius: 4, backgroundColor: colors.warning },
  selection: { flexDirection: 'row', flexWrap: 'wrap', gap: 4, borderRadius: 13, backgroundColor: colors.background, padding: 4 },
  option: { flexGrow: 1, minHeight: 42, justifyContent: 'center', alignItems: 'center', paddingHorizontal: 9, borderRadius: 9 },
  activeOption: { backgroundColor: colors.accentSurface },
  optionText: { color: colors.muted, fontWeight: '600', fontSize: 13 },
  notice: { color: colors.warning, backgroundColor: colors.warningSurface, borderRadius: 12, padding: 12, lineHeight: 20 },
});
