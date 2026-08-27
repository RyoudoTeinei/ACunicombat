using System;
using UnityEngine;

namespace ACunicombat.Mechs
{
    public enum MechThrusterRole
    {
        Main = 0,
        Reverse = 10,
        StrafePositive = 20,
        StrafeNegative = 21,
        ShoulderLift = 30,
        LegLift = 40,
        Descent = 45,
        ShoulderRcs = 50,
        LegRcs = 60,
        Translation = 70,
    }

    public enum MechThrusterSide
    {
        Center = 0,
        Left = 10,
        Right = 20,
    }

    /// <summary>
    /// Runtime state for one physical or provisional thruster nozzle. Translation forces are
    /// applied to the mech as a net force; nozzle positions do not create attitude torque.
    /// </summary>
    [Serializable]
    public sealed class MechThrusterChannelState
    {
        [SerializeField] private string id;
        [SerializeField] private MechThrusterRole role;
        [SerializeField] private MechThrusterSide side;
        [SerializeField] private Transform anchor;
        [SerializeField] private Vector3 fallbackLocalPosition;
        [SerializeField] private bool usesProvisionalOrigin;
        [SerializeField] [Range(0f, 1f)] private float health = 1f;
        [SerializeField] private Vector3 currentForceNewtons;
        [SerializeField] private float currentRatedCapacityNewtons;

        public MechThrusterChannelState(
            string channelId,
            MechThrusterRole channelRole,
            MechThrusterSide channelSide,
            Transform channelAnchor,
            Vector3 localPosition,
            bool provisionalOrigin)
        {
            id = channelId;
            role = channelRole;
            side = channelSide;
            anchor = channelAnchor;
            fallbackLocalPosition = localPosition;
            usesProvisionalOrigin = provisionalOrigin;
        }

        public string Id => id;
        public MechThrusterRole Role => role;
        public MechThrusterSide Side => side;
        public Transform Anchor => anchor;
        public bool UsesProvisionalOrigin => usesProvisionalOrigin;
        public float Health => health;
        public Vector3 CurrentForceNewtons => currentForceNewtons;
        public float CurrentOutputFraction => currentRatedCapacityNewtons > Mathf.Epsilon
            ? Mathf.Clamp01(currentForceNewtons.magnitude / currentRatedCapacityNewtons)
            : 0f;
        public bool Operational => health > 0.0001f;

        public Vector3 ResolveWorldOrigin(Transform vehicleRoot)
        {
            return anchor != null
                ? anchor.position
                : vehicleRoot.TransformPoint(fallbackLocalPosition);
        }

        public void SetHealth(float value)
        {
            health = Mathf.Clamp01(value);
        }

        public void ClearForce()
        {
            currentForceNewtons = Vector3.zero;
            currentRatedCapacityNewtons = 0f;
        }

        public void AddForce(Vector3 forceNewtons, float ratedCapacityNewtons)
        {
            currentForceNewtons += forceNewtons;
            currentRatedCapacityNewtons = Mathf.Max(
                currentRatedCapacityNewtons,
                Mathf.Max(0f, ratedCapacityNewtons));
        }

        public void SetForce(Vector3 forceNewtons, float ratedCapacityNewtons)
        {
            currentForceNewtons = forceNewtons;
            currentRatedCapacityNewtons = Mathf.Max(0f, ratedCapacityNewtons);
        }
    }
}
