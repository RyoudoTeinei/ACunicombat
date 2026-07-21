using System;
using UnityEngine;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// 上半身武器命令层（架构第 4 层；w0xx 武器行为图编译前的正式等价物，
    /// 状态序列 = w020 官方图：待機→(抬臂)→射撃待機→射撃→射撃終了→待機）。
    /// 与全身层并行：只经 MechBodyActionPlayer 的上半身遮罩层播放，不触碰第 0 层。
    /// 左右手独立入口；同侧连发停留在 Hold，不重播抬臂。
    /// 非循环动作结束与收枪超时自动回落，ClearUpperBody 交还基础姿态。
    /// </summary>
    public sealed class MechWeaponArmLayer : MonoBehaviour
    {
        public enum ArmPhase : byte
        {
            Lowered = 0,
            Raising = 1,
            Holding = 2,
            Firing = 3,
        }

        [Serializable]
        public struct ArmActionSet
        {
            public string raiseActionId;
            public string holdActionId;
            public string fireActionId;
            [Tooltip("抬臂动作时长（秒）。TAE/clip 时长进 IR 后由数据替换。")]
            public float raiseSeconds;
            [Tooltip("开火/后坐动作时长（秒）。")]
            public float fireSeconds;
        }

        [Tooltip("Right-hand action set. Bone-curve inspection proves 03002x/03003x drives the anatomical right arm.")]
        [SerializeField] private ArmActionSet rightArm = CreateRightArmActionSet();

        [Tooltip("Left-hand action set. Bone-curve inspection proves 03004x/03005x drives the anatomical left arm.")]
        [SerializeField] private ArmActionSet leftArm = CreateLeftArmActionSet();

        [Tooltip("无开火输入后收枪的超时（秒）。")]
        [SerializeField] private float lowerTimeoutSeconds = 1.5f;

        private MechBodyActionPlayer player;
        private ArmPhase phase;
        private bool activeSideIsRight = true;
        private float phaseElapsed;
        private float idleElapsed;

        public ArmPhase Phase => phase;

        public bool ActiveSideIsRight => activeSideIsRight;

        public string CurrentActionId => player != null ? player.ActiveUpperBodyActionId : string.Empty;

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
            // Serialized action sets in already-generated scenes may contain the old reversed mapping.
            ApplyVerifiedSideMapping();
        }

        public void ApplyVerifiedSideMapping()
        {
            rightArm = CreateRightArmActionSet();
            leftArm = CreateLeftArmActionSet();
        }

        public void Configure(MechBodyActionPlayer actionPlayer)
        {
            // Upgrade stale generated scenes whose serialized action sets predate the verified mapping.
            ApplyVerifiedSideMapping();
            player = actionPlayer;
            phase = ArmPhase.Lowered;
        }

        /// <summary>suppress = 行为图裁决当前全身状态禁用上半身（QB/硬直等，由 Rig 判定传入）。</summary>
        public void Tick(in MechPlayerInputFrame frame, bool suppress, float deltaTime)
        {
            if (player == null)
            {
                return;
            }

            phaseElapsed += deltaTime;

            if (suppress)
            {
                if (phase != ArmPhase.Lowered)
                {
                    SetPhase(ArmPhase.Lowered);
                    player.ClearUpperBody();
                }

                return;
            }

            var wantFire = frame.PrimaryFireHeld || frame.SecondaryFireHeld;
            var fireEdge = frame.PrimaryFireEdge || frame.SecondaryFireEdge;
            if (wantFire)
            {
                idleElapsed = 0f;
                // 左右手独立入口：以本帧边沿侧优先，否则保持当前侧。
                if (frame.SecondaryFireEdge)
                {
                    activeSideIsRight = true;
                }
                else if (frame.PrimaryFireEdge)
                {
                    activeSideIsRight = false;
                }
            }
            else
            {
                idleElapsed += deltaTime;
            }

            ref readonly var set = ref activeSideIsRight ? ref rightArm : ref leftArm;
            switch (phase)
            {
                case ArmPhase.Lowered:
                    if (wantFire)
                    {
                        PlayUpper(set.raiseActionId);
                        SetPhase(ArmPhase.Raising);
                    }

                    break;

                case ArmPhase.Raising:
                    if (phaseElapsed >= set.raiseSeconds)
                    {
                        PlayUpper(set.holdActionId);
                        SetPhase(ArmPhase.Holding);
                    }

                    break;

                case ArmPhase.Holding:
                    if (fireEdge)
                    {
                        PlayUpper(set.fireActionId);
                        SetPhase(ArmPhase.Firing);
                    }
                    else if (idleElapsed >= lowerTimeoutSeconds)
                    {
                        player.ClearUpperBody();
                        SetPhase(ArmPhase.Lowered);
                    }

                    break;

                case ArmPhase.Firing:
                    if (phaseElapsed >= set.fireSeconds)
                    {
                        // 连发：仍按住则回 Hold 等下一次边沿；不重播抬臂。
                        PlayUpper(set.holdActionId);
                        SetPhase(ArmPhase.Holding);
                    }

                    break;
            }
        }

        private void PlayUpper(string actionId)
        {
            if (!string.IsNullOrEmpty(actionId) && player.HasAction(actionId))
            {
                player.PlayUpperBody(actionId);
            }
        }

        private void SetPhase(ArmPhase next)
        {
            phase = next;
            phaseElapsed = 0f;
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
            };
        }
    }
}
