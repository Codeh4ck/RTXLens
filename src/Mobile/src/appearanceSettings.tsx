import { useState } from 'react';
import { Pressable, Text, View } from 'react-native';
import { schemes, useColors, type Appearance, type Palette } from './appearance';
import { ColorPicker } from './colorPicker';
import { Button, Card, Icon, Notice, Selection, useStyles } from './ui';

export function AppearanceSettings({ value, save }: { value: Appearance; save: (next: Appearance) => Promise<void> }) {
  const styles = useStyles();
  const colors = useColors();
  const [custom, setCustom] = useState(value.custom);
  const [error, setError] = useState('');
  const [picking, setPicking] = useState<keyof Palette | null>(null);
  async function change(next: Appearance) {
    try { await save(next); setError(''); }
    catch { setError('Could not save the color scheme.'); }
  }

  return <Card title="Appearance" icon="palette">
    <Selection options={[{ id: 'dark', label: 'Dark' }, { id: 'light', label: 'Light' }]} selected={value.light ? 'light' : 'dark'}
      onSelect={mode => void change({ ...value, light: mode === 'light' })} />
    <Selection options={[...schemes.map(([id]) => ({ id, label: id })), { id: 'Custom', label: 'Custom' }]} selected={value.scheme}
      onSelect={scheme => void change({ ...value, scheme })} />
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
    {error && <Notice>{error}</Notice>}
  </Card>;
}
