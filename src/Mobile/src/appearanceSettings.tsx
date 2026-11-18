import { useState } from 'react';
import { Text, TextInput, View } from 'react-native';
import { schemes, validColor, type Appearance, type Palette } from './appearance';
import { Button, Card, Notice, Selection, useStyles } from './ui';

export function AppearanceSettings({ value, save }: { value: Appearance; save: (next: Appearance) => Promise<void> }) {
  const styles = useStyles();
  const [custom, setCustom] = useState(value.custom);
  const [error, setError] = useState('');
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
      {(Object.keys(custom) as (keyof Palette)[]).map(key =>
        <View key={key} style={styles.row}>
          <Text style={[styles.text, { flex: 1, textTransform: 'capitalize' }]}>{key}</Text>
          <View style={{ width: 24, height: 24, borderRadius: 12, backgroundColor: validColor(custom[key]) ? custom[key] : 'transparent' }} />
          <TextInput accessibilityLabel={key + ' hex color'} maxLength={7} autoCorrect={false} autoCapitalize="characters"
            style={[styles.button, styles.text]} value={custom[key]} onChangeText={hex => setCustom(old => ({ ...old, [key]: hex }))} />
        </View>)}
      <Button icon="check" label="Apply custom colors" disabled={!Object.values(custom).every(validColor)}
        onPress={() => void change({ ...value, custom })} />
    </>}
    {error && <Notice>{error}</Notice>}
  </Card>;
}
