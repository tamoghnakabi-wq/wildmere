// Raster brand images (Open Graph card, touch icon), rendered at build time from the
// same SVG code as the page. No PNGs are committed (the repo routes *.png to Git LFS).
import sharp from 'sharp';
import { NIGHT, skyLayer, landLayer, foreLayer } from './art';
import { emblemInner, wordmarkInner, WORDMARK_WIDTH, WORDMARK_HEIGHT, BRAND } from './brand';

export async function ogPng(): Promise<Buffer> {
  const W = 1200;
  const H = 630;
  const scene =
    `<svg x="0" y="0" width="${W}" height="${H}" viewBox="0 0 1600 900" preserveAspectRatio="xMidYMax slice">` +
    skyLayer(NIGHT, 'os') +
    landLayer(NIGHT, 'ol') +
    foreLayer(NIGHT, 'of', 3, { glowcaps: true }) +
    `</svg>`;
  const wmW = 760;
  const wmH = (wmW / WORDMARK_WIDTH) * WORDMARK_HEIGHT;
  const svg =
    `<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${H}" viewBox="0 0 ${W} ${H}">` +
    scene +
    `<defs><linearGradient id="ogv" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#020509" stop-opacity="0.55"/><stop offset="0.6" stop-color="#020509" stop-opacity="0.15"/><stop offset="1" stop-color="#020509" stop-opacity="0.6"/></linearGradient></defs>` +
    `<rect width="${W}" height="${H}" fill="url(#ogv)"/>` +
    `<svg x="${(W - 112) / 2}" y="92" width="112" height="112" viewBox="0 0 64 64">${emblemInner('oge', { weight: 3, bg: null })}</svg>` +
    `<svg x="${(W - wmW) / 2}" y="250" width="${wmW}" height="${wmH}" viewBox="0 0 ${WORDMARK_WIDTH} ${WORDMARK_HEIGHT}">${wordmarkInner('ogw', BRAND.bone)}</svg>` +
    `</svg>`;
  return sharp(Buffer.from(svg)).png({ compressionLevel: 9 }).toBuffer();
}

export async function iconPng(size: number): Promise<Buffer> {
  const svg =
    `<svg xmlns="http://www.w3.org/2000/svg" width="${size}" height="${size}" viewBox="0 0 64 64">` +
    `<rect width="64" height="64" fill="${BRAND.ink}"/>` +
    `<svg x="6" y="6" width="52" height="52" viewBox="0 0 64 64">${emblemInner('ic', { weight: 3.4, bg: null })}</svg>` +
    `</svg>`;
  return sharp(Buffer.from(svg)).png().toBuffer();
}
