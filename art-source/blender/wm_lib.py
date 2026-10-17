"""Wildmere Blender asset library (shared by every build_*.py script).

Everything here is deterministic: the same script + seed produces the same mesh,
colours and FBX contents (FBX headers still carry a timestamp).

Coordinate conventions (see BLENDER_ASSETS.md for the verified mapping):
  * Blender scene is Z-up, metres.  Models are authored FACING -Y (Blender "front").
  * Animal's LEFT side is +X in Blender.  Tools: long axis +Z, working edge toward -Y.
  * FBX export (axis_forward='-Z', axis_up='Y', bake_space_transform=True) maps
    Blender (x, y, z) -> Unity (-x, z, -y).  So Blender -Y -> Unity +Z (forward),
    Blender +Z -> Unity +Y (up), Blender +X (animal left) -> Unity -X (animal left).
"""
import bpy
import bmesh
import math
import os
import sys
import json
import random
import time
import numpy as np
from mathutils import Vector, Matrix, noise
from mathutils.bvhtree import BVHTree

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
MODELS_DIR = os.path.join(REPO, "Assets", "Resources", "Models")
ICON_DIR = os.path.join(MODELS_DIR, "Icons")
PREVIEW_DIR = os.path.join(HERE, "previews")
MANIFEST = os.path.join(MODELS_DIR, "models.json")

BUDGET = {"Props": 1500, "Wildlife": 2500, "Bird": 600, "Tools": 800}


# --------------------------------------------------------------------------- args
def parse_args():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    a = {"only": None, "icons": True, "preview": True, "export": True}
    i = 0
    while i < len(argv):
        k = argv[i]
        if k == "--only":
            a["only"] = set(argv[i + 1].split(","))
            i += 1
        elif k == "--no-icons":
            a["icons"] = False
        elif k == "--no-preview":
            a["preview"] = False
        elif k == "--no-export":
            a["export"] = False
        i += 1
    return a


# --------------------------------------------------------------------------- colour
def hexc(h):
    """sRGB hex -> linear RGB numpy array."""
    h = h.lstrip("#")
    c = np.array([int(h[i:i + 2], 16) / 255.0 for i in (0, 2, 4)])
    return srgb_to_lin(c)


def srgb_to_lin(c):
    c = np.asarray(c, dtype=float)
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def lin_to_srgb(c):
    c = np.clip(np.asarray(c, dtype=float), 0, 1)
    return np.where(c <= 0.0031308, c * 12.92, 1.055 * c ** (1 / 2.4) - 0.055)


def mix(a, b, t):
    t = np.asarray(t, dtype=float)
    if t.ndim == 1:
        t = t[:, None]
    return a * (1 - t) + b * t


def sstep(e0, e1, x):
    t = np.clip((np.asarray(x, dtype=float) - e0) / (e1 - e0), 0, 1)
    return t * t * (3 - 2 * t)


def fbm(P, scale=1.0, octaves=3, seed=0, basis='PERLIN_NEW'):
    """Fractal noise in roughly [-1, 1] for each row of P (N,3)."""
    off = Vector(((seed * 17.31) % 97.0, (seed * 7.77) % 89.0, (seed * 3.19) % 83.0))
    out = np.empty(len(P))
    for i, p in enumerate(P):
        v = Vector(p) * scale + off
        out[i] = noise.fractal(v, 0.6, 2.0, octaves, noise_basis=basis)
    return out


def noise1(p, scale=1.0, seed=0):
    off = Vector(((seed * 17.31) % 97.0, (seed * 7.77) % 89.0, (seed * 3.19) % 83.0))
    return noise.noise(Vector(p) * scale + off, noise_basis='PERLIN_NEW')


# --------------------------------------------------------------------------- materials
# The runtime maps materials by NAME to its own shaders, so the set is deliberately small.
# Albedo always comes from the vertex colours (sRGB).  name: (roughness, metallic)
SURFACE = {
    "M_Fur": (0.85, 0),     # fur and feathers (soft, matte)
    "M_Horn": (0.4, 0),     # antler, hoof, beak, bird legs, eyes (tag #eye) - keratin, slight sheen
    "M_Wood": (0.75, 0),    # worked / cut wood: tool handles + heads, log end grain, stump top
    "M_Bark": (0.9, 0),     # tree bark (logs, stump sides, dead tree)
    "M_Stone": (0.85, 0),   # granite, pebbles, sandstone, knapped stone tool heads
    "M_Metal": (0.32, 0.9),  # iron tool heads
    "M_Cloth": (0.9, 0),    # cord lashings, leather grips, torch wrap (tags #cord #leather #cloth)
    "M_Leaves": (0.7, 0),   # foliage, reeds, lily pads, berries, petals (wind-sway candidates)
    "M_Cap": (0.55, 0),     # mushroom caps and stems (non-glowing)
    "M_Glow": (0.5, 0),     # self-lit: glowcap caps, torch ember top (#ember)
    "M_Flame": (1.0, 0),    # torch flame (unlit / additive)
    "M_Ice": (0.08, 0),     # ice spikes (glossy, optional translucency)
}
EMISSIVE = {"M_Flame": 6.0, "M_Glow": 3.0}
TAGS = [""]


