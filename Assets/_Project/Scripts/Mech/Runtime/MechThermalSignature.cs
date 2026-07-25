using System.Collections.Generic;
using UnityEngine;

namespace ACunicombat.Mechs
{
    public enum MechThermalChannel
    {
        Skin = 0,
        Thrusters = 1,
        LeftWeapon = 2,
        RightWeapon = 3,
    }

    /// <summary>
    /// 机体红外特征（sim 层热状态，07-21 决策的热资源与红外可见性同源）：
    /// 按通道维护一阶热模型——推进器由主节流/AB 持续加热、QB 边沿注入脉冲、
    /// 武器开火注入脉冲，真空散热简化为向基线温度的指数回落（慢冷却=热惯性，
    /// 刚机动完的目标在热像里持续显眼）。温度经 MaterialPropertyBlock 写进
    /// 逐 renderer 的 _ThermalTemp，由热辐射 override shader 消费；将来的
    /// 红外导引头/IRST 读同一组 GetChannelTemperature，与玩家所见同源。
    /// 部件→通道映射基于 MechRuntimeState 的装配记录，装配完成后自动重绑。
    /// </summary>
    public sealed class MechThermalSignature : MonoBehaviour
    {
        private static readonly List<MechThermalSignature> registry = new();
        public static IReadOnlyList<MechThermalSignature> Registry => registry;

        private static readonly int ThermalTempId = Shader.PropertyToID("_ThermalTemp");
        private const int ChannelCount = 4;

        [SerializeField] private MechFlightMotionController motion;

        [Header("基线与幅度（开尔文）")]
        [SerializeField] private float skinBaselineK = 285f;
        [SerializeField] private float thrusterBaselineK = 300f;
        [Tooltip("满节流推进器相对基线的温升。")]
        [SerializeField] private float thrusterMaxRiseK = 900f;
        [SerializeField] private float weaponBaselineK = 285f;
        [Tooltip("单次开火注入的温度脉冲。")]
        [SerializeField] private float weaponShotImpulseK = 240f;
        [SerializeField] private float quickBoostImpulseK = 350f;
        [Tooltip("AB 激活时的等效推进驱动（可超过 1，比满节流更烫）。")]
        [SerializeField] private float assaultBoostDrive = 1.25f;

        [Header("时间常数（秒）")]
        [SerializeField] private float heatingTauSeconds = 1.2f;
        [Tooltip("真空辐射散热慢，冷却时间常数显著大于加热——这是热惯性玩法的来源。")]
        [SerializeField] private float coolingTauSeconds = 6f;
        [Tooltip("蒙皮从推进器传导升温的比例。")]
        [Range(0f, 1f)] [SerializeField] private float skinConductionFraction = 0.22f;

        private readonly float[] temperatures = new float[ChannelCount];
        private readonly float[] appliedTemperatures = new float[ChannelCount];
        private readonly List<Renderer>[] channelRenderers =
        {
            new List<Renderer>(), new List<Renderer>(), new List<Renderer>(), new List<Renderer>(),
        };

        private MechRuntimeState runtimeState;
        private MaterialPropertyBlock propertyBlock;
        private int boundPartCount = -1;
        private bool previousQuickBoostActive;

        public void Configure(MechFlightMotionController flightMotion)
        {
            motion = flightMotion;
        }

        public float GetChannelTemperature(MechThermalChannel channel) =>
            temperatures[(int)channel];

        /// <summary>供开火路径调用（玩家由 MechPlayerRig 接线，AI 将来走同一入口）。</summary>
        public void NotifyWeaponFired(bool leftHand)
        {
            var channel = leftHand ? MechThermalChannel.LeftWeapon : MechThermalChannel.RightWeapon;
            temperatures[(int)channel] += weaponShotImpulseK;
        }

        private void Awake()
        {
            if (motion == null) motion = GetComponent<MechFlightMotionController>();
            runtimeState = GetComponent<MechRuntimeState>();
            propertyBlock = new MaterialPropertyBlock();
            temperatures[(int)MechThermalChannel.Skin] = skinBaselineK;
            temperatures[(int)MechThermalChannel.Thrusters] = thrusterBaselineK;
            temperatures[(int)MechThermalChannel.LeftWeapon] = weaponBaselineK;
            temperatures[(int)MechThermalChannel.RightWeapon] = weaponBaselineK;
            for (var i = 0; i < ChannelCount; i++)
            {
                appliedTemperatures[i] = float.MinValue;
            }
        }

        private void OnEnable() => registry.Add(this);

        private void OnDisable() => registry.Remove(this);

        private void Update()
        {
            EnsureBinding();
            Integrate(Time.deltaTime);
            ApplyToRenderers();
        }

