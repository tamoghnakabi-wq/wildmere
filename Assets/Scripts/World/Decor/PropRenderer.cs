using System.Collections.Generic;
using UnityEngine;

namespace Wildmere.World.Decor
{
    /// <summary>
    /// Draws generator-placed decoration props (Blender models) with GPU instancing, per loaded column.
    /// Phase 1 stub: tracks columns; rendering arrives with the Blender asset integration.
    /// </summary>
    public sealed class PropRenderer : MonoBehaviour
    {
        public static PropRenderer Instance { get; private set; }
        readonly HashSet<Chunk> mChunks = new HashSet<Chunk>();

        void Awake() => Instance = this;
        void OnDestroy() { if (Instance == this) Instance = null; }

        public void OnChunkLoaded(Chunk c) { if (c.Props.Count > 0) mChunks.Add(c); }
        public void OnChunkUnloaded(Chunk c) => mChunks.Remove(c);
    }
}