def get_material(name):
    m = bpy.data.materials.get(name)
    if m:
        return m
    m = bpy.data.materials.new(name)
    if name not in SURFACE:
        raise ValueError("unknown surface material %s" % name)
    rough, metal = SURFACE[name]
    nt = m.node_tree
    nt.nodes.clear()
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    bsdf = nt.nodes.new("ShaderNodeBsdfPrincipled")
    col = nt.nodes.new("ShaderNodeVertexColor")
    col.layer_name = "Col"
    nt.links.new(col.outputs["Color"], bsdf.inputs["Base Color"])
    bsdf.inputs["Roughness"].default_value = rough
    bsdf.inputs["Metallic"].default_value = metal
    if name in EMISSIVE:
        nt.links.new(col.outputs["Color"], bsdf.inputs["Emission Color"])
        bsdf.inputs["Emission Strength"].default_value = EMISSIVE[name]
    if name == "M_Ice":
        bsdf.inputs["Coat Weight"].default_value = 0.6
    nt.links.new(bsdf.outputs["BSDF"], out.inputs["Surface"])
    return m


# --------------------------------------------------------------------------- mesh builder
class MB:
    """Accumulates verts/faces (asset space, Blender coords) with a material per face."""

    def __init__(self):
        self.v = []
        self.f = []
        self.m = []
        self.w = {}   # optional per-vertex weight -> mesh attribute wm_w -> PaintCtx.w

    def add(self, co):
        self.v.append(Vector(co))
        return len(self.v) - 1

    def face(self, idx, mat):
        self.f.append(list(idx))
        self.m.append(mat)

    def extend(self, other):
        o = len(self.v)
        for k, val in other.w.items():
            self.w[k + o] = val
        self.v += [v.copy() for v in other.v]
        self.f += [[i + o for i in f] for f in other.f]
        self.m += list(other.m)

    def transform(self, M):
        for i, v in enumerate(self.v):
            self.v[i] = M @ v
        return self

    def tris(self):
        return sum(len(f) - 2 for f in self.f)


def frames_along(path, up_hint):
    """Tangent / side / up frames along a polyline (parallel-transport style)."""
    n = len(path)
    T = []
    for i in range(n):
        a = path[max(i - 1, 0)]
        b = path[min(i + 1, n - 1)]
        t = (Vector(b) - Vector(a))
        T.append(t.normalized())
    up = Vector(up_hint).normalized()
    frames = []
    U0 = up.cross(T[0])
    if U0.length < 1e-6:
        U0 = Vector((1, 0, 0)).cross(T[0])
    U0.normalize()
    prevU = U0
    for i in range(n):
        U = up.cross(T[i])
        if U.length < 1e-4:
            U = prevU - T[i] * prevU.dot(T[i])
        U.normalize()
        V = T[i].cross(U).normalized()
        frames.append((T[i], U, V))
        prevU = U
    return frames


def ring(n, rx, rv_top, rv_bot=None, power=2.0, phase=0.0, mod=None):
    """Closed (u, v) profile. power>2 squarer, <2 diamond-ish. mod(theta)->radial mult."""
    if rv_bot is None:
        rv_bot = rv_top
    pts = []
    for k in range(n):
        th = phase + 2 * math.pi * k / n
        c, s = math.cos(th), math.sin(th)
        u = rx * math.copysign(abs(c) ** (2 / power), c)
        v = (rv_top if s >= 0 else rv_bot) * math.copysign(abs(s) ** (2 / power), s)
        if mod:
            r = mod(th)
            u *= r
            v *= r
        pts.append((u, v))
    return pts


def loft(mb, path, profiles, mats, up_hint=(0, 0, 1), cap0="flat", cap1="flat",
         tip0=None, tip1=None, offsets=None):
    """Skin rings along `path`. mats: str or per-segment list (len(path)-1).
    cap: 'flat' | 'point' (fan to tip) | None. offsets: per-ring (du, dv) shift."""
    path = [Vector(p) for p in path]
    fr = frames_along(path, up_hint)
    n = len(profiles[0])
    rings = []
    for i, (p, prof) in enumerate(zip(path, profiles)):
        T, U, V = fr[i]
        du, dv = offsets[i] if offsets else (0, 0)
        rings.append([mb.add(p + U * (u + du) + V * (v + dv)) for (u, v) in prof])
    segm = mats if isinstance(mats, list) else [mats] * (len(path) - 1)
    for i in range(len(rings) - 1):
        a, b = rings[i], rings[i + 1]
        for k in range(n):
            k1 = (k + 1) % n
            mb.face([a[k], a[k1], b[k1], b[k]], segm[i])
    m0, m1 = segm[0], segm[-1]
    if cap0 == "flat":
        mb.face(list(reversed(rings[0])), m0)
    elif cap0 == "point":
        t = mb.add(tip0 if tip0 is not None else path[0] - fr[0][0] * 0.01)
        for k in range(n):
            mb.face([rings[0][(k + 1) % n], rings[0][k], t], m0)
    if cap1 == "flat":
        mb.face(list(rings[-1]), m1)
    elif cap1 == "point":
        t = mb.add(tip1 if tip1 is not None else path[-1] + fr[-1][0] * 0.01)
        for k in range(n):
            mb.face([rings[-1][k], rings[-1][(k + 1) % n], t], m1)
    return rings


def lerp_path(pts, n):
    """Catmull-Rom resample of a control polyline into n points."""
    P = [Vector(p) for p in pts]
    if len(P) == 2:
        return [P[0].lerp(P[1], i / (n - 1)) for i in range(n)]
    seglen = [(P[i + 1] - P[i]).length for i in range(len(P) - 1)]
    total = sum(seglen)
    out = []
    for j in range(n):
        d = total * j / (n - 1)
        i = 0
        while i < len(seglen) - 1 and d > seglen[i]:
            d -= seglen[i]
            i += 1
        t = d / seglen[i] if seglen[i] > 0 else 0
        p0 = P[max(i - 1, 0)]
        p1, p2 = P[i], P[i + 1]
        p3 = P[min(i + 2, len(P) - 1)]
        t2, t3 = t * t, t * t * t
        out.append(0.5 * ((2 * p1) + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2 +
                          (-p0 + 3 * p1 - 3 * p2 + p3) * t3))
    return out


