using System;
using System.Collections.Generic;
using UnityEngine;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// FFX id -> translated effect prefab. Filled by the editor-side FXR effect factory from
    /// locally extracted game data; when an id has no entry the effect player falls back to
    /// its placeholder particle, so the library can grow effect by effect.
    /// </summary>
    public sealed class MechEffectPrefabLibrary : ScriptableObject
    {
        [Serializable]
        public sealed class Entry
        {
            public int ffxId;
            public GameObject prefab;
        }

        public List<Entry> entries = new List<Entry>();

        public bool TryGet(int ffxId, out GameObject prefab)
        {
            for (var i = 0; i < entries.Count; i++)
            {
                if (entries[i].ffxId == ffxId && entries[i].prefab != null)
                {
                    prefab = entries[i].prefab;
                    return true;
                }
            }

            prefab = null;
            return false;
        }
    }
}
