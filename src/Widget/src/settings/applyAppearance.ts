import { chartSettings } from './chartSettings.ts';
import { Settings } from './Settings.ts';
import { defaults } from './definitions.ts';
import { hexToRGB } from './hexToRGB.ts';

export function applyAppearance(root: HTMLElement, settings: Settings): void {
    const light = settings.text('theme') === 'Light';
    const colors: Record<string, [string, string]> = {
        textColor: ['text', '#17323e'],
        backgroundColor: ['bg', '#eaf2f4'],
        mutedColor: ['muted', '#506d7a'],
        panelColor: ['panel', '#ffffff'],
        borderColor: ['border', '#bfd0d6'],
        trackColor: ['track', '#dbe6eb'],
        unitColor: ['unit', '#506d7a'],
        gridColor: ['grid', '#bfd0d6'],
        axisColor: ['axis', '#506d7a'],
        peakColor: ['peak-color', '#7954b3'],
        spreadColor: ['spread', '#7954b3'],
        headroomColor: ['headroom', '#147344'],
        comparisonColor: ['comparison', '#506d7a'],
        pinFillColor: ['pin-fill', '#007f73'],
        warningColor: ['warning', '#915000'],
        errorColor: ['error', '#b52643'],
        alertColor: ['alert', '#ffe5eb']
    };

    const target = document.documentElement;

    for (const [id, [token, lightValue]] of Object.entries(colors)) {
        const configured = settings.text(id);
        target.style.setProperty(`--${token}`, light && configured === defaults[id] ? lightValue : configured);
    }

    const accent = settings.text('accentColor');
    target.style.setProperty('--accent', light && accent === defaults.accentColor ? '#007f73' : accent);

    const panel = target.style.getPropertyValue('--panel');
    target.style.setProperty('--panel-surface', `rgba(${hexToRGB(panel)}, ${settings.number('panelOpacity') / 100})`);
    target.style.colorScheme = light ? 'light' : 'dark';

    const factors: Record<string, string> = {
        panelPadding: 'padding-scale',
        panelGap: 'gap-scale',
        iconSize: 'icon-scale',
        barGap: 'bar-gap'
    };

    for (const [id, token] of Object.entries(factors)) {
        target.style.setProperty(`--${token}`, String(settings.number(id) / 100));
    }

    for (const [id, token] of Object.entries({
        panelRadius: 'panel-radius',
        borderWidth: 'border-width',
        glow: 'glow'
    })) {
        target.style.setProperty(`--${token}`, `${settings.number(id)}px`);
    }

    target.style.setProperty('--bar-width', `${settings.number('barWidth')}%`);
    target.style.setProperty('--bar-width-ratio', String(settings.number('barWidth') / 100));
    target.style.setProperty('--bar-rounding', String(settings.number('barRadius') / 100));
    target.style.setProperty('--background-opacity', String(1 - settings.number('transparency') / 100));
    target.style.setProperty('--bg-blur', `${settings.number('glassBlur')}px`);
    target.style.setProperty('--bg-brightness', `${settings.number('bgBrightness')}%`);
    target.style.setProperty('--scanlines', String(settings.number('scanlines') / 100));

    const family =
        settings
            .text('fontFamily')
            .replace(/[^\p{L}\p{N} _-]/gu, '')
            .trim() || 'Consolas';
    target.style.setProperty('--font-family', `"${family}", monospace`);

    for (const role of ['title', 'label', 'value', 'unit', 'pin', 'axis', 'comparison', 'alert']) {
        target.style.setProperty(`--${role}-scale`, String(settings.number(`${role}Size`) / 100));
    }

    for (const [id, token] of Object.entries({
        showIcons: 'icons',
        [chartSettings.Source]: 'source',
        showScanlines: 'scanlines',
        [chartSettings.Headroom]: 'headroom',
        [chartSettings.Axis]: 'axis',
        showGrid: 'grid',
        [chartSettings.Spread]: 'spread',
        alertPulse: 'alertPulse',
        reduceMotion: 'reduceMotion'
    })) {
        root.dataset[token] = String(settings.enabled(id));
    }

    root.dataset.panelCount = settings.text('panelCount');
    root.dataset.largeType = String(
        ['title', 'label', 'value', 'unit', 'pin', 'comparison', 'alert'].some((role) => settings.number(`${role}Size`) > 120)
    );
}