def interp(keys, t):
    """Piecewise-linear interpolation of [(t, value)] (value float or tuple)."""
    for i in range(len(keys) - 1):
        t0, v0 = keys[i]
        t1, v1 = keys[i + 1]
        if t <= t1 or i == len(keys) - 2:
            s = 0 if t1 == t0 else min(max((t - t0) / (t1 - t0), 0), 1)
            s = s * s * (3 - 2 * s) * 0.5 + s * 0.5
            if isinstance(v0, tuple):
                return tuple(a + (b - a) * s for a, b in zip(v0, v1))
            return v0 + (v1 - v0) * s
    return keys[-1][1]


def tube(mb, pts, radii, mat, sides=6, up_hint=(0, 0, 1), cap0="flat", cap1="point",
         flat=1.0, tip1=None, mats=None, phase=None):
    profs = [ring(sides, r, r * flat, phase=(math.pi / sides if phase is None else phase))
             for r in radii]
    return loft(mb, pts, profs, mats if mats else mat, up_hint=up_hint, cap0=cap0, cap1=cap1,
                tip1=tip1)


def ico(mb, center, radius, mat, subdiv=1, scale=(1, 1, 1), jitter=0.0, seed=0, rot=None):
    bm = bmesh.new()
    bmesh.ops.create_icosphere(bm, subdivisions=subdiv, radius=1.0)
    o = len(mb.v)
    R = rot if rot is not None else Matrix.Identity(3)
    for v in bm.verts:
        p = v.co.copy()
        if jitter:
            p *= 1 + jitter * noise1(p * 1.7, 1.0, seed)
        p = Vector((p.x * scale[0], p.y * scale[1], p.z * scale[2])) * radius
        mb.v.append(Vector(center) + R @ p)
    for f in bm.faces:
        mb.face([o + v.index for v in f.verts], mat)
    bm.free()


# --------------------------------------------------------------------------- scene / objects
def clear_scene():
    for o in list(bpy.data.objects):
        bpy.data.objects.remove(o, do_unlink=True)
    for m in list(bpy.data.meshes):
        bpy.data.meshes.remove(m)
    for c in list(bpy.data.cameras):
        bpy.data.cameras.remove(c)
    for l in list(bpy.data.lights):
        bpy.data.lights.remove(l)


def new_empty(name, loc=(0, 0, 0)):
    o = bpy.data.objects.new(name, None)
    o.empty_display_size = 0.1
    o.location = loc
    bpy.context.scene.collection.objects.link(o)
    return o


def mesh_from_mb(name, mb, pivot, smooth_angle=None, displace=None):
    """Create a triangulated mesh object at `pivot` (asset space) from MB data."""
    me = bpy.data.meshes.new(name)
    mats = []
    for m in mb.m:
        base = m.split("#")[0]
        if base not in mats:
            mats.append(base)
    bm = bmesh.new()
    pv = Vector(pivot)
    wl = bm.verts.layers.float.new("wm_w")       # layers first: adding one invalidates refs
    tag_layer = bm.faces.layers.int.new("wm_tag")
    bverts = [bm.verts.new(v - pv) for v in mb.v]
    for i, bv in enumerate(bverts):
        bv[wl] = mb.w.get(i, 0.0)
    for f, m in zip(mb.f, mb.m):
        base, _, tag = m.partition("#")
        if tag not in TAGS:
            TAGS.append(tag)
        try:
            face = bm.faces.new([bverts[i] for i in f])
            face.material_index = mats.index(base)
            face[tag_layer] = TAGS.index(tag)
        except ValueError:
            pass  # duplicate face
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-5)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    bmesh.ops.triangulate(bm, faces=bm.faces, quad_method='BEAUTY', ngon_method='BEAUTY')
    bm.to_mesh(me)
    bm.free()
    for m in mats:
        me.materials.append(get_material(m))
    o = bpy.data.objects.new(name, me)
    o.location = pv
    bpy.context.scene.collection.objects.link(o)
    set_shading(o, smooth_angle)
    return o


def set_shading(o, smooth_angle):
    me = o.data
    if smooth_angle is None:
        me.shade_flat()
    else:
        me.shade_smooth()
        me.set_sharp_from_angle(angle=math.radians(smooth_angle))


def parent(child, par):
    """Parent keeping world transform (no rotations anywhere, so locals are offsets)."""
    bpy.context.view_layer.update()
    wm = child.matrix_world.copy()
    child.parent = par
    child.matrix_parent_inverse = Matrix.Identity(4)
    child.location = wm.translation - par.matrix_world.translation
    bpy.context.view_layer.update()


def descendants(o):
    out = [o]
    for c in o.children:
        out += descendants(c)
    return out


def world_verts(o):
    me = o.data
    M = o.matrix_world
    co = np.empty(len(me.vertices) * 3)
    me.vertices.foreach_get("co", co)
    co = co.reshape(-1, 3)
    R = np.array(M.to_3x3())
    return co @ R.T + np.array(M.translation)


def tri_count(o):
    return sum(len(p.vertices) - 2 for p in o.data.polygons)


