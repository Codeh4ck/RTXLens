import { useEffect, useRef, useState, type ComponentRef, type ReactNode } from 'react';
import { ScrollView, Text, View, type GestureResponderEvent } from 'react-native';

import { useColors } from './appearance';
import { homeDropIndex, type PanelBounds } from './homeDropTarget';
import { homePanelKey, moveHomePanel, type HomePanel } from './mobilePreferences';
import { Sheet } from './sheet';
import { Button, HomeInteractionContext, Icon, Notice, useStyles } from './ui';

type PanelOption = { panel: HomePanel; title: string };
type Contact = { key?: string; x: number; y: number; lastX: number; lastY: number };
type DragStart = { key: string; bounds: PanelBounds; slots: (PanelBounds | undefined)[]; startOffset: number; target: number };
type Preview = { key: string; target: number; x: number; y: number; slot: PanelBounds };

const gap = 10;
const edge = 56;

export function HomeLayout({ panels, options, titleFor, renderPanel, save, enabled, initiallyEditing, onEditingChange, footer }: {
  panels: HomePanel[];
  options: PanelOption[];
  titleFor: (panel: HomePanel) => string;
  renderPanel: (panel: HomePanel) => ReactNode;
  save: (panels: HomePanel[]) => Promise<void>;
  enabled: boolean;
  initiallyEditing: boolean;
  onEditingChange: (editing: boolean) => void;
  footer: ReactNode;
}) {
  const styles = useStyles();
  const colors = useColors();
  const [draft, setDraft] = useState<HomePanel[] | null>(() => initiallyEditing ? panels : null);
  const [picker, setPicker] = useState(false);
  const [preview, setPreview] = useState<Preview | null>(null);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [gridWidth, setGridWidth] = useState(0);
  const scroll = useRef<ComponentRef<typeof ScrollView>>(null);
  const viewport = useRef<ComponentRef<typeof View>>(null);
  const viewportBounds = useRef({ top: 0, height: 0 });
  const layout = useRef(new Map<string, PanelBounds>());
  const contentHeight = useRef(0);
  const offset = useRef(0);
  const finger = useRef<Contact | null>(null);
  const drag = useRef<DragStart | null>(null);
  const hold = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const autoScroll = useRef<ReturnType<typeof setInterval> | undefined>(undefined);
  const editing = draft !== null;
  const current = draft ?? panels;
  const from = preview ? current.findIndex(panel => homePanelKey(panel) === preview.key) : -1;
  const displayed = preview ? moveHomePanel(current, from, preview.target) : current;

  useEffect(() => () => {
    clearTimeout(hold.current);
    clearInterval(autoScroll.current);
  }, []);

  useEffect(() => {
    onEditingChange(editing);
    return () => onEditingChange(false);
  }, [editing, onEditingChange]);

  function startEditing() {
    setDraft(previous => previous ?? panels);
  }

  function measureViewport() {
    viewport.current?.measure((_x, _y, _width, height, _pageX, pageY) => {
      viewportBounds.current = { top: pageY, height };
    });
  }

  function updatePreview() {
    const active = drag.current;
    const touch = finger.current;
    if (!active || !touch) return;

    const { bounds } = active;
    const x = bounds.x + touch.lastX - touch.x;
    const y = bounds.y + touch.lastY - touch.y + offset.current - active.startOffset;
    active.target = homeDropIndex(active.slots, x + bounds.width / 2, y + bounds.height / 2, active.target);
    setPreview({ key: active.key, target: active.target, x, y, slot: layout.current.get(active.key) ?? bounds });
  }

  function lift() {
    const key = finger.current?.key;
    const bounds = key && layout.current.get(key);
    if (!key || !bounds) return;

    const keys = current.map(homePanelKey);
    drag.current = { key, bounds, slots: keys.map(id => layout.current.get(id)), startOffset: offset.current, target: keys.indexOf(key) };
    measureViewport();
    updatePreview();
    autoScroll.current = setInterval(() => {
      const touch = finger.current;
      const { top, height } = viewportBounds.current;
      if (!touch || height === 0) return;

      const y = touch.lastY - top;
      const delta = y < edge ? -12 : y > height - edge ? 12 : 0;
      const max = Math.max(0, contentHeight.current - height);
      const next = Math.max(0, Math.min(max, offset.current + delta));
      if (next !== offset.current) {
        offset.current = next;
        scroll.current?.scrollTo({ y: next, animated: false });
        updatePreview();
      }
    }, 24);
  }

  function begin(event: GestureResponderEvent, key?: string, handle = false) {
    if (!enabled || saving || finger.current || (editing && !key)) return;

    const { pageX: x, pageY: y } = event.nativeEvent;
    finger.current = { key, x, y, lastX: x, lastY: y };
    if (handle) {
      lift();
      return;
    }

    hold.current = setTimeout(() => {
      startEditing();
      lift();
    }, editing ? 220 : 450);
  }

  function move(event: GestureResponderEvent) {
    const touch = finger.current;
    if (!touch) return;

    touch.lastX = event.nativeEvent.pageX;
    touch.lastY = event.nativeEvent.pageY;
    if (!drag.current && Math.hypot(touch.lastX - touch.x, touch.lastY - touch.y) > 8) clearTimeout(hold.current);
    updatePreview();
  }

  function finish(cancelled: boolean) {
    clearTimeout(hold.current);
    clearInterval(autoScroll.current);
    const active = drag.current;
    if (active && !cancelled) {
      setDraft(previous => previous && moveHomePanel(previous, previous.findIndex(panel => homePanelKey(panel) === active.key), active.target));
    }
    finger.current = null;
    drag.current = null;
    setPreview(null);
  }

  function cancel() {
    finish(true);
    setPicker(false);
    setError('');
    setDraft(null);
  }

  async function saveLayout() {
    if (!draft || saving) return;

    setSaving(true);
    setError('');
    try {
      if (draft !== panels) await save(draft);
      setDraft(null);
    } catch {
      setError('Could not save your Home layout. Try OK again.');
    } finally {
      setSaving(false);
    }
  }

  function openPicker() {
    startEditing();
    setPicker(true);
  }

  return (
    <View style={styles.screen}>
      <View style={{ backgroundColor: colors.background, borderBottomColor: colors.line, borderBottomWidth: editing ? 1 : 0 }}>
        <View onTouchStart={event => begin(event)} onTouchEnd={() => finish(false)} onTouchCancel={() => finish(true)}
          style={[styles.row, { minHeight: 64, width: '100%', maxWidth: 680, alignSelf: 'center', paddingHorizontal: 20 }]}>
          {editing ? <>
            <Button icon="close" label="Cancel" disabled={saving || !!preview} onPress={cancel} />
            <View style={{ flex: 1 }} />
            <Button icon="add" label="Panels" accessibilityLabel="Add or remove Home panels" disabled={saving || !!preview} onPress={openPicker} />
            <Button icon="check" label="OK" variant="primary" disabled={saving || !!preview} onPress={() => void saveLayout()} />
          </> : <Text accessibilityRole="header" style={styles.title}>RTX Lens</Text>}
        </View>
        {error && <View style={{ paddingHorizontal: 20, paddingBottom: 8 }}><Notice>{error}</Notice></View>}
      </View>
      <View ref={viewport} collapsable={false} onLayout={measureViewport} style={{ flex: 1 }}
        onMoveShouldSetResponderCapture={() => drag.current !== null}
        onResponderTerminationRequest={() => drag.current === null}
        onResponderTerminate={() => finish(true)}
        onTouchStart={event => begin(event)} onTouchMove={move} onTouchEnd={() => finish(false)} onTouchCancel={() => finish(true)}>
        <ScrollView ref={scroll} scrollEnabled={!preview} removeClippedSubviews={false}
          contentContainerStyle={[styles.page, { flexGrow: 1 }]}
          onContentSizeChange={(_width, height) => { contentHeight.current = height; }}
          scrollEventThrottle={16} onScroll={event => { offset.current = event.nativeEvent.contentOffset.y; }}>
          <View onLayout={event => setGridWidth(event.nativeEvent.layout.width)} style={{ flexDirection: 'row', flexWrap: 'wrap', gap }}>
            {displayed.map((panel, index) => {
              const key = homePanelKey(panel);
              const moving = preview?.key === key ? preview : null;
              const wide = panel.kind === 'pins' || panel.id === 'status';
              const width = wide ? '100%' : gridWidth > gap ? (gridWidth - gap) / 2 : '47%';

              return (
                <View key={key} accessible={editing} accessibilityLabel={editing ? titleFor(panel) : undefined}
                  accessibilityActions={editing ? [
                    ...(index > 0 ? [{ name: 'decrement', label: 'Move earlier' }] : []),
                    ...(index < displayed.length - 1 ? [{ name: 'increment', label: 'Move later' }] : []),
                  ] : []}
                  onAccessibilityAction={event => {
                    if (saving || drag.current) return;
                    const step = event.nativeEvent.actionName === 'increment' ? 1 : -1;
                    setDraft(previous => previous && moveHomePanel(previous, index, index + step));
                  }}
                  onTouchStart={event => begin(event, key)}
                  onLayout={event => {
                    const bounds = event.nativeEvent.layout;
                    layout.current.set(key, bounds);
                    if (drag.current?.key === key) updatePreview();
                  }}
                  style={{ width, zIndex: moving ? 1 : 0 }}>
                  {moving && <View pointerEvents="none" accessibilityElementsHidden importantForAccessibility="no-hide-descendants"
                    style={{ position: 'absolute', inset: 0, borderWidth: 2, borderStyle: 'dashed',
                      borderRadius: 18, borderColor: colors.accent, backgroundColor: colors.accentSurface }} />}
                  <View style={moving && { transform: [{ translateX: moving.x - moving.slot.x }, { translateY: moving.y - moving.slot.y }],
                    opacity: 0.94, elevation: 6 }}>
                    <HomeInteractionContext.Provider value>
                      <View pointerEvents={editing ? 'none' : 'auto'}>{renderPanel(panel)}</View>
                    </HomeInteractionContext.Provider>
                    {editing && <View accessible={false} onTouchStart={event => begin(event, key, true)}
                      style={{ position: 'absolute', top: 6, right: 6, width: 44, height: 44, alignItems: 'center', justifyContent: 'center',
                        borderRadius: 12, borderWidth: 1, borderColor: colors.line, backgroundColor: colors.raised }}>
                      <Icon name="grip" color={colors.text} size={16} />
                    </View>}
                  </View>
                </View>
              );
            })}
          </View>
          {!current.length && <Button icon="add" label="Add panels" disabled={!enabled || saving} onPress={openPicker} />}
          {footer}
        </ScrollView>
      </View>
      {picker && draft && <PanelPicker options={options} panels={draft} titleFor={titleFor} close={() => setPicker(false)}
        add={panel => setDraft(previous => previous && (previous.some(item => homePanelKey(item) === homePanelKey(panel))
          ? previous : [...previous, panel]))}
        remove={key => setDraft(previous => previous && previous.filter(panel => homePanelKey(panel) !== key))} />}
    </View>
  );
}

