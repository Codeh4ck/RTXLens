import assert from 'node:assert/strict';
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { resolve, dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { chromium } from 'playwright';
import { fixture, catalog, configuration } from '../tests/nvidia-fixtures.mjs';
import { verifyFlyouts } from './verification/verifyFlyouts.mjs';
import { verifyUpdates } from './verification/verifyUpdates.mjs';
import { verifyBars } from './verification/verifyBars.mjs';
import { verifyGpuStatus } from './verification/verifyGpuStatus.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const latest = JSON.parse(await readFile(resolve(root, '../../.dist/latest.json'), 'utf8'));
const output = resolve(root, '../../.dist/verification');
await mkdir(output, { recursive: true });
const browser = await chromium.launch({ channel: process.env.PLAYWRIGHT_CHANNEL || 'msedge', headless: true });
const errors = [];
const requests = [];
const messages = [];
const reports = [];
const sockets = new Set();
let paused = false;
let sequence = 0;
let transform = frame => frame;
let configuredKeys = [];
let metadata = configuration(1200);
let pluginCatalog = catalog();
let configurationError = false;
let updateManifest = { schemaVersion: 1, releaseTag: 'v1.0.0', desktop: '1.0.0', service: '1.0.0', widget: '1.0.0' };
let updateFailure = false;
const page = await browser.newPage({ reducedMotion: 'no-preference', hasTouch: true });
page.setDefaultTimeout(6000);
page.on('pageerror', error => errors.push(error.message));

function send(socket) {
    try { socket.send(JSON.stringify(transform({ ...fixture(), sequence: ++sequence }))); }
    catch { sockets.delete(socket); }
}

async function properties(values) {
    await page.evaluate(({ previous, values }) => {
        for (const key of previous) delete window[key];
        Object.assign(window, values);
        icueEvents.onDataUpdated();
    }, { previous: configuredKeys, values });
    configuredKeys = Object.keys(values);
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve()))));
}

async function measure(name) {
    return page.evaluate(name => {
        const visible = element => element.getClientRects().length && getComputedStyle(element).visibility !== 'hidden';
        const panels = [...document.querySelectorAll('.panel')].filter(visible);
        const overflow = [];
        for (const element of document.querySelectorAll('.panel,.metric-value,.row-value,.row-label,.pin-value,.headroom,.psu-comparison,.panel-title,.status-pill,.spread-badge,.chart-axis span,.gpu-usage,.core-voltage,.gpu-card')) {
            if (!visible(element)) continue;
            const bounds = element.getBoundingClientRect();
            if (element.scrollWidth > element.clientWidth + 2 || element.scrollHeight > element.clientHeight + 2
                || bounds.left < -1 || bounds.right > innerWidth + 1 || bounds.bottom > innerHeight + 1) {
                overflow.push({ selector: element.className, text: element.textContent.slice(0, 70), width: element.clientWidth,
                    scrollWidth: element.scrollWidth, height: element.clientHeight, scrollHeight: element.scrollHeight });
            }
        }
        return { name, width: innerWidth, height: innerHeight, panels: panels.length, overflow,
            summaryFont: document.querySelector('.metric-value') ? getComputedStyle(document.querySelector('.metric-value')).fontSize : null,
            barHeights: panels.flatMap(panel => [...panel.querySelectorAll('.pin-track')].filter(visible).map(track => track.clientHeight)) };
    }, name);
}

