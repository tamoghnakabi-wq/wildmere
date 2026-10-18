import type { APIRoute } from 'astro';
import { ogPng } from '../lib/og';

export const GET: APIRoute = async () =>
  new Response(new Uint8Array(await ogPng()), { headers: { 'Content-Type': 'image/png' } });
