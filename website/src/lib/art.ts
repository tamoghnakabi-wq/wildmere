// Procedural placeholder art for the website, generated at build time.
//
// Everything here is drawn from code with a seeded RNG: no photos, no AI images and
// nothing taken from any other game. It stands in until real in-game screenshots are
// added to src/data/media.json, and the page labels it as illustration.

export type Rand = () => number;

export function rng(seed: number): Rand {
  let s = seed >>> 0;
  return () => {
    s = (s + 0x6d2b79f5) >>> 0;
    let t = s;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

function noise1(rand: Rand) {
  const pts = Array.from({ length: 256 }, () => rand());
  return (x: number) => {
    const i = Math.floor(x);
    const f = x - i;
    const a = pts[((i % 256) + 256) % 256];
    const b = pts[(((i + 1) % 256) + 256) % 256];
    const t = f * f * (3 - 2 * f);
    return a + (b - a) * t;
  };
}

function fbm(n: (x: number) => number, x: number, oct = 4) {
  let s = 0;
  let a = 0.5;
  let f = 1;
  let norm = 0;
  for (let o = 0; o < oct; o++) {
    s += a * n(x * f);
    norm += a;
    a *= 0.5;
    f *= 2.03;
  }
  return s / norm;
}

const r1 = (n: number) => Math.round(n * 10) / 10;

// ---------------------------------------------------------------------------
// Colour helpers
type RGB = [number, number, number];
function hex(h: string): RGB {
  const v = parseInt(h.slice(1), 16);
  return [(v >> 16) & 255, (v >> 8) & 255, v & 255];
}
function toHex(c: RGB): string {
  return (
    '#' +
    c
      .map((v) => Math.max(0, Math.min(255, Math.round(v))).toString(16).padStart(2, '0'))
      .join('')
  );
}
function mix(a: RGB, b: RGB, t: number): RGB {
  return [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t];
}

// ---------------------------------------------------------------------------
// Layered landscape (hero + "Wander" spotlight)

export type Palette = {
  sky: string[];
  orb: { x: number; y: number; r: number; color: string; halo: string; haloOpacity: number };
  stars: number;
  aurora?: string;
  ridges: { top: string; bottom: string; snow?: string }[];
  water: [string, string];
  ripple: string;
  streak: string;
  ground: string;
  flowers: string[];
};

export const NIGHT: Palette = {
  sky: ['#020509', '#061018', '#0c1f26', '#183538'],
  orb: { x: 1215, y: 205, r: 38, color: '#f1e9d3', halo: '#bfe3dc', haloOpacity: 0.22 },
  stars: 170,
  aurora: '#3fbfa8',
  ridges: [
    { top: '#2b4b52', bottom: '#172b31', snow: '#9dbcbb' },
    { top: '#1a333b', bottom: '#0f2026' },
    { top: '#10222a', bottom: '#0a161b' },
    { top: '#0a171c', bottom: '#071013' },
  ],
  water: ['#10262c', '#02070a'],
  ripple: '#cfe9e6',
  streak: '#f1e9d3',
  ground: '#03080a',
  flowers: ['#b9524a', '#c9a843', '#4d7cc0', '#c9c8bd'],
};

export const DUSK: Palette = {
  sky: ['#1a1426', '#432a3d', '#9a5242', '#eda25e'],
  orb: { x: 760, y: 470, r: 50, color: '#ffe0aa', halo: '#ffb36b', haloOpacity: 0.45 },
  stars: 26,
  ridges: [
    { top: '#8a6278', bottom: '#5b4260', snow: '#f0c7aa' },
    { top: '#573a57', bottom: '#3a2742' },
    { top: '#33233c', bottom: '#22182b' },
    { top: '#1e1525', bottom: '#150f1b' },
  ],
  water: ['#6a3c3a', '#120c14'],
  ripple: '#ffd7a8',
  streak: '#ffd59a',
  ground: '#0e0a12',
  flowers: ['#d0604f', '#e0b84a', '#6d8fd0', '#e6e0d2'],
};

const VW = 1600;
const VH = 900;
const WATER = 700;

type RidgeSpec = {
  bw: number;
  base: number;
  amp: number;
  freq: number;
  seed: number;
  peaks?: { x: number; w: number; h: number }[];
  trees?: number;
  snowLine?: number;
  banks?: boolean;
};

function ridge(spec: RidgeSpec, floorY: number) {
  const n = noise1(rng(spec.seed));
  const rand = rng(spec.seed * 7 + 3);
  const cols = Math.ceil(VW / spec.bw) + 1;
  let d = `M0 ${floorY}`;
  const snow: string[] = [];
  const trees: string[] = [];
  for (let i = 0; i < cols; i++) {
    const x = i * spec.bw;
    let y = spec.base - spec.amp * (fbm(n, x * spec.freq + 11) - 0.5) * 2;
    for (const p of spec.peaks ?? []) y -= p.h * Math.exp(-(((x - p.x) / p.w) ** 2));
    if (spec.banks) {
      // shores rise at the edges and dip under the waterline in the middle
      const e = Math.min(x, VW - x);
      const rise = Math.max(0, 150 - e * 0.42);
      y = rise > spec.bw ? floorY - rise + (fbm(n, x * 0.02) - 0.5) * 40 : floorY;
    }
    y = Math.min(floorY, Math.round(y / spec.bw) * spec.bw);
    d += ` V${y} H${Math.min(VW, x + spec.bw)}`;
    if (spec.snowLine && y < spec.snowLine) {
      const h = spec.bw * (1 + (rand() < 0.5 ? 1 : 0));
      snow.push(`M${x} ${y}h${spec.bw}v${h}h${-spec.bw}z`);
    }
    if (spec.trees && y < floorY - spec.bw && rand() < spec.trees) {
      const s = spec.bw * (0.42 + rand() * 0.12);
      const cx = x + spec.bw / 2;
      const tiers = [5, 5, 3, 3, 1].slice(rand() < 0.4 ? 1 : 0);
      let ty = y - s; // trunk
      trees.push(`M${r1(cx - s / 2)} ${r1(ty)}h${r1(s)}v${r1(s)}h${r1(-s)}z`);
      for (const t of tiers) {
        ty -= s;
        const w = t * s;
        trees.push(`M${r1(cx - w / 2)} ${r1(ty)}h${r1(w)}v${r1(s + 0.5)}h${r1(-w)}z`);
      }
    }
  }
  d += ` V${floorY} Z`;
  return { d: d + trees.join(''), snow: snow.join('') };
}

const RIDGES: RidgeSpec[] = [
  { bw: 8, base: 545, amp: 70, freq: 0.004, seed: 11, peaks: [{ x: 1080, w: 170, h: 170 }, { x: 330, w: 220, h: 70 }], snowLine: 470 },
  { bw: 12, base: 600, amp: 60, freq: 0.006, seed: 23, peaks: [{ x: 560, w: 200, h: 60 }], trees: 0.16 },
  { bw: 16, base: 650, amp: 44, freq: 0.008, seed: 37, trees: 0.3 },
  { bw: 20, base: 700, amp: 0, freq: 0.01, seed: 41, trees: 0.42, banks: true },
];

function stars(p: Palette, rand: Rand) {
  let out = '';
  for (let i = 0; i < p.stars; i++) {
    const x = rand() * VW;
    const y = Math.pow(rand(), 1.6) * 520;
    const s = r1(1 + rand() * 1.8);
    const o = r1(0.25 + rand() * 0.7);
    const tw = rand() < 0.22 ? ` class="tw tw${1 + Math.floor(rand() * 3)}"` : '';
    out += `<rect x="${r1(x)}" y="${r1(y)}" width="${s}" height="${s}" opacity="${o}"${tw}/>`;
  }
  return `<g fill="#f4f1e6">${out}</g>`;
}

/** Sky layer: gradient, aurora, moon/sun and stars. */
export function skyLayer(p: Palette, id: string, seed = 5): string {
  const rand = rng(seed);
  const stops = p.sky
    .map((c, i) => `<stop offset="${(i / (p.sky.length - 1)).toFixed(2)}" stop-color="${c}"/>`)
    .join('');
  const o = p.orb;
  return (
    `<defs>` +
    `<linearGradient id="${id}-sky" x1="0" y1="0" x2="0" y2="${WATER}" gradientUnits="userSpaceOnUse">${stops}</linearGradient>` +
    `<radialGradient id="${id}-halo" cx="${o.x}" cy="${o.y}" r="${o.r * 8}" gradientUnits="userSpaceOnUse">` +
    `<stop offset="0" stop-color="${o.halo}" stop-opacity="${o.haloOpacity}"/><stop offset="0.25" stop-color="${o.halo}" stop-opacity="${r1(o.haloOpacity * 0.35)}"/><stop offset="1" stop-color="${o.halo}" stop-opacity="0"/>` +
    `</radialGradient>` +
    (p.aurora
      ? `<radialGradient id="${id}-aur" cx="0.5" cy="0.5" r="0.5"><stop offset="0" stop-color="${p.aurora}" stop-opacity="0.16"/><stop offset="1" stop-color="${p.aurora}" stop-opacity="0"/></radialGradient>`
      : '') +
    `</defs>` +
    `<rect width="${VW}" height="${VH}" fill="url(#${id}-sky)"/>` +
    (p.aurora
      ? `<ellipse cx="520" cy="250" rx="620" ry="120" fill="url(#${id}-aur)" transform="rotate(-8 520 250)"/>` +
        `<ellipse cx="980" cy="170" rx="420" ry="70" fill="url(#${id}-aur)" transform="rotate(6 980 170)"/>`
      : '') +
    stars(p, rand) +
    `<circle cx="${o.x}" cy="${o.y}" r="${o.r * 8}" fill="url(#${id}-halo)"/>` +
    `<circle cx="${o.x}" cy="${o.y}" r="${o.r}" fill="${o.color}"/>`
  );
}

/** Land layer: stepped voxel ridges, the lake and its reflection. */
export function landLayer(p: Palette, id: string, seed = 9): string {
  const rand = rng(seed);
  let defs = '';
  let body = '';
  const refs: string[] = [];
  RIDGES.forEach((spec, i) => {
    const c = p.ridges[i];
    const r = ridge(spec, WATER);
    defs += `<linearGradient id="${id}-r${i}" x1="0" y1="${spec.base - spec.amp - 200}" x2="0" y2="${WATER}" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="${c.top}"/><stop offset="1" stop-color="${c.bottom}"/></linearGradient>`;
    body += `<path id="${id}-p${i}" d="${r.d}" fill="url(#${id}-r${i})"/>`;
    refs.push(`<use href="#${id}-p${i}"/>`);
    if (r.snow && c.snow) {
      body += `<path id="${id}-s${i}" d="${r.snow}" fill="${c.snow}" opacity="0.5"/>`;
      refs.push(`<use href="#${id}-s${i}"/>`);
    }
  });
  defs +=
    `<linearGradient id="${id}-water" x1="0" y1="${WATER}" x2="0" y2="${VH}" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="${p.water[0]}"/><stop offset="1" stop-color="${p.water[1]}"/></linearGradient>` +
    `<linearGradient id="${id}-fadeg" x1="0" y1="${WATER}" x2="0" y2="${VH}" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="#fff" stop-opacity="0.7"/><stop offset="0.7" stop-color="#fff" stop-opacity="0.1"/><stop offset="1" stop-color="#fff" stop-opacity="0"/></linearGradient>` +
    `<mask id="${id}-fade" maskUnits="userSpaceOnUse" x="0" y="${WATER}" width="${VW}" height="${VH - WATER}"><rect x="0" y="${WATER}" width="${VW}" height="${VH - WATER}" fill="url(#${id}-fadeg)"/></mask>`;

  // ripples and the orb's broken reflection
  let ripples = '';
  for (let i = 0; i < 90; i++) {
    const y = WATER + 4 + Math.pow(rand(), 1.3) * (VH - WATER - 4);
    const w = 20 + rand() * 140 * (1 + (y - WATER) / 200);
    const x = rand() * VW;
    ripples += `<rect x="${r1(x)}" y="${r1(y)}" width="${r1(w)}" height="${r1(0.8 + (y - WATER) / 160)}" opacity="${r1(0.04 + rand() * 0.1)}"/>`;
  }
  let streak = '';
  for (let i = 0; i < 26; i++) {
    const y = WATER + 6 + i * 7 + rand() * 3;
    const w = (34 + rand() * 60) * (1 - i / 40);
    streak += `<rect x="${r1(p.orb.x - w / 2 + (rand() - 0.5) * 16)}" y="${r1(y)}" width="${r1(w)}" height="2" opacity="${r1(0.55 * (1 - i / 28))}"/>`;
  }
  return (
    `<defs>${defs}</defs>` +
    body +
    `<rect x="0" y="${WATER}" width="${VW}" height="${VH - WATER}" fill="url(#${id}-water)"/>` +
    `<g mask="url(#${id}-fade)"><g transform="matrix(1 0 0 -1 0 ${WATER * 2})">${refs.join('')}</g></g>` +
    `<g fill="${p.ripple}">${ripples}</g>` +
    `<g fill="${p.streak}">${streak}</g>` +
    `<rect x="0" y="${WATER}" width="${VW}" height="1.5" fill="${p.ripple}" opacity="0.18"/>`
  );
}

/** Foreground: blocky shore at the corners with grass, flowers, glowcaps and fireflies. */
export function foreLayer(p: Palette, id: string, seed = 3, opts: { glowcaps?: boolean; fireflies?: boolean } = {}): string {
  const rand = rng(seed);
  const bw = 26;
  let ground = `M0 ${VH}`;
  const tops: { x: number; y: number }[] = [];
  for (let x = 0; x <= VW; x += bw) {
    const e = Math.min(x, VW - x);
    let y = e < 520 ? 790 + Math.max(0, e - 120) * 0.28 : VH + 10;
    y += (rand() - 0.5) * 30;
    y = Math.round(y / bw) * bw;
    ground += ` V${y} H${x + bw}`;
    if (y < VH) tops.push({ x, y });
  }
  ground += ` V${VH} Z`;
  let grass = '';
  let flowers = '';
  for (const t of tops) {
    const n = 1 + Math.floor(rand() * 3);
    for (let i = 0; i < n; i++) {
      const gx = t.x + rand() * bw;
      const h = 6 + rand() * 14;
      grass += `M${r1(gx)} ${r1(t.y - h)}h3v${r1(h)}h-3z`;
    }
    if (rand() < 0.3) {
      const fx = t.x + 4 + rand() * (bw - 10);
      const fh = 10 + rand() * 10;
      grass += `M${r1(fx + 2)} ${r1(t.y - fh)}h2v${r1(fh)}h-2z`;
      flowers += `<rect x="${r1(fx)}" y="${r1(t.y - fh - 5)}" width="6" height="6" fill="${p.flowers[Math.floor(rand() * p.flowers.length)]}" opacity="0.75"/>`;
    }
  }
  let caps = '';
  if (opts.glowcaps) {
    const spots = tops.filter((t) => t.x < 420 && t.x > 40);
    for (let i = 0; i < 4 && spots.length; i++) {
      const t = spots.splice(Math.floor(rand() * spots.length), 1)[0];
      const cx = t.x + bw / 2;
      const s = 0.8 + rand() * 0.5;
      caps +=
        `<g class="glowcap gc${1 + (i % 3)}">` +
        `<circle cx="${r1(cx)}" cy="${r1(t.y - 12 * s)}" r="${r1(46 * s)}" fill="url(#${id}-gc)"/>` +
        `<rect x="${r1(cx - 2.5 * s)}" y="${r1(t.y - 14 * s)}" width="${r1(5 * s)}" height="${r1(14 * s)}" fill="#bfeee6"/>` +
        `<rect x="${r1(cx - 9 * s)}" y="${r1(t.y - 22 * s)}" width="${r1(18 * s)}" height="${r1(8 * s)}" fill="#63e0d0"/>` +
        `</g>`;
    }
  }
  let flies = '';
  if (opts.fireflies) {
    for (let i = 0; i < 18; i++) {
      const x = 80 + rand() * (VW - 160);
      const y = 560 + rand() * 260;
      flies += `<circle class="ff ff${1 + (i % 4)}" cx="${r1(x)}" cy="${r1(y)}" r="${r1(1.4 + rand() * 1.4)}"/>`;
    }
    flies = `<g fill="#ffe9a8" filter="url(#${id}-ffglow)">${flies}</g>`;
  }
  return (
    `<defs>` +
    `<radialGradient id="${id}-gc" cx="0.5" cy="0.5" r="0.5"><stop offset="0" stop-color="#63e0d0" stop-opacity="0.55"/><stop offset="1" stop-color="#63e0d0" stop-opacity="0"/></radialGradient>` +
    `<filter id="${id}-ffglow" x="-2" y="-2" width="5" height="5"><feGaussianBlur stdDeviation="2.2" result="b"/><feMerge><feMergeNode in="b"/><feMergeNode in="SourceGraphic"/></feMerge></filter>` +
    `</defs>` +
    flies +
    `<path d="${ground}${grass}" fill="${p.ground}"/>` +
    flowers +
    caps
  );
}

export function svgWrap(inner: string, opts: { w?: number; h?: number; cls?: string; par?: string; label?: string } = {}): string {
  const w = opts.w ?? VW;
  const h = opts.h ?? VH;
  const aria = opts.label ? ` role="img" aria-label="${opts.label}"` : ' aria-hidden="true" focusable="false"';
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${w} ${h}" width="${w}" height="${h}" preserveAspectRatio="${opts.par ?? 'xMidYMax slice'}"${opts.cls ? ` class="${opts.cls}"` : ''}${aria}>${inner}</svg>`;
}

/** One-piece landscape (used for the OG image and the "Wander" spotlight). */
export function landscapeSvg(p: Palette, id: string, label?: string): string {
  return svgWrap(skyLayer(p, id + 's') + landLayer(p, id + 'l') + foreLayer(p, id + 'f', 3, { glowcaps: p === NIGHT }), {
    label,
  });
}

// ---------------------------------------------------------------------------
// Isometric voxel dioramas ("Build" and "Deep" spotlights)

type Mat =
  | 'grass'
  | 'dirt'
  | 'stone'
  | 'coal'
  | 'iron'
  | 'gold'
  | 'sand'
  | 'water'
  | 'log'
  | 'leaves'
  | 'planks'
  | 'roof'
  | 'glass'
  | 'cobble';

const MAT: Record<Mat, { top: string; side: string }> = {
  grass: { top: '#6c9f4c', side: '#5f6f3a' },
  dirt: { top: '#7d5b3f', side: '#73533a' },
  stone: { top: '#7b8287', side: '#6d747a' },
  cobble: { top: '#6f7378', side: '#62666b' },
  coal: { top: '#7b8287', side: '#6d747a' },
  iron: { top: '#7b8287', side: '#6d747a' },
  gold: { top: '#7b8287', side: '#6d747a' },
  sand: { top: '#d6c28d', side: '#c4ae78' },
  water: { top: '#3b8fb0', side: '#2f7898' },
  log: { top: '#a37b52', side: '#5f442f' },
  leaves: { top: '#3f7d40', side: '#356b37' },
  planks: { top: '#b48654', side: '#a2764a' },
  roof: { top: '#7a4f33', side: '#6a432b' },
  glass: { top: '#a9d9e6', side: '#ffcf7a' },
};

const ORE: Partial<Record<Mat, string>> = { coal: '#24262a', iron: '#d6a77c', gold: '#f2c54b' };

type Light = { x: number; y: number; z: number; color: RGB; power: number; radius: number };

type IsoOpts = {
  id: string;
  w: number;
  h: number;
  tw: number;
  th: number;
  bh: number;
  ox: number;
  oy: number;
  ambient: (y: number) => RGB;
  lights: Light[];
  seed: number;
};

class Voxels {
  m = new Map<string, Mat>();
  k(x: number, y: number, z: number) {
    return `${x},${y},${z}`;
  }
  set(x: number, y: number, z: number, v: Mat | null) {
    if (v) this.m.set(this.k(x, y, z), v);
    else this.m.delete(this.k(x, y, z));
  }
  get(x: number, y: number, z: number) {
    return this.m.get(this.k(x, y, z));
  }
  opaque(x: number, y: number, z: number) {
    const v = this.get(x, y, z);
    return !!v && v !== 'water' && v !== 'glass';
  }
}

function renderIso(vox: Voxels, o: IsoOpts, extra: { decals?: string; before?: string; after?: string }) {
  const rand = rng(o.seed);
  const P = (X: number, Y: number, Z: number) =>
    `${r1(o.ox + ((X - Z) * o.tw) / 2)} ${r1(o.oy + ((X + Z) * o.th) / 2 - Y * o.bh)}`;
  const blocks = [...vox.m.entries()].map(([k, v]) => {
    const [x, y, z] = k.split(',').map(Number);
    return { x, y, z, v };
  });
  blocks.sort((a, b) => a.x + a.z - (b.x + b.z) || a.y - b.y || a.x - b.x);
  const light = (x: number, y: number, z: number): RGB => {
    const a = o.ambient(y);
    let c: RGB = [a[0], a[1], a[2]];
    for (const L of o.lights) {
      const d2 = (x - L.x) ** 2 + (y - L.y) ** 2 + (z - L.z) ** 2;
      const f = L.power / (1 + d2 / (L.radius * L.radius));
      c = [c[0] + L.color[0] * f, c[1] + L.color[1] * f, c[2] + L.color[2] * f];
    }
    return c;
  };
  const shade = (base: string, f: number, l: RGB, j: number): string => {
    const b = hex(base);
    return toHex([b[0] * f * l[0] * j, b[1] * f * l[1] * j, b[2] * f * l[2] * j]);
  };
  let out = '';
  const speck = (corners: [number, number, number][], color: string) => {
    // a few small ore flecks placed by bilinear interpolation across the face
    let s = '';
    for (let i = 0; i < 3; i++) {
      const u = 0.2 + rand() * 0.55;
      const v = 0.2 + rand() * 0.55;
      const du = 0.16;
      const dv = 0.14;
      const at = (uu: number, vv: number) => {
        const [a, b, c, d] = corners;
        const lerp = (p: number[], q: number[], t: number) => p.map((pv, k) => pv + (q[k] - pv) * t);
        const top = lerp(a, b, uu);
        const bot = lerp(d, c, uu);
        const pt = lerp(top, bot, vv);
        return P(pt[0], pt[1], pt[2]);
      };
      s += `M${at(u, v)}L${at(u + du, v)}L${at(u + du, v + dv)}L${at(u, v + dv)}Z`;
    }
    return `<path d="${s}" fill="${color}"/>`;
  };
  for (const b of blocks) {
    const { x, y, z, v } = b;
    const mat = MAT[v];
    const j = 0.94 + rand() * 0.12;
    const alpha = v === 'water' ? ' fill-opacity="0.82"' : '';
    if (!vox.opaque(x, y + 1, z) && vox.get(x, y + 1, z) !== v) {
      const l = light(x + 0.5, y + 1, z + 0.5);
      const top: [number, number, number][] = [
        [x, y + 1, z],
        [x + 1, y + 1, z],
        [x + 1, y + 1, z + 1],
        [x, y + 1, z + 1],
      ];
      out += `<path d="M${P(x, y + 1, z)}L${P(x + 1, y + 1, z)}L${P(x + 1, y + 1, z + 1)}L${P(x, y + 1, z + 1)}Z" fill="${shade(mat.top, 1, l, j)}"${alpha}/>`;
      if (ORE[v]) out += speck(top, shade(ORE[v]!, 1, l, 1));
    }
    if (!vox.opaque(x + 1, y, z) && vox.get(x + 1, y, z) !== v) {
      const l = light(x + 1, y + 0.5, z + 0.5);
      const glow = v === 'glass';
      const face: [number, number, number][] = [
        [x + 1, y + 1, z],
        [x + 1, y + 1, z + 1],
        [x + 1, y, z + 1],
        [x + 1, y, z],
      ];
      out += `<path d="M${P(x + 1, y, z)}L${P(x + 1, y, z + 1)}L${P(x + 1, y + 1, z + 1)}L${P(x + 1, y + 1, z)}Z" fill="${glow ? mat.side : shade(mat.side, 0.62, l, j)}"${alpha}/>`;
      if (ORE[v]) out += speck(face, shade(ORE[v]!, 0.7, l, 1));
    }
    if (!vox.opaque(x, y, z + 1) && vox.get(x, y, z + 1) !== v) {
      const l = light(x + 0.5, y + 0.5, z + 1);
      const glow = v === 'glass';
      const face: [number, number, number][] = [
        [x, y + 1, z + 1],
        [x + 1, y + 1, z + 1],
        [x + 1, y, z + 1],
        [x, y, z + 1],
      ];
      out += `<path d="M${P(x, y, z + 1)}L${P(x + 1, y, z + 1)}L${P(x + 1, y + 1, z + 1)}L${P(x, y + 1, z + 1)}Z" fill="${glow ? mat.side : shade(mat.side, 0.8, l, j)}"${alpha}/>`;
      if (ORE[v]) out += speck(face, shade(ORE[v]!, 0.85, l, 1));
    }
  }
  return svgWrap(
    (extra.before ?? '') +
      `<g stroke="#000" stroke-opacity="0.16" stroke-width="0.8" stroke-linejoin="round">${out}</g>` +
      (extra.decals ?? '') +
      (extra.after ?? ''),
    { w: o.w, h: o.h, par: 'xMidYMid slice' },
  );
}

function isoPoint(o: { ox: number; oy: number; tw: number; th: number; bh: number }, X: number, Y: number, Z: number) {
  return { x: o.ox + ((X - Z) * o.tw) / 2, y: o.oy + ((X + Z) * o.th) / 2 - Y * o.bh };
}

/** A floating chunk at blue hour: a small cabin, a tree and a pond. */
export function buildScene(): string {
  const N = 10;
  const vox = new Voxels();
  const n = noise1(rng(77));
  const heights: number[][] = [];
  for (let x = 0; x < N; x++) {
    heights[x] = [];
    for (let z = 0; z < N; z++) {
      let h = 4 + Math.round((fbm(n, x * 0.35 + z * 0.9) - 0.5) * 4);
      if (x >= 1 && x <= 5 && z >= 1 && z <= 5) h = 4; // cabin plot
      if (x >= 6 && z >= 6) h = 2; // pond basin
      heights[x][z] = Math.max(2, Math.min(6, h));
    }
  }
  for (let x = 0; x < N; x++)
    for (let z = 0; z < N; z++) {
      const h = heights[x][z];
      for (let y = 0; y <= h; y++) {
        const depth = h - y;
        vox.set(x, y, z, depth === 0 ? (x >= 5 && z >= 5 && h <= 3 ? 'sand' : 'grass') : depth < 3 ? 'dirt' : 'stone');
      }
      if (x >= 6 && z >= 6) vox.set(x, 3, z, 'water');
    }
  // cabin: walls y 5..7, corners are logs, planks between; door on +Z, window on +X
  for (let x = 1; x <= 4; x++)
    for (let z = 1; z <= 4; z++) {
      const edge = x === 1 || x === 4 || z === 1 || z === 4;
      if (!edge) continue;
      const corner = (x === 1 || x === 4) && (z === 1 || z === 4);
      for (let y = 5; y <= 7; y++) vox.set(x, y, z, corner ? 'log' : 'planks');
    }
  vox.set(2, 5, 4, null); // doorway
  vox.set(2, 6, 4, null);
  vox.set(4, 6, 2, 'glass'); // windows facing +X
  vox.set(4, 6, 3, 'glass');
  vox.set(3, 6, 4, 'glass'); // window facing +Z
  for (let x = 0; x <= 5; x++) for (let z = 0; z <= 5; z++) vox.set(x, 8, z, 'roof');
  for (let x = 1; x <= 4; x++) for (let z = 1; z <= 4; z++) vox.set(x, 9, z, 'roof');
  for (let x = 2; x <= 3; x++) for (let z = 2; z <= 3; z++) vox.set(x, 10, z, 'roof');
  // tree
  const tx = 7;
  const tz = 2;
  const th = heights[tx][tz];
  for (let y = th + 1; y <= th + 4; y++) vox.set(tx, y, tz, 'log');
  const lr = rng(5);
  for (let x = tx - 1; x <= tx + 1; x++)
    for (let z = tz - 1; z <= tz + 1; z++)
      for (let y = th + 4; y <= th + 5; y++) {
        const corner = Math.abs(x - tx) + Math.abs(z - tz) === 2;
        if (corner && lr() < 0.5) continue;
        if (x === tx && z === tz && y === th + 4) continue;
        vox.set(x, y, z, 'leaves');
      }
  vox.set(tx, th + 6, tz, 'leaves');
  // stepping stones to the pond
  vox.set(5, 5, 6, 'cobble');

  const o = { id: 'build', w: 1200, h: 900, tw: 72, th: 36, bh: 42, ox: 600, oy: 470, seed: 9 };
  const torch = { x: 1.5, y: 5, z: 5.5 };
  const lights: Light[] = [
    { x: 4.6, y: 6.5, z: 2.5, color: [1.0, 0.62, 0.28], power: 0.55, radius: 1.8 },
    { x: 3, y: 6.5, z: 4.6, color: [1.0, 0.62, 0.28], power: 0.45, radius: 1.6 },
    { x: torch.x, y: torch.y + 0.8, z: torch.z, color: [1.0, 0.58, 0.25], power: 0.7, radius: 1.4 },
  ];
  const tp = isoPoint(o, torch.x, torch.y, torch.z);
  const wp = isoPoint(o, 5, 6.5, 2.5);
  const decals =
    `<rect x="${r1(tp.x - 2)}" y="${r1(tp.y - 26)}" width="4" height="26" fill="#5f442f"/>` +
    `<rect x="${r1(tp.x - 4)}" y="${r1(tp.y - 34)}" width="8" height="9" fill="#ffd27a"/>`;
  const before =
    `<defs><linearGradient id="bs-sky" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#060c14"/><stop offset="0.6" stop-color="#12273a"/><stop offset="1" stop-color="#1d3b48"/></linearGradient>` +
    `<radialGradient id="bs-back" cx="0.5" cy="0.55" r="0.55"><stop offset="0" stop-color="#3a6f7a" stop-opacity="0.55"/><stop offset="1" stop-color="#3a6f7a" stop-opacity="0"/></radialGradient>` +
    `<radialGradient id="bs-warm" cx="0.5" cy="0.5" r="0.5"><stop offset="0" stop-color="#ffb35c" stop-opacity="0.5"/><stop offset="1" stop-color="#ffb35c" stop-opacity="0"/></radialGradient></defs>` +
    `<rect width="1200" height="900" fill="url(#bs-sky)"/>` +
    `<ellipse cx="600" cy="460" rx="560" ry="380" fill="url(#bs-back)"/>` +
    starsField(rng(31), 1200, 520, 70);
  const after =
    `<circle cx="${r1(tp.x)}" cy="${r1(tp.y - 30)}" r="90" fill="url(#bs-warm)"/>` +
    `<circle cx="${r1(wp.x)}" cy="${r1(wp.y)}" r="120" fill="url(#bs-warm)" opacity="0.7"/>`;
  return renderIso(vox, {
    ...o,
    ambient: () => [0.72, 0.8, 0.95],
    lights,
  }, { decals, before, after });
}

function starsField(rand: Rand, w: number, h: number, count: number) {
  let s = '';
  for (let i = 0; i < count; i++) {
    const sz = r1(1 + rand() * 1.6);
    s += `<rect x="${r1(rand() * w)}" y="${r1(Math.pow(rand(), 1.4) * h)}" width="${sz}" height="${sz}" opacity="${r1(0.2 + rand() * 0.6)}"/>`;
  }
  return `<g fill="#f4f1e6">${s}</g>`;
}

/** A cut-away chunk: strata, ore seams and a glowcap grotto lit by one torch. */
export function deepScene(): string {
  const N = 10;
  const vox = new Voxels();
  const rand = rng(1234);
  const n = noise1(rng(99));
  for (let x = 0; x < N; x++)
    for (let z = 0; z < N; z++) {
      const h = 8 + Math.round((fbm(n, x * 0.4 + z * 0.7) - 0.5) * 2);
      for (let y = 0; y <= h; y++) {
        const depth = h - y;
        let m: Mat = depth === 0 ? 'grass' : depth < 3 ? 'dirt' : 'stone';
        if (m === 'stone') {
          const r = rand();
          if (r < 0.07) m = 'coal';
          else if (r < 0.1 && y < 5) m = 'iron';
          else if (r < 0.115 && y < 3) m = 'gold';
        }
        vox.set(x, y, z, m);
      }
    }
  // carve the grotto: open the front corner down to a stone floor at y = 0
  for (let x = 5; x < N; x++)
    for (let z = 5; z < N; z++) for (let y = 1; y <= 12; y++) vox.set(x, y, z, null);
  // a side tunnel heading back into the rock
  for (let y = 1; y <= 2; y++) {
    vox.set(4, y, 7, null);
    vox.set(3, y, 7, null);
    vox.set(7, y, 4, null);
    vox.set(7, y, 3, null);
  }
  const o = { id: 'deep', w: 1200, h: 900, tw: 72, th: 36, bh: 42, ox: 600, oy: 520, seed: 4 };
  const caps = [
    { x: 6.4, z: 7.2, s: 1.1 },
    { x: 7.6, z: 6.3, s: 0.8 },
    { x: 8.3, z: 8.1, s: 1.25 },
    { x: 6.1, z: 8.9, s: 0.7 },
    { x: 8.9, z: 5.8, s: 0.9 },
  ];
  const torch = { x: 5, y: 3.2, z: 6.4 };
  const lights: Light[] = [
    { x: torch.x + 0.3, y: torch.y + 0.4, z: torch.z, color: [1.0, 0.6, 0.26], power: 1.1, radius: 1.5 },
    ...caps.map((c) => ({ x: c.x, y: 1.3, z: c.z, color: [0.2, 0.85, 0.75] as RGB, power: 0.5 * c.s, radius: 1.2 })),
  ];
  let glowcaps = '';
  for (const c of caps) {
    const p = isoPoint(o, c.x, 1, c.z);
    const s = c.s;
    glowcaps +=
      `<ellipse cx="${r1(p.x)}" cy="${r1(p.y)}" rx="${r1(60 * s)}" ry="${r1(34 * s)}" fill="url(#ds-teal)"/>` +
      `<rect x="${r1(p.x - 3 * s)}" y="${r1(p.y - 18 * s)}" width="${r1(6 * s)}" height="${r1(18 * s)}" fill="#c6f3ec"/>` +
      `<rect x="${r1(p.x - 12 * s)}" y="${r1(p.y - 27 * s)}" width="${r1(24 * s)}" height="${r1(10 * s)}" fill="#6ff0de"/>`;
  }
  const tp = isoPoint(o, torch.x, torch.y, torch.z);
  const torchDecal =
    `<circle cx="${r1(tp.x + 4)}" cy="${r1(tp.y - 26)}" r="150" fill="url(#ds-warm)"/>` +
    `<path d="M${r1(tp.x - 2)} ${r1(tp.y)} L${r1(tp.x + 8)} ${r1(tp.y - 22)} L${r1(tp.x + 12)} ${r1(tp.y - 20)} L${r1(tp.x + 2)} ${r1(tp.y + 2)}Z" fill="#5f442f"/>` +
    `<rect x="${r1(tp.x + 4)}" y="${r1(tp.y - 32)}" width="9" height="10" fill="#ffd27a"/>`;
  const before =
    `<defs><linearGradient id="ds-bg" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#0a1114"/><stop offset="1" stop-color="#030607"/></linearGradient>` +
    `<radialGradient id="ds-teal" cx="0.5" cy="0.5" r="0.5"><stop offset="0" stop-color="#63e0d0" stop-opacity="0.5"/><stop offset="1" stop-color="#63e0d0" stop-opacity="0"/></radialGradient>` +
    `<radialGradient id="ds-warm" cx="0.5" cy="0.5" r="0.5"><stop offset="0" stop-color="#ffb35c" stop-opacity="0.45"/><stop offset="1" stop-color="#ffb35c" stop-opacity="0"/></radialGradient>` +
    `<radialGradient id="ds-back" cx="0.5" cy="0.6" r="0.55"><stop offset="0" stop-color="#1f4a4a" stop-opacity="0.6"/><stop offset="1" stop-color="#1f4a4a" stop-opacity="0"/></radialGradient></defs>` +
    `<rect width="1200" height="900" fill="url(#ds-bg)"/>` +
    `<ellipse cx="600" cy="520" rx="580" ry="400" fill="url(#ds-back)"/>`;
  // motes drifting up from the grotto
  let motes = '';
  const mr = rng(8);
  for (let i = 0; i < 26; i++) {
    const p = isoPoint(o, 5.5 + mr() * 4, 1.2 + mr() * 5, 5.5 + mr() * 4);
    motes += `<rect x="${r1(p.x)}" y="${r1(p.y)}" width="2.4" height="2.4" opacity="${r1(0.3 + mr() * 0.6)}"/>`;
  }
  return renderIso(vox, {
    ...o,
    ambient: (y) => {
      const t = Math.max(0, Math.min(1, (y - 1) / 8));
      const k = 0.18 + 0.72 * t * t;
      return [k * 0.85, k * 0.95, k * 1.08];
    },
    lights,
  }, { before, decals: glowcaps + torchDecal, after: `<g fill="#9ff5e8">${motes}</g>` });
}