await page.route('**/*', async route => {
    const request = route.request();
    const url = new URL(request.url());
    if (url.protocol === 'file:' || url.protocol === 'data:') return route.continue();
    requests.push({ url: request.url(), method: request.method() });
    if (request.url() === 'https://rtxlens.com/updates/version.json') {
        if (updateFailure) return route.fulfill({ status: 503, headers: { 'Access-Control-Allow-Origin': '*' } });
        return route.fulfill({ json: updateManifest, headers: { 'Access-Control-Allow-Origin': '*' } });
    }
    if (url.hostname !== 'localhost'  || url.port !== '59273' || request.method() !== 'GET') return route.abort();
    let value;
    if (url.pathname === '/health') value = { status: 'ok' };
    if (url.pathname === '/plugins') value = pluginCatalog;
    if (url.pathname === '/configuration' && configurationError) return route.fulfill({ status: 503, headers: { 'Access-Control-Allow-Origin': '*' } });
    if (url.pathname === '/configuration') value = metadata;
    if (url.pathname === '/plugins/wireview-pro2/device/config') value = { friendlyName: 'WireView Pro 2', wireOcpFaultThresholdA: 9.2 };
    if (!value) return route.abort();
    return route.fulfill({ json: value, headers: { 'Access-Control-Allow-Origin': '*' } });
});
await page.routeWebSocket('ws://localhost:59273/sensors', socket => {
    sockets.add(socket);
    socket.onMessage(message => messages.push(String(message)));
    socket.onClose(() => sockets.delete(socket));
    send(socket);
});
const ticker = setInterval(() => { if (!paused) for (const socket of sockets) send(socket); }, 350);

