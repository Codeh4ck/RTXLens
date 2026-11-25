import { useEffect, useMemo, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';
import { StatusBar } from 'expo-status-bar';
import { SafeAreaView } from 'react-native-safe-area-context';
import { ThemeContext, isLight, themeColors, type Appearance } from './appearance';
import { AppearanceSettings } from './appearanceSettings';
import { clearDemoAlerts, demoAlerts, demoFrame, triggerDemoAlert, type DemoAlert } from './demoTelemetry';
import { IncidentHistory } from './history';
import type { Incident } from './incidentProtocol';
import { defaultDisplayPreferences, type TemperatureUnit } from './mobilePreferences';
import { DisplaySettings, ReadingFormatContext, TemperatureSettings, readingFormat } from './mobileSettings';
import { Home, Gpu, Power, Warnings } from './screens';
import { BottomNavigation, Button, Card, Page, useStyles, type Tab } from './ui';

export function DemoSession({ appearance, active, exit }: { appearance: Appearance; active: boolean; exit: () => void }) {
  const [theme, setTheme] = useState(appearance);
  const palette = useMemo(() => themeColors(theme), [theme]);
  return <ThemeContext.Provider value={palette}>
    <StatusBar style={isLight(palette.background) ? 'dark' : 'light'} />
    <DemoScreens active={active} exit={exit} appearance={theme} saveAppearance={async value => setTheme(value)} />
  </ThemeContext.Provider>;
}

function DemoScreens({ active, exit, appearance, saveAppearance }: {
  active: boolean; exit: () => void; appearance: Appearance; saveAppearance: (value: Appearance) => Promise<void>;
}) {
  const styles = useStyles();
  const [tab, setTab] = useState<Tab>('home');
  const [now, setNow] = useState(Date.now);
  const [incidents, setIncidents] = useState<Incident[]>([]);
  const [display, setDisplay] = useState(defaultDisplayPreferences);
  const [temperature, setTemperature] = useState<TemperatureUnit>('celsius');
  const [editing, setEditing] = useState(false);
  useEffect(() => {
    if (!active) return;
    setNow(Date.now());
    const timer = setInterval(() => setNow(Date.now()), 2000);
    return () => clearInterval(timer);
  }, [active]);
  const frame = useMemo(() => demoFrame(now, incidents), [now, incidents]);

  function trigger(alert: DemoAlert) {
    const at = Date.now();
    setNow(at);
    setIncidents(previous => triggerDemoAlert(previous, alert, at));
    setTab('warnings');
  }

  let content;
  switch (tab) {
    case 'home':
      content = <Home demo frame={frame} live remote={false} panels={display.homePanels} layoutEnabled initiallyEditing={editing}
        onEditingChange={setEditing} saveLayout={async homePanels => setDisplay(previous => ({ ...previous, homePanels }))}
        onNavigate={setTab} />;
      break;
    case 'power':
      content = <Power frame={frame} live hideIdleMsiConnectors={display.hideIdleMsiConnectors} />;
      break;
    case 'gpu':
      content = <Gpu frame={frame} live />;
      break;
    case 'warnings':
      content = <Warnings frame={frame} live simulations={[]} history={<IncidentHistory computer={null}
        destination={null} live demoIncidents={incidents} />} />;
      break;
    case 'settings':
      content = <Page title="Settings">
        <Card title="Sample alerts" icon="warnings">
          <Text style={styles.caption}>Trigger a warning to see it in Warnings and inspect the readings captured in History.</Text>
          {demoAlerts.map(alert => <Button key={alert.source} icon={alert.icon} label={alert.title}
            disabled={incidents.some(item => item.source === alert.source && !item.endedAt)} onPress={() => trigger(alert)} />)}
          <Button icon="check" label="Clear sample alerts" disabled={frame.warnings.length === 0} onPress={() => {
            const at = Date.now();
            setIncidents(previous => clearDemoAlerts(previous, at));
            setTab('warnings');
          }} />
        </Card>
        <AppearanceSettings value={appearance} save={saveAppearance} canEditHome editHome={() => { setEditing(true); setTab('home'); }} />
        <TemperatureSettings unit={temperature} save={async value => setTemperature(value)} />
        <DisplaySettings value={display} busy={false} save={async value => setDisplay(value)} />
      </Page>;
      break;
  }
  return <View style={styles.screen}>
    <View style={[styles.row, local.banner]}>
      <View style={local.title}><Text style={styles.heading}>Demo mode</Text>
        <Text style={styles.caption}>Simulated hardware · Sample alerts in Settings</Text></View>
      <Button icon="close" label="Exit demo" onPress={exit} />
    </View>
    <ReadingFormatContext.Provider value={readingFormat(temperature, display.useGrouping)}>
      <View key={tab} style={styles.screen}>{content}</View>
    </ReadingFormatContext.Provider>
    {editing && tab === 'home' ? <SafeAreaView edges={['bottom']} /> :
      <BottomNavigation selected={tab} warningCount={frame.warnings.length} onSelect={setTab} />}
  </View>;
}

const local = StyleSheet.create({
  banner: { alignSelf: 'center', width: '100%', maxWidth: 680, paddingHorizontal: 20, paddingVertical: 8 },
  title: { flex: 1 },
});
