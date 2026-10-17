#!/usr/bin/env node
// Turns real in-game captures into web-ready media and registers them in
// src/data/media.json.
//
//   npm run import-shots -- shots/*.png               # add gallery screenshots
//   npm run import-shots -- --hero shots/vista.png    # set the hero still
//   npm run import-shots -- --hero-video clip.mp4 --hero shots/vista.png
//
// Screenshots become public/media/shots/<name>.webp (max 2560 px wide) plus a 960 px
// <name>-sm.webp for phones. WebP/WebM are used because the repo's .gitattributes
// routes PNG/JPG/MP4 to Git LFS, which Vercel does not fetch by default.
import { readFileSync, writeFileSync, mkdirSync, readdirSync, statSync, existsSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import sharp from 'sharp';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const mediaFile = path.join(root, 'src', 'data', 'media.json');
const outShots = path.join(root, 'public', 'media', 'shots');
const IMG = /\.(png|jpe?g|webp|tiff?)$/i;

const args = process.argv.slice(2);
if (!args.length || args.includes('-h') || args.includes('--help')) {
  console.log(readFileSync(fileURLToPath(import.meta.url), 'utf8').split('\n').slice(1, 13).join('\n'));
  process.exit(args.length ? 0 : 1);
}

let heroImage = null;
let heroVideo = null;
const inputs = [];
for (let i = 0; i < args.length; i++) {
  if (args[i] === '--hero') heroImage = args[++i];
  else if (args[i] === '--hero-video') heroVideo = args[++i];
  else inputs.push(args[i]);
}

const files = [];
for (const p of inputs) {
  const abs = path.resolve(process.cwd(), p);
  if (!existsSync(abs)) {
    console.error(`import-shots: no such file or folder: ${p}`);
    process.exit(1);
  }
  if (statSync(abs).isDirectory()) {
    for (const f of readdirSync(abs).sort()) if (IMG.test(f)) files.push(path.join(abs, f));
  } else if (IMG.test(abs)) files.push(abs);
  else console.warn(`import-shots: skipping ${p} (not an image)`);
}

const slug = (f) =>
  path
    .basename(f, path.extname(f))
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-|-$/g, '') || 'shot';

const media = JSON.parse(readFileSync(mediaFile, 'utf8'));
media.gallery ??= [];
mkdirSync(outShots, { recursive: true });

for (const f of files) {
  const name = slug(f);
  const big = path.join(outShots, `${name}.webp`);
  const small = path.join(outShots, `${name}-sm.webp`);
  const img = sharp(f, { failOn: 'error' });
  const meta = await img.metadata();
  await sharp(f).resize({ width: 2560, withoutEnlargement: true }).webp({ quality: 82, effort: 5 }).toFile(big);
  await sharp(f).resize({ width: 960, withoutEnlargement: true }).webp({ quality: 78, effort: 5 }).toFile(small);
  const src = `/media/shots/${name}.webp`;
  const out = await sharp(big).metadata();
  const entry = { src, width: out.width, height: out.height, caption: '', alt: 'Wildmere in-game screenshot' };
  const at = media.gallery.findIndex((g) => g.src === src);
  if (at >= 0) media.gallery[at] = { ...entry, caption: media.gallery[at].caption, alt: media.gallery[at].alt };
  else media.gallery.push(entry);
  console.log(`import-shots: ${path.basename(f)} (${meta.width}x${meta.height}) -> public${src}`);
}

if (heroImage) {
  const abs = path.resolve(process.cwd(), heroImage);
  mkdirSync(path.join(root, 'public', 'media'), { recursive: true });
  await sharp(abs).resize({ width: 2560, withoutEnlargement: true }).webp({ quality: 80, effort: 5 }).toFile(path.join(root, 'public', 'media', 'hero.webp'));
  media.hero = { ...(media.hero ?? {}), image: '/media/hero.webp', alt: media.hero?.alt ?? 'Wildmere in-game view' };
  console.log(`import-shots: hero still -> public/media/hero.webp`);
}

if (heroVideo) {
  const abs = path.resolve(process.cwd(), heroVideo);
  const dest = path.join(root, 'public', 'media', 'hero.webm');
  mkdirSync(path.dirname(dest), { recursive: true });
  // short, silent, loopable VP9 clip sized for a full-bleed background
  const r = spawnSync(
    'ffmpeg',
    ['-y', '-i', abs, '-an', '-t', '20', '-vf', 'scale=1920:-2:flags=lanczos,fps=30', '-c:v', 'libvpx-vp9', '-b:v', '0', '-crf', '36', '-row-mt', '1', '-deadline', 'good', dest],
    { stdio: 'inherit' },
  );
  if (r.status !== 0) {
    console.error('import-shots: ffmpeg failed (is it installed?)');
    process.exit(1);
  }
  media.hero = { ...(media.hero ?? {}), video: ['/media/hero.webm'], alt: media.hero?.alt ?? 'Wildmere in-game view' };
  if (!media.hero.image) console.warn('import-shots: also pass --hero <still> so reduced-motion visitors get a poster frame');
  console.log('import-shots: hero loop -> public/media/hero.webm');
}

writeFileSync(mediaFile, JSON.stringify(media, null, 2) + '\n');
console.log(`import-shots: updated src/data/media.json (${media.gallery.length} gallery item(s)).`);
console.log('import-shots: now edit each "caption" and give each "alt" a specific description, then run npm run build.');
