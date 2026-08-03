using System.Collections.Generic;
using UnityEngine;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// 给程序化透明特效一个<b>显式</b>的热成像表现。
    ///
    /// 背景（`01_待开发事项/画面与渲染升级计划.md` 阶段 5a）：`AC6ThermalObjects` 这个
    /// RenderObjects pass 的 `RenderQueueType` 是 Opaque，只重画不透明队列；而
    /// `AC6ThermalScreen.shader` 对整个颜色缓冲取亮度当温度
    /// （`g = dot(color, luma)`）。两者相加的后果是：透明队列里的特效不是"在热像里看不见"，
    /// 而是<b>带着可见光外观被当成温度编码重映射</b>——一团冷尘埃只要画得亮就会被读成热源。
    /// <c>MechThermalPointEmitter</c> 显式跳过了非 Mesh renderer，粒子完全不在它的覆盖范围内。
    ///
    /// 这里采用升级计划列出的第二条路（特效不依赖主透明队列的偶然行为，自己声明热像表现），
    /// 而不是新增一个 Transparent 队列的 RenderObjects pass——后者会把稀薄的 RCS 喷流
    /// 渲染成实心温度块，且需要为混合状态单独准备覆盖材质。机体级大面积废气云出现时
    /// 可以再回到那条路，本组件不阻碍。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MechEffectThermalAppearance : MonoBehaviour
    {
        public enum Signature
        {
            /// <summary>燃气/白热碎屑等真实高温源：热像里应当过曝为纯白。</summary>
            HotSaturating,

            /// <summary>冷物质（漂浮尘埃等）：没有红外特征，热像里应当消失。</summary>
            ColdInvisible,
        }

        private static Material thermalSaturatedMaterial;

        [SerializeField] private Signature signature = Signature.HotSaturating;

        private readonly List<ParticleSystemRenderer> trackedRenderers = new();
        private readonly List<Material> visibleMaterials = new();
        private bool thermalApplied;

        public void Configure(Signature newSignature)
        {
            signature = newSignature;
            // 强制下一次 Update 重新求值，避免切换后停留在旧表现。
            thermalApplied = !MechThermalSignature.RendererTemperatureOutputEnabled;
        }

        /// <summary>由特效自身在构建完粒子系统后登记；不做 GetComponentsInChildren 猜测。</summary>
        public void Track(ParticleSystemRenderer renderer)
        {
            if (renderer == null || trackedRenderers.Contains(renderer))
            {
                return;
            }

            trackedRenderers.Add(renderer);
            visibleMaterials.Add(renderer.sharedMaterial);
        }

        private void Update()
        {
            var thermalOn = MechThermalSignature.RendererTemperatureOutputEnabled;
            if (thermalOn == thermalApplied)
            {
                return;
            }

            ApplyAppearance(thermalOn);
            thermalApplied = thermalOn;
        }

        private void OnDisable()
        {
            if (thermalApplied)
            {
                ApplyAppearance(false);
                thermalApplied = false;
            }
        }

        private void ApplyAppearance(bool thermalOn)
        {
            for (var i = 0; i < trackedRenderers.Count; i++)
            {
                var renderer = trackedRenderers[i];
                if (renderer == null)
                {
                    continue;
                }

                if (!thermalOn)
                {
                    renderer.enabled = true;
                    renderer.sharedMaterial = visibleMaterials[i];
                    continue;
                }

                if (signature == Signature.ColdInvisible)
                {
                    renderer.enabled = false;
                    continue;
                }

                renderer.enabled = true;
                renderer.sharedMaterial = GetThermalSaturatedMaterial() ?? visibleMaterials[i];
            }
        }

        /// <summary>
        /// 热像下的高温源代理材质：平白加色。屏幕 pass 取亮度，因此纯白即"过曝的热源"，
        /// 与 <c>AC6ThermalRadiance</c> 对饱和源的处理一致（见 `变更记录` 2026-07-30 的
        /// agcMaxWindowRadianceRatio 条目）。
        /// </summary>
        private static Material GetThermalSaturatedMaterial()
        {
            if (thermalSaturatedMaterial == null)
            {
                thermalSaturatedMaterial = MechEffectResources.CreateAdditiveParticleMaterial(
                    "AC6 Effect Thermal Saturated (Runtime)",
                    new Color(1.6f, 1.6f, 1.6f, 1f));
            }

            return thermalSaturatedMaterial;
        }
    }
}
