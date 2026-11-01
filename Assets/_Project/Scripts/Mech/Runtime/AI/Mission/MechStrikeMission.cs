using UnityEngine;

namespace ACunicombat.Mechs
{
    /// <summary>Strike package phases (after Sea Power's AirStrike state machine).</summary>
    public enum MechStrikePhase : byte { Assemble, Ingress, Attack, Egress, Rejoin, Complete }

    /// <summary>Observed inputs for one strike evaluation; built from fleet reports and own tracks.</summary>
    public struct MechStrikeInputs
    {
        public float Now;
        public bool TargetObserved;
        public Vector3 TargetPosition;
        /// <summary>Package reference (leader report, or own position for the leader).</summary>
        public Vector3 PackagePosition;
        public float AttackRangeMeters;
        public int MembersFresh, MembersOnStation;
        public bool TargetDestroyedOrLost;
    }

    /// <summary>
    /// Phase machine for a Strike mission L3 layer. Every member runs its own copy
    /// over the same received reports, so phases agree without new packets.
    /// </summary>
    public sealed class MechStrikeMission
    {
        public const float AssembleTimeoutSeconds = 25f;
        public const float IngressLineFactor = 1.2f;
        public const float EgressDistanceFactor = 1.5f;
        public const float TargetLostSeconds = 8f;

        public MechStrikePhase Phase { get; private set; } = MechStrikePhase.Assemble;
        public float PhaseEnteredAt { get; private set; }
        private float lastTargetSeen = float.NegativeInfinity;
        private Vector3 lastTargetPosition;
        private bool started;

        public void Reset(float now) { Phase = MechStrikePhase.Assemble; PhaseEnteredAt = now; lastTargetSeen = float.NegativeInfinity; started = true; }

        public MechStrikePhase Update(in MechStrikeInputs s)
        {
            if (!started) Reset(s.Now);
            if (s.TargetObserved) { lastTargetSeen = s.Now; lastTargetPosition = s.TargetPosition; }
            bool targetGone = s.TargetDestroyedOrLost || s.Now - lastTargetSeen > TargetLostSeconds && Phase >= MechStrikePhase.Attack;
            float attackRange = Mathf.Max(500f, s.AttackRangeMeters);
            float packageRange = lastTargetSeen > float.NegativeInfinity
                ? Vector3.Distance(s.PackagePosition, lastTargetPosition) : float.PositiveInfinity;
            switch (Phase)
            {
                case MechStrikePhase.Assemble:
                    if (s.MembersFresh <= 1 || s.MembersOnStation >= s.MembersFresh || s.Now - PhaseEnteredAt >= AssembleTimeoutSeconds)
                        Enter(MechStrikePhase.Ingress, s.Now);
                    break;
                case MechStrikePhase.Ingress:
                    if (targetGone) Enter(MechStrikePhase.Rejoin, s.Now);
                    else if (packageRange <= attackRange * IngressLineFactor) Enter(MechStrikePhase.Attack, s.Now);
                    break;
                case MechStrikePhase.Attack:
                    if (targetGone) Enter(MechStrikePhase.Egress, s.Now);
                    break;
                case MechStrikePhase.Egress:
                    if (!float.IsFinite(packageRange) || packageRange >= attackRange * EgressDistanceFactor || s.Now - PhaseEnteredAt > 30f)
                        Enter(MechStrikePhase.Rejoin, s.Now);
                    break;
                case MechStrikePhase.Rejoin:
                    if (s.MembersOnStation >= s.MembersFresh || s.Now - PhaseEnteredAt > 20f) Enter(MechStrikePhase.Complete, s.Now);
                    break;
            }
            return Phase;
        }

        private void Enter(MechStrikePhase phase, float now) { Phase = phase; PhaseEnteredAt = now; }

        /// <summary>Where the package leader flies during ingress: the attack line on the bearing to the target.</summary>
        public static Vector3 IngressPoint(Vector3 package, Vector3 target, float attackRange)
        {
            var toPackage = package - target;
            if (toPackage.sqrMagnitude < 1f) return package;
            return target + toPackage.normalized * Mathf.Max(500f, attackRange) * (IngressLineFactor * 0.9f);
        }

        /// <summary>Egress: straight away from the target to the egress distance.</summary>
        public static Vector3 EgressPoint(Vector3 own, Vector3 target, float attackRange)
        {
            var away = own - target;
            if (away.sqrMagnitude < 1f) away = Vector3.back;
            return target + away.normalized * Mathf.Max(500f, attackRange) * (EgressDistanceFactor + 0.2f);
        }

        /// <summary>Escort cover point: ahead of the attackers on the threat axis, offset to its own side.</summary>
        public static Vector3 EscortCoverPoint(Vector3 attackers, Vector3 threatAxis, float spacing, int formationIndex)
        {
            var axis = threatAxis.sqrMagnitude > 0.01f ? threatAxis.normalized : Vector3.forward;
            var side = Vector3.Cross(Vector3.up, axis);
            if (side.sqrMagnitude < 0.001f) side = Vector3.right;
            float sign = formationIndex % 2 == 0 ? 1f : -1f;
            return attackers + axis * spacing * 1.5f + side.normalized * spacing * sign;
        }
    }
}
