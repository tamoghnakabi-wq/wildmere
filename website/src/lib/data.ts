// Build-time access to the data files. Components never read JSON directly, so the
// lead can edit src/data/*.json without touching markup.
import { existsSync } from 'node:fs';
import path from 'node:path';
import sharp from 'sharp';
import { marked } from 'marked';

import releaseProd from '../data/release.json';
import mediaProd from '../data/media.json';
import releaseDemo from '../data/demo/release.json';
import mediaDemo from '../data/demo/media.json';
import siteData from '../data/site.json';

export const DEMO = process.env.WILDMERE_DEMO === '1';

export type Verified = boolean | { arm64?: boolean; x64?: boolean } | undefined;
export type Asset = {
  platform: 'macos' | 'windows';
  label: string;
  file: string;
  url: string;
  size: number;
  sha256: string;
  verified?: Verified;
};
export type Release = {
  version: string;
  date: string | null;
  notes: string;
  repo: string;
  checksums: string | null;
  assets: Asset[];
};
export type Shot = { src: string; alt: string; caption?: string; width: number; height: number; srcset?: string };
export type Hero = { image?: string; video?: string[]; alt: string; caption?: string } | null;

export const release = (DEMO ? releaseDemo : releaseProd) as unknown as Release;
const media = (DEMO ? mediaDemo : mediaProd) as unknown as {
  hero: Hero;
  gallery: { src: string; alt: string; caption?: string; width?: number; height?: number }[];
};
export const site = siteData;
export const hero: Hero = media.hero;

export const asset = (p: Asset['platform']) => release.assets.find((a) => a.platform === p) ?? null;
export const hasBuilds = release.assets.length > 0;
export const releasesUrl = `${release.repo}/releases`;

export function formatMB(bytes: number) {
  return `${(bytes / 1_000_000).toFixed(1)} MB`;
}

export function formatDate(iso: string | null) {
  if (!iso) return null;
  const d = new Date(iso + 'T00:00:00Z');
  return d.toLocaleDateString('en-GB', { day: 'numeric', month: 'long', year: 'numeric', timeZone: 'UTC' });
}

/** Verification state per platform. Missing flags mean "not yet verified". */
export function verification() {
  const mac = asset('macos');
  const win = asset('windows');
  const mv = mac?.verified;
  const macArm = mv === true || (typeof mv === 'object' && mv?.arm64 === true);
  const macIntel = mv === true || (typeof mv === 'object' && mv?.x64 === true);
  return { macArm, macIntel, windows: win?.verified === true };
}

export const notesHtml = release.notes ? (marked.parse(release.notes, { async: false }) as string) : '';

const publicDir = path.join(process.cwd(), 'public');

/** Gallery entries with real pixel sizes (read from the files) and an optional -sm srcset. */
export async function gallery(): Promise<Shot[]> {
  const out: Shot[] = [];
  for (const g of media.gallery) {
    let { width, height } = g;
    const file = path.join(publicDir, g.src);
    if ((!width || !height) && existsSync(file)) {
      const meta = await sharp(file).metadata();
      width = meta.width;
      height = meta.height;
    }
    let srcset: string | undefined;
    const small = g.src.replace(/(\.[a-z0-9]+)$/i, '-sm$1');
    if (small !== g.src && existsSync(path.join(publicDir, small)) && width) {
      srcset = `${small} 960w, ${g.src} ${width}w`;
    }
    out.push({ src: g.src, alt: g.alt, caption: g.caption, width: width ?? 1600, height: height ?? 900, srcset });
  }
  return out;
}
