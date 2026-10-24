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
    /// 按通道维护一阶热模型——推进器由实际主推输出/AB 持续加热、QB 边沿注入脉冲、
    /// 武器开火注入脉冲，真空散热简化为向基线温度的指数回落（慢冷却=热惯性，
    /// 刚机动完的目标在热像里持续显眼）。温度可选地经 MaterialPropertyBlock
    /// 写进逐 renderer 的 _ThermalTemp，由热辐射 override shader 消费；将来的
    /// 红外导引头/IRST 读取同一组 GetChannelTemperature，与玩家所见同源。
    /// 部件→通道映射基于 MechRuntimeState 的装配记录，装配完成后自动重绑。
    /// </summary>
    public sealed class MechThermalSignature : MonoBehaviour
    {
        private const string Ac6PartsApproxShaderName =
            "ACunicombat/AC6/Parts Approx";
        private const float RendererIntegrityIntervalSeconds = 0.5f;
        private static readonly List<MechThermalSignature> registry = new();
        public static IReadOnlyList<MechThermalSignature> Registry => registry;
        public static bool RendererTemperatureOutputEnabled { get; set; }

        private static readonly int ThermalTempId = Shader.PropertyToID("_ThermalTemp");
        private const int ChannelCount = 4;

        [SerializeField] private MechFlightMotionController motion;

        [Header("基线与幅度（开尔文）")]
        [SerializeField] private float skinBaselineK = 285f;
        [SerializeField] private float thrusterBaselineK = 300f;
        [Tooltip("主推进器达到额定输出时相对基线的温升。")]
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
        [Tooltip("标准测试机体的总散热能力。实际冷却时间按当前配装的总散热能力成反比缩放。")]
        [SerializeField] private float referenceHeatDissipationKw = 20400f;
        [SerializeField] private float minimumCoolingTauSeconds = 1.5f;
        [SerializeField] private float maximumCoolingTauSeconds = 90f;
        [Tooltip("蒙皮从推进器传导升温的比例。")]
        [Range(0f, 1f)] [SerializeField] private float skinConductionFraction = 0.22f;

        [Header("渲染输出")]
        [Tooltip("向原始机体 Renderer 写入温度属性。当前 AC6 多材质渲染路径不兼容，默认关闭；热状态仿真不受影响。")]
        [SerializeField] private bool writeRendererTemperaturePropertyBlocks;

        private readonly float[] temperatures = new float[ChannelCount];
        private readonly float[] appliedTemperatures = new float[ChannelCount];
        private readonly List<Renderer>[] channelRenderers =
        {
            new List<Renderer>(), new List<Renderer>(), new List<Renderer>(), new List<Renderer>(),
        };
        private readonly Dictionary<Renderer, MaterialPropertyBlock>
            rendererPropertyBlockBackups = new();

        private MechRuntimeState runtimeState;
        private MaterialPropertyBlock propertyBlock;
        private int boundPartCount = -1;
        private readonly Dictionary<string,float> equipmentTemperatures=new();
        private readonly List<string> equipmentSlots=new();
        private readonly Dictionary<Renderer,string> equipmentRenderers=new();
        public float GetEquipmentTemperature(string slotId)=>equipmentTemperatures.TryGetValue(slotId,out var value)?value:weaponBaselineK;
        public float InfraredSignatureMultiplier
        {
            get {float peak=Mathf.Max(GetChannelTemperature(MechThermalChannel.Skin),GetChannelTemperature(MechThermalChannel.Thrusters));
                foreach(var value in equipmentTemperatures.Values)peak=Mathf.Max(peak,value);
                return Mathf.Max(1,Mathf.Pow(peak/Mathf.Max(1,skinBaselineK),2));}
        }
        public void NotifyWeaponFired(MechEquipmentSlot slot)
        {
            EnsureBinding();
            if(!equipmentTemperatures.ContainsKey(slot.Id))return;
            var weapons=GetComponent<MechTestWeaponSystem>();
            equipmentTemperatures[slot.Id]=weapons!=null&&weapons.TryGetEquipmentHeatFraction(slot.Id,out float fraction)
                ?weaponBaselineK+900*fraction:equipmentTemperatures[slot.Id]+weaponShotImpulseK;
        }
        private bool previousQuickBoostActive;
        private float nextRendererIntegrityCheckTime;
        private bool loggedIncompatibleOverride;
        private bool rendererOutputWasEnabled;

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
            // RenderObjects override materials only inherit renderer-level
            // MPBs reliably. Restore the previous blocks when thermal view ends.
            writeRendererTemperaturePropertyBlocks = true;
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
            ClearLegacyRendererTemperatureOverrides();
        }

        private void OnEnable() => registry.Add(this);

        private void OnDisable()
        {
            registry.Remove(this);
            RestoreRendererTemperatureOverrides();
            rendererOutputWasEnabled = false;
        }

        private void Update()
        {
            EnsureBinding();
            Integrate(Time.deltaTime);
            var weapons=GetComponent<MechTestWeaponSystem>();
            foreach(var slot in equipmentSlots)
                equipmentTemperatures[slot]=weapons!=null&&weapons.TryGetEquipmentHeatFraction(slot,out float fraction)
                    ?weaponBaselineK+900*fraction
                    :Mathf.Lerp(equipmentTemperatures[slot],weaponBaselineK,1-Mathf.Exp(-Time.deltaTime/ResolveCoolingTauSeconds()));
            UpdateRendererTemperatureOutput();
            ProtectAc6RenderersFromPropertyBlocks();
        }

        private void Integrate(float dt)
        {
            var thrusterDrive = 0f;
            if (motion != null)
            {
                thrusterDrive = motion.MainEngineOutput;
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
            var tau = targetK > current ? heatingTauSeconds : ResolveCoolingTauSeconds();
            temperatures[index] = Mathf.Lerp(current, targetK, 1f - Mathf.Exp(-dt / Mathf.Max(0.01f, tau)));
        }

        private float ResolveCoolingTauSeconds()
        {
            float dissipationKw = runtimeState?.AggregatedStats != null
                ? runtimeState.AggregatedStats.heatDissipationMw * 1000f
                : referenceHeatDissipationKw;
            dissipationKw *= GetComponent<MechPartDamageController>()?.RoleEfficiency(MechPartSlot.Radiator) ?? 1;
            if (dissipationKw <= 0.01f)
            {
                return maximumCoolingTauSeconds;
            }

            float scaledTau = coolingTauSeconds *
                Mathf.Max(0.01f, referenceHeatDissipationKw) / dissipationKw;
            return Mathf.Clamp(
                scaledTau,
                Mathf.Max(0.01f, minimumCoolingTauSeconds),
                Mathf.Max(minimumCoolingTauSeconds, maximumCoolingTauSeconds));
        }

        /// <summary>部件在 showcase 控制器 Start() 里才装配完，按装配数变化惰性重绑。</summary>
        private void EnsureBinding()
        {
            if (runtimeState == null)
            {
                runtimeState = GetComponent<MechRuntimeState>();
            }

            var partCount = 17;
            if(runtimeState!=null)foreach(var record in runtimeState.AssembledParts)partCount=unchecked(partCount*31+record.InstanceId.GetHashCode());
            if (partCount == boundPartCount)
            {
                return;
            }

            boundPartCount = partCount;
            RestoreRendererTemperatureOverrides();equipmentTemperatures.Clear();equipmentRenderers.Clear();equipmentSlots.Clear();
            if(runtimeState!=null)foreach(var part in runtimeState.AssembledParts)
                if(part.part.Category==MechPartCategory.Weapon){equipmentTemperatures[part.SlotId]=weaponBaselineK;equipmentSlots.Add(part.SlotId);}
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

                var owner=runtimeState!=null?MechDamageGeometry.Owner(renderer,runtimeState):null;
                if(owner!=null&&equipmentTemperatures.ContainsKey(owner.SlotId))equipmentRenderers[renderer]=owner.SlotId;
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
                if (equipmentRenderers.Count==0 && Mathf.Abs(temperatures[c] - appliedTemperatures[c]) < 0.25f)
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
                    if (!rendererPropertyBlockBackups.ContainsKey(renderer))
                    {
                        var backup = new MaterialPropertyBlock();
                        renderer.GetPropertyBlock(backup);
                        rendererPropertyBlockBackups.Add(
                            renderer,
                            backup.isEmpty ? null : backup);
                    }

                    propertyBlock.Clear();
                    renderer.GetPropertyBlock(propertyBlock);
                    propertyBlock.SetFloat(ThermalTempId, equipmentRenderers.TryGetValue(renderer,out var slot)?GetEquipmentTemperature(slot):temperatures[c]);
                    renderer.SetPropertyBlock(propertyBlock);
                }
            }
        }

        private void UpdateRendererTemperatureOutput()
        {
            var outputEnabled =
                writeRendererTemperaturePropertyBlocks &&
                RendererTemperatureOutputEnabled;
            if (outputEnabled != rendererOutputWasEnabled)
            {
                rendererOutputWasEnabled = outputEnabled;
                if (outputEnabled)
                {
                    for (var i = 0; i < ChannelCount; i++)
                    {
                        appliedTemperatures[i] = float.MinValue;
                    }
                }
                else
                {
                    RestoreRendererTemperatureOverrides();
                }
            }

            if (outputEnabled)
            {
                ApplyToRenderers();
            }
        }

        private void RestoreRendererTemperatureOverrides()
        {
            foreach (var pair in rendererPropertyBlockBackups)
            {
                if (pair.Key != null)
                {
                    pair.Key.SetPropertyBlock(pair.Value);
                }
            }

            rendererPropertyBlockBackups.Clear();
        }

        private void ClearLegacyRendererTemperatureOverrides()
        {
            var renderers = GetComponentsInChildren<Renderer>(true);
            for (var i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer is not MeshRenderer &&
                    renderer is not SkinnedMeshRenderer)
                {
                    continue;
                }

                // Normal rendering does not consume thermal MPBs. Clear the
                // entire stale block because Unity has no per-property removal
                // API and a value of zero can itself black out legacy shaders.
                renderer.SetPropertyBlock(null);
            }
        }

        private void ProtectAc6RenderersFromPropertyBlocks()
        {
            if (rendererOutputWasEnabled)
            {
                return;
            }

            if (Time.unscaledTime < nextRendererIntegrityCheckTime)
            {
                return;
            }
            nextRendererIntegrityCheckTime =
                Time.unscaledTime + RendererIntegrityIntervalSeconds;

            var clearedCount = 0;
            var renderers = GetComponentsInChildren<Renderer>(true);
            for (var i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer == null ||
                    !UsesAc6PartsApproxShader(renderer))
                {
                    continue;
                }

                propertyBlock.Clear();
                renderer.GetPropertyBlock(propertyBlock);
                if (propertyBlock.isEmpty)
                {
                    continue;
                }

                renderer.SetPropertyBlock(null);
                clearedCount++;
            }

            if (clearedCount > 0 && !loggedIncompatibleOverride)
            {
                loggedIncompatibleOverride = true;
                Debug.LogWarning(
                    $"[MechThermalSignature] Cleared incompatible MPBs from " +
                    $"{clearedCount} AC6 renderers on {name}.",
                    this);
            }
        }

        private static bool UsesAc6PartsApproxShader(Renderer renderer)
        {
            var materials = renderer.sharedMaterials;
            for (var i = 0; i < materials.Length; i++)
            {
                var shader = materials[i] != null
                    ? materials[i].shader
                    : null;
                if (shader != null &&
                    shader.name == Ac6PartsApproxShaderName)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
