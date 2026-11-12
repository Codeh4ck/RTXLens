import assert from 'node:assert/strict';
import { join } from 'node:path';

export async function verifyUpdates(page, base, properties, setManifest, fail, requests, output, installedVersion) {
    await page.setViewportSize({ width: 1688, height: 696 });
    await properties({ ...base, checkUpdates: false, showAlerts: false });
    await page.evaluate(() => {
        window.openedUpdateLinks = [];
        window.plugins = { Linkprovider: { open: url => window.openedUpdateLinks.push(url) } };
    });

    const [major, minor, patch] = installedVersion.split('.').map(Number);
    const nextVersion = `${major}.${minor}.${patch + 1}`;
    const followingVersion = `${major}.${minor}.${patch + 2}`;
    const publishFixture = version => setManifest({
        schemaVersion: 1,
        releaseTag: `v${version}`,
        desktop: version,
        service: version,
        widget: version
    });

    publishFixture(nextVersion);
    const before = requests.filter(request => request.url.includes('rtxlens.com/updates/version.json')).length;
    await properties({ ...base, checkUpdates: false, updateFrequency: 'Monthly', showAlerts: false });
    assert.equal(requests.filter(request => request.url.includes('rtxlens.com/updates/version.json')).length, before);
    await page.locator('#panel1 .panel-title').dblclick();
    await page.locator('.check-updates').click();
    await page.getByRole('button', { name: 'View release', exact: true }).waitFor();
    assert.equal(await page.locator('.widget-flyout').getAttribute('data-mode'), 'updates');
    const updateText = await page.locator('.flyout-body').innerText();
    assert.ok(updateText.includes(installedVersion), `Expected installed widget version ${installedVersion}`);
    assert.ok(updateText.includes(nextVersion), `Expected available widget version ${nextVersion}`);
    await page.getByRole('button', { name: 'View release', exact: true }).click();
    assert.deepEqual(await page.evaluate(() => window.openedUpdateLinks),
        [`https://github.com/Codeh4ck/RTXLens/releases/tag/v${nextVersion}`]);
    await page.screenshot({ path: join(output, 'L-H-updates.png') });
    await page.getByRole('button', { name: 'Skip this version', exact: true }).click();
    assert.equal(await page.locator('.update-notice').count(), 0);
    assert.match(await page.evaluate(() => localStorage.getItem('rtx-monitor-updates/default')), /"skipped":true/);
    await page.locator('.flyout-close').click();

    publishFixture(followingVersion);
    await page.locator('#panel1 .panel-title').dblclick();
    await page.locator('.check-updates').click();
    await page.getByRole('button', { name: 'View release', exact: true }).waitFor();
    assert.ok((await page.locator('.flyout-body').innerText()).includes(followingVersion));
    await page.setViewportSize({ width: 840, height: 344 });
    const bounds = await page.locator('.widget-flyout').boundingBox();
    assert.ok(bounds.x >= 0 && bounds.y >= 0 && bounds.x + bounds.width <= 840 && bounds.y + bounds.height <= 344);
    await page.screenshot({ path: join(output, 'S-H-updates.png') });
    await page.getByRole('button', { name: 'Remind me later', exact: true }).click();
    assert.equal(await page.locator('.update-notice').count(), 0);

    fail(true);
    await page.getByRole('button', { name: 'Check now', exact: true }).click();
    await page.waitForFunction(() => document.querySelector('.flyout-body').textContent.includes('Could not check for updates'));
    await page.locator('.flyout-close').click();
    fail(false);
    await page.evaluate(() => {
        window.uniqueId = 'update-verification-instance';
        icueEvents.onICUEInitialized();
    });
    await page.locator('#panel1 .panel-title').dblclick();
    await page.locator('.check-updates').click();
    await page.getByRole('button', { name: 'View release', exact: true }).waitFor();
    await page.getByRole('button', { name: 'Skip this version', exact: true }).click();
    assert.match(await page.evaluate(() => localStorage.getItem('rtx-monitor-updates/update-verification-instance')), /"skipped":true/);
    assert.ok(await page.evaluate(() => localStorage.getItem('rtx-monitor-updates/default')));
    await page.locator('.flyout-close').click();
}
