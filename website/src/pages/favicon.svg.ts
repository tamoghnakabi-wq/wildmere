import type { APIRoute } from 'astro';
import { emblemSvg } from '../lib/brand';

export const GET: APIRoute = () =>
  new Response(emblemSvg({ id: 'fav', weight: 4.2 }), { headers: { 'Content-Type': 'image/svg+xml' } });