# --------------------------------------------------------------------------- vertex colour
class PaintCtx:
    """Per-CORNER arrays for paint functions (asset space = world space, Blender coords).

    P   corner position (its vertex), N smooth vertex normal, FN face normal,
    mat material name of the corner's face, vi vertex index, n corner count.
    """

    def __init__(self, o):
        me = o.data
        self.obj = o
        nl = len(me.loops)
        vi = np.empty(nl, dtype=np.int64)
        me.loops.foreach_get("vertex_index", vi)
        lp = np.empty(nl, dtype=np.int64)
        names = [m.name for m in me.materials]
        fm = []
        for p in me.polygons:
            lp[p.loop_start:p.loop_start + p.loop_total] = p.index
            fm.append(names[p.material_index] if names else "")
        ta = me.attributes.get("wm_tag")
        ft = np.zeros(len(me.polygons), dtype=np.int64)
        if ta is not None and ta.domain == 'FACE':
            ta.data.foreach_get("value", ft)
        self.tag = np.array(TAGS)[ft][lp]
        wa = me.attributes.get("wm_w")
        vw = np.zeros(len(me.vertices))
        if wa is not None and wa.domain == 'POINT':
            wa.data.foreach_get("value", vw)
        self.w = vw[vi]
        R = np.array(o.matrix_world.to_3x3())
        VP = world_verts(o)
        n = np.empty(len(me.vertices) * 3)
        me.vertices.foreach_get("normal", n)
        VN = n.reshape(-1, 3) @ R.T
        VN /= np.maximum(np.linalg.norm(VN, axis=1, keepdims=True), 1e-9)
        fn = np.empty(len(me.polygons) * 3)
        me.polygons.foreach_get("normal", fn)
        FN = fn.reshape(-1, 3) @ R.T
        FN /= np.maximum(np.linalg.norm(FN, axis=1, keepdims=True), 1e-9)
        self.VP, self.VN, self.vi, self.lp = VP, VN, vi, lp
        self.P = VP[vi]
        self.N = VN[vi]
        self.FN = FN[lp]
        self.mat = np.array(fm)[lp] if fm else np.array([""] * nl)
        self.pivot = np.array(o.matrix_world.translation)
        self.L = self.P - self.pivot
        self.n = nl

    def is_mat(self, name):
        return self.mat == name

    def vnoise(self, scale=(1, 1, 1), octaves=3, seed=0, offset=(0, 0, 0)):
        """Fractal noise evaluated per vertex (then expanded to corners)."""
        s = np.array(scale, dtype=float) * np.ones(3)
        vals = fbm((self.VP + np.array(offset)) * s, 1.0, octaves, seed)
        return vals[self.vi]

    def fnoise(self, scale=1.0, seed=0):
        """Noise evaluated per FACE centre (gives faceted patchiness)."""
        me = self.obj.data
        c = np.empty(len(me.polygons) * 3)
        me.polygons.foreach_get("center", c)
        c = c.reshape(-1, 3) @ np.array(self.obj.matrix_world.to_3x3()).T + self.pivot
        return fbm(c, scale, 2, seed)[self.lp]


def bake_ao(objs, occluders, rays=40, dist=0.5, ground=True, strength=1.0, seed=1):
    """Ray-traced ambient occlusion per VERTEX against all occluder meshes (+ ground plane)."""
    verts, polys = [], []
    for o in occluders:
        wv = world_verts(o)
        base = len(verts)
        verts += [Vector(v) for v in wv]
        polys += [[base + i for i in p.vertices] for p in o.data.polygons]
    if ground:
        g = 50.0
        base = len(verts)
        verts += [Vector((-g, -g, -0.002)), Vector((g, -g, -0.002)), Vector((g, g, -0.002)),
                  Vector((-g, g, -0.002))]
        polys.append([base, base + 1, base + 2, base + 3])
    bvh = BVHTree.FromPolygons(verts, polys, epsilon=0.0)
    rng = np.random.default_rng(seed)
    dirs = []
    for i in range(rays):  # cosine-weighted fibonacci hemisphere
        u = (i + 0.5) / rays
        r = math.sqrt(u)
        phi = i * 2.399963 + rng.uniform(0, 0.3)
        dirs.append((r * math.cos(phi), r * math.sin(phi), math.sqrt(max(0, 1 - u))))
    out = {}
    for o in objs:
        VP = world_verts(o)
        n = np.empty(len(o.data.vertices) * 3)
        o.data.vertices.foreach_get("normal", n)
        VN = n.reshape(-1, 3) @ np.array(o.matrix_world.to_3x3()).T
        occ = np.zeros(len(VP))
        for i in range(len(VP)):
            nrm = Vector(VN[i]).normalized()
            t = Vector((1, 0, 0)) if abs(nrm.x) < 0.9 else Vector((0, 1, 0))
            b1 = nrm.cross(t).normalized()
            b2 = nrm.cross(b1)
            p = Vector(VP[i]) + nrm * 0.003
            hit = 0.0
            for d in dirs:
                w = b1 * d[0] + b2 * d[1] + nrm * d[2]
                loc, _, _, dd = bvh.ray_cast(p, w, dist)
                if loc is not None:
                    hit += 1.0 - (dd / dist) ** 2 * 0.6
            occ[i] = hit / rays
        out[o.name] = np.clip(1.0 - occ * strength, 0.0, 1.0)
    return out


