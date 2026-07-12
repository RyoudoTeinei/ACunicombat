using System;
using System.Collections.Generic;
using UnityEngine;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// One TAE SpawnOneShotFFX event: at <see cref="time"/> seconds into the body action, the
    /// game spawns effect <see cref="ffxId"/> at the dummy addressed by
    /// <see cref="dummyPolyId"/> (thousands-prefix routing: 1xxx head, 2xxx core, 3xxx arms,
    /// 4xxx legs, 10/11/20/21xxx weapons; the last three digits are the part-local dummy ref).
    /// </summary>
    [Serializable]
    public sealed class MechActionEffectEvent
    {
        public float time;
        public int ffxId;
        public int dummyPolyId;
    }

    /// <summary>
    /// Effect timeline for one body action, extracted from the game's TAE via
    /// Tools/ExtractAC6EffectTimelines.py. Assets are generated locally (they carry
    /// game-derived data) and are never committed.
    /// </summary>
    public sealed class MechActionEffectTimeline : ScriptableObject
    {
        public string actionId;
        public List<MechActionEffectEvent> events = new List<MechActionEffectEvent>();
    }
}
