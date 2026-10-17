#!/usr/bin/env node
// Validates the website's data files before every build (npm "prebuild") and from
// tools/release/publish.sh. A bad download link or missing screenshot fails the
// build instead of reaching visitors. No dependencies: plain Node.
//
//   node scripts/check-data.mjs            # checks src/data/*.json
//   WILDMERE_DEMO=1 node scripts/check-data.mjs   # checks src/data/demo/*.json
import { readFileSync, existsSync, openSync, readSync, closeSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const demo = process.env.WILDMERE_DEMO === '1';
const dataDir = path.join(root, 'src', 'data', demo ? 'demo' : '');
const errors = [];
const warnings = [];
const err = (m) => errors.push(m);
const warn = (m) => warnings.push(m);

function load(file, fallbackDir) {
  let p = path.join(dataDir, file);
  if (!existsSync(p) && fallbackDir) p = path.join(fallbackDir, file);
  try {
    return JSON.parse(readFileSync(p, 'utf8'));
  } catch (e) {
    err(`${path.relative(root, p)}: ${e.message}`);
    return null;
  }
}

if (demo && process.env.VERCEL) {
  err('WILDMERE_DEMO=1 is set in a Vercel build. Demo data must never be deployed; remove the variable.');
}

const SEMVER = /^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$/;
const DATE = /^\d{4}-\d{2}-\d{2}$/;
const SHA = /^[0-9a-f]{64}$/;
const REPO = /^https:\/\/github\.com\/[\w.-]+\/[\w.-]+$/;
const FILES = { macos: (v) => `Wildmere-${v}-macOS.zip`, windows: (v) => `Wildmere-${v}-Windows-x64.zip` };

// ---- release.json ---------------------------------------------------------
const rel = load('release.json');
let assetCount = 0;
if (rel) {
  const known = new Set(['version', 'date', 'notes', 'repo', 'checksums', 'assets']);
  for (const k of Object.keys(rel)) if (!known.has(k)) warn(`release.json: unknown key "${k}" is ignored`);
  if (typeof rel.version !== 'string' || !SEMVER.test(rel.version)) err('release.json: "version" must be semver like 0.1.0');
  if (rel.date !== null && rel.date !== undefined) {
    if (typeof rel.date !== 'string' || !DATE.test(rel.date) || Number.isNaN(Date.parse(rel.date))) err('release.json: "date" must be YYYY-MM-DD or null');
  }
  if (rel.notes !== undefined && typeof rel.notes !== 'string') err('release.json: "notes" must be a markdown string');
  if (typeof rel.repo !== 'string' || !REPO.test(rel.repo)) err('release.json: "repo" must be https://github.com/<owner>/<repo>');
  const dl = `${rel.repo}/releases/download/v${rel.version}/`;
  if (rel.checksums != null && (typeof rel.checksums !== 'string' || !rel.checksums.startsWith(dl))) {
    err(`release.json: "checksums" must be null or a URL under ${dl}`);
  }
  if (!Array.isArray(rel.assets)) err('release.json: "assets" must be an array (use [] until builds exist)');
  else {
    assetCount = rel.assets.length;
    if (rel.assets.length && !rel.date) err('release.json: "date" is required once assets are listed');
    const seen = new Set();
    rel.assets.forEach((a, i) => {
      const where = `release.json assets[${i}]`;
      if (!a || typeof a !== 'object') return err(`${where}: must be an object`);
      if (!(a.platform in FILES)) return err(`${where}: "platform" must be "macos" or "windows"`);
      if (seen.has(a.platform)) err(`${where}: duplicate platform "${a.platform}"`);
      seen.add(a.platform);
      if (typeof a.label !== 'string' || !a.label.trim()) err(`${where}: "label" is required`);
      const expected = FILES[a.platform](rel.version);
      if (a.file !== expected) err(`${where}: "file" must be ${expected}`);
      if (a.url !== dl + a.file) err(`${where}: "url" must be exactly ${dl}${a.file} (a real GitHub release asset)`);
      if (!Number.isInteger(a.size) || a.size <= 0) err(`${where}: "size" must be the byte count`);
      else if (a.size < 1_000_000) err(`${where}: size ${a.size} bytes is far too small for a game build`);
      if (typeof a.sha256 !== 'string' || !SHA.test(a.sha256)) err(`${where}: "sha256" must be 64 lowercase hex digits`);
      else if (!demo && /^(.)\1{63}$/.test(a.sha256)) err(`${where}: "sha256" looks like a placeholder`);
      const v = a.verified;
      if (v !== undefined && typeof v !== 'boolean') {
        const okObj =
          a.platform === 'macos' &&
          v && typeof v === 'object' &&
          Object.keys(v).every((k) => (k === 'arm64' || k === 'x64') && typeof v[k] === 'boolean');
        if (!okObj) err(`${where}: "verified" must be true/false (macOS may also use {"arm64": bool, "x64": bool})`);
      }
    });
  }
}

// ---- media.json -------------------------------------------------------------
const media = load('media.json');
const pub = path.join(root, 'public');
const IMG = /\.(webp|avif|jpe?g|png)$/i;
const VID = /\.(webm|mp4)$/i;

function checkFile(src, where, re) {
  if (typeof src !== 'string' || !src.startsWith('/')) return err(`${where}: path must start with /`);
  if (demo && src.startsWith('/art/')) return; // generated at build time
  if (!src.startsWith('/media/')) err(`${where}: put media under website/public/media/ and reference it as /media/...`);
  if (!re.test(src)) err(`${where}: unsupported file type ${path.extname(src) || '(none)'}`);
  const file = path.join(pub, src);
  if (!existsSync(file)) return err(`${where}: ${src} does not exist in website/public`);
  const fd = openSync(file, 'r');
  const head = Buffer.alloc(40);
  readSync(fd, head, 0, 40, 0);
  closeSync(fd);
  if (head.toString('utf8').startsWith('version https://git-lfs')) {
    err(`${where}: ${src} is a Git LFS pointer, not the real file. Use .webp/.webm (not routed to LFS), or enable Git LFS in the Vercel project.`);
  }
  if (/\.(png|jpe?g|mp4)$/i.test(src)) warn(`${where}: ${src} is routed to Git LFS by .gitattributes; prefer .webp / .webm (npm run import-shots converts).`);
}

let galleryCount = 0;
if (media) {
  if (media.hero != null) {
    const h = media.hero;
    if (typeof h !== 'object') err('media.json: "hero" must be null or an object');
    else {
      if (!h.image && !(Array.isArray(h.video) && h.video.length)) err('media.json hero: needs "image" (poster) and/or "video": [..]');
      if (h.image) checkFile(h.image, 'media.json hero.image', IMG);
      if (h.video !== undefined) {
        if (!Array.isArray(h.video)) err('media.json hero.video: must be an array of /media/... files');
        else h.video.forEach((v, i) => checkFile(v, `media.json hero.video[${i}]`, VID));
        if (!h.image) warn('media.json hero: add an "image" poster so reduced-motion visitors see a still frame');
      }
      if (typeof h.alt !== 'string' || !h.alt.trim()) err('media.json hero: "alt" text is required');
    }
  }
  if (!Array.isArray(media.gallery)) err('media.json: "gallery" must be an array ([] hides the gallery)');
  else {
    galleryCount = media.gallery.length;
    media.gallery.forEach((g, i) => {
      const where = `media.json gallery[${i}]`;
      if (!g || typeof g !== 'object') return err(`${where}: must be an object`);
      checkFile(g.src, where, IMG);
      if (typeof g.alt !== 'string' || !g.alt.trim()) err(`${where}: "alt" text is required`);
      if (g.caption !== undefined && typeof g.caption !== 'string') err(`${where}: "caption" must be a string`);
      for (const k of ['width', 'height']) if (g[k] !== undefined && !(Number.isInteger(g[k]) && g[k] > 0)) err(`${where}: "${k}" must be a positive integer`);
    });
  }
}

// ---- site.json ---------------------------------------------------------------
const site = load('site.json', path.join(root, 'src', 'data'));
if (site) {
  for (const k of ['name', 'tagline', 'description', 'pitch', 'about', 'spotlights', 'features', 'requirements', 'credits']) {
    if (site[k] === undefined) err(`site.json: missing "${k}"`);
  }
}

for (const w of warnings) console.warn(`check-data: warning: ${w}`);
if (errors.length) {
  for (const e of errors) console.error(`check-data: error: ${e}`);
  console.error(`check-data: ${errors.length} error(s) in ${path.relative(process.cwd(), dataDir) || '.'}`);
  process.exit(1);
}
console.log(
  `check-data: OK${demo ? ' (DEMO data)' : ''}: release v${rel.version}, ${assetCount} asset(s), ${galleryCount} gallery image(s), hero ${media.hero ? 'media' : 'illustration'}`,
);
