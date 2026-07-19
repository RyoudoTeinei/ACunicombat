using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ACunicombat.Mechs
{
    /// <summary>一帧玩家输入的稳定快照。适配器采集，运动层/行为图/武器层只读。</summary>
    [Serializable]
    public struct MechPlayerInputFrame
    {
        /// <summary>机体局部平移输入：x=右(+)/左(−)，y=前(+)/后(−)。W/S/A/D。</summary>
        public Vector2 MoveLocal;
        public Vector2 MouseDelta;
        /// <summary>Space 按住（上升推力）。</summary>
        public bool RiseHeld;
        /// <summary>Space 按下边沿。</summary>
        public bool RiseEdge;
        /// <summary>双击 Space 边沿（AB 触发，按键边沿 + 双击时间窗判定）。</summary>
        public bool AssaultBoostEdge;
        /// <summary>Shift 按下边沿（QB 触发）。</summary>
        public bool QuickBoostEdge;
        public bool FireLeftHeld;
        public bool FireLeftEdge;
        public bool FireRightHeld;
        public bool FireRightEdge;
    }

    /// <summary>
    /// 行为图 PadBits 的位定义常量。**与 c0000_condition_catalog.json 的 padBitNames 一一对应**，
    /// 该表由目录生成脚本按引用频率序固定，改目录必须同步此文件。
    /// </summary>
    public static class MechPadBits
    {
        public const int Move = 0;              // 移動入力
        public const int GroundBoost = 1;       // 地上ブースト
        public const int QuickBoost = 2;        // クイックブースト発動
        public const int DashSidestep = 3;      // ダッシュ回り込み
        public const int FlightBoost = 4;       // 飛行ブースト
        public const int Run = 5;               // 走行
        public const int Jump = 6;              // ジャンプ
        public const int RiseStartBoost = 7;    // 上昇開始ブースト（長押し起飛）
        public const int MoveDown = 8;          // ↓移動
        public const int AimPressed = 13;       // エイム押した
        public const int FierceDash = 14;       // 猛ダッシュ（AB 候选映射）
        public const int MoveUp = 12;           // ↑移動
        public const int JumpHeld = 17;         // ジャンプ押している
        public const int NoInput = 20;          // 入力無し（一瞬で判定）——必须由适配器求反算出
    }

    /// <summary>
    /// 玩家输入适配层（架构第 1 层）：只采集输入、判边沿/双击窗，产出输入快照并折算
    /// PadBits。不做运动积分、不选动画。AI 与 demo 用各自的适配器产出同一种快照。
    /// </summary>
    public sealed class MechPlayerInputAdapter : MonoBehaviour
    {
        [Tooltip("双击 Space 触发 AB 的时间窗（秒）。")]
        [SerializeField] private float doubleTapWindowSeconds = 0.3f;
        [Tooltip("长按 Space 判定为上昇開始ブースト的时长（秒）。")]
        [SerializeField] private float riseHoldBoostSeconds = 0.25f;

        private float lastSpaceDownTime = -10f;
        private float spaceHeldSince = -1f;

        public MechPlayerInputFrame Current { get; private set; }

        /// <summary>每帧调用（由 MechPlayerRig 驱动，保证与运动层同帧序）。</summary>
        public MechPlayerInputFrame Sample()
        {
            var keyboard = Keyboard.current;
            var mouse = Mouse.current;
            var frame = default(MechPlayerInputFrame);
            if (keyboard == null)
            {
                Current = frame;
                return frame;
            }

            frame.MoveLocal = new Vector2(
                (keyboard.dKey.isPressed ? 1f : 0f) - (keyboard.aKey.isPressed ? 1f : 0f),
                (keyboard.wKey.isPressed ? 1f : 0f) - (keyboard.sKey.isPressed ? 1f : 0f));

            var spaceDown = keyboard.spaceKey.wasPressedThisFrame;
            frame.RiseHeld = keyboard.spaceKey.isPressed;
            frame.RiseEdge = spaceDown;
            if (spaceDown)
            {
                frame.AssaultBoostEdge = Time.unscaledTime - lastSpaceDownTime <= doubleTapWindowSeconds;
                lastSpaceDownTime = Time.unscaledTime;
                spaceHeldSince = Time.unscaledTime;
            }

            if (!frame.RiseHeld)
            {
                spaceHeldSince = -1f;
            }

            frame.QuickBoostEdge = keyboard.leftShiftKey.wasPressedThisFrame;

            if (mouse != null)
            {
                frame.MouseDelta = mouse.delta.ReadValue();
                frame.FireLeftHeld = mouse.leftButton.isPressed;
                frame.FireLeftEdge = mouse.leftButton.wasPressedThisFrame;
                frame.FireRightHeld = mouse.rightButton.isPressed;
                frame.FireRightEdge = mouse.rightButton.wasPressedThisFrame;
            }

            Current = frame;
            return frame;
        }

        /// <summary>把输入快照折算成行为图 PadBits（映射表见类注释与设计文档）。</summary>
        public uint BuildPadBits(in MechPlayerInputFrame frame, bool airborne)
        {
            var bits = 0u;
            var hasMove = frame.MoveLocal.sqrMagnitude > 0.0001f;
            if (hasMove)
            {
                bits |= 1u << MechPadBits.Move;
                bits |= 1u << MechPadBits.Run;
                // 空中的方向输入即飞行推进；地面即地上ブースト（AC6 默认 boost 移动）。
                bits |= airborne ? 1u << MechPadBits.FlightBoost : 1u << MechPadBits.GroundBoost;
            }
            else
            {
                bits |= 1u << MechPadBits.NoInput;
            }

            if (frame.RiseEdge)
            {
                bits |= 1u << MechPadBits.Jump;
            }

            if (frame.RiseHeld)
            {
                bits |= 1u << MechPadBits.JumpHeld;
                bits |= 1u << MechPadBits.MoveUp;
                if (spaceHeldSince >= 0f && Time.unscaledTime - spaceHeldSince >= riseHoldBoostSeconds)
                {
                    bits |= 1u << MechPadBits.RiseStartBoost;
                }
            }

            if (frame.QuickBoostEdge)
            {
                bits |= 1u << MechPadBits.QuickBoost;
                bits |= 1u << MechPadBits.DashSidestep;
            }

            if (frame.AssaultBoostEdge)
            {
                bits |= 1u << MechPadBits.FierceDash;
            }

            if (frame.FireLeftHeld || frame.FireRightHeld)
            {
                bits |= 1u << MechPadBits.AimPressed;
            }

            return bits;
        }
    }
}
