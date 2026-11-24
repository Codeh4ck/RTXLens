// Run with Node after installing the widget's existing Playwright dev dependency.
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { join } from 'node:path';

const root = fileURLToPath(new URL('../../../', import.meta.url));
const require = createRequire(join(root, 'src/Widget/package.json'));
const { chromium } = require('playwright');
const template = await readFile(join(root,
    'src/Desktop/Presentation/Performance/PerformanceChart.html'), 'utf8').then(text => text.replaceAll(String.fromCharCode(13), ''));
const series = Array.from({ length: 8 }, (_, index) => ({
    trace: { key: 'trace' + index, group: 'Group ' + index, label: 'Source ' + index,
        deviceId: 'test', color: '#39eed1', unit: 'A' },
    selected: true,
    available: true,
    offset: 0,
    count: 101,
    samples: Array.from({ length: 101 }, (_, seconds) => ({ seconds, value: 2 + Math.sin(seconds) }))
}));
const recording = { sessionId: 'synthetic', startedAt: '2026-11-07T12:00:00Z',
    durationSeconds: 100, recording: false, sampleCount: 808, status: 'Test fixture', series };
const instrumentation = String.raw`<script>
window.baseDraws = 0;
window.messages = [];
const originalClear = CanvasRenderingContext2D.prototype.clearRect;
CanvasRenderingContext2D.prototype.clearRect = function(...args) {
    if (!this.canvas.classList.contains("overlay")) {
        window.baseDraws++;
        this.canvas.dataset.drawn = "true";
    }
    return originalClear.apply(this, args);
};
if (EMBEDDED) {
    window.chrome ??= {};
    window.chrome.webview = {
        addEventListener: (_, handler) => window.receiveChart = data => handler({ data }),
        postMessage: message => window.messages.push(message)
    };
}
</script>`;
const document = (embedded, frame) => template
    .replace('<script>\n(() => {', instrumentation.replace('EMBEDDED', String(embedded)) + '<script>\n(() => {')
    .replace('/*SESSION_DATA*/null', JSON.stringify(frame));

