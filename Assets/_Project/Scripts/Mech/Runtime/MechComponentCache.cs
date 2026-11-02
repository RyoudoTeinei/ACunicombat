using System.Collections.Generic;
using UnityEngine;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// GetComponent memo for hot per-peer loops. A found component is reused while it is
    /// alive; a miss is re-queried at most once per frame, since parts can be attached at
    /// runtime. Main thread only.
    /// </summary>
    public static class MechComponentCache<T> where T : Component
    {
        private struct Entry { public T Value; public int MissFrame; }
        private static readonly Dictionary<GameObject, Entry> entries = new();
        private static int prunedFrame = -1;

        public static T Get(Component owner)
        {
            if (owner == null) return null;
            var key = owner.gameObject;
            int frame = Time.frameCount;
            if (entries.TryGetValue(key, out var entry))
            {
                if (entry.Value != null) return entry.Value;
                if (entry.MissFrame == frame) return null;
            }
            else if (entries.Count >= 1024 && prunedFrame != frame)
            {
                // Destroyed owners are dropped occasionally instead of tracked.
                prunedFrame = frame; entries.Clear();
            }
            var value = owner.GetComponent<T>();
            entries[key] = new Entry { Value = value, MissFrame = value == null ? frame : -1 };
            return value;
        }
    }
}
