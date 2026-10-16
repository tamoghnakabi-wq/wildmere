using UnityEngine;
using Wildmere.World;

namespace Wildmere.Player
{
    /// <summary>One pooled particle system that spits small lit cubes in a block's colour.</summary>
    public sealed class BreakParticles : MonoBehaviour
    {
        ParticleSystem mPs;

        void Awake()
        {
            mPs = gameObject.AddComponent<ParticleSystem>();
            mPs.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = mPs.main;
            main.loop = false;
            main.playOnAwake = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.45f, 0.9f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(1.2f, 3.4f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.05f, 0.11f);
            main.gravityModifier = 1.6f;
            main.maxParticles = 600;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startRotation3D = true;
            var emission = mPs.emission; emission.enabled = false;
            var shape = mPs.shape; shape.enabled = false;
            var col = mPs.collision;
            col.enabled = true;
            col.type = ParticleSystemCollisionType.World;
            col.mode = ParticleSystemCollisionMode.Collision3D;
            col.enabled = false; // terrain has no colliders; particles just fall (cheap)
            var sol = mPs.sizeOverLifetime;
            sol.enabled = true;
            sol.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0, 1, 1, 0.2f));
            var r = GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Mesh;
            r.mesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            var sh = Shader.Find("Universal Render Pipeline/Particles/Simple Lit");
            if (sh == null) sh = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            r.sharedMaterial = new Material(sh) { name = "BreakParticles" };
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.alignment = ParticleSystemRenderSpace.World;
        }

        public void Burst(VoxelHit hit, BlockDef def, int count, float spread)
        {
            if (mPs == null) return;
            Color32 baseCol = def.Particle;
            Vector3 center = hit.Block + new Vector3(0.5f, 0.5f, 0.5f);
            Vector3 face = hit.Normal == Vector3Int.zero ? Vector3.up : (Vector3)hit.Normal;
            var ep = new ParticleSystem.EmitParams();
            for (int i = 0; i < count; i++)
            {
                Vector3 offs = Random.insideUnitSphere * 0.42f * spread;
                ep.position = center + offs + face * 0.5f * (1f - spread * 0.6f);
                ep.velocity = (face * Random.Range(0.6f, 1.6f) + Random.insideUnitSphere * 1.6f + Vector3.up * 1.2f) * (0.8f + spread);
                float j = Random.Range(0.75f, 1.15f);
                ep.startColor = new Color32((byte)Mathf.Min(255, baseCol.r * j), (byte)Mathf.Min(255, baseCol.g * j), (byte)Mathf.Min(255, baseCol.b * j), 255);
                ep.rotation3D = Random.insideUnitSphere * 180f;
                mPs.Emit(ep, 1);
            }
        }
    }
}
