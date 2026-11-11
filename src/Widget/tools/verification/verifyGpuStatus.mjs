import assert from 'node:assert/strict';

export async function verifyGpuStatus(page, base, properties, transform) {
    await page.setViewportSize({ width: 840, height: 696 });
    await properties({ ...base, panelCount: 'One', panel1: 'nvidiaStatus', showHotspot: true, precision: '2' });
    await page.waitForFunction(() => document.querySelector('#panel1 .core-voltage-value')?.textContent.replaceAll(' ', '').includes('925.00mV'));
    assert.equal(await page.locator('#panel1 .status-pill').isVisible(), false);
    const cards = page.locator('#panel1 .status-rows > .gpu-card');
    assert.deepEqual(await cards.evaluateAll(nodes => nodes.map(node => node.getAttribute('aria-label'))),
        ['Clocks', 'VRAM usage', 'Temperatures', 'Fans']);
    const cardStyles = await cards.evaluateAll(nodes => nodes.map(node => {
        const style = getComputedStyle(node);
        return {
            background: style.backgroundColor,
            border: style.border,
            accent: getComputedStyle(node.closest('.panel')).borderTopColor,
            borderColor: style.borderTopColor,
            radius: style.borderRadius,
            padding: style.padding
        };
    }));
    assert.equal(new Set(cardStyles.map(style => JSON.stringify(style))).size, 1);
    assert.ok(cardStyles.every(style => style.border.startsWith('1px solid') && style.borderColor === style.accent),
        'GPU status cards share a visible accent border');
    assert.deepEqual(await page.locator('#panel1 .gpu-temperatures .row-label').allTextContents(),
        ['Core temp', 'VRAM temp']);
    assert.match(await page.locator('#panel1 .gpu-temperatures .gpu-reading-columns').innerText(), /62\.0.*70\.0/s);
    const vram = page.locator('#panel1 .gpu-vram');
    assert.ok(await vram.locator('.gpu-card-title [data-icon="vramusage"]').isVisible());
    assert.ok(await page.locator('#panel1 .gpu-card-title > .icon').evaluateAll(icons => {
        const panelIcon = getComputedStyle(icons[0].closest('.panel').querySelector('.panel-icon'));
        return icons.length === 4 && icons.every(icon => {
            const style = getComputedStyle(icon);
            return style.width === panelIcon.width && style.height === panelIcon.height
                && style.backgroundColor === panelIcon.backgroundColor;
        });
    }), 'GPU status cards use the original accent-colored icon size');
    assert.ok(await vram.evaluate(node => {
        const title = node.querySelector('.gpu-card-title').getBoundingClientRect();
        const reading = node.querySelector('.gpu-vram-reading').getBoundingClientRect();
        return title.right < reading.left && Math.abs((title.top + title.bottom) - (reading.top + reading.bottom)) < 2;
    }), 'VRAM uses the same vertically centered heading layout');
    const vramTrack = vram.locator('.gpu-vram-track');
    assert.equal(await vramTrack.getAttribute('aria-valuenow'), '75');
    assert.ok((await vramTrack.boundingBox()).height >= 7.5);
    const fanDuty = page.locator('#panel1 .gpu-fans .fan-duty');
    assert.equal(await fanDuty.count(), 2);
    assert.ok(await fanDuty.evaluateAll(nodes => nodes.every(node => {
        const rpm = node.parentElement.querySelector('.metric-part').getBoundingClientRect();
        const duty = node.getBoundingClientRect();
        return Math.abs((rpm.top + rpm.bottom) - (duty.top + duty.bottom)) < 2;
    })), 'Fan duty fits alongside RPM');
    assert.deepEqual(await page.locator('#panel1 .gpu-reading-columns').evaluateAll(groups => groups.map(group => {
        const heading = group.previousElementSibling.getBoundingClientRect();
        const columns = group.getBoundingClientRect();
        const readings = [...group.querySelectorAll('.status-row')];
        const divider = readings[1].getBoundingClientRect().left;
        const valueCenters = readings.map(row => {
            const value = row.querySelector('.row-value').getBoundingClientRect();
            return (value.left + value.right) / 2;
        });
        return heading.right < columns.left && Math.abs((heading.top + heading.bottom) - (columns.top + columns.bottom)) < 2
            && getComputedStyle(readings[1]).borderInlineStartWidth === '1px'
            && Math.abs(divider - (valueCenters[0] + valueCenters[1]) / 2) < 1;
    })), [true, true, true], 'Headers are centered beside divided reading columns');
    assert.ok(await cards.evaluateAll(nodes => {
        const lastCard = nodes.at(-1);
        return Math.abs(lastCard.getBoundingClientRect().bottom - lastCard.parentElement.getBoundingClientRect().bottom) < 1;
    }), 'Cards fill the available height');

    const unitTone = await page.locator('#panel1 .fan-duty .number').first().evaluate(node => ({
        color: getComputedStyle(node).color,
        expected: getComputedStyle(node.closest('.row-value').querySelector('.unit')).color
    }));
    assert.equal(unitTone.color, unitTone.expected, 'Fan duty uses unit coloring');

    await page.evaluate(() => {
        window.gpuStatusNodes = {
            voltage: document.querySelector('#panel1 .core-voltage-value .number'),
            fan: document.querySelector('#panel1 [data-metric="fan1"] .fan-duty .number')
        };
    });
    await transform(frame => ({ ...frame, sensors: frame.sensors.map(reading => reading.identity.pluginId === 'generic-nvidia'
        ? reading.identity.component === 'GpuCore' && reading.identity.metric === 'Voltage' ? { ...reading, value: 1.025 }
            : reading.identity.component === 'GpuFan' && reading.identity.metric === 'FanDuty' ? { ...reading, value: 55 } : reading
        : reading) }));
    await page.waitForFunction(() => document.querySelector('#panel1 .core-voltage-value')?.textContent.includes('1025.00'));
    assert.ok(await page.evaluate(() => window.gpuStatusNodes.voltage === document.querySelector('#panel1 .core-voltage-value .number')
        && window.gpuStatusNodes.fan === document.querySelector('#panel1 [data-metric="fan1"] .fan-duty .number')),
    'Sensor updates reuse the existing text nodes');

    await properties({ ...base, panelCount: 'One', panel1: 'nvidiaStatus', showFanDuty: false });
    assert.equal(await page.locator('#panel1 .fan-duty').count(), 0);
    assert.deepEqual(await page.locator('#panel1 .gpu-fans .row-label').allTextContents(), ['Fan 1', 'Fan 2']);
    assert.equal(await page.locator('#panel1 [data-metric="temperatureHotspot"]').count(), 0);
    await transform(frame => frame);
    await properties(base);
}
