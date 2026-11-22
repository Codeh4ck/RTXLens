import { build } from 'esbuild';
import { readFile, writeFile, mkdir, readdir, cp, open, unlink, mkdtemp, rm, rename } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { dirname, resolve, join, sep, basename } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { definitions, hostDefinitions } from '../src/settings/definitions.ts';
import { validateImportMetadata } from './validation/validateImportMetadata.mjs';
import { readZipEntries } from './validation/readZipEntries.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const output = resolve(root, '../..', '.dist');
const lockPath = join(root, '.build.lock');
let lock;
let temporary;
const skipChecks = process.argv.includes('--skip-checks');

function run(command, args) {
    const result = spawnSync(command, args, { cwd: root, encoding: 'utf8', stdio: 'inherit', windowsHide: true });
    if (result.error) throw result.error;
    if (result.status !== 0) throw new Error(`${command} failed with exit code ${result.status}.`);
}

function attribute(value) {
    return String(value).replaceAll('&', '&amp;').replaceAll('"', '&quot;').replaceAll('<', '&lt;').replaceAll('>', '&gt;');
}

function translate(key) { return `tr(${JSON.stringify(key)})`; }

function propertyMetadata() {
    return definitions.map(definition => {
        const attributes = { name: 'x-icue-property', content: definition.id, 'data-label': translate(definition.label), 'data-type': definition.type };
        if (definition.defaultValue !== null) attributes['data-default'] = JSON.stringify(definition.defaultValue);
        if (definition.type === 'slider') {
            Object.assign(attributes, { 'data-min': definition.min, 'data-max': definition.max, 'data-step': definition.step,
                'data-unit-label': JSON.stringify(definition.unit ?? '') });
        }
        if (definition.options) attributes['data-values'] = '[' + definition.options.map(option =>
            `{key:${JSON.stringify(option.key)},value:${translate(option.label)}}`).join(',') + ']';
        if (definition.type === 'media-selector') attributes['data-filters'] = "['*.png','*.jpg','*.jpeg','*.gif','*.webp','*.svg','*.bmp']";
        return '    <meta ' + Object.entries(attributes).map(([name, value]) => `${name}="${attribute(value)}"`).join(' ') + ' />';
    }).join('\n');
}

function bridge() {
    const cases = definitions.map(definition => `case ${JSON.stringify(definition.id)}: value = typeof ${definition.id} !== 'undefined' ? ${definition.id} : undefined; break;`).join('\n');
    return `
function readRtxProperty(name) {
    var value;
    try { switch (name) { ${cases} } } catch (_) {}
    if (value !== undefined && !(value instanceof Element)) return value;
    var globalValue = window[name];
    return globalValue instanceof Element ? undefined : globalValue;
}
function openRtxRelease(url) {
    try {
        var providers = typeof plugins !== 'undefined' ? plugins : window.plugins;
        if (providers && providers.Linkprovider && typeof providers.Linkprovider.open === 'function') {
            providers.Linkprovider.open(url);
            return true;
        }
    } catch (_) {}
    return false;
}
function configureRTXLens(ready) {
    var temperature = '°C';
    try { if (typeof iCUE !== 'undefined' && typeof iCUE.defaultTemperatureUnit === 'function') temperature = iCUE.defaultTemperatureUnit(); } catch (_) {}
    var instance = typeof uniqueId !== 'undefined' ? String(uniqueId) : 'default';
    RTXLensRuntime.initialize(readRtxProperty, temperature, instance, openRtxRelease, typeof iCUE === 'undefined' || ready === true);
}
if (typeof icueEvents === 'undefined') globalThis.icueEvents = {};
icueEvents = {
    onICUEInitialized: function () { configureRTXLens(true); },
    onDataUpdated: function () { configureRTXLens(true); }
};
configureRTXLens(false);`;
}