def cavity(o, strength=0.8, edge=0.25, blur=1):
    """Dirty-vertex-colours style concavity term. Returns multiplier per VERTEX."""
    me = o.data
    nv = len(me.vertices)
    co = np.empty(nv * 3)
    me.vertices.foreach_get("co", co)
    co = co.reshape(-1, 3)
    nr = np.empty(nv * 3)
    me.vertices.foreach_get("normal", nr)
    nr = nr.reshape(-1, 3)
    ev = np.empty(len(me.edges) * 2, dtype=np.int64)
    me.edges.foreach_get("vertices", ev)
    ev = ev.reshape(-1, 2)
    acc = np.zeros(nv)
    cnt = np.zeros(nv)
    for a, b in ((0, 1), (1, 0)):
        va, vb = ev[:, a], ev[:, b]
        d = co[vb] - co[va]
        d /= np.maximum(np.linalg.norm(d, axis=1, keepdims=True), 1e-9)
        dot = np.einsum("ij,ij->i", d, nr[va])
        np.add.at(acc, va, dot)
        np.add.at(cnt, va, 1)
    c = acc / np.maximum(cnt, 1)
    for _ in range(blur):
        acc2 = np.zeros(nv)
        np.add.at(acc2, ev[:, 0], c[ev[:, 1]])
        np.add.at(acc2, ev[:, 1], c[ev[:, 0]])
        c = 0.5 * c + 0.5 * acc2 / np.maximum(cnt, 1)
    conc = np.clip(c * 2.2, -1, 1)
    return 1.0 - strength * np.clip(conc, 0, 1) * 0.6 + edge * np.clip(-conc, 0, 1) * 0.5


def write_colors(o, ccol, face_jitter=0.04, seed=0):
    """Write per-corner linear colours with a small per-face brightness variation."""
    me = o.data
    attr = me.color_attributes.get("Col") or me.color_attributes.new("Col", 'BYTE_COLOR',
                                                                   'CORNER')
    me.color_attributes.active_color = attr
    me.color_attributes.render_color_index = me.color_attributes.find("Col")
    nl = len(me.loops)
    lp = np.empty(nl, dtype=np.int64)
    for p in me.polygons:
        lp[p.loop_start:p.loop_start + p.loop_total] = p.index
    rng = np.random.default_rng(seed)
    fj = 1.0 + rng.uniform(-face_jitter, face_jitter, len(me.polygons))
    rgba = np.ones((nl, 4))
    rgba[:, :3] = np.clip(ccol * fj[lp][:, None], 0, 1)
    attr.data.foreach_set("color", rgba.ravel())


def box_uv(o):
    """World-scale box projection (1 UV unit = 1 m) - lets shaders add detail maps."""
    me = o.data
    if not me.uv_layers:
        me.uv_layers.new(name="UVMap")
    uv = me.uv_layers[0]
    co = world_verts(o)
    data = np.zeros((len(me.loops), 2))
    for p in me.polygons:
        n = p.normal
        ax = max(range(3), key=lambda i: abs(n[i]))
        keep = [(1, 2), (0, 2), (0, 1)][ax]
        for li in range(p.loop_start, p.loop_start + p.loop_total):
            v = co[me.loops[li].vertex_index]
            data[li] = (v[keep[0]], v[keep[1]])
    uv.data.foreach_set("uv", data.ravel())


# --------------------------------------------------------------------------- asset assembly
class Part:
    def __init__(self, name, mb, pivot, parent=None, paint=None, smooth=40, lod=True,
                 lod_ratio=None):
        self.name, self.mb, self.pivot, self.parent = name, mb, Vector(pivot), parent
        self.paint, self.smooth, self.lod, self.lod_ratio = paint, smooth, lod, lod_ratio
        self.obj = None


def displace_mb(mb, amp, scale, seed, axis_weights=(1, 1, 1), keep=None):
    """Noise displacement of MB vertices. keep(v)->True leaves a vertex in place."""
    for i, v in enumerate(mb.v):
        if keep is not None and keep(v):
            continue
        d = Vector((noise1(v, scale, seed), noise1(v, scale, seed + 11),
                    noise1(v, scale, seed + 23)))
        mb.v[i] = v + Vector((d.x * axis_weights[0], d.y * axis_weights[1],
                              d.z * axis_weights[2])) * amp


def _colour_objects(pairs, occluders, ao, ground, cav, face_jitter, seed, rays):
    aomap = bake_ao([o for o, _ in pairs], occluders, rays=rays, dist=ao.get("dist", 0.5),
                    ground=ao.get("ground", ground), strength=ao.get("strength", 0.9), seed=seed)
    for i, (o, p) in enumerate(pairs):
        ctx = PaintCtx(o)
        base = p.paint(ctx) if p.paint else np.full((ctx.n, 3), 0.5)
        mult = (aomap[o.name] * cavity(o, cav[0], cav[1]))[ctx.vi]
        glow = getattr(p.paint, "no_ao", ())
        if glow:
            keep = np.isin(ctx.mat, list(glow))
            mult = np.where(keep, 1.0, mult)
        write_colors(o, base * mult[:, None], face_jitter, seed + i)
        box_uv(o)
        if os.environ.get("WM_DEBUG_TAGS"):
            print("WM_TAGS", o.name, sorted(set(ctx.tag.tolist())))


