using System;
using System.Collections.Generic;
using ACunicombat.Sensing.Contracts;
using UnityEngine;

namespace ACunicombat.Mechs
{
    /// <summary>A friendly member available for tasking, built from its heartbeat and capability reports.</summary>
    public struct MechForceMember
    {
        public Guid Id;
        public Vector3 Position;
        public float Strength;
        /// <summary>Longest offensive reach; a threat beyond it is not assignable (0 = unlimited).</summary>
        public float MaximumRange;
        public bool Busy;
    }

    /// <summary>An observed hostile contact.</summary>
    public struct MechForceThreat
    {
        public TrackSnapshot Track;
        public Vector3 Position;
        public float Strength;
    }

    public readonly struct MechForceAssignment
    {
        public readonly Guid Member;
        public readonly int ThreatIndex;
        public MechForceAssignment(Guid member, int threatIndex) { Member = member; ThreatIndex = threatIndex; }
    }

    /// <summary>
    /// Force allocation after Sea Power's MaintainAirDefence, adapted to a fleet with
    /// no deck: instead of launching more interceptors the leader commits members
    /// already on station until committed strength matches the threat, and keeps the
    /// rest as a reserve holding formation.
    /// </summary>
    public static class MechForceAllocation
    {
        public const float StrengthMargin = 1.2f;
        public const int MaximumPerThreat = 2;
        public const float ReactionSeconds = 0.6f;

        /// <summary>Sea Power's CanAircraftHelpAgainstWeapon: intercept time plus reaction must beat impact.</summary>
        public static bool CanReachBeforeImpact(float interceptSeconds, float impactSeconds)
            => float.IsFinite(interceptSeconds) && (impactSeconds <= 0f || !float.IsFinite(impactSeconds) || interceptSeconds + ReactionSeconds <= impactSeconds);

        /// <summary>Observed combat value of a hostile track (weight, role, confidence).</summary>
        public static float ThreatStrength(in TrackSnapshot track)
        {
            float weight = track.IdentifiedWeight switch
            {
                ObservedWeightClass.Light => 0.8f,
                ObservedWeightClass.Heavy => 1.4f,
                _ => 1f,
            };
            float role = track.IdentifiedRole switch
            {
                ObservedCombatRole.LongRangeAirToAir => 1.3f,
                ObservedCombatRole.AirToAir => 1.15f,
                ObservedCombatRole.AirDefense => 1f,
                ObservedCombatRole.Reconnaissance => 0.5f,
                _ => 1f,
            };
            return weight * role * Mathf.Lerp(0.6f, 1f, Mathf.Clamp01(track.Confidence));
        }

        /// <summary>Combat value of a friendly member from health and the offensive weapons it reports.</summary>
        public static float MemberStrength(float healthFraction, int offensiveWeapons, bool hasMissiles, bool winchester)
        {
            if (winchester || offensiveWeapons <= 0) return 0f;
            float arms = Mathf.Min(1.5f, 0.6f + 0.3f * offensiveWeapons) + (hasMissiles ? 0.2f : 0f);
            return arms * Mathf.Lerp(0.3f, 1f, Mathf.Clamp01(healthFraction));
        }

        /// <summary>
        /// Greedy allocation. Threats are served nearest-to-package first; each takes
        /// the nearest free members until committed strength reaches the threat times
        /// the margin (at most <see cref="MaximumPerThreat"/>). A second pass guarantees
        /// at least ceil(threats/2) engaged members when strength alone would commit fewer.
        /// Members never assigned form the reserve.
        /// </summary>
        public static List<MechForceAssignment> Allocate(IList<MechForceMember> members, IList<MechForceThreat> threats, Vector3 packagePosition)
        {
            var result = new List<MechForceAssignment>();
            if (members == null || threats == null || threats.Count == 0) return result;
            var free = new List<int>();
            for (int i = 0; i < members.Count; i++) if (!members[i].Busy && members[i].Strength > 0f) free.Add(i);
            var order = new List<int>();
            for (int i = 0; i < threats.Count; i++) order.Add(i);
            order.Sort((a, b) => (threats[a].Position - packagePosition).sqrMagnitude.CompareTo((threats[b].Position - packagePosition).sqrMagnitude));
            var perThreat = new int[threats.Count];

            foreach (int t in order)
            {
                float committed = 0f, needed = threats[t].Strength * StrengthMargin;
                while (committed < needed && perThreat[t] < MaximumPerThreat)
                {
                    int pick = Nearest(members, free, threats[t].Position);
                    if (pick < 0) break;
                    committed += members[free[pick]].Strength;
                    result.Add(new MechForceAssignment(members[free[pick]].Id, t));
                    free.RemoveAt(pick); perThreat[t]++;
                }
            }
            int minimumEngaged = (threats.Count + 1) / 2;
            foreach (int t in order)
            {
                if (result.Count >= minimumEngaged || free.Count == 0) break;
                if (perThreat[t] > 0) continue;
                int pick = Nearest(members, free, threats[t].Position);
                if (pick < 0) continue;
                result.Add(new MechForceAssignment(members[free[pick]].Id, t));
                free.RemoveAt(pick); perThreat[t]++;
            }
            return result;
        }

        /// <summary>Nearest free member that can reach the point with its weapons; -1 if none.</summary>
        private static int Nearest(IList<MechForceMember> members, List<int> free, Vector3 point)
        {
            int best = -1; float bestDistance = float.PositiveInfinity;
            for (int i = 0; i < free.Count; i++)
            {
                var m = members[free[i]];
                float d = (m.Position - point).sqrMagnitude;
                if (m.MaximumRange > 0f && d > m.MaximumRange * m.MaximumRange) continue;
                if (d < bestDistance) { bestDistance = d; best = i; }
            }
            return best;
        }
    }
}
