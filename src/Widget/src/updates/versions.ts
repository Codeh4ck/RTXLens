import { releaseLinks } from './releaseLinks.ts';
import type { ReleaseManifest } from './ReleaseManifest.ts';

export const updateManifestUrl = releaseLinks.ManifestUrl;
export const repositoryUrl = releaseLinks.RepositoryUrl;
export const releaseTagUrl = (tag: string) => repositoryUrl + releaseLinks.TagPath + encodeURIComponent(tag);

export function parseVersion(value: unknown): number[] | null {
    if (typeof value !== 'string' || !/^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/.test(value)) {
        return null;
    }

    const parts = value.split('.').map(Number);

    return parts.every(part => Number.isSafeInteger(part) && part <= 2147483647) ? parts : null;
}

export function isNewerVersion(latest: string, installed: string): boolean {
    const remote = parseVersion(latest);
    const current = parseVersion(installed);

    if (!remote || !current) {
        return false;
    }

    for (let index = 0; index < 3; index++) {
        if (remote[index] !== current[index]) {
            return remote[index]! > current[index]!;
        }
    }

    return false;
}

export function parseReleaseManifest(value: unknown): ReleaseManifest {
    if (!value || typeof value !== 'object') {
        throw new Error('Invalid update manifest.');
    }

    const manifest = value as Record<string, unknown>;

    if (manifest.schemaVersion !== 1 || !parseVersion(manifest.desktop) || !parseVersion(manifest.service)
        || !parseVersion(manifest.widget) || typeof manifest.releaseTag !== 'string'
        || !manifest.releaseTag.startsWith('v') || !parseVersion(manifest.releaseTag.slice(1))) {
        throw new Error('Unsupported update manifest.');
    }

    const tag = manifest.releaseTag;
    const releaseUrl = manifest.releaseUrl;
    if (releaseUrl !== undefined
        && releaseUrl !== releaseTagUrl(tag)
        && releaseUrl !== releaseLinks.LegacyRepositoryUrl + releaseLinks.TagPath + tag) {
        throw new Error('Unsupported release URL.');
    }

    return {
        releaseUrl: releaseUrl as string | undefined,
        schemaVersion: 1,
        releaseTag: manifest.releaseTag,
        desktop: manifest.desktop as string,
        service: manifest.service as string,
        widget: manifest.widget as string
    };
}

export function nextUpdateCheck(last: number, frequency: string): number {
    if (frequency !== 'Monthly') {
        return last + (frequency === 'Weekly' ? 7 : 1) * 86400000;
    }

    const date = new Date(last);
    const day = date.getUTCDate();
    date.setUTCDate(1);
    date.setUTCMonth(date.getUTCMonth() + 1);
    const lastDay = new Date(Date.UTC(date.getUTCFullYear(), date.getUTCMonth() + 1, 0)).getUTCDate();
    date.setUTCDate(Math.min(day, lastDay));

    return date.getTime();
}