function PanelPicker({ options, panels, titleFor, close, add, remove }: {
  options: PanelOption[]; panels: HomePanel[]; titleFor: (panel: HomePanel) => string;
  close: () => void; add: (panel: HomePanel) => void; remove: (key: string) => void;
}) {
  const styles = useStyles();
  const active = new Set(panels.map(homePanelKey));
  const available = options.filter(({ panel }) => !active.has(homePanelKey(panel)));

  return (
    <Sheet title="Home panels" close={close}>
      <Text accessibilityRole="header" style={styles.heading}>On Home</Text>
      {panels.length === 0 && <Text style={styles.caption}>Your Home screen is empty.</Text>}
      {panels.map(panel => (
        <View key={homePanelKey(panel)} style={styles.row}>
          <Text style={[styles.text, { flex: 1 }]}>{titleFor(panel)}</Text>
          <Button icon="close" label="Remove" accessibilityLabel={`Remove ${titleFor(panel)}`} onPress={() => remove(homePanelKey(panel))} />
        </View>
      ))}
      <Text accessibilityRole="header" style={styles.heading}>Available</Text>
      {available.length === 0 && <Text style={styles.caption}>All available panels are on Home.</Text>}
      {available.map(({ panel, title }) => (
        <View key={homePanelKey(panel)} style={styles.row}>
          <Text style={[styles.text, { flex: 1 }]}>{title}</Text>
          <Button icon="add" label="Add" accessibilityLabel={`Add ${title}`} onPress={() => add(panel)} />
        </View>
      ))}
    </Sheet>
  );
}
