using System;
using System.Collections.Generic;
using UnityEngine;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// Diagnostic record for one spawned effect, mirroring the fields the validation matrix
    /// requires: what fired, from which part and dummy, where and which way it pointed.
    /// </summary>
    public sealed class MechActionEffectSpawnRecord
    {
        public float Time;
        public int FfxId;
        public int DummyPolyId;
        public MechPartSlot Slot;
        public string DummyPath;
        public Vector3 WorldPosition;
        public Vector3 WorldForward;
    }

    /// <summary>
    /// Placeholder VFX channel: fires the TAE-derived effect timeline of the current body
    /// action on the assembled mech. Dummy addressing uses the game's thousands-prefix routing
    /// and spawns a placeholder particle burst at every matching dummy node. The point of this
    /// component is to prove the event->dummy->direction pipeline; swapping the placeholder for
    /// decoded native FXR assets later does not change any of the routing.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MechActionEffectPlayer : MonoBehaviour
    {
        [SerializeField] private MechActionEffectTimeline timeline;
        [SerializeField] private MechRuntimeState runtimeState;
        [SerializeField] private MechSynchronizedAnimationPlayer bodyPlayer;
        [SerializeField] private bool spawnPlaceholderParticles = true;

        private readonly List<MechActionEffectSpawnRecord> spawnRecords = new List<MechActionEffectSpawnRecord>();
        private readonly List<GameObject> spawnedObjects = new List<GameObject>();
        private readonly List<string> unresolvedEvents = new List<string>();
        private readonly List<string> optionalSkips = new List<string>();
        private double lastEvaluatedTime;

        public MechActionEffectTimeline Timeline => timeline;
        public IReadOnlyList<MechActionEffectSpawnRecord> SpawnRecords => spawnRecords;
        public IReadOnlyList<string> UnresolvedEvents => unresolvedEvents;
        public IReadOnlyList<string> OptionalSkips => optionalSkips;

        public void Configure(MechActionEffectTimeline effectTimeline, MechRuntimeState state, MechSynchronizedAnimationPlayer player)
        {
            timeline = effectTimeline;
            runtimeState = state;
            bodyPlayer = player;
            ResetPlayback();
        }

        public void ResetPlayback()
        {
            lastEvaluatedTime = -1d; // window is (from, to]; -1 lets t=0 events fire
            spawnRecords.Clear();
            unresolvedEvents.Clear();
            optionalSkips.Clear();
            for (var i = 0; i < spawnedObjects.Count; i++)
            {
                if (spawnedObjects[i] != null)
                {
                    if (Application.isPlaying)
                    {
                        Destroy(spawnedObjects[i]);
                    }
                    else
                    {
                        DestroyImmediate(spawnedObjects[i]);
                    }
                }
            }

            spawnedObjects.Clear();
        }

        private void Update()
        {
            if (bodyPlayer == null || !bodyPlayer.IsGraphValid)
            {
                return;
            }

            var current = bodyPlayer.PlaybackTime;
            if (current < lastEvaluatedTime)
            {
                // Loop restarted: flush the tail of the previous cycle, then rewind.
                EvaluateWindow((float)lastEvaluatedTime, float.MaxValue);
                lastEvaluatedTime = -1d;
            }

            EvaluateWindow((float)lastEvaluatedTime, (float)current);
            lastEvaluatedTime = current;
        }

        /// <summary>
        /// Fires every event with fromExclusive &lt; time &lt;= toInclusive. Validation drives
        /// this directly at its sample times; runtime playback drives it from Update.
        /// </summary>
        public void EvaluateWindow(float fromExclusive, float toInclusive)
        {
            if (timeline == null || runtimeState == null)
            {
                return;
            }

            for (var i = 0; i < timeline.events.Count; i++)
            {
                var effectEvent = timeline.events[i];
                if (effectEvent.time > fromExclusive && effectEvent.time <= toInclusive)
                {
                    Fire(effectEvent);
                }
            }
        }

        private void Fire(MechActionEffectEvent effectEvent)
        {
            var slot = SlotForDummyPolyId(effectEvent.dummyPolyId);
            var reference = effectEvent.dummyPolyId % 1000;
            var suffix = "_dmy_" + reference;
            var resolved = 0;

            var parts = runtimeState.AssembledParts;
            for (var p = 0; p < parts.Count; p++)
            {
                var record = parts[p];
                if (record?.instance == null || record.slot != slot)
                {
                    continue;
                }

                var transforms = record.instance.GetComponentsInChildren<Transform>(true);
                for (var t = 0; t < transforms.Length; t++)
                {
                    var candidate = transforms[t];
                    if (!candidate.name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    resolved++;
                    var spawn = new MechActionEffectSpawnRecord
                    {
                        Time = effectEvent.time,
                        FfxId = effectEvent.ffxId,
                        DummyPolyId = effectEvent.dummyPolyId,
                        Slot = slot,
                        DummyPath = candidate.name,
                        WorldPosition = candidate.position,
                        // Dummy frames encode their emission direction as the local -Z axis.
                        WorldForward = -candidate.forward,
                    };
                    spawnRecords.Add(spawn);

                    if (spawnPlaceholderParticles)
                    {
                        spawnedObjects.Add(SpawnPlaceholder(candidate, effectEvent.ffxId));
                    }
                }
            }

            if (resolved == 0)
            {
                // TAE timelines are authored against the superset of every part: a mech whose
                // part simply lacks the dummy is a legitimate skip (the game does the same).
                // Dummy refs are per-category namespaces, so the same ref existing on another
                // slot proves nothing. The only detectable routing error is an unmappable
                // prefix (slot == None).
                var line = $"t={effectEvent.time:0.###} ffx={effectEvent.ffxId} dummy={effectEvent.dummyPolyId} ({slot}{suffix})";
                if (slot == MechPartSlot.None)
                {
                    unresolvedEvents.Add($"{line} — unmappable dummy prefix");
                }
                else
                {
                    optionalSkips.Add(line);
                }
            }
        }

        private static MechPartSlot SlotForDummyPolyId(int dummyPolyId)
        {
            var prefix = dummyPolyId / 1000;
            switch (prefix)
            {
                case 1: return MechPartSlot.Head;
                case 2: return MechPartSlot.Core;
                case 3: return MechPartSlot.Arms;
                case 4: return MechPartSlot.Legs;
                case 10: return MechPartSlot.RightHandWeapon;
                case 11: return MechPartSlot.RightBackWeapon;
                case 20: return MechPartSlot.LeftHandWeapon;
                case 21: return MechPartSlot.LeftBackWeapon;
                default: return MechPartSlot.None;
            }
        }

        private static GameObject SpawnPlaceholder(Transform dummy, int ffxId)
        {
            var go = new GameObject($"FFX_{ffxId}_Placeholder");
            go.transform.SetParent(dummy, false);
            go.transform.localPosition = Vector3.zero;
            // Particle systems emit along their +Z; the dummy's emission direction is -Z.
            go.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);

            var particles = go.AddComponent<ParticleSystem>();
            var main = particles.main;
            main.duration = 0.6f;
            main.loop = false;
            main.startLifetime = 0.45f;
            main.startSpeed = 3.5f;
            main.startSize = 0.14f;
            main.startColor = new Color(1f, 0.55f, 0.15f, 0.9f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;

            var emission = particles.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 30) });

            var shape = particles.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 18f;
            shape.radius = 0.05f;

            if (Application.isPlaying)
            {
                particles.Play();
            }
            else
            {
                particles.Simulate(0.08f, withChildren: true, restart: true);
            }

            return go;
        }
    }
}
