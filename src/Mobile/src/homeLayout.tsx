import { useColors } from './appearance';
import { useEffect, useRef, useState, type ComponentRef, type ReactNode } from 'react';
import { ScrollView, Text, View, type GestureResponderEvent, type LayoutRectangle } from 'react-native';
import { Button, HomeInteractionContext, Notice, useStyles } from './ui';

type Panel = { key: string; title: string; wide: boolean; content: ReactNode };

export function HomeLayout({ panels, move, footer, configure }: {
  panels: Panel[]; move: (from: number, to: number) => Promise<void>; footer: ReactNode; configure: () => void;
}) {
  const styles = useStyles();
  const colors = useColors();
  const [editing, setEditing] = useState(false);
  const [drag, setDrag] = useState<{ key: string; dx: number; dy: number; target: string } | null>(null);
  const [error, setError] = useState('');
  const scroll = useRef<ComponentRef<typeof ScrollView>>(null);
  const offset = useRef(0);
  const viewport = useRef({ top: 0, height: 0 });
  const contentHeight = useRef(0);
  const layouts = useRef(new Map<string, LayoutRectangle>());
  const finger = useRef<{ key?: string; x: number; y: number; startOffset: number; lastY: number; lastX: number } | null>(null);
  const held = useRef(false);
  const editMode = useRef(false);
  const destination = useRef('');
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const scroller = useRef<ReturnType<typeof setInterval> | undefined>(undefined);
  const saving = useRef(false);

  function clearTimers() {
    clearTimeout(timer.current);
    clearInterval(scroller.current);
  }
  useEffect(() => clearTimers, []);

  function position() {
    const touch = finger.current;
    if (!held.current || !touch?.key) return;
    const start = layouts.current.get(touch.key);
    if (!start) return;
    const dx = touch.lastX - touch.x;
    const dy = touch.lastY - touch.y + offset.current - touch.startOffset;
    const x = start.x + start.width / 2 + dx;
    const y = start.y + start.height / 2 + dy;
    let nearest = touch.key;
    let distance = Infinity;
    for (const panel of panels) {
      const box = layouts.current.get(panel.key);
      if (!box) continue;
      const next = Math.hypot(Math.max(box.x - x, x - box.x - box.width, 0), Math.max(box.y - y, y - box.y - box.height, 0));
      if (next < distance) { nearest = panel.key; distance = next; }
    }
    destination.current = nearest;
    setDrag({ key: touch.key, dx, dy, target: nearest });
  }

  function activate() {
    editMode.current = true;
    setEditing(true);
    if (!finger.current?.key) return;
    held.current = true;
    position();
    scroller.current = setInterval(() => {
      const touch = finger.current;
      if (!touch) return;
      const y = touch.lastY - viewport.current.top;
      const delta = y < 55 ? -12 : y > viewport.current.height - 55 ? 12 : 0;
      const max = Math.max(0, contentHeight.current - viewport.current.height);
      const next = Math.max(0, Math.min(max, offset.current + delta));
      if (next !== offset.current) {
        offset.current = next;
        scroll.current?.scrollTo({ y: next, animated: false });
        position();
      }
    }, 24);
  }

  function begin(event: GestureResponderEvent, key?: string) {
    if (saving.current || finger.current) return;
    const { pageX: x, pageY: y } = event.nativeEvent;
    finger.current = { key, x, y, lastX: x, lastY: y, startOffset: offset.current };
    if (editMode.current && key) activate();
    else timer.current = setTimeout(activate, 450);
  }

  function update(event: GestureResponderEvent) {
    const touch = finger.current;
    if (!touch) return;
    touch.lastX = event.nativeEvent.pageX;
    touch.lastY = event.nativeEvent.pageY;
    if (!held.current && Math.hypot(touch.lastX - touch.x, touch.lastY - touch.y) > 8) clearTimeout(timer.current);
    position();
  }

  async function reorder(from: number, to: number) {
    if (saving.current || from === to || from < 0 || to < 0 || to >= panels.length) return;
    saving.current = true;
    try {
      await move(from, to);
      setError('');
    } catch {
      setError('Could not save the panel order.');
    } finally {
      saving.current = false;
    }
  }

  function finish(cancelled: boolean) {
    clearTimers();
    const key = finger.current?.key;
    if (!cancelled && held.current && key) {
      void reorder(panels.findIndex(panel => panel.key === key), panels.findIndex(panel => panel.key === destination.current));
    }
    finger.current = null;
    held.current = false;
    setDrag(null);
  }

  return <ScrollView ref={scroll} scrollEnabled={!drag} contentContainerStyle={[styles.page, { flexGrow: 1 }]}
    onLayout={() => scroll.current?.measureInWindow((_x, top, _width, height) => { viewport.current = { top, height }; })}
    onContentSizeChange={(_width, height) => { contentHeight.current = height; }}
    scrollEventThrottle={16} onScroll={event => { offset.current = event.nativeEvent.contentOffset.y; }}
    onTouchStart={event => begin(event)} onTouchMove={update} onTouchEnd={() => finish(false)} onTouchCancel={() => finish(true)}>
    <Text accessibilityRole="header" style={styles.title} accessibilityActions={[{ name: 'activate', label: 'Reorder panels' }]}
      onAccessibilityAction={() => { editMode.current = true; setEditing(true); }}>RTX Lens</Text>
    {editing && <Button icon="check" label="Done reordering" onPress={() => { editMode.current = false; setEditing(false); }} />}
    {error && <Notice>{error}</Notice>}
    <View style={{ flexDirection: 'row', flexWrap: 'wrap', gap: 10 }}>
      {panels.map((panel, index) => {
        const moving = drag?.key === panel.key;
        return <View key={panel.key} accessible={editing} accessibilityLabel={editing ? panel.title : undefined}
          accessibilityActions={editing ? [{ name: 'increment', label: 'Move later' }, { name: 'decrement', label: 'Move earlier' }] : []}
          onAccessibilityAction={event => void reorder(index, index + (event.nativeEvent.actionName === 'increment' ? 1 : -1))}
          onMoveShouldSetResponderCapture={() => held.current}
          onTouchStart={event => begin(event, panel.key)} onLayout={event => layouts.current.set(panel.key, event.nativeEvent.layout)}
          style={[{ flexBasis: panel.wide ? '100%' : '45%', flexGrow: 1 },
            editing && { borderWidth: 1, borderRadius: 18, borderColor: drag?.target === panel.key ? colors.accent : colors.line },
            moving && { zIndex: 2, opacity: 0.85, transform: [{ translateX: drag.dx }, { translateY: drag.dy }] }]}>
          <HomeInteractionContext.Provider value><View pointerEvents={editing ? 'none' : 'auto'}>{panel.content}</View></HomeInteractionContext.Provider>
        </View>;
      })}
    </View>
    {!panels.length && <Button icon="add" label="Add Home panels" onPress={configure} />}
    {footer}
  </ScrollView>;
}
