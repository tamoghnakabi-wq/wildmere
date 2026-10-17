// @ts-check
import { defineConfig } from 'astro/config';

// Absolute site URL (used for canonical and Open Graph tags). Order: explicit SITE_URL,
// then Vercel's production domain (set automatically on Vercel builds), then localhost.
const site =
  process.env.SITE_URL ||
  (process.env.VERCEL_PROJECT_PRODUCTION_URL ? `https://${process.env.VERCEL_PROJECT_PRODUCTION_URL}` : 'http://localhost:4321');

export default defineConfig({
  site,
  output: 'static',
  trailingSlash: 'ignore',
  // HTML-style whitespace (a line break between text and an inline tag stays a space).
  // The Astro 7 default ("jsx") drops it, which glues words to <em>/<code> in the copy.
  compressHTML: true,
  build: { inlineStylesheets: 'auto' },
  devToolbar: { enabled: false },
});
