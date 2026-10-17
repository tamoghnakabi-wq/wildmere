// Placeholder illustrations, generated at build time (see src/lib/art.ts).
import type { APIRoute, GetStaticPaths } from 'astro';
import { NIGHT, DUSK, landscapeSvg, buildScene, deepScene } from '../../lib/art';

const ART: Record<string, () => string> = {
  wander: () => landscapeSvg(DUSK, 'wd', 'Illustration: voxel mountains at dusk above a lake'),
  night: () => landscapeSvg(NIGHT, 'nt', 'Illustration: a moonlit lake below voxel mountains'),
  build: buildScene,
  deep: deepScene,
};

export const getStaticPaths: GetStaticPaths = () => Object.keys(ART).map((name) => ({ params: { name } }));

export const GET: APIRoute = ({ params }) =>
  new Response(ART[params.name as string](), { headers: { 'Content-Type': 'image/svg+xml' } });