try {
    lock = await open(lockPath, 'wx');
    if (!skipChecks) {
        run(process.execPath, [join(root, 'node_modules/typescript/bin/tsc'), '--noEmit']);
        run(process.execPath, ['--experimental-strip-types', '--test', 'tests/*.test.mjs']);
    }
    const cliLookup = spawnSync(process.platform === 'win32' ? 'where.exe' : 'which', ['icuewidget'], { encoding: 'utf8', windowsHide: true });
    const cli = process.env.ICUEWIDGET_CLI || cliLookup.stdout?.trim().split(/\r?\n/)[0];
    if (!cli || !existsSync(cli)) throw new Error('Install icuewidget on PATH or set ICUEWIDGET_CLI before building.');
    run(cli, ['--version']);
    await mkdir(output, { recursive: true });
    const manifest = JSON.parse(await readFile(join(root, 'manifest.json'), 'utf8'));
    const name = 'rtxlens';
    const stage = join(output, name);
    const packagePath = join(output, `${name}.icuewidget`);
    const versions = JSON.parse(await readFile(join(root, '../../version.json'), 'utf8'));

    if (versions.schemaVersion !== 1 || !/^v\d+\.\d+\.\d+$/.test(versions.releaseTag)
        || !['desktop', 'service', 'widget'].every(component => /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/.test(versions[component]))) {
        throw new Error('Unsupported version.json release manifest.');
    }

    const version = versions.widget;
    const pendingPackage = join(output, `${name}.building.icuewidget`);
    await mkdir(stage, { recursive: true });
    const bundle = await build({ entryPoints: [join(root, 'src/main.ts')], bundle: true, write: false,
        define: { RTX_WIDGET_VERSION: JSON.stringify(version) },
        target: 'chrome130', format: 'iife', globalName: 'RTXLensRuntime', minify: false, legalComments: 'inline' });
    const styles = await Promise.all(['tokens', 'panels', 'charts', 'icons', 'flyouts'].map(name => readFile(join(root, `src/styles/${name}.css`), 'utf8')));
    let stylesheet = styles.join('\n');
    // Chromium masks loaded from a file origin can be blocked by CORS. Keep the original SVGs
    // in the package and inline their exact bytes for CSS masks, without a runtime fetch.
    for (const filename of await readdir(join(root, 'resources'))) {
        if (!filename.endsWith('.svg')) continue;
        const data = (await readFile(join(root, 'resources', filename))).toString('base64');
        stylesheet = stylesheet.replaceAll(`url('resources/${filename}')`, `url("data:image/svg+xml;base64,${data}")`);
    }
    // Keep the old property IDs declared so iCUE can inject saved instance values on upgrade.
    // Only host-owned controls belong in iCUE's settings panels.
    const profileGroup = 'RTX Lens profile';
    const profileHelp = 'In RTX Lens, open Configuration and select iCUE Widget. Enter the profile name here, or enter Instance to use the settings saved in iCUE for this widget.';
    const groups = [
        { title: translate(profileGroup), info: translate(profileHelp), properties: ['widgetProfile'] },
        { title: translate('Widget Personalization'), properties: hostDefinitions.filter(setting => setting.id !== 'widgetProfile').map(setting => setting.id) }
    ];
    const metadata = propertyMetadata() + '\n<script type="application/json" id="x-icue-groups">' + JSON.stringify(groups) + '</script>';
    const runtime = bundle.outputFiles[0].text + bridge();
    const template = await readFile(join(root, 'index.html'), 'utf8');
    const html = template.replace('<!-- ICUE_PROPERTIES -->', () => metadata).replace('/* WIDGET_STYLES */', () => stylesheet)
        .replace('/* WIDGET_RUNTIME */', () => runtime.replaceAll('</script', '<\\/script'));
    await writeFile(join(stage, 'index.html'), html);
    await writeFile(join(stage, 'manifest.json'), JSON.stringify({ ...manifest, version }, null, 2));
    const translations = { 'RTX Lens': 'RTX Lens', [profileGroup]: profileGroup, [profileHelp]: profileHelp };
    for (const definition of definitions) {
        translations[definition.label] = definition.label;
        translations[definition.group] = definition.group;
        for (const option of definition.options ?? []) translations[option.label] = option.label;
    }
    const localization = { en: { translation: translations } };
    validateImportMetadata(html, localization);
    await writeFile(join(stage, 'translation.json'), JSON.stringify(localization, null, 2));
    await cp(join(root, 'resources'), join(stage, 'resources'), { recursive: true, filter: source => basename(source) !== 'fonts' });
    await writeFile(join(stage, 'INSTALL.md'), [
        '# RTX Lens for Xeneon Edge', '',
        'Install the RTX Lens Windows service on this PC. Import rtxlens.icuewidget in iCUE and assign it to a Xeneon Edge slot.', '',
        '[Setup and troubleshooting](https://github.com/Codeh4ck/RTXLens#download-and-install)', ''
    ].join('\n'));
    await cp(join(root, 'THIRD-PARTY-NOTICES.md'), join(stage, 'THIRD-PARTY-NOTICES.md'));
    // CLI 0.2.3 excludes hidden path components, including a .dist ancestor. Package a temporary
    // copy outside .dist, then retain the actual runtime and package only under the requested output.
    temporary = await mkdtemp(join(tmpdir(), 'rtxlens-'));
    await cp(stage, temporary, { recursive: true });
    if (!skipChecks) run(cli, ['validate', temporary]);
    run(cli, ['package', temporary, '--output', pendingPackage]);
    if (!existsSync(pendingPackage)) throw new Error('Packaging did not produce an icuewidget file.');
    const archive = await readFile(pendingPackage);
    const directory = archive.lastIndexOf(Buffer.from([0x50, 0x4b, 0x05, 0x06]));
    if (directory < 0 || archive.readUInt16LE(directory + 10) < 18 || archive.length < 20000
        || !['manifest.json', 'index.html', 'translation.json', 'resources/icon.svg'].every(name => archive.includes(Buffer.from(name)))) {
        throw new Error('The generated archive is empty or missing required runtime files.');
    }
    const entries = readZipEntries(archive);
    const packagedHtml = entries.get('index.html')?.toString('utf8');
    const packagedTranslations = entries.get('translation.json')?.toString('utf8');
    if (packagedHtml !== html || packagedTranslations !== JSON.stringify(localization, null, 2)) {
        throw new Error('The packaged metadata differs from the validated build output.');
    }
    const imported = validateImportMetadata(packagedHtml, JSON.parse(packagedTranslations));
    for (const asset of imported.assets) {
        if (!entries.has(asset)) throw new Error(`Missing packaged metadata asset: ${asset}`);
    }
    console.log(`Import metadata verified: ${imported.title}; icon, preview and English translations present.`);
    await rename(pendingPackage, packagePath);
    const sha256 = createHash('sha256').update(archive).digest('hex');
    await writeFile(join(output, 'latest.json'), JSON.stringify({ version, package: packagePath, runtime: stage, sha256 }, null, 2));
    const propertyReference = definitions.map(definition => `| ${definition.group} | ${definition.label} | ${definition.id} | ${JSON.stringify(definition.defaultValue)} |`).join('\n');
    await writeFile(join(output, 'settings-reference.md'), `# iCUE display settings\n\n| Group | Setting | ID | Default |\n|---|---|---|---|\n${propertyReference}\n`);
    console.log(`\nPackaged ${packagePath}\nVersion ${version}\nSHA256 ${sha256}`);
} catch (error) {
    console.error(error instanceof Error ? error.message : error);
    process.exitCode = 1;
} finally {
    if (temporary && resolve(temporary).startsWith(resolve(tmpdir()) + sep) && basename(temporary).startsWith('rtxlens-')) {
        await rm(temporary, { recursive: true, force: true });
    }
    if (lock) { await lock.close(); await unlink(lockPath); }
}
