// Wildmere brand marks, drawn as code so they stay crisp and need no font files.
//
// Wordmark idea: "WILD" above the water, "MERE" its reflection. The M is the exact
// vertical mirror of the W, so the name reads like a ridge and its image on a lake.
// Letters are monoline strokes clipped to the cap band, which flattens terminals
// and apexes into clean horizontal cuts.

export const BRAND = {
  bone: '#ece6d6',
  amber: '#f0b45c',
  glow: '#63e0d0',
  ink: '#06090b',
};

const CAP = 100; // cap height in wordmark units
const SW = 8.5; // stroke width
const TRACK = 34; // gap between glyph boxes

type Glyph = { w: number; d: string };

// W points: tops overshoot the band (clipped flat), bottoms overshoot below it.
const W: Glyph = { w: 116, d: 'M0 -14 L29 114 L58 26 L87 114 L116 -14' };
// M is W mirrored about the middle of the cap band.
const M: Glyph = { w: 116, d: 'M0 114 L29 -14 L58 74 L87 -14 L116 114' };
const I: Glyph = { w: 0, d: 'M0 -10 V110' };
const L: Glyph = { w: 54, d: `M0 -10 V${CAP - SW / 2} H54` };
const D: Glyph = {
  w: 80,
  d: `M0 -10 V110 M0 ${SW / 2} H30 C62 ${SW / 2} 80 24 80 50 C80 76 62 ${CAP - SW / 2} 30 ${CAP - SW / 2} H0`,
};
const E: Glyph = {
  w: 54,
  d: `M0 -10 V110 M0 ${SW / 2} H54 M0 ${CAP - SW / 2} H54 M0 50 H42`,
};
const R: Glyph = {
  w: 70,
  d: `M0 -10 V110 M0 ${SW / 2} H36 C60 ${SW / 2} 70 17 70 31 C70 45 60 57 36 57 H0 M34 57 L70 114`,
};

const WORD: Glyph[] = [W, I, L, D, M, E, R, E];

function layout() {
  let x = SW / 2;
  const parts: string[] = [];
  for (const g of WORD) {
    parts.push(`<path transform="translate(${x.toFixed(2)} 0)" d="${g.d}"/>`);
    x += g.w + TRACK;
  }
  const width = x - TRACK + SW / 2;
  return { parts: parts.join(''), width };
}

const LAYOUT = layout();
export const WORDMARK_WIDTH = Math.ceil(LAYOUT.width);
export const WORDMARK_HEIGHT = CAP;

/** Inner SVG markup of the wordmark (no <svg> wrapper). `id` must be unique per page. */
export function wordmarkInner(id: string, color = 'currentColor'): string {
  return (
    `<defs><clipPath id="${id}-band"><rect x="-20" y="0" width="${WORDMARK_WIDTH + 40}" height="${CAP}"/></clipPath></defs>` +
    `<g clip-path="url(#${id}-band)" fill="none" stroke="${color}" stroke-width="${SW}" stroke-linejoin="miter" stroke-miterlimit="10" stroke-linecap="butt">` +
    LAYOUT.parts +
    `</g>`
  );
}

/** Standalone wordmark SVG. */
export function wordmarkSvg(opts: { id?: string; color?: string; title?: string } = {}): string {
  const id = opts.id ?? 'wm';
  const title = opts.title ?? 'Wildmere';
  return (
    `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${WORDMARK_WIDTH} ${CAP}" role="img" aria-label="${title}">` +
    wordmarkInner(id, opts.color ?? BRAND.bone) +
    `</svg>`
  );
}

// Emblem: a pointed-top hexagon (the outline of a block seen corner-on) holding a
// double peak above the waterline and its reflection below.
export function emblemInner(id: string, opts: { weight?: number; bg?: string | null } = {}): string {
  const w = opts.weight ?? 3.2;
  const hex = 'M32 3 L57.1 17.5 L57.1 46.5 L32 61 L6.9 46.5 L6.9 17.5 Z';
  const bg = opts.bg === undefined ? BRAND.ink : opts.bg;
  return (
    `<defs>` +
    `<clipPath id="${id}-hex"><path d="${hex}"/></clipPath>` +
    `<linearGradient id="${id}-refl" x1="0" y1="34" x2="0" y2="56" gradientUnits="userSpaceOnUse">` +
    `<stop offset="0" stop-color="${BRAND.glow}" stop-opacity="0.95"/><stop offset="1" stop-color="${BRAND.glow}" stop-opacity="0.15"/>` +
    `</linearGradient>` +
    `</defs>` +
    (bg ? `<path d="${hex}" fill="${bg}"/>` : '') +
    `<g clip-path="url(#${id}-hex)" fill="none" stroke-linejoin="miter" stroke-miterlimit="10">` +
    // peaks (M) above the line
    `<path d="M9 32 L21 16 L32 27 L43 16 L55 32" stroke="${BRAND.amber}" stroke-width="${w}"/>` +
    // reflection (W) below the line
    `<path d="M9 36 L21 52 L32 41 L43 52 L55 36" stroke="url(#${id}-refl)" stroke-width="${w}"/>` +
    `</g>` +
    `<path d="M5 34 H59" stroke="${BRAND.bone}" stroke-width="${Math.max(1.4, w * 0.5)}" stroke-linecap="round"/>` +
    `<path d="${hex}" fill="none" stroke="${BRAND.bone}" stroke-width="${w * 0.75}" stroke-linejoin="round"/>`
  );
}

export function emblemSvg(opts: { id?: string; weight?: number; bg?: string | null; size?: number } = {}): string {
  const id = opts.id ?? 'em';
  const size = opts.size ? ` width="${opts.size}" height="${opts.size}"` : '';
  return (
    `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64"${size} role="img" aria-label="Wildmere">` +
    emblemInner(id, opts) +
    `</svg>`
  );
}