def assemble(name, parts, root_mode="empty", ground=True, ao=None, cav=(0.8, 0.25),
             face_jitter=0.04, seed=1, lod_ratio=0.4, lod=True):
    """Build objects from parts. root_mode 'empty': root empty named `name` at the origin with
    every part below it. root_mode 'mesh': the first part IS the root (renamed to `name`)."""
    clear_scene()
    if ground:
        zmin = min(min(v.z for v in p.mb.v) for p in parts if p.mb.v)
        for i, p in enumerate(parts):
            p.mb.transform(Matrix.Translation((0, 0, -zmin)))
            if not (root_mode == "mesh" and i == 0):   # a mesh root keeps its origin on the ground
                p.pivot = p.pivot - Vector((0, 0, zmin))
    byname = {}
    for p in parts:
        p.obj = mesh_from_mb(p.name, p.mb, p.pivot, smooth_angle=p.smooth)
        byname[p.name] = p
    if root_mode == "empty":
        root = new_empty(name)
    else:
        root = parts[0].obj
        root.name = name
        root.data.name = name
    for p in parts:
        if p.obj is root:
            continue
        par = byname[p.parent].obj if p.parent else root
        parent(p.obj, par)
    bpy.context.view_layer.update()
    lod0 = [p.obj for p in parts]
    ao = ao or {}
    _colour_objects([(p.obj, p) for p in parts], lod0, ao, ground, cav, face_jitter, seed,
                    ao.get("rays", 40))
    if not lod:
        return root
    lod1 = []
    for p in parts:
        if not p.lod:
            continue
        src = p.obj
        ratio = p.lod_ratio or lod_ratio
        if tri_count(src) < 40:
            ratio = max(ratio, 0.75)
        tmp = src.copy()
        tmp.data = src.data.copy()
        bpy.context.scene.collection.objects.link(tmp)
        tmp.parent = None
        tmp.matrix_world = src.matrix_world.copy()
        md = tmp.modifiers.new("dec", 'DECIMATE')
        md.decimate_type = 'COLLAPSE'
        md.ratio = ratio
        md.use_collapse_triangulate = True
        bpy.context.view_layer.update()
        dg = bpy.context.evaluated_depsgraph_get()
        me = bpy.data.meshes.new_from_object(tmp.evaluated_get(dg))
        bpy.data.objects.remove(tmp, do_unlink=True)
        lo = bpy.data.objects.new(src.name + "_LOD1", me)
        bpy.context.scene.collection.objects.link(lo)
        lo.matrix_world = src.matrix_world.copy()
        set_shading(lo, p.smooth)
        parent(lo, src)
        lod1.append((lo, p))
    bpy.context.view_layer.update()
    _colour_objects(lod1, lod0, ao, ground, (cav[0] * 0.7, cav[1] * 0.7), face_jitter,
                    seed + 100, 24)
    return root


# --------------------------------------------------------------------------- stats / export
def to_unity(v):
    """Blender (x, y, z) -> Unity (-x, z, -y) for the FBX settings used in export_fbx."""
    return (-v[0], v[2], -v[1])


def asset_stats(root):
    objs = [o for o in descendants(root) if o.type == 'MESH']
    l0 = [o for o in objs if not o.name.endswith("_LOD1")]
    l1 = [o for o in objs if o.name.endswith("_LOD1")]
    allv = np.concatenate([world_verts(o) for o in l0])
    u = np.stack([-allv[:, 0], allv[:, 2], -allv[:, 1]], axis=1)
    mn, mx = u.min(axis=0), u.max(axis=0)
    mats = []
    for o in l0 + l1:
        for m in o.data.materials:
            if m.name not in mats:
                mats.append(m.name)
    parts = {}
    for o in descendants(root):
        if o is root or o.name.endswith("_LOD1"):
            continue
        parts[o.name] = {
            "parent": o.parent.name if o.parent else None,
            "pivot": [round(x, 4) for x in to_unity(o.matrix_world.translation)],
            "tris": tri_count(o) if o.type == 'MESH' else 0,
        }
    return {
        "tris": {"lod0": sum(tri_count(o) for o in l0), "lod1": sum(tri_count(o) for o in l1)},
        "bounds": {"min": [round(float(x), 4) for x in mn], "max": [round(float(x), 4) for x in mx],
                   "size": [round(float(x), 4) for x in (mx - mn)]},
        "materials": sorted(mats),
        "parts": parts,
    }


_FBX_PATCHED = False


def _patch_fbx_exporter():
    """Fix io_scene_fbx's bake_space_transform for nested mesh/empty hierarchies (Blender 5.2.1).

    With bake_space_transform the geometry is written as G @ v (G = axis conversion), so every
    object's FBX local matrix must be G @ L @ G^-1 (L = its Blender local matrix); the product down
    any parent chain is then G @ World @ G^-1 and vertices land at G @ (world position).  Stock
    ObjectWrapper.fbx_object_matrix gets this right for depth 0 and 1 only: for deeper children it
    re-derives the parent's FBX local space via local_space=True, which returns G^-1 for the root
    instead of the identity it actually wrote, so every even-depth node arrives in Unity with a
    90 degree X rotation (seen in ModelImportCheck: Neck, legs, Tail, ears, *_LOD1).
    Only MESH/EMPTY objects whose parent is also MESH/EMPTY (or none) are affected by this patch;
    everything else (bones, cameras, lights) still uses the original code path."""
    global _FBX_PATCHED
    if _FBX_PATCHED:
        return
    from io_scene_fbx import fbx_utils
    orig = fbx_utils.ObjectWrapper.fbx_object_matrix
    ok = {'MESH', 'EMPTY'}

    def fixed(self, scene_data, rest=False, local_space=False, global_space=False):
        st = scene_data.settings
        par = self.parent
        if (st.bake_space_transform and not local_space and not global_space and self._tag == 'OB'
                and self.bdata.type in ok and not self.parented_to_armature
                and (par is None or (par._tag == 'OB' and par.bdata.type in ok and
                                     self.has_valid_parent(scene_data.objects)))):
            L = self.matrix_rest_local if rest else self.matrix_local
            return st.global_matrix @ L @ st.global_matrix_inv
        return orig(self, scene_data, rest=rest, local_space=local_space, global_space=global_space)

    fbx_utils.ObjectWrapper.fbx_object_matrix = fixed
    _FBX_PATCHED = True