const browser = await chromium.launch({ channel: 'msedge', headless: true });
try {
    const page = await browser.newPage({ viewport: { width: 1200, height: 700 } });
    const errors = [];
    const network = [];
    page.on('pageerror', error => { errors.push(error.message); console.error(error.message); });
    page.on('request', request => { if (/^https?:/.test(request.url())) network.push(request.url()); });

    await page.setContent(document(false, recording));
    await page.waitForFunction(() => window.baseDraws > 0);
    await page.waitForTimeout(100);
    assert.equal(await page.locator('#legend-values').getAttribute('role'), 'switch');
    const controlsBox = await page.locator('nav').boundingBox();
    const toggleBox = await page.locator('.legend-option').boundingBox();
    const infoBox = await page.locator('#info').boundingBox();
    assert.ok(toggleBox.x + toggleBox.width <= infoBox.x,
        'The info button must be rightmost, after the legend toggle.');
    assert.ok(Math.abs(infoBox.x + infoBox.width - (controlsBox.x + controlsBox.width - 12)) < 2,
        'The options must align with the far right of the toolbar.');
    assert.equal(await page.locator('nav button').count(), await page.locator('nav button svg').count(),
        'Every navigation button must have an SVG icon.');

    assert.equal(await page.locator('#legend-values').isChecked(), false);
    assert.equal(await page.locator('.legend strong').first().isVisible(), false,
        'Legend values must be hidden by default.');
    await page.locator('#legend-values').check();
    assert.equal(await page.locator('.legend strong').first().isVisible(), true);

    const drawn = await page.locator('canvas:not(.overlay)[data-drawn]').count();
    assert.ok(drawn > 0 && drawn < series.length, 'Offscreen graphs must not be rendered.');

    const first = page.locator('canvas:not(.overlay)').first();
    const box = await first.boundingBox();
    const before = await page.evaluate(() => window.baseDraws);
    for (let offset = 0; offset < 12; offset++) {
        await page.mouse.move(box.x + 150 + offset * 10, box.y + 90);
    }
    await page.waitForSelector('#hover:not([hidden])');
    await page.waitForTimeout(70);
    assert.equal(await page.evaluate(() => window.baseDraws), before,
        'Moving the cursor must not redraw trace canvases.');
    assert.ok((await page.locator('#hover-values').innerText()).includes('Source 7'),
        'Hover must include selected metrics outside the viewport.');

    await page.locator('#zoom-in').click();
    await page.waitForFunction(count => window.baseDraws > count, before);
    await page.locator('section').last().scrollIntoViewIfNeeded();
    await page.waitForFunction(() => document.querySelectorAll('canvas:not(.overlay)')[7].dataset.drawn === 'true');

    await page.goto('about:blank');
    await page.setContent(document(true, null));
    await page.waitForFunction(() => window.messages.includes('ready'));
    const display = { ...recording, displayOnly: true,
        series: series.map(item => ({ ...item, samples: [] })) };
    await page.evaluate(frame => window.receiveChart(frame), display);
    await page.waitForFunction(() => window.messages.some(message => message.type === 'viewport' && message.keys.length > 0));
    const viewport = await page.evaluate(() => window.messages.filter(message => message.type === 'viewport').at(-1));
    assert.ok(viewport.keys.length < series.length, 'Native transfer must request only visible traces.');

    const filled = { ...display, series: series.map(item => ({
        ...item, samples: viewport.keys.includes(item.trace.key) ? item.samples : []
    })) };
    await page.evaluate(frame => window.receiveChart(frame), filled);
    await page.waitForTimeout(100);
    const liveBox = await page.locator('canvas:not(.overlay)').first().boundingBox();
    await page.locator("#legend-values").check();
    await page.waitForTimeout(70);
    const baseline = await page.evaluate(() => window.baseDraws);
    await page.mouse.move(liveBox.x + 220, liveBox.y + 80);
    await page.waitForFunction(() => window.messages.some(message => message.type === 'hover'));
    const hover = await page.evaluate(() => window.messages.filter(message => message.type === 'hover').at(-1));
    await page.evaluate(({ hover, keys }) => window.receiveChart({
        ...hover, values: Object.fromEntries(keys.map(key => [key, 999.12349]))
    }), { hover, keys: series.map(item => item.trace.key) });
    await page.waitForFunction(() => document.querySelector('#hover-values').textContent.includes('999.12'));
    assert.equal(await page.evaluate(() => window.baseDraws), baseline,
        'Exact native hover replies must update overlays without redrawing traces.');

    await page.evaluate(() => {
        window.retainedHoverRow = document.querySelector('#hover-values .hover-row');
        window.retainedHoverText = document.querySelector('#hover-values').textContent;
        window.retainedHoverTime = document.querySelector('#hover-time').textContent;
    });
    await page.mouse.move(liveBox.x + 280, liveBox.y + 80);
    await page.waitForFunction(previous => window.messages.filter(message => message.type === 'hover').at(-1).requestId > previous,
        hover.requestId);
    assert.equal(await page.evaluate(() => document.querySelector('#hover').hidden), false);
    assert.equal(await page.evaluate(() => document.querySelector('#hover-values').textContent === window.retainedHoverText), true,
        'Pending hover requests must not replace displayed values with dashes.');
    assert.equal(await page.evaluate(() => document.querySelector('#hover-time').textContent === window.retainedHoverTime), true,
        'The timestamp must match the retained values until the next result arrives.');
    const nextHover = await page.evaluate(() => window.messages.filter(message => message.type === 'hover').at(-1));
    await page.evaluate(({ hover, keys }) => window.receiveChart({
        ...hover, values: Object.fromEntries(keys.map(key => [key, 9.87654]))
    }), { hover: nextHover, keys: series.map(item => item.trace.key) });
    await page.waitForFunction(() => document.querySelector('#hover-values').textContent.includes('9.88'));
    assert.equal(await page.evaluate(() => document.querySelector('#hover-values .hover-row') === window.retainedHoverRow), true,
        'Hover rows must be updated in place.');
    assert.ok((await page.locator('.legend strong').first().innerText()).includes('9.88'),
        'Enabled legend values must follow the resolved hover values.');

    await page.evaluate(({ hover, keys }) => window.receiveChart({
        ...hover, values: Object.fromEntries(keys.map(key => [key, 111]))
    }), { hover, keys: series.map(item => item.trace.key) });
    assert.ok((await page.locator('#hover-values').innerText()).includes('9.88'),
        'Out-of-order hover replies must not overwrite newer values.');

    await page.evaluate(frame => window.receiveChart(frame), filled);
    await page.waitForTimeout(100);
    assert.equal(await page.evaluate(() => window.baseDraws), baseline,
        'Unchanged data must reuse the cached trace image.');
    await page.goto('about:blank');
    const gpu = { ...series[0], trace: { ...series[0].trace, group: 'Current P-state', label: 'ROG Astral · Current P-state',
        stepped: true, unit: '' }, count: 2, samples: [{ seconds: 0, value: 5 }, { seconds: 100, value: 0 }] };
    const gpuRecording = { ...recording, sampleCount: 2, series: [gpu],
        gpuDetails: [{ deviceId: 'gpu0', description: 'Supported P-states: P0, P5' }] };
    await page.setContent(document(false, gpuRecording));
    assert.equal(await page.getByText('GPU capabilities', { exact: true }).count(), 0);
    await page.locator('#legend-values').check();
    const gpuPlot = await page.locator('canvas:not(.overlay)').first().boundingBox();
    await page.mouse.move(gpuPlot.x + gpuPlot.width * 0.8, gpuPlot.y + gpuPlot.height * 0.5);
    await page.waitForFunction(() => document.querySelector('.legend strong').textContent === 'P5');
    assert.deepEqual(network, [], 'The exported viewer must remain offline.');
    assert.deepEqual(errors, [], 'The renderer must not produce JavaScript errors.');
    console.log('PASS: offline and native chart rendering, overlays, viewport requests, exact hover and redraw caching');
} finally {
    await browser.close();
}
