import { Settings } from '../settings/Settings.ts';

export class Formatter {
    private settings: Settings;
    private numbers = new Map<string, Intl.NumberFormat>();
    private locale = '';
    private fahrenheit = false;

    constructor(settings: Settings) {
        this.settings = settings;
    }

    configure(defaultTemperatureUnit: string): void {
        this.numbers.clear();
        this.locale = this.settings.text('numberLocale').trim();

        try {
            new Intl.NumberFormat(this.locale || undefined);
        } catch {
            this.locale = '';
        }

        this.fahrenheit =
            this.settings.text('temperatureUnit') === '°F' ||
            (this.settings.text('temperatureUnit') === 'iCUE' && defaultTemperatureUnit === '°F');
    }

    number(value: number | null, precision = this.settings.number('precision'), useGrouping = true): string {
        if (value === null || !Number.isFinite(value)) {
            return '-';
        }

        const key = `${precision}/${useGrouping}`;
        let formatter = this.numbers.get(key);

        if (!formatter) {
            formatter = new Intl.NumberFormat(this.locale || undefined, {
                minimumFractionDigits: precision,
                maximumFractionDigits: precision,
                useGrouping
            });
            this.numbers.set(key, formatter);
        }

        return formatter.format(value);
    }

    value(value: number | null, unit = '', precision = this.settings.number('precision')): string {
        if (value === null || !Number.isFinite(value)) {
            return '-';
        }

        return (
            this.number(value, precision, unit !== 'MHz' && unit !== 'RPM' && unit !== 'mV') +
            (unit && this.settings.enabled('showUnits') ? `${unit === '%' ? '' : ' '}${unit}` : '')
        );
    }

    localTime(timestamp: number): string {
        const date = new Date(timestamp);
        const hours = date.getHours();
        const twelveHour = this.settings.text('widgetClockFormat') === '12h';
        const hour = twelveHour ? hours % 12 || 12 : hours;
        const minutes = String(date.getMinutes()).padStart(2, '0');
        const suffix = twelveHour ? (hours < 12 ? ' AM' : ' PM') : '';
        return String(hour).padStart(2, '0') + ':' + minutes + suffix;
    }

    temperature(value: number | null): string {
        return this.value(value === null ? null : this.fahrenheit ? (value * 9) / 5 + 32 : value, this.fahrenheit ? '°F' : '°C', 1);
    }

    unit(name: string): string {
        return (
            (
                {
                    Watts: 'W',
                    Amperes: 'A',
                    Volts: 'V',
                    Celsius: '°C',
                    Megahertz: 'MHz',
                    Rpm: 'RPM',
                    Percent: '%',
                    Megabytes: 'MB',
                    Bitmask: '',
                    Boolean: ''
                } as Record<string, string>
            )[name] ?? name
        );
    }

    memory(used: number | null, total: number | null, percentage: number | null): string {
        if (used === null || total === null || total <= 0) {
            return '-';
        }

        const percent = percentage ?? (used / total) * 100;

        return `${this.value(used / 1024, 'GB')} / ${this.value(total / 1024, 'GB')} (${this.value(percent, '%', 0)})`;
    }
}