try {
    await page.setViewportSize({ width: 1688, height: 696 });
    await page.goto(pathToFileURL(join(latest.runtime, 'index.html')).href);
    await page.waitForFunction(() => document.querySelector('.widget').dataset.connection === 'live');
    const base = { panel1: 'asusPins', panel2: 'asusStatus', panel3: 'msiConnector1', numberLocale: 'en-GB',
        asusCompare: true, wireviewCompare: true, showPeak: true, showAlerts: false };
    await verifyGpuStatus(page, base, properties, async value => { transform = value; for (const socket of sockets) send(socket); });
    await verifyBars(page, base, properties, value => { transform = value; for (const socket of sockets) send(socket); }, output);
    const largeFonts = Object.fromEntries(['title','label','value','unit','pin','axis','comparison','alert'].map(role=>[role+'Size',140]));
    for (const [name, width, height, count] of [['S-H',840,344,1], ['S-V',696,416,1], ['M-H',840,696,1],
        ['M-V',696,840,2], ['L-H',1688,696,2], ['L-V',696,1688,3], ['XL-H',2536,696,3], ['XL-V',696,2536,3]]) {
        await page.setViewportSize({ width, height });
        await properties(count === 1
            ? { ...base, panel1: 'nvidiaStatus', panel2: 'asusPins' }
            : base);
        await page.screenshot({ path: join(output, `${name}-dark.png`) });
        const report = await measure(name);
        assert.equal(report.panels, count, name+' panel count');
        reports.push(report);
    }
    for(const [name,width,height] of [['S-H',840,344],['M-V',696,840],['L-H',1688,696],['XL-H',2536,696]]) {
        await page.setViewportSize({width,height});
        await properties({ ...base, ...largeFonts, ...(name === 'S-H' ? { panel1: 'nvidiaStatus' } : {}) });
        const report=await measure(name+' maximum font sizes');
        reports.push(report);
        await page.screenshot({path:join(output,`${name}-max-fonts.png`)});
    }
    await page.setViewportSize({ width: 2536, height: 696 });
    await properties({ ...base, theme: 'Light', valueSize: 120, labelSize: 110 });
    assert.equal(await page.locator('html').evaluate(node=>node.style.getPropertyValue('--accent')),'#007f73');
    await page.screenshot({ path: join(output, 'XL-H-light.png') });
    reports.push(await measure('XL-H light large fonts'));
    await properties({ ...base, panel1: 'wireviewPins', panel2: 'msiConnector2', panel3: 'msiStatus', theme: 'Custom' });
    await page.screenshot({ path: join(output, 'XL-H-wireview-msi.png') });
    reports.push(await measure('WireView and both MSI connectors'));
    assert.match(await page.locator('#panel2 .pin-value').first().innerText(), /^0/);
    assert.match(await page.locator('#panel3 .panel-source').innerText(), /Ai1600TS/);
    assert.equal(await page.locator('#panel2 .metric-switch').isVisible(), false);

    await properties({ ...base, asusMetric: 'V' });
    const voltage = await page.locator('#panel1 .pin-fill').first().evaluate(element => ({ bottom: getComputedStyle(element).bottom,
        fill: parseFloat(element.style.getPropertyValue('--fill')) }));
    assert.equal(voltage.bottom, '0px');
    assert.ok(Math.abs(voltage.fill - 50) < .001);
    await page.locator('#panel1 [data-metric="W"]').click();
    await page.waitForFunction(() => document.querySelector('#panel1 [data-metric="W"]').getAttribute('aria-pressed') === 'true');
    assert.equal(await page.locator('#panel1 [data-metric="W"]').getAttribute('aria-pressed'), 'true');
    await page.locator('#panel1 .reset-peak').click();
    await properties({ ...base, showPeak: false });
    assert.equal(await page.locator('#panel1 .reset-peak').isVisible(), false);

    await properties({ ...base, panel1: 'msiConnector1', panel2: 'asusPins', panel3: 'wireviewPins', asusConnector: '2', wireviewConnector: '1' });
    assert.match(await page.locator('#panel2 .psu-comparison').first().innerText(), /^0/);
    assert.match(await page.locator('#panel3 .psu-comparison').first().innerText(), /5.8/);
    await properties({ ...base, panel1Title: '<img src=x onerror=alert(1)>' });
    assert.equal(await page.locator('#panel1 .panel-title img').count(), 0);
    assert.match(await page.locator('#panel2 .gpu-clocks .row-label').first().innerText(), /Core clock/);

    await properties(base);
    assert.equal(await page.locator('#panel1 .panel-summary').isVisible(),false);
    assert.match(await page.locator('#panel2 .summary-metric').first().innerText(),/Power/);
    assert.equal(await page.locator('.hero-metric').count(),0);
    assert.match(await page.locator('#panel2 .gpu-usage').innerText(),/97/);
    assert.equal(await page.locator('.summary-metric[data-metric="boardPower"]:visible,.summary-metric[data-metric="gpuUsage"]:visible').count(),0);
    await properties(base);
    assert.equal(await page.locator('.text-baseline').count(),0);
    assert.equal(await page.locator('#panel1 .unit').first().evaluate(node=>getComputedStyle(node).transform),'none');
    assert.equal(await page.locator('#panel1 .spread-label').innerText(),'Max Spread');
    assert.match(await page.locator('#panel1 .spread-reading').innerText(),/^0.4\s*A\s*Max Spread$/);
    const spreadFonts=await page.locator('#panel1').evaluate(panel=>['.spread-value .unit','.spread-label','.extreme-label'].map(selector=>getComputedStyle(panel.querySelector(selector)).fontSize));
    assert.equal(new Set(spreadFonts).size,1,'Spread unit and suffix match High/Low label size');
    const summaries=await page.locator('#panel2 .summary-metric').evaluateAll(tiles=>tiles.map(tile=>({
        width:tile.getBoundingClientRect().width,font:getComputedStyle(tile.querySelector('.metric-value')).fontSize,
        unit:getComputedStyle(tile.querySelector('.unit')).fontSize,border:getComputedStyle(tile).borderColor,
        borderWidth:getComputedStyle(tile).borderWidth,padding:parseFloat(getComputedStyle(tile).paddingLeft)
    })));
    assert.equal(new Set(summaries.map(tile=>tile.font)).size,1,'Summary values share one size');
    assert.equal(new Set(summaries.map(tile=>tile.unit)).size,1,'Summary units share one size');
    assert.ok(Math.max(...summaries.map(tile=>tile.width))-Math.min(...summaries.map(tile=>tile.width))<1,'Summary tiles have equal width');
    assert.ok(summaries.every(tile=>tile.borderWidth==='1px'&&tile.padding>=14));
    assert.equal(await page.locator('#panel2 .gpu-clocks .status-row').first().evaluate(node=>getComputedStyle(node).borderWidth),'0px');
    const usage=await page.locator('#panel2 .gpu-usage').boundingBox();
    const coreVoltage=await page.locator('#panel2 .core-voltage').boundingBox();
    assert.ok(usage.x+usage.width<=coreVoltage.x,'Core voltage sits beside GPU usage');
    assert.equal(await page.locator('.status-pill:visible').count(), 0, 'Healthy sources do not display Connected badges');
    assert.ok(coreVoltage.x-usage.x-usage.width<20);
    assert.ok(await page.locator('#panel2 .gpu-usage').evaluate(node=>parseFloat(getComputedStyle(node).fontSize))>=24);
    assert.match(await page.locator('#panel1 .spread-extreme').first().innerText(),/^P2\s+High$/);
    assert.match(await page.locator('#panel1 .spread-extreme').last().innerText(),/^P1\s+Low$/);
    assert.match(await page.locator('#panel1 .psu-comparison').first().innerText(),/^6/);
    assert.match(await page.locator('#panel1 .psu-comparison').last().innerText(),/^5.8/);
    await page.evaluate(()=>{
        const value=document.querySelector('#panel1 .pin-value');
        window.metricUnitBefore=value.querySelector('.unit');
        window.metricNumberBefore=value.querySelector('.number').firstChild;
        window.metricMutations=[];
        window.metricObserver=new MutationObserver(records=>window.metricMutations.push(...records.map(record=>record.type)));
        window.metricObserver.observe(value,{subtree:true,childList:true,attributes:true,characterData:true});
    });
    transform=frame=>({...frame,sensors:frame.sensors.map(reading=>reading.identity.pluginId==='asus-astral'
        && reading.identity.component==='GpuPin' && reading.identity.metric==='Current' && reading.identity.channel===1
        ? {...reading,value:reading.value+.2}:reading)});
    for(const socket of sockets) send(socket);
    await page.waitForFunction(()=>document.querySelector('#panel1 .pin-value .number').textContent==='6.0');
    const updates=await page.evaluate(()=>{
        window.metricObserver.disconnect();
        return {sameUnit:window.metricUnitBefore===document.querySelector('#panel1 .pin-value .unit'),
            sameText:window.metricNumberBefore===document.querySelector('#panel1 .pin-value .number').firstChild,
            mutations:window.metricMutations};
    });
    assert.equal(updates.sameUnit,true);
    assert.equal(updates.sameText,true);
    assert.ok(updates.mutations.includes('characterData'));
    assert.ok(updates.mutations.every(type=>type==='characterData'),'Updates must not rebuild metric elements or change unit positioning');
    transform=frame=>frame;
    for(const socket of sockets) send(socket);
    await page.waitForFunction(()=>document.querySelector('#panel1 .pin-value .number').textContent==='5.8');
    const spreadHeight=await page.locator('#panel1 .spread-badge').evaluate(node=>node.getBoundingClientRect().height);
    const buttonHeight=await page.locator('#panel1 .metric-switch button').first().evaluate(node=>node.getBoundingClientRect().height);
    assert.equal(spreadHeight,buttonHeight,'Spread and controls share one height');
    assert.equal(spreadHeight,52);
    await properties({...base,pinFillColor:'#ff44cc'});
    const fills=await page.locator('.pin-fill:visible').evaluateAll(nodes=>nodes.map(node=>getComputedStyle(node).backgroundColor));
    assert.equal(new Set(fills).size,1,'One fill colour controls every normal pin');
    assert.equal(fills[0],'rgb(255, 68, 204)');
    await properties(base);
    assert.equal(await page.locator('#panel1 .headroom').first().evaluate(node=>getComputedStyle(node).borderRadius),'5px');
    assert.equal(await page.locator('#panel1 .psu-comparison').first().evaluate(node=>getComputedStyle(node).borderRadius),'5px');
    assert.match(await page.locator('#panel1 .panel-icon').evaluate(node=>getComputedStyle(node).maskImage),/^url\("data:image\/svg\+xml;base64,/);

    await page.setViewportSize({width:2536,height:696});
    await properties({...base,asusCompare:false,showPeak:false,panel3:'msiStatus'});
    assert.equal(await page.locator('.limit-label,.axis-description').count(),0);
    assert.equal(await page.locator('#panel1 .panel-detail').isVisible(),false);
    assert.equal(await page.locator('#panel3 .panel-detail').isVisible(),false);
    assert.match(await page.locator('#panel1 .highest-pin').innerText(),/^P\d$/);
    assert.match(await page.locator('#panel1 .lowest-pin').innerText(),/^P\d$/);
    const chart = await page.locator('#panel1 .pin-chart').evaluate(element=>({
        height:element.clientHeight, bar:element.querySelector('.pin-track').clientHeight
    }));
    assert.ok(chart.bar>330,`Chart should use the available height with larger pin values: ${JSON.stringify(chart)}`);
    await page.screenshot({path:join(output,'XL-H-simplified.png')});
    await properties({...base,panel1:'msiConnector1',showPeak:false});
    assert.equal(await page.locator('#panel1 .headroom').first().isVisible(),false);
    assert.equal(await page.locator('#panel1 .chart-caption').isVisible(),false);
    assert.ok(await page.locator('#panel1 .pin-track').first().evaluate(element=>element.clientHeight)>320);
    await page.setViewportSize({width:840,height:344});
    await properties({...base,smallPanel:'3'});
    assert.equal(await page.locator('#panel3').isVisible(),true,'Compact selector starts on the requested panel');
    await page.setViewportSize({width:2536,height:696});
    await properties(base);

    await properties({...base,backgroundMedia:{pathToAsset:pathToFileURL(join(latest.runtime,'resources/asus.svg')).href,
        baseWidth:800,baseHeight:800,scale:1.5,positionX:12,positionY:18,angle:15}});
    await page.waitForFunction(()=>document.querySelector('#media-background img')?.complete);
    assert.equal(await page.locator('#media-background .rotation-container').evaluate(node=>node.style.transform),'rotate(15deg)');
    await properties(base);
    assert.equal(await page.locator('#media-background img').count(),0);

    await properties({...base,backgroundMedia:{pathToAsset:'file:///C:/removed-video.webm'}});
    assert.equal(await page.locator('#media-background video,#media-background img').count(),0);
    await properties(base);

    const connectionsBefore=sockets.size;
    assert.equal(connectionsBefore,1,'one socket per widget');
    for(const socket of [...sockets]) {sockets.delete(socket);await socket.close({code:1011,reason:'fixture service restart'});}
    await page.waitForFunction(()=>document.querySelector('.widget').dataset.connection === 'reconnecting');
    await page.waitForFunction(()=>document.querySelector('.widget').dataset.connection === 'live');
    assert.equal(sockets.size,1);

    await verifyFlyouts(page, base, properties, update => {
        transform = update;
        for (const socket of sockets) send(socket);
    }, requests, output, metadata, value => { configurationError = value; });

    await properties({ ...base, showAlerts: true });
    transform = frame => ({ ...frame, warnings: { instanceId: 'fixture-server', sequence: 1, notificationsEnabled: true, states: [{
        id: 'asus/overcurrent', pluginId: 'asus-astral', deviceId: '/gpu-nvidia/0', typeId: 'asus-astral/pin-overcurrent', icon: 'current', title: 'ROG Astral - Pin overcurrent',
        detail: 'P2: 10 A', phase: 'Active', episodeId: 'first', startedAt: frame.timestamp, lastNotificationAt: frame.timestamp,
        notificationSequence: 1, policy: { notifyOnStart: true, repeatWhileActive: false, repeatIntervalSeconds: 5, clearAfterSeconds: 2 }
    }] } });
    for (const socket of sockets) send(socket);
    await page.locator('.warning-card').waitFor();
    assert.equal(await page.locator('.widget-flyout').getAttribute('data-mode'), 'warnings');
    assert.equal(await page.locator('.preview-notice').count(), 0);
    await page.locator('.flyout-close').click();
    await page.waitForFunction(() => !document.querySelector('#service-alert').hidden);
    assert.match(await page.locator('#service-alert').innerText(), /overcurrent/i);
    await page.waitForTimeout(500);
    assert.equal(await page.locator('dialog').count(), 0, 'Unchanged warning sequences must not reopen a dismissed flyout');

    const activeWarning = transform;
    transform = frame => {
        const updated = activeWarning(frame);
        updated.warnings.sequence = 2;
        updated.warnings.states[0].notificationSequence = 2;
        updated.warnings.states[0].policy.repeatWhileActive = true;
        updated.warnings.states[0].detail = 'P2: 10.2 A';
        return updated;
    };

    for (const socket of sockets) send(socket);
    await page.locator('.warning-card').waitFor();
    assert.match(await page.locator('.widget-flyout').innerText(), /10.2 A/);
    await page.locator('.flyout-close').click();

    const reminderWarning = transform;
    transform = frame => {
        const updated = reminderWarning(frame);
        updated.warnings.sequence = 3;
        updated.warnings.states[0].notificationSequence = 3;
        updated.warnings.states[0].phase = 'Unavailable';
        return updated;
    };

    for (const socket of sockets) send(socket);
    await page.waitForTimeout(500);
    assert.equal(await page.locator('dialog').count(), 0, 'Unavailable warnings must not open a new flyout');
    assert.match(await page.locator('#service-alert').innerText(), /Data unavailable/);

    transform = frame => frame;
    for (const socket of sockets) send(socket);
    await page.waitForFunction(() => document.querySelector('#service-alert').hidden);

    await properties(base);
    paused = true;
    await page.waitForTimeout(1900);
    assert.equal((await page.locator('#panel1 .pin-value').first().innerText()).trim(), '-');
    paused = false;
    for (const socket of sockets) send(socket);
    await page.waitForFunction(() => document.querySelector('#panel1 .pin-value').textContent.includes('5.8'));
    assert.equal(await page.locator('footer,.chart-footer,#service-dialog').count(),0);
    assert.equal(await page.locator('#panel1 .status-pill').isVisible(),false);
    const typography = await page.locator('#panel1 .pin-value').first().evaluate(node=>({
        font:getComputedStyle(node).fontFamily,weight:getComputedStyle(node).fontWeight
    }));
    assert.match(typography.font,/Consolas/);
    assert.equal(typography.weight,'700');
    await properties({...base,fontFamily:'Arial',showScanlines:true});
    assert.match(await page.locator('#panel2 .row-value').first().evaluate(node=>getComputedStyle(node).fontFamily),/Arial/);
    assert.equal(await page.locator('.widget').getAttribute('data-scanlines'),'true');
    await properties(base);

    pluginCatalog = { schemaVersion:1, plugins: catalog().plugins.map(plugin => plugin.id==='wireview-pro2' ? { ...plugin, enabled:false, state:'Disabled' } : plugin) };
    metadata = configuration(1200);
    metadata.revision = 8;
    metadata.configuration.plugins['wireview-pro2'].enabled = false;
    transform = frame => ({ ...frame, configurationRevision:8, sensors:frame.sensors.filter(reading=>reading.identity.pluginId!=='wireview-pro2'),
        plugins:frame.plugins.map(plugin=>plugin.pluginId==='wireview-pro2'?{...plugin,state:'Disabled'}:plugin) });
    for(const socket of sockets) send(socket);
    await page.waitForTimeout(600);
    await properties({...base,panel1:'wireviewPins'});
    assert.equal(await page.locator('#panel1 .status-pill').innerText(),'Disabled');
    transform = frame => ({ ...frame, schemaVersion:2 });
    for(const socket of sockets) send(socket);
    await page.waitForFunction(()=>document.querySelector('.widget').dataset.connection === 'incompatible');
    transform = frame => frame;
    paused = true;

    const runtimeHtml=await readFile(join(latest.runtime,'index.html'),'utf8');
    const reader=runtimeHtml.slice(runtimeHtml.indexOf('function readRtxProperty(name)'),runtimeHtml.indexOf('function configureRTXLens('));
    const lexicalProperty=await page.evaluate(source=>{
        delete window.panel1;
        const read=Function('panel1',source+'; return readRtxProperty;')('msiConnector2');
        return {value:read('panel1'),namedElement:window.panel1 instanceof Element};
    },reader);
    assert.equal(lexicalProperty.namedElement,true,'test exercises the DOM named-property collision');
    assert.equal(lexicalProperty.value,'msiConnector2','iCUE sandbox-local settings win over DOM named properties');

    await verifyUpdates(page, base, properties, value => { updateManifest = value; }, value => { updateFailure = value; }, requests, output, latest.version);
    await page.setViewportSize({ width: 840, height: 344 });
    paused = false;
    await page.reload();
    await page.waitForFunction(() => document.querySelector('.widget').dataset.connection === 'live');
    const execution = {
        id: 'e22d33b8-0eb6-4749-bd00-3b35fccbd40e', script: 'Low power profile & shutdown',
        connector: 2, startedAt: new Date().toISOString(), status: 'Running'
    };
    transform = frame => ({ ...frame, safeguardExecutions: [execution] });
    for (const socket of sockets) send(socket);
    const safeguard = page.locator('dialog[data-mode="safeguard"]');
    await safeguard.waitFor();
    await safeguard.getByText(/Connector 2.*Running/).waitFor();
    execution.status = 'Shutdown requested';
    for (const socket of sockets) send(socket);
    await safeguard.getByText(/Connector 2.*Shutdown requested/).waitFor();
    assert.equal(await safeguard.locator('.warning-card').count(), 1);
    await safeguard.getByRole('button', { name: 'Close flyout' }).click();
    await page.reload();
    await page.waitForFunction(() => document.querySelector('.widget').dataset.connection === 'live');
    await page.waitForTimeout(700);
    assert.equal(await safeguard.count(), 0, 'A replayed Safeguard+ execution must not notify again after reload.');
    transform = frame => frame;
    for (const socket of sockets) send(socket);

    assert.equal(messages.length, 0, 'No commands sent through the telemetry socket');
    assert.ok(requests.every(request => request.method === 'GET' && (
        request.url.startsWith('http://localhost:59273/')
        || request.url === 'https://rtxlens.com/updates/version.json')));
    assert.deepEqual(errors, []);
    const overflow = reports.filter(report=>report.overflow.length || report.barHeights.some(height=>height<20));
    await writeFile(join(output, 'report.json'), JSON.stringify({ reports, voltage, errors, requests, clientSocketMessages:messages }, null, 2));
    console.log(JSON.stringify({ package: latest.package, screenshots:output, layouts:reports.map(report=>({name:report.name,
        size:`${report.width}x${report.height}`,panels:report.panels,overflow:report.overflow.length,
        minimumBar:report.barHeights.length?Math.min(...report.barHeights):null})), errors, readOnlyRequests:requests.length,
        scenarios:'sources, comparison connectors, metric switching, peaks, compact starting panel, simplified chart captions and height, SVG masks, background image transforms, video rejection, shared font, scan line toggle, reconnect, stale expiry, alerts, disabled plugins, unsupported schema, fixed usage width, ungrouped clocks/RPM, hidden content removal, paused background rendering, mouse/touch double tap, scrollable read-only config in all slots, configuration error/retry, warning previews and acknowledgement' }, null, 2));
    assert.deepEqual(overflow, [], 'Visible metrics must fit their assigned slots');
} catch (error) {
    await page.screenshot({ path: join(output, 'failure.png') }).catch(() => {});
    console.error(JSON.stringify({ errors, requests, socketCount: sockets.size, reports, media: await page.evaluate(()=>({
        html:document.querySelector('#media-background')?.innerHTML,reducedMotion:matchMedia('(prefers-reduced-motion: reduce)').matches,
        state:[...document.querySelectorAll('video')].map(video=>({ready:video.readyState,paused:video.paused,error:video.error?.message}))
    })), body: (await page.locator('body').innerText().catch(()=>'' )).slice(0, 500) }, null, 2));
    throw error;
} finally {
    clearInterval(ticker);
    await browser.close();
}
