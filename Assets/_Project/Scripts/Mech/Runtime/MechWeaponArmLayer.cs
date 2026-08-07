using System;
using UnityEngine;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// Independent right/left weapon animation state machines layered over locomotion.
    /// Each hand owns its own raise, hold, fire and lower transitions.
    /// </summary>
    public sealed class MechWeaponArmLayer : MonoBehaviour
    {
        public enum ArmPhase : byte
        {
            Lowered = 0,
            Raising = 1,
            Holding = 2,
            Firing = 3,
            SustainedFiring = 4,
        }

        [Serializable]
        public struct ArmActionSet
        {
            public string raiseActionId;
            public string holdActionId;
            public string fireActionId;
            [Min(0f)] public float raiseSeconds;
            [Min(0f)] public float fireSeconds;
            public bool repeatFireWhileHeld;
            [Min(0f)] public float sustainedFirePoseSeconds;
        }

        [Tooltip("Anatomical right-arm actions: 03002x/03003x.")]
        [SerializeField] private ArmActionSet rightArm =
            CreateRightArmActionSet();

        [Tooltip("Anatomical left-arm actions: 03004x/03005x.")]
        [SerializeField] private ArmActionSet leftArm =
            CreateLeftArmActionSet();

        [Tooltip("Delay after releasing an arm before it smoothly lowers.")]
        [SerializeField, Min(0f)] private float lowerTimeoutSeconds = 1.5f;

        private readonly ArmRuntimeState rightState = new ArmRuntimeState();
        private readonly ArmRuntimeState leftState = new ArmRuntimeState();
        private MechBodyActionPlayer player;
        private MechTestWeaponSystem weaponSystem;
        private bool activeSideIsRight = true;
        private bool externalRightFireEdge;
        private bool externalLeftFireEdge;
        private float externalRightFireUntil = float.NegativeInfinity;
        private float externalLeftFireUntil = float.NegativeInfinity;

        public ArmPhase Phase =>
            activeSideIsRight ? rightState.Phase : leftState.Phase;

        public ArmPhase RightPhase => rightState.Phase;

        public ArmPhase LeftPhase => leftState.Phase;

        public bool ActiveSideIsRight => activeSideIsRight;

        public string CurrentActionId =>
            player == null
                ? string.Empty
                : activeSideIsRight
                    ? player.ActiveRightArmActionId
                    : player.ActiveLeftArmActionId;

        public string RightFireActionId => rightArm.fireActionId;

        public string LeftFireActionId => leftArm.fireActionId;

        public bool HasVerifiedSideMapping =>
            rightArm.raiseActionId == "a200_030020" &&
            rightArm.holdActionId == "a200_030022" &&
            rightArm.fireActionId == "a200_030030" &&
            leftArm.raiseActionId == "a200_030040" &&
            leftArm.holdActionId == "a200_030042" &&
            leftArm.fireActionId == "a200_030050";

        private void Awake()
        {
            ApplyVerifiedSideMapping();
            weaponSystem = GetComponent<MechTestWeaponSystem>();
        }

        public void ApplyVerifiedSideMapping()
        {
            rightArm = CreateRightArmActionSet();
            leftArm = CreateLeftArmActionSet();
        }

        public void Configure(MechBodyActionPlayer actionPlayer)
        {
            ApplyVerifiedSideMapping();
            player = actionPlayer;
            weaponSystem ??= GetComponent<MechTestWeaponSystem>();
            rightState.Reset();
            leftState.Reset();
        }

        public void NotifyExternalFire(bool rightHand, float holdSeconds = 0.12f)
        {
            var holdUntil = Time.time + Mathf.Max(holdSeconds, 0.02f);
            if (rightHand)
            {
                externalRightFireEdge = true;
                externalRightFireUntil = Mathf.Max(
                    externalRightFireUntil,
                    holdUntil);
            }
            else
            {
                externalLeftFireEdge = true;
                externalLeftFireUntil = Mathf.Max(
                    externalLeftFireUntil,
                    holdUntil);
            }
        }

        public void Tick(
            in MechPlayerInputFrame frame,
            bool suppress,
            float deltaTime)
        {
            if (player == null)
            {
                return;
            }

            // The playable-scene builder can attach the weapon system after
            // this layer has already run Awake/Configure. Resolve lazily so
            // reload gating cannot remain permanently disconnected.
            weaponSystem ??= GetComponent<MechTestWeaponSystem>();

            if (suppress)
            {
                SuppressArm(rightState, true);
                SuppressArm(leftState, false);
                return;
            }

            var rightReloading =
                weaponSystem != null &&
                weaponSystem.IsHandWeaponReloading(
                    MechPartSlot.RightHandWeapon);
            var leftReloading =
                weaponSystem != null &&
                weaponSystem.IsHandWeaponReloading(
                    MechPartSlot.LeftHandWeapon);
            var rightFireHeld =
                (frame.SecondaryFireHeld ||
                 Time.time <= externalRightFireUntil) &&
                !rightReloading;
            var rightFireEdge =
                (frame.SecondaryFireEdge || externalRightFireEdge) &&
                rightFireHeld;
            var leftFireHeld =
                (frame.PrimaryFireHeld ||
                 Time.time <= externalLeftFireUntil) &&
                !leftReloading;
            var leftFireEdge =
                (frame.PrimaryFireEdge || externalLeftFireEdge) &&
                leftFireHeld;
            var rightSustainedFire = UsesSustainedFirePose(
                MechPartSlot.RightHandWeapon,
                rightArm.repeatFireWhileHeld);
            var leftSustainedFire = UsesSustainedFirePose(
                MechPartSlot.LeftHandWeapon,
                leftArm.repeatFireWhileHeld);
            externalRightFireEdge = false;
            externalLeftFireEdge = false;

            if (rightFireEdge)
            {
                activeSideIsRight = true;
            }
            else if (leftFireEdge)
            {
                activeSideIsRight = false;
            }

            if (rightReloading)
            {
                SettleArmForReload(rightState, in rightArm, true);
            }
            else
            {
                TickArm(
                    rightState,
                    in rightArm,
                    true,
                    rightFireHeld,
                    rightFireEdge,
                    frame.SecondaryFireHeld,
                    rightSustainedFire,
                    deltaTime);
            }

            if (leftReloading)
            {
                SettleArmForReload(leftState, in leftArm, false);
            }
            else
            {
                TickArm(
                    leftState,
                    in leftArm,
                    false,
                    leftFireHeld,
                    leftFireEdge,
                    frame.PrimaryFireHeld,
                    leftSustainedFire,
                    deltaTime);
            }
        }

        private void SettleArmForReload(
            ArmRuntimeState state,
            in ArmActionSet set,
            bool isRight)
        {
            state.FireQueued = false;
            state.IdleElapsed = 0f;
            if (state.Phase == ArmPhase.Lowered ||
                state.Phase == ArmPhase.Holding)
            {
                return;
            }

            if (state.Phase == ArmPhase.SustainedFiring)
            {
                ReleaseHeldPose(isRight);
            }

            PlayArm(isRight, set.holdActionId);
            SetPhase(state, ArmPhase.Holding);
        }

        private bool UsesSustainedFirePose(
            MechPartSlot slot,
            bool fallback)
        {
            return weaponSystem == null
                ? fallback
                : weaponSystem.GetEquippedWeaponRole(slot) ==
                  MechWeaponRole.AutomaticCannon;
        }

        private void SuppressArm(ArmRuntimeState state, bool isRight)
        {
            if (state.Phase == ArmPhase.Lowered)
            {
                return;
            }

            state.Reset();
            ReleaseHeldPose(isRight);
            ClearArm(isRight);
        }

        private void TickArm(
            ArmRuntimeState state,
            in ArmActionSet set,
            bool isRight,
            bool fireHeld,
            bool fireEdge,
            bool keepRaised,
            bool sustainedFire,
            float deltaTime)
        {
            state.PhaseElapsed += deltaTime;
            state.IdleElapsed = keepRaised
                ? 0f
                : state.IdleElapsed + deltaTime;
            state.FireQueued |= fireEdge;

            switch (state.Phase)
            {
                case ArmPhase.Lowered:
                    if (keepRaised || fireHeld || fireEdge)
                    {
                        PlayArm(isRight, set.raiseActionId);
                        SetPhase(state, ArmPhase.Raising);
                    }

                    break;

                case ArmPhase.Raising:
                    if (state.PhaseElapsed >= set.raiseSeconds)
                    {
                        if (state.FireQueued ||
                            (fireHeld && sustainedFire))
                        {
                            state.FireQueued = false;
                            PlayArm(isRight, set.fireActionId);
                            SetPhase(state, ArmPhase.Firing);
                        }
                        else
                        {
                            PlayArm(isRight, set.holdActionId);
                            SetPhase(state, ArmPhase.Holding);
                        }
                    }

                    break;

                case ArmPhase.Holding:
                    if (fireEdge ||
                        (fireHeld && sustainedFire))
                    {
                        state.FireQueued = false;
                        PlayArm(isRight, set.fireActionId);
                        SetPhase(state, ArmPhase.Firing);
                    }
                    else if (!keepRaised &&
                             state.IdleElapsed >= lowerTimeoutSeconds)
                    {
                        ClearArm(isRight);
                        state.Reset();
                    }

                    break;

                case ArmPhase.Firing:
                    if (sustainedFire &&
                        fireHeld &&
                        state.PhaseElapsed >=
                        set.sustainedFirePoseSeconds &&
                        HoldArmPose(
                            isRight,
                            set.sustainedFirePoseSeconds))
                    {
                        SetPhase(
                            state,
                            ArmPhase.SustainedFiring);
                        break;
                    }

                    if (state.PhaseElapsed >= set.fireSeconds)
                    {
                        if ((fireHeld && sustainedFire) ||
                            state.FireQueued)
                        {
                            state.FireQueued = false;
                            ReplayArm(isRight, set.fireActionId);
                            SetPhase(state, ArmPhase.Firing);
                        }
                        else
                        {
                            PlayArm(isRight, set.holdActionId);
                            SetPhase(state, ArmPhase.Holding);
                        }
                    }

                    break;

                case ArmPhase.SustainedFiring:
                    if (!fireHeld)
                    {
                        ReleaseHeldPose(isRight);
                        state.Phase = ArmPhase.Firing;
                        state.PhaseElapsed =
                            set.sustainedFirePoseSeconds;
                    }

                    break;
            }
        }

        private void ReleaseHeldPose(bool isRight)
        {
            if (isRight)
            {
                player.ReleaseRightArmPose();
            }
            else
            {
                player.ReleaseLeftArmPose();
            }
        }

        private bool HoldArmPose(bool isRight, float actionTimeSeconds)
        {
            return isRight
                ? player.HoldRightArmPose(actionTimeSeconds)
                : player.HoldLeftArmPose(actionTimeSeconds);
        }

        private void PlayArm(bool isRight, string actionId)
        {
            if (string.IsNullOrEmpty(actionId) ||
                !player.HasAction(actionId))
            {
                return;
            }

            if (isRight)
            {
                player.PlayRightArm(actionId);
            }
            else
            {
                player.PlayLeftArm(actionId);
            }
        }

        private void ClearArm(bool isRight)
        {
            if (isRight)
            {
                player.ClearRightArm(0.25f);
            }
            else
            {
                player.ClearLeftArm(0.25f);
            }
        }

        private void ReplayArm(bool isRight, string actionId)
        {
            if (string.IsNullOrEmpty(actionId) ||
                !player.HasAction(actionId))
            {
                return;
            }

            if (isRight)
            {
                player.ReplayRightArm(actionId);
            }
            else
            {
                player.ReplayLeftArm(actionId);
            }
        }

        private static void SetPhase(
            ArmRuntimeState state,
            ArmPhase next)
        {
            state.Phase = next;
            state.PhaseElapsed = 0f;
        }

        private static ArmActionSet CreateRightArmActionSet()
        {
            return new ArmActionSet
            {
                raiseActionId = "a200_030020",
                holdActionId = "a200_030022",
                fireActionId = "a200_030030",
                raiseSeconds = 0.5f,
                fireSeconds = 0.4f,
                repeatFireWhileHeld = true,
                sustainedFirePoseSeconds = 0.2f,
            };
        }

        private static ArmActionSet CreateLeftArmActionSet()
        {
            return new ArmActionSet
            {
                raiseActionId = "a200_030040",
                holdActionId = "a200_030042",
                fireActionId = "a200_030050",
                raiseSeconds = 0.5f,
                fireSeconds = 0.4f,
                repeatFireWhileHeld = false,
                sustainedFirePoseSeconds = 0.2f,
            };
        }

        private sealed class ArmRuntimeState
        {
            public ArmPhase Phase;
            public float PhaseElapsed;
            public float IdleElapsed;
            public bool FireQueued;

            public void Reset()
            {
                Phase = ArmPhase.Lowered;
                PhaseElapsed = 0f;
                IdleElapsed = 0f;
                FireQueued = false;
            }
        }
    }
}
