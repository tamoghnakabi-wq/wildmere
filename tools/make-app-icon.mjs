// Renders the Wildmere emblem (same geometry as website/src/lib/brand.ts) into a 1024 px app icon.
// usage: node tools/make-app-icon.mjs   (run from repo root; uses website/node_modules/sharp)
import { createRequire } from 'module';
const require = createRequire(import.meta.url);
const sharp = require('../website/node_modules/sharp');
const ink = '#06090b', bone = '#ece6d6', amber = '#f0b45c', glow = '#63e0d0';
const hex = 'M32 3 L57.1 17.5 L57.1 46.5 L32 61 L6.9 46.5 L6.9 17.5 Z';
const w = 3.2;
const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1024 1024">
<defs>
 <linearGradient id="bg" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#16232a"/><stop offset="0.55" stop-color="#0b1216"/><stop offset="1" stop-color="#05080a"/></linearGradient>
 <radialGradient id="sun" cx="0.5" cy="0.36" r="0.55"><stop offset="0" stop-color="${amber}" stop-opacity="0.28"/><stop offset="1" stop-color="${amber}" stop-opacity="0"/></radialGradient>
 <clipPath id="hexc"><path d="${hex}"/></clipPath>
 <linearGradient id="refl" x1="0" y1="34" x2="0" y2="56" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="${glow}" stop-opacity="0.95"/><stop offset="1" stop-color="${glow}" stop-opacity="0.15"/></linearGradient>
</defs>
<rect x="100" y="100" width="824" height="824" rx="185" fill="url(#bg)"/>
<rect x="100" y="100" width="824" height="824" rx="185" fill="url(#sun)"/>
<rect x="101.5" y="101.5" width="821" height="821" rx="184" fill="none" stroke="#ffffff" stroke-opacity="0.08" stroke-width="3"/>
<g transform="translate(192 192) scale(10)">
 <path d="${hex}" fill="${ink}"/>
 <g clip-path="url(#hexc)" fill="none" stroke-linejoin="miter" stroke-miterlimit="10">
  <path d="M9 32 L21 16 L32 27 L43 16 L55 32" stroke="${amber}" stroke-width="${w}"/>
  <path d="M9 36 L21 52 L32 41 L43 52 L55 36" stroke="url(#refl)" stroke-width="${w}"/>
 </g>
 <path d="M5 34 H59" stroke="${bone}" stroke-width="1.6" stroke-linecap="round"/>
 <path d="${hex}" fill="none" stroke="${bone}" stroke-width="${w * 0.75}" stroke-linejoin="round"/>
</g></svg>`;
await sharp(Buffer.from(svg)).png().toFile('Assets/Art/Icons/AppIcon.png');
console.log('wrote Assets/Art/Icons/AppIcon.png');