def export_fbx(root, category):
    _patch_fbx_exporter()
    d = os.path.join(MODELS_DIR, category)
    os.makedirs(d, exist_ok=True)
    path = os.path.join(d, root.name + ".fbx")
    bpy.ops.object.select_all(action='DESELECT')
    for o in descendants(root):
        o.hide_set(False)
        o.select_set(True)
    bpy.context.view_layer.objects.active = root
    bpy.ops.export_scene.fbx(
        filepath=path, use_selection=True, apply_scale_options='FBX_SCALE_UNITS',
        axis_forward='-Z', axis_up='Y', bake_space_transform=True,
        object_types={'MESH', 'EMPTY'}, mesh_smooth_type='FACE', add_leaf_bones=False,
        use_mesh_modifiers=True, colors_type='SRGB', prioritize_active_color=True,
        use_custom_props=False, use_tspace=False, bake_anim=False, path_mode='STRIP',
        embed_textures=False, use_metadata=False)
    return path


def update_manifest(entries):
    data = {"version": 1, "units": "metres", "space": "Unity (+Y up, +Z forward)", "assets": {}}
    if os.path.exists(MANIFEST):
        with open(MANIFEST) as f:
            data = json.load(f)
    data.setdefault("assets", {})
    for k, v in entries.items():
        data["assets"][k] = v
    data["assets"] = dict(sorted(data["assets"].items()))
    with open(MANIFEST, "w") as f:
        json.dump(data, f, indent=2)
        f.write("\n")


# --------------------------------------------------------------------------- rendering
def _studio(scene, res):
    scene.render.engine = 'BLENDER_EEVEE'
    scene.render.resolution_x = res[0]
    scene.render.resolution_y = res[1]
    scene.render.resolution_percentage = 100
    scene.render.film_transparent = True
    scene.render.image_settings.file_format = 'PNG'
    scene.render.image_settings.color_mode = 'RGBA'
    scene.view_settings.view_transform = 'Standard'
    scene.view_settings.look = 'None'
    scene.view_settings.exposure = -0.5
    scene.eevee.taa_render_samples = 32
    w = bpy.data.worlds.get("WM_Studio") or bpy.data.worlds.new("WM_Studio")
    w.use_nodes = True
    bg = w.node_tree.nodes.get("Background")
    bg.inputs[0].default_value = (0.42, 0.44, 0.47, 1)
    bg.inputs[1].default_value = 0.45
    scene.world = w


def _light(name, kind, loc, target, energy, size=1.0, color=(1, 1, 1)):
    ld = bpy.data.lights.new(name, kind)
    ld.energy = energy
    ld.color = color
    if kind == 'AREA':
        ld.size = size
    if kind == 'SUN':
        ld.angle = math.radians(size)
    o = bpy.data.objects.new(name, ld)
    bpy.context.scene.collection.objects.link(o)
    o.location = loc
    o.rotation_euler = (Vector(target) - Vector(loc)).to_track_quat('-Z', 'Y').to_euler()
    return o


def _clear_studio():
    for o in list(bpy.data.objects):
        if o.name.startswith("WMS_"):
            bpy.data.objects.remove(o, do_unlink=True)


def _fit_camera(cam, pts, view_dir, up=(0, 0, 1), margin=1.12):
    vd = Vector(view_dir).normalized()
    upv = Vector(up)
    right = vd.cross(upv).normalized()
    camup = right.cross(vd).normalized()
    c = (pts.min(axis=0) + pts.max(axis=0)) / 2
    xs = (pts - c) @ np.array(right)
    ys = (pts - c) @ np.array(camup)
    cx = (xs.max() + xs.min()) / 2
    cy = (ys.max() + ys.min()) / 2
    centre = Vector(c) + right * cx + camup * cy
    span = max(xs.max() - xs.min(), ys.max() - ys.min())
    radius = float(np.linalg.norm(pts - c, axis=1).max())
    cam.data.type = 'ORTHO'
    cam.data.ortho_scale = span * margin
    cam.data.clip_start = 0.01
    cam.data.clip_end = radius * 6 + 10
    cam.location = centre - vd * (radius * 3 + 1)
    cam.rotation_euler = vd.to_track_quat('-Z', 'Y').to_euler()


_TMP_N = 0


def render_views(root, path, views, res=(256, 256), extra_objs=(), margin=1.12,
                 hide_lod1=True, key_dir=(-0.6, -0.8, 1.0)):
    """Render one image per (view_dir) into path (list) or a horizontal strip."""
    scene = bpy.context.scene
    _studio(scene, res)
    _clear_studio()
    objs = [o for o in descendants(root) if o.type == 'MESH']
    for o in objs:
        hid = hide_lod1 and o.name.endswith("_LOD1")
        o.hide_render = hid
    vis = [o for o in objs if not o.hide_render] + list(extra_objs)
    pts = np.concatenate([world_verts(o) for o in vis])
    c = (pts.min(axis=0) + pts.max(axis=0)) / 2
    R = float(np.linalg.norm(pts - c, axis=1).max()) + 0.5
    cam = bpy.data.objects.new("WMS_Cam", bpy.data.cameras.new("WMS_Cam"))
    scene.collection.objects.link(cam)
    scene.camera = cam
    imgs = []
    for i, frm in enumerate(views):
        vdv = -Vector(frm).normalized()  # views are camera directions FROM the asset
        vd = tuple(vdv)
        # lights follow the camera so every view reads the same
        for o in list(bpy.data.objects):
            if o.name.startswith("WMS_L"):
                bpy.data.objects.remove(o, do_unlink=True)
        side = vdv.cross(Vector((0, 0, 1))).normalized()
        key = Vector(c) - vdv * R * 2 - side * R * 1.6 + Vector((0, 0, R * 2.2))
        fill = Vector(c) - vdv * R * 2 + side * R * 2.0 + Vector((0, 0, R * 0.6))
        rim = Vector(c) + vdv * R * 2 + Vector((0, 0, R * 1.5))
        _light("WMS_LKey", 'AREA', key, c, 200 * R * R, size=R * 2.2)
        _light("WMS_LFill", 'AREA', fill, c, 60 * R * R, size=R * 2.5, color=(0.85, 0.9, 1.0))
        _light("WMS_LRim", 'AREA', rim, c, 110 * R * R, size=R * 1.5, color=(1.0, 0.95, 0.85))
        _fit_camera(cam, pts, vd, margin=margin)
        global _TMP_N
        _TMP_N += 1
        out = path if isinstance(path, str) and len(views) == 1 else \
            os.path.join(PREVIEW_DIR, "_tmp_%d.png" % _TMP_N)
        scene.render.filepath = out
        bpy.ops.render.render(write_still=True)
        imgs.append(out)
    _clear_studio()
    for o in objs:
        o.hide_render = False
    return imgs


