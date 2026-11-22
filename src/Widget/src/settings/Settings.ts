import { defaults, definitions } from './definitions.ts';

export class Settings {
    private values: Record<string, unknown> = { ...defaults };

    update(read: (name: string) => unknown): void {
        for (const definition of definitions) {
            const raw = read(definition.id);
            const value = raw == null ? definition.defaultValue : raw;

            if (definition.type === 'slider') {
                const number = Number(value);
                this.values[definition.id] = Number.isFinite(number)
                    ? Math.max(definition.min!, Math.min(definition.max!, number))
                    : definition.defaultValue;
            } else if (definition.type === 'switch') {
                this.values[definition.id] = value === true || value === 'true' || value === 1;
            } else if (definition.type === 'combobox') {
                // Keep numeric values saved by older slider controls compatible with dropdowns.
                const storedKey = typeof value === 'number' ? String(value) : value;
                const key = /^panel[123]$/.test(definition.id) && storedKey === 'asusStatus' ? 'nvidiaStatus' : storedKey;
                this.values[definition.id] = definition.options?.some((option) => option.key === key) ? key : definition.defaultValue;
            } else if (definition.type === 'color') {
                this.values[definition.id] = typeof value === 'string' && /^#[\da-f]{6}$/i.test(value) ? value : definition.defaultValue;
            } else if (definition.type === 'textfield') {
                this.values[definition.id] = typeof value === 'string' ? value.slice(0, 2000) : definition.defaultValue;
            } else {
                this.values[definition.id] = value;
            }
        }
    }

    text(id: string): string {
        return String(this.values[id] ?? '');
    }

    number(id: string): number {
        return Number(this.values[id]);
    }

    enabled(id: string): boolean {
        return this.values[id] === true;
    }

    get stackGpuClocks(): boolean {
        return this.enabled('alwaysShowInternalClocks') || this.enabled('hideGpuFans');
    }

    raw(id: string): unknown {
        return this.values[id];
    }

    setPinMetric(prefix: 'asus' | 'wireview', metric: string): void {
        if (['A', 'W', 'V'].includes(metric)) {
            this.values[`${prefix}Metric`] = metric;
        }
    }
}