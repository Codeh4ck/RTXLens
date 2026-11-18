import { useState } from 'react';
import { StyleSheet, Text, View, type GestureResponderEvent } from 'react-native';
import Svg, { Defs, LinearGradient, Rect, Stop } from 'react-native-svg';

import { hexToHsv, hsvToHex } from './colorValues';
import { Sheet } from './sheet';
import { Button, useStyles } from './ui';

const hueColors = ['#FF0000', '#FFFF00', '#00FF00', '#00FFFF', '#0000FF', '#FF00FF', '#FF0000'];
const fieldHeight = 176;

function clamp(value: number) {
  return Math.max(0, Math.min(1, value));
}

export function ColorPicker({ title, value, close, select }: {
  title: string; value: string; close: () => void; select: (hex: string) => void;
}) {
  const styles = useStyles();
  const [color, setColor] = useState(() => hexToHsv(value));
  const [fieldWidth, setFieldWidth] = useState(0);
  const [hueWidth, setHueWidth] = useState(0);
  const hex = hsvToHex(color);
  const hueHex = hsvToHex({ hue: color.hue, saturation: 1, brightness: 1 });

  function selectTone(event: GestureResponderEvent) {
    if (fieldWidth === 0) return;
    const { locationX, locationY } = event.nativeEvent;
    setColor(previous => ({ ...previous, saturation: clamp(locationX / fieldWidth), brightness: 1 - clamp(locationY / fieldHeight) }));
  }

  function selectHue(event: GestureResponderEvent) {
    if (hueWidth === 0) return;
    const hue = 360 * clamp(event.nativeEvent.locationX / hueWidth);
    setColor(previous => ({ ...previous, hue }));
  }

  return (
    <Sheet title={title} close={close} footer={<Button icon="check" label="Use color" variant="primary" onPress={() => select(hex)} />}>
      <Text style={styles.caption}>Saturation and brightness</Text>
      <View accessible accessibilityLabel="Saturation and brightness"
        accessibilityValue={{ text: `${Math.round(color.saturation * 100)} percent saturation, ${Math.round(color.brightness * 100)} percent brightness` }}
        accessibilityActions={[
          { name: 'moreSaturation', label: 'Increase saturation' }, { name: 'lessSaturation', label: 'Decrease saturation' },
          { name: 'brighter', label: 'Increase brightness' }, { name: 'darker', label: 'Decrease brightness' },
        ]}
        onAccessibilityAction={event => {
          const action = event.nativeEvent.actionName;
          setColor(previous => {
            switch (action) {
              case 'moreSaturation': return { ...previous, saturation: clamp(previous.saturation + 0.05) };
              case 'lessSaturation': return { ...previous, saturation: clamp(previous.saturation - 0.05) };
              case 'brighter': return { ...previous, brightness: clamp(previous.brightness + 0.05) };
              case 'darker': return { ...previous, brightness: clamp(previous.brightness - 0.05) };
              default: return previous;
            }
          });
        }}
        onLayout={event => setFieldWidth(event.nativeEvent.layout.width)}
        onStartShouldSetResponder={() => true} onResponderTerminationRequest={() => false}
        onResponderGrant={selectTone} onResponderMove={selectTone}
        style={[local.field, { height: fieldHeight, backgroundColor: hueHex }]}>
        <View pointerEvents="none" style={StyleSheet.absoluteFill}>
          <Svg height="100%" width="100%">
            <Defs>
              <LinearGradient id="saturation" x1="0%" y1="0%" x2="100%" y2="0%">
                <Stop offset="0" stopColor="#FFFFFF" />
                <Stop offset="1" stopColor="#FFFFFF" stopOpacity="0" />
              </LinearGradient>
              <LinearGradient id="brightness" x1="0%" y1="0%" x2="0%" y2="100%">
                <Stop offset="0" stopColor="#000000" stopOpacity="0" />
                <Stop offset="1" stopColor="#000000" />
              </LinearGradient>
            </Defs>
            <Rect width="100%" height="100%" fill="url(#saturation)" />
            <Rect width="100%" height="100%" fill="url(#brightness)" />
          </Svg>
        </View>
        <View pointerEvents="none" style={[local.cursor, { left: color.saturation * (fieldWidth - 22),
          top: (1 - color.brightness) * (fieldHeight - 22), backgroundColor: hex }]} />
      </View>
      <Text style={styles.caption}>Hue</Text>
      <View accessible accessibilityRole="adjustable" accessibilityLabel="Hue"
        accessibilityValue={{ min: 0, max: 360, now: Math.round(color.hue) }}
        accessibilityActions={[{ name: 'increment' }, { name: 'decrement' }]}
        onAccessibilityAction={event => {
          const step = event.nativeEvent.actionName === 'increment' ? 5 : -5;
          setColor(previous => ({ ...previous, hue: (previous.hue + step + 360) % 360 }));
        }}
        onLayout={event => setHueWidth(event.nativeEvent.layout.width)}
        onStartShouldSetResponder={() => true} onResponderTerminationRequest={() => false}
        onResponderGrant={selectHue} onResponderMove={selectHue}
        style={[local.field, { height: 44 }]}>
        <View pointerEvents="none" style={StyleSheet.absoluteFill}>
          <Svg height="100%" width="100%">
            <Defs>
              <LinearGradient id="hue" x1="0%" y1="0%" x2="100%" y2="0%">
                {hueColors.map((stopColor, index) => <Stop key={index} offset={index / (hueColors.length - 1)} stopColor={stopColor} />)}
              </LinearGradient>
            </Defs>
            <Rect width="100%" height="100%" fill="url(#hue)" />
          </Svg>
        </View>
        <View pointerEvents="none" style={[local.cursor, { left: color.hue / 360 * (hueWidth - 22), top: 11, backgroundColor: hueHex }]} />
      </View>
      <View style={styles.row}>
        <View style={[local.swatch, { backgroundColor: hex }]} />
        <Text style={styles.text}>{hex}</Text>
      </View>
    </Sheet>
  );
}

const local = StyleSheet.create({
  field: { overflow: 'hidden', borderRadius: 12 },
  cursor: { position: 'absolute', width: 22, height: 22, borderRadius: 11, borderWidth: 2, borderColor: '#FFFFFF', elevation: 2,
    shadowColor: '#000000', shadowOpacity: 0.8, shadowRadius: 2, shadowOffset: { width: 0, height: 1 } },
  swatch: { width: 28, height: 28, borderRadius: 14 },
});