def stitch(images, out_path, cols=None):
    """Combine equally sized PNGs into one strip/grid with numpy (no PIL in Blender)."""
    loaded = []
    for p in images:
        im = bpy.data.images.load(p)
        w, h = im.size
        a = np.empty(w * h * 4, dtype=np.float32)
        im.pixels.foreach_get(a)
        loaded.append(a.reshape(h, w, 4))
        bpy.data.images.remove(im)
    cols = cols or len(loaded)
    rows = (len(loaded) + cols - 1) // cols
    h, w = loaded[0].shape[:2]
    canvas = np.zeros((rows * h, cols * w, 4), dtype=np.float32)
    for i, a in enumerate(loaded):
        r, c = divmod(i, cols)
        r = rows - 1 - r  # Blender images are bottom-up
        canvas[r * h:(r + 1) * h, c * w:(c + 1) * w] = a
    # opaque neutral backdrop so review sheets read on any viewer
    bgc = np.array([0.20, 0.21, 0.23, 1.0], dtype=np.float32)
    al = canvas[..., 3:4]
    canvas = canvas * al + bgc * (1 - al)
    canvas[..., 3] = 1
    img = bpy.data.images.new("stitch", cols * w, rows * h, alpha=True)
    img.pixels.foreach_set(canvas.ravel())
    img.filepath_raw = out_path
    img.file_format = 'PNG'
    img.save()
    bpy.data.images.remove(img)
    for p in images:
        if os.path.basename(p).startswith("_tmp_"):
            os.remove(p)


def voxel_cube(x=0.0, y=0.0):
    """1 m reference block for scale previews (not exported)."""
    clear = bpy.data.objects.get("WMRef_Cube")
    if clear:
        bpy.data.objects.remove(clear, do_unlink=True)
    mb = MB()
    s = [mb.add((x + dx, y + dy, dz)) for dz in (0, 1) for dy in (0, 1) for dx in (0, 1)]
    for f in ([0, 2, 3, 1], [4, 5, 7, 6], [0, 1, 5, 4], [2, 6, 7, 3], [0, 4, 6, 2], [1, 3, 7, 5]):
        mb.face(f, "M_Stone")
    o = mesh_from_mb("WMRef_Cube", mb, (0, 0, 0))
    me = o.data
    attr = me.color_attributes.new("Col", 'BYTE_COLOR', 'CORNER')
    cols = []
    for p in me.polygons:
        c = (0.18, 0.35, 0.08, 1) if p.normal.z > 0.5 else (0.30, 0.19, 0.10, 1)
        cols += list(c) * p.loop_total
    attr.data.foreach_set("color", cols)
    return o


def finish_asset(root, category, entry, args, icon_views=None, preview_views=None,
                 cube_offset=None, icon_rot=None):
    """Export FBX, write manifest entry, render icon + preview strip."""
    t0 = time.time()
    stats = asset_stats(root)
    e = {"path": "Models/%s/%s" % (category, root.name),
         "file": "Assets/Resources/Models/%s/%s.fbx" % (category, root.name),
         "category": category}
    e.update(stats)
    e.update(entry)
    if args["export"]:
        export_fbx(root, category)
    if args["icons"] and icon_views:
        os.makedirs(ICON_DIR, exist_ok=True)
        if icon_rot:
            root.rotation_euler = icon_rot
            bpy.context.view_layer.update()
        render_views(root, os.path.join(ICON_DIR, root.name + ".png"), icon_views,
                     res=(256, 256), margin=1.08)
        root.rotation_euler = (0, 0, 0)
        bpy.context.view_layer.update()
        e["icon"] = "Models/Icons/%s" % root.name
    if args["preview"] and preview_views:
        os.makedirs(PREVIEW_DIR, exist_ok=True)
        imgs = render_views(root, None, preview_views, res=(400, 400))
        if cube_offset is not None:
            if cube_offset == "auto":
                pts = np.concatenate([world_verts(o) for o in descendants(root)
                                      if o.type == 'MESH' and not o.name.endswith("_LOD1")])
                cube_offset = (float(pts[:, 0].max()) + 0.35, -0.5)
            cube = voxel_cube(*cube_offset)
            imgs += render_views(root, None, [(1.2, -2.2, 0.9)], res=(400, 400),
                                 extra_objs=[cube])
            bpy.data.objects.remove(cube, do_unlink=True)
        stitch(imgs, os.path.join(PREVIEW_DIR, root.name + ".png"))
    print("WM_ASSET %s tris=%d/%d size=%s (%.1fs)" % (
        root.name, stats["tris"]["lod0"], stats["tris"]["lod1"],
        "x".join("%.2f" % s for s in stats["bounds"]["size"]), time.time() - t0))
    return e