        private void Integrate(float dt)
        {
            var thrusterDrive = 0f;
            if (motion != null)
            {
                thrusterDrive = motion.MainThrottle;
                if (motion.AssaultBoostActive)
                {
                    thrusterDrive = Mathf.Max(thrusterDrive, assaultBoostDrive);
                }

                var quickBoostActive = motion.QuickBoostActive;
                if (quickBoostActive && !previousQuickBoostActive)
                {
                    temperatures[(int)MechThermalChannel.Thrusters] += quickBoostImpulseK;
                }

                previousQuickBoostActive = quickBoostActive;
            }

            StepChannel(MechThermalChannel.Thrusters,
                thrusterBaselineK + thrusterDrive * thrusterMaxRiseK, dt);
            StepChannel(MechThermalChannel.Skin,
                skinBaselineK + skinConductionFraction *
                (temperatures[(int)MechThermalChannel.Thrusters] - thrusterBaselineK), dt);
            StepChannel(MechThermalChannel.LeftWeapon, weaponBaselineK, dt);
            StepChannel(MechThermalChannel.RightWeapon, weaponBaselineK, dt);
        }

        private void StepChannel(MechThermalChannel channel, float targetK, float dt)
        {
            var index = (int)channel;
            var current = temperatures[index];
            var tau = targetK > current ? heatingTauSeconds : coolingTauSeconds;
            temperatures[index] = Mathf.Lerp(current, targetK, 1f - Mathf.Exp(-dt / Mathf.Max(0.01f, tau)));
        }

        /// <summary>部件在 showcase 控制器 Start() 里才装配完，按装配数变化惰性重绑。</summary>
        private void EnsureBinding()
        {
            if (runtimeState == null)
            {
                runtimeState = GetComponent<MechRuntimeState>();
            }

            var partCount = runtimeState != null ? runtimeState.AssembledParts.Count : 0;
            if (partCount == boundPartCount)
            {
                return;
            }

            boundPartCount = partCount;
            foreach (var group in channelRenderers)
            {
                group.Clear();
            }

            var thrusterRoots = new List<Transform>();
            var leftWeaponRoots = new List<Transform>();
            var rightWeaponRoots = new List<Transform>();
            if (runtimeState != null)
            {
                for (var i = 0; i < runtimeState.AssembledParts.Count; i++)
                {
                    var record = runtimeState.AssembledParts[i];
                    if (record?.instance == null)
                    {
                        continue;
                    }

                    switch (record.slot)
                    {
                        case MechPartSlot.Booster:
                            thrusterRoots.Add(record.instance.transform);
                            break;
                        case MechPartSlot.LeftHandWeapon:
                            leftWeaponRoots.Add(record.instance.transform);
                            break;
                        case MechPartSlot.RightHandWeapon:
                            rightWeaponRoots.Add(record.instance.transform);
                            break;
                    }
                }
            }

            foreach (var renderer in GetComponentsInChildren<Renderer>(true))
            {
                if (renderer is not MeshRenderer && renderer is not SkinnedMeshRenderer)
                {
                    continue; // 粒子等特效 renderer 不参与温度绑定。
                }

                var channel = MechThermalChannel.Skin;
                if (IsUnderAny(renderer.transform, thrusterRoots)) channel = MechThermalChannel.Thrusters;
                else if (IsUnderAny(renderer.transform, leftWeaponRoots)) channel = MechThermalChannel.LeftWeapon;
                else if (IsUnderAny(renderer.transform, rightWeaponRoots)) channel = MechThermalChannel.RightWeapon;
                channelRenderers[(int)channel].Add(renderer);
            }

            for (var i = 0; i < ChannelCount; i++)
            {
                appliedTemperatures[i] = float.MinValue; // 强制重推 MPB。
            }
        }

        private static bool IsUnderAny(Transform t, List<Transform> roots)
        {
            for (var i = 0; i < roots.Count; i++)
            {
                if (t.IsChildOf(roots[i]))
                {
                    return true;
                }
            }

            return false;
        }

        private void ApplyToRenderers()
        {
            for (var c = 0; c < ChannelCount; c++)
            {
                if (Mathf.Abs(temperatures[c] - appliedTemperatures[c]) < 0.25f)
                {
                    continue;
                }

                appliedTemperatures[c] = temperatures[c];
                var group = channelRenderers[c];
                for (var i = 0; i < group.Count; i++)
                {
                    var renderer = group[i];
                    if (renderer == null)
                    {
                        continue;
                    }

                    // 取回既有 block 再叠加，避免覆盖其他系统（涂装等）的 MPB 属性。
                    renderer.GetPropertyBlock(propertyBlock);
                    propertyBlock.SetFloat(ThermalTempId, temperatures[c]);
                    renderer.SetPropertyBlock(propertyBlock);
                }
            }
        }
    }
}
