import type { ReactNode } from 'react';
import { Modal, ScrollView, Text, View } from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';

import { useColors } from './appearance';
import { Button, useStyles } from './ui';

export function Sheet({ title, close, children, footer }: {
  title: string; close: () => void; children: ReactNode; footer?: ReactNode;
}) {
  const colors = useColors();
  const styles = useStyles();

  return (
    <Modal transparent animationType="slide" onRequestClose={close}>
      <View style={{ flex: 1, justifyContent: 'flex-end', alignItems: 'center', backgroundColor: '#00000080' }}>
        <SafeAreaView edges={['bottom', 'left', 'right']} accessibilityViewIsModal
          style={{ width: '100%', maxWidth: 680, maxHeight: '85%', backgroundColor: colors.surface, borderTopLeftRadius: 24,
            borderTopRightRadius: 24 }}>
          <View style={[styles.row, { padding: 16, paddingHorizontal: 20 }]}>
            <Text accessibilityRole="header" style={[styles.heading, { flex: 1 }]}>{title}</Text>
            <Button icon="close" label="Close" onPress={close} />
          </View>
          <ScrollView style={{ flexShrink: 1 }} contentContainerStyle={{ paddingHorizontal: 20, paddingBottom: 20, gap: 12 }}>
            {children}
          </ScrollView>
          {footer && <View style={{ padding: 16, paddingHorizontal: 20 }}>{footer}</View>}
        </SafeAreaView>
      </View>
    </Modal>
  );
}
