using System;
using System.Collections.Generic;
using UnityEngine;

namespace ACunicombat.Mechs
{
    /// <summary>条件原子的求值绑定类别。目录数据：Tools/AssetManifests/c0000_condition_catalog.json。</summary>
    public enum MechConditionKind : byte
    {
        /// <summary>未映射原子：恒假。其"反転"变体因取反恒真——ソフトアセン/プロペラ/
        /// オーバーヒート等高频反転原子由此免费获得正确默认值。</summary>
        ConstFalse = 0,
        ConstTrue = 1,
        PadInput = 2,
        Grounded = 3,
        Airborne = 4,
        EnergyEmpty = 5,
        LockedOn = 6,
        TargetAttacking = 7,
        Guarding = 8,
        BodyAnimFinished = 9,
        CharControlFlag = 10,
        AttackStateType = 11,
        BodyStateType = 12,
    }

    /// <summary>
    /// 解释器每 Tick 的外部世界快照。blittable，唯一的消费方边界：
    /// 玩家控制器、AI、demo 脚本都只填这个结构——demo 是脚本化输入，不是脚本化动画。
    /// </summary>
    [Serializable]
    public struct MechGraphContext
    {
        /// <summary>按位的按键状态；位定义 = 条件目录 padBitNames（bit0=移動入力、…）。</summary>
        public uint PadBits;
        /// <summary>TAE ChrActionFlag 派生的取消窗口位；位定义 = 目录 charControlFlagBitNames。</summary>
        public uint CharControlFlags;
        public bool Grounded;
        public bool Airborne;
        public bool EnergyEmpty;
        public bool LockedOn;
        public bool TargetAttacking;
        public bool Guarding;
        /// <summary>目录 stateTypeIds 定义的枚举值（1=ブーストモード、2=エイムモード…）。</summary>
        public byte AttackStateType;
        public byte BodyStateType;
    }

    /// <summary>
    /// 编译后的 BHV 行为图（数据资产）。结构全部为扁平数组 + 索引区间：
    /// 运行时零字典、零字符串比较、零分配——转换按存储序线性扫描（存储序 = 原版裁决序），
    /// 条件读预求值的原子位掩码，单次判定为 O(该转换条件数) 次位测试。
    /// 由 AC6BehaviorGraphCompiler 从 basenormal_states.json + 条件目录编译生成。
    /// </summary>
    [CreateAssetMenu(menuName = "ACunicombat/Mech Behavior Graph", fileName = "MechBehaviorGraph")]
    public sealed class MechBehaviorGraphAsset : ScriptableObject
    {
        [Serializable]
        public struct State
        {
            public string officialName;
            /// <summary>动作库 actionId（如 a000_120200）；无物理动画时为空 = 状态不可用。</summary>
            public string actionId;
            public bool animAvailable;
            public int transitionStart;
            public int transitionCount;
            /// <summary>TAE Blend 事件的官方 blend-in；无事件为 0（硬切）。</summary>
            public float blendInSeconds;
            public bool loop;
        }

        [Serializable]
        public struct Transition
        {
            public ushort targetState;
            public int conditionStart;
            public int conditionCount;
        }

        [Serializable]
        public struct Condition
        {
            public ushort atomIndex;
            public bool negate;
        }

        [Serializable]
        public struct Atom
        {
            /// <summary>原始日文条件文本（仅调试用，运行时不读）。</summary>
            public string debugText;
            public MechConditionKind kind;
            public int param;
        }

        public State[] states = Array.Empty<State>();
        public Transition[] transitions = Array.Empty<Transition>();
        public Condition[] conditions = Array.Empty<Condition>();
        public Atom[] atoms = Array.Empty<Atom>();
        public int initialState;

        /// <summary>原子位掩码所需的 ulong 数（≤4 = 最多 256 原子）。</summary>
        public int AtomMaskWords => (atoms.Length + 63) >> 6;

        /// <summary>把全部原子求值成位掩码。mask 长度 ≥ AtomMaskWords，调用方栈上分配。</summary>
        public void EvaluateAtoms(in MechGraphContext ctx, Span<ulong> mask)
        {
            mask.Clear();
            for (var i = 0; i < atoms.Length; i++)
            {
                if (EvaluateAtom(atoms[i], in ctx))
                {
                    mask[i >> 6] |= 1UL << (i & 63);
                }
            }
        }

        private static bool EvaluateAtom(in Atom atom, in MechGraphContext ctx)
        {
            switch (atom.kind)
            {
                case MechConditionKind.ConstTrue: return true;
                case MechConditionKind.PadInput: return (ctx.PadBits >> atom.param & 1u) != 0;
                case MechConditionKind.Grounded: return ctx.Grounded;
                case MechConditionKind.Airborne: return ctx.Airborne;
                case MechConditionKind.EnergyEmpty: return ctx.EnergyEmpty;
                case MechConditionKind.LockedOn: return ctx.LockedOn;
                case MechConditionKind.TargetAttacking: return ctx.TargetAttacking;
                case MechConditionKind.Guarding: return ctx.Guarding;
                case MechConditionKind.BodyAnimFinished: return false; // Runner 用 animFinished 覆盖求值，见 Runner.Tick
                case MechConditionKind.CharControlFlag: return (ctx.CharControlFlags >> atom.param & 1u) != 0;
                case MechConditionKind.AttackStateType: return ctx.AttackStateType == atom.param;
                case MechConditionKind.BodyStateType: return ctx.BodyStateType == atom.param;
                default: return false;
            }
        }

        /// <summary>BodyAnimFinished 类原子的索引列表（Runner 在位掩码上直接补写，避免逐帧重扫）。</summary>
        public int[] BuildAnimFinishedAtomIndices()
        {
            var list = new List<int>();
            for (var i = 0; i < atoms.Length; i++)
            {
                if (atoms[i].kind == MechConditionKind.BodyAnimFinished)
                {
                    list.Add(i);
                }
            }

            return list.ToArray();
        }
    }
}
