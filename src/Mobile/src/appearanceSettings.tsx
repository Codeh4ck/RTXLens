import { useState } from 'react';
import { Pressable, Switch, Text, View } from 'react-native';
import { schemes, selectedPalette, useColors, type Appearance, type Palette } from './appearance';
import { ColorPicker } from './colorPicker';
import { Sheet } from './sheet';
import { Button, Card, Icon, Notice, Selection, useStyles } from './ui';

const schemeNames = [...schemes.map(([name]) => name), 'Custom'];

export function AppearanceSettings({ value, save, editHome, canEditHome }: {
  value: Appearance; save: (next: Appearance) => Promise<void>; editHome: () => void; canEditHome: boolean;
}) {
  const styles = useStyles();
  const colors = useColors();
  const [custom, setCustom] = useState(value.custom);
  const [error, setError] = useState('');
  const [showSchemes, setShowSchemes] = useState(false);
  const [picking, setPicking] = useState<keyof Palette | null>(null);
  async function change(next: Appearance) {
    try { await save(next); setError(''); }
    catch { setError('Could not save appearance settings.'); }
  }

  return <Card title="Appearance" icon="palette">
    <Selection options={[{ id: 'dark', label: 'Dark' }, { id: 'light', label: 'Light' }]} selected={value.light ? 'light' : 'dark'}
      onSelect={mode => void change({ ...value, light: mode === 'light' })} />
    <Pressable accessibilityRole="combobox" accessibilityLabel={`Color scheme, ${value.scheme}`}
      accessibilityState={{ expanded: showSchemes }} onPress={() => setShowSchemes(true)}
      style={({ pressed }) => [styles.button, { paddingVertical: 10 }, pressed && styles.pressed]}>
      <View style={{ flex: 1, gap: 2 }}>
        <Text style={styles.caption}>Color scheme</Text>
        <Text style={styles.heading}>{value.scheme}</Text>
      </View>
      <SchemeSwatches palette={selectedPalette(value)} />
      <Icon name="down" color={colors.muted} size={16} />
    </Pressable>
    {showSchemes && <Sheet title="Color scheme" close={() => setShowSchemes(false)}>
      {schemeNames.map(scheme => {
        const selected = scheme === value.scheme;
        return (
          <Pressable key={scheme} accessibilityRole="button" accessibilityState={{ selected }} onPress={() => {
            setShowSchemes(false);
            if (!selected) void change({ ...value, scheme });
          }} style={({ pressed }) => [
            styles.button, { paddingVertical: 8, backgroundColor: selected ? colors.accentSurface : colors.raised },
            pressed && styles.pressed,
          ]}>
            <Text style={[styles.text, { flex: 1 }, selected && { color: colors.accent, fontWeight: '700' }]}>{scheme}</Text>
            <SchemeSwatches palette={selectedPalette({ ...value, scheme })} />
            <View style={{ width: 18 }}>{selected && <Icon name="check" size={18} />}</View>
          </Pressable>
        );
      })}
    </Sheet>}
    {value.scheme === 'Custom' && <>
      {(Object.keys(custom) as (keyof Palette)[]).map(key => (
        <Pressable key={key} accessibilityRole="button" accessibilityLabel={`${key} color, ${custom[key]}`}
          onPress={() => setPicking(key)} style={({ pressed }) => [styles.row, { minHeight: 46 }, pressed && styles.pressed]}>
          <Text style={[styles.text, { flex: 1, textTransform: 'capitalize' }]}>{key}</Text>
          <View style={{ width: 24, height: 24, borderRadius: 12, backgroundColor: custom[key], borderColor: colors.line, borderWidth: 1 }} />
          <View style={[styles.button, { width: 125, justifyContent: 'space-between' }]}>
            <Text style={[styles.text, { fontVariant: ['tabular-nums'] }]}>{custom[key]}</Text>
            <Icon name="next" size={15} color={colors.muted} />
          </View>
        </Pressable>
      ))}
      <Button icon="check" label="Apply custom colors" onPress={() => void change({ ...value, custom })} />
      {picking && <ColorPicker title={`Pick ${picking} color`} value={custom[picking]} close={() => setPicking(null)} select={hex => {
        setCustom(previous => ({ ...previous, [picking]: hex }));
        setPicking(null);
      }} />}
    </>}
    <View style={styles.row}>
      <Text style={[styles.text, { flex: 1 }]}>Panel glow</Text>
      <Switch accessibilityLabel="Panel glow" value={value.panelGlow} trackColor={{ false: colors.line, true: colors.accentSurface }}
        thumbColor={value.panelGlow ? colors.accent : colors.muted}
        onValueChange={panelGlow => void change({ ...value, panelGlow })} />
    </View>
    {error && <Notice>{error}</Notice>}
    <Button icon="edit" label="Edit home page" disabled={!canEditHome} onPress={editHome} />
  </Card>;
}

function SchemeSwatches({ palette }: { palette: Palette }) {
  const colors = useColors();
  return (
    <View accessible={false} style={{ flexDirection: 'row', gap: 4 }}>
      {[palette.primary, palette.secondary, palette.tertiary].map((color, index) => (
        <View key={index} style={{ width: 14, height: 14, borderRadius: 7, borderWidth: 1, borderColor: colors.line, backgroundColor: color }} />
      ))}
    </View>
  );
}
