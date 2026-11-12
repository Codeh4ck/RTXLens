import assert from 'node:assert/strict';
import { join } from 'node:path';
import { warningOptions } from '../../src/warnings/previewWarnings.ts';

export async function verifyFlyouts(page, base, properties, setTransform, requests, output, metadata, failConfiguration) {
    await page.setViewportSize({ width: 2536, height: 696 });
    await properties(base);
    const widths = [];
    for (const usage of [5, 60, 100]) {
        setTransform(frame => ({ ...frame, sensors: frame.sensors.map(reading => reading.identity.pluginId === 'generic-nvidia'
            && reading.identity.component === 'GpuCore' && reading.identity.metric === 'Utilization' ? { ...reading, value: usage } : reading) }));
        await page.waitForFunction(value => document.querySelector('#panel2 .gpu-usage-value').textContent.includes(value + '%'), usage);
        widths.push((await page.locator('#panel2 .gpu-usage').boundingBox()).width);
    }
    assert.ok(Math.max(...widths) - Math.min(...widths) < .1, 'GPU usage reserves width through 100%');
    setTransform(frame => frame);
    assert.match(await page.locator('#panel2 .status-rows').innerText(), /2760\s+MHz/);
    assert.match(await page.locator('#panel2 .status-rows').innerText(), /14001\s+MHz/);
    assert.match(await page.locator('#panel2 .status-rows').innerText(), /1220\s+RPM/);

    // Hidden slots and disabled features must not retain background DOM.
    await page.setViewportSize({ width: 840, height: 344 });
    await properties({ ...base, smallPanel: '1', showSource: false, showAxis: false, showHeadroom: false,
        showSpread: false, showPeak: false, asusCompare: false, panel1Hero: false, panel3: 'off' });
    assert.equal(await page.locator('#panel2 * ,#panel3 *').count(), 0);
    assert.equal(await page.locator('#panel1 .status-rows,#panel1 .panel-summary,#panel1 .panel-source,#panel1 .chart-axis,#panel1 .headroom,#panel1 .spread-badge,#panel1 .peak-marker,#panel1 .psu-comparison').count(), 0);
    await page.evaluate(() => {
        window.hiddenMutations = [];
        window.hiddenObserver = new MutationObserver(records => window.hiddenMutations.push(...records));
        window.hiddenObserver.observe(document.querySelector('#panel2'), { subtree: true, childList: true, characterData: true });
    });
    await page.waitForTimeout(700);
    assert.equal(await page.evaluate(() => window.hiddenMutations.length), 0);
    await page.evaluate(() => window.hiddenObserver.disconnect());
    await properties({ ...base, smallPanel: '2' });
    assert.equal(await page.locator('#panel1 *').count(), 0);
    assert.equal(await page.locator('#panel2 .pin-chart,#panel2 .chart-caption').count(), 0);
    assert.equal(await page.locator('#panel2 .status-row').count(), 7);

    for (const [name, width, height] of [['S-H', 840, 344], ['S-V', 696, 416], ['M-H', 840, 696], ['M-V', 696, 840],
        ['L-H', 1688, 696], ['L-V', 696, 1688], ['XL-H', 2536, 696], ['XL-V', 696, 2536]]) {
        await page.setViewportSize({ width, height });
        await properties({ ...base, smallPanel: '1' });
        const before = requests.filter(request => request.url.endsWith('/configuration')).length;
        await page.locator('#panel1 .panel-title').dblclick();
        await page.locator('.configuration-notice').waitFor();
        assert.equal(await page.locator('.widget-flyout').getAttribute('data-mode'), 'configuration');
        assert.ok(requests.filter(request => request.url.endsWith('/configuration')).length > before, 'Double tap fetches current settings');
        assert.match(await page.locator('.flyout-body').innerText(), /ROG Astral[\s\S]*WireView Pro II[\s\S]*MSI MPG PSU/);
        assert.match(await page.locator('.flyout-body').innerText(), /Per-pin overcurrent \(A\)[\s\S]*9.2/);
        assert.equal(await page.locator('.widget-flyout input,.widget-flyout select,.widget-flyout textarea').count(), 0);
        const bounds = await page.locator('.widget-flyout').boundingBox();
        assert.ok(bounds.x >= 0 && bounds.y >= 0 && bounds.x + bounds.width <= width && bounds.y + bounds.height <= height);
        const scroll = await page.locator('.flyout-body').evaluate(node => ({ scroll: node.scrollHeight, height: node.clientHeight, overflow: getComputedStyle(node).overflowY }));
        assert.equal(scroll.overflow, 'auto');
        if (height < 800) assert.ok(scroll.scroll > scroll.height, `${name} configuration scrolls`);
        if (name === 'XL-H' || name === 'S-H') await page.screenshot({ path: join(output, `${name}-configuration.png`) });
        await page.locator('.flyout-body').evaluate(node => { node.scrollTop = node.scrollHeight; });
        assert.ok(await page.locator('.configuration-notice').isVisible());
        await page.locator('.flyout-close').click();
        assert.equal(await page.locator('dialog').count(), 0, 'Closing releases flyout content');
    }

    await page.setViewportSize({ width: 2536, height: 696 });
    await properties(base);
    const originalInterval = metadata.configuration.plugins['asus-astral'].pollIntervalMs;
    metadata.configuration.plugins['asus-astral'].pollIntervalMs = 2345;
    // Touch pointers exercise the same gesture used on Xeneon Edge.
    const heading = await page.locator('#panel1 .panel-title').boundingBox();
    await page.touchscreen.tap(heading.x + 10, heading.y + 10);
    await page.touchscreen.tap(heading.x + 10, heading.y + 10);
    await page.locator('.configuration-notice').waitFor();
    assert.match(await page.locator('.flyout-body').innerText(), /2345/);
    metadata.configuration.plugins['asus-astral'].pollIntervalMs = originalInterval;
    await page.evaluate(() => {
        window.backgroundMutations = [];
        window.backgroundObserver = new MutationObserver(records => window.backgroundMutations.push(...records));
        window.backgroundObserver.observe(document.querySelector('.main-content'), { subtree: true, childList: true, characterData: true, attributes: true });
    });
    await page.waitForTimeout(900);
    assert.equal(await page.evaluate(() => window.backgroundMutations.length), 0, 'Dashboard stops rendering beneath the flyout');
    await page.evaluate(() => window.backgroundObserver.disconnect());
    await page.keyboard.press('Escape');
    assert.equal(await page.locator('dialog').count(), 0);
    await page.locator('#panel1 [data-metric="A"]').dblclick();
    assert.equal(await page.locator('dialog').count(), 0, 'Double tapping a control does not open configuration');

    failConfiguration(true);
    await page.locator('#panel1 .panel-title').dblclick();
    await page.locator('.flyout-error').waitFor();
    assert.match(await page.locator('.flyout-error').innerText(), /HTTP 503/);
    failConfiguration(false);
    await page.locator('.flyout-retry').click();
    await page.locator('.configuration-section').first().waitFor();
    await page.locator('.flyout-close').click();

    for (const { key } of warningOptions.filter(option => option.key !== 'none')) {
        await properties({ ...base, warningPreview: key });
        assert.equal(await page.locator('.widget-flyout').getAttribute('data-mode'), 'preview');
        const count = ['electrical', 'thermal', 'combined'].includes(key) ? 2 : 1;
        assert.equal(await page.locator('.warning-card').count(), count);
        assert.match(await page.locator('.preview-notice').innerText(), /SIMULATED/);
        assert.match(await page.locator('.configuration-notice').innerText(), /desktop app/);
        if (key === 'combined') await page.screenshot({ path: join(output, 'XL-H-warning-preview.png') });
        await page.locator('.flyout-close').click();
        await page.waitForTimeout(400);
        assert.equal(await page.locator('dialog').count(), 0, 'Dismissed preview stays dismissed');
    }
    await properties({ ...base, warningPreview: 'thermal' });
    await properties(base);
    assert.equal(await page.locator('dialog').count(), 0, 'Off closes the warning preview');
    assert.equal(await page.locator('#panel1 .status-pill').isVisible(), false);
    assert.equal(await page.locator('#panel1 .pin-value').first().textContent(), '5.8 A', 'Previews never change real telemetry');
}
