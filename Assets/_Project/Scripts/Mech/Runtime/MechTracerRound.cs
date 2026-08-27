using UnityEngine;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// 曳光弹视觉：炽白弹头 + 沿弹道拖出的加色亮痕。
    ///
    /// 取代原本的 FFX 弹丸特效 <c>ffx_6013101</c>——它的 Model 发射器引用 `s88060`，
    /// 而该模型 matxml 的 `g_DiffuseTexture` 为空，尺寸又是 `2.5×2.5×20`，
    /// 渲染出来是一根无贴图的纯红色加色长锥（与命中处那个"红色三角"同源）。
    ///
    /// 用 <c>TrailRenderer</c> 而非世界空间粒子来画亮痕：步枪初速 `1500 m/s`，
    /// 粒子要连成线需要每秒上千发射率，而拖尾是几何体，长度与速度无关且不吃粒子预算。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MechTracerRound : MonoBehaviour
    {
        public readonly struct VisualProfile
        {
            public VisualProfile(
                float headSizeMeters,
                float trailWidthMeters,
                float trailLifetimeSeconds,
                float targetTrailLengthMeters)
            {
                HeadSizeMeters = headSizeMeters;
                TrailWidthMeters = trailWidthMeters;
                TrailLifetimeSeconds = trailLifetimeSeconds;
                TargetTrailLengthMeters = targetTrailLengthMeters;
            }

            public float HeadSizeMeters { get; }
            public float TrailWidthMeters { get; }
            public float TrailLifetimeSeconds { get; }
            public float TargetTrailLengthMeters { get; }
        }

        private static Material headMaterial;
        private static Material trailMaterial;
        private static Material trailGlowMaterial;

        private GameObject streakObject;
        private TrailRenderer streakTrail;
        private TrailRenderer streakGlowTrail;
        private float streakLifetime;

        /// <summary>
        /// 光球尺寸由口径决定；拖尾先确定目标物理长度，再按弹速换算保留时间。
        /// </summary>
        public void Build(
            Color hotColor,
            float caliberMillimeters,
            float muzzleVelocityMetersPerSecond)
        {
            VisualProfile profile = ResolveVisualProfile(
                caliberMillimeters,
                muzzleVelocityMetersPerSecond);
            BuildTrail(
                hotColor,
                profile.TrailWidthMeters,
                profile.TrailLifetimeSeconds);
            BuildHead(profile.HeadSizeMeters);
        }

        public static VisualProfile ResolveVisualProfile(
            float caliberMillimeters,
            float muzzleVelocityMetersPerSecond)
        {
            float caliber = Mathf.Clamp(
                caliberMillimeters > 0f ? caliberMillimeters : 20f,
                6f,
                80f);
            float caliberScale = Mathf.Sqrt(caliber / 20f);
            float headSize = Mathf.Clamp(
                0.306f * caliberScale,
                0.17f,
                0.578f);
            float trailWidth = headSize;
            float targetTrailLength = Mathf.Lerp(
                24f,
                55f,
                Mathf.InverseLerp(7.62f, 57f, caliber));
            float speed = Mathf.Max(muzzleVelocityMetersPerSecond, 1f);
            float trailLifetime = Mathf.Clamp(targetTrailLength / speed, 0.012f, 0.09f);
            return new VisualProfile(
                headSize,
                trailWidth,
                trailLifetime,
                targetTrailLength);
        }

        private void BuildTrail(Color hotColor, float trailWidth, float streakSeconds)
        {
            // 亮痕单独挂在子物体上，命中时能与弹体分离后就地淡出。
            // 挂在弹体本体上时，Destroy(projectile) 会让整条 135 m 亮痕瞬间消失——
            // 已经在空中的光不会随弹头一起熄灭，那一下"啪"地不见是最明显的失真。
            streakObject = new GameObject("TracerStreak");
            streakObject.transform.SetParent(transform, false);
            streakSeconds = Mathf.Max(0.012f, streakSeconds);
            streakLifetime = streakSeconds;

            var columnWidthCurve = new AnimationCurve(
                new Keyframe(0f, 1f),
                new Keyframe(0.15f, 0.96f),
                new Keyframe(0.7f, 0.88f),
                new Keyframe(0.9f, 0.62f),
                new Keyframe(1f, 0f));

            // 前端需要和弹头形成连续的泛光区，随后迅速回落为橙红色亮痕。
            // 颜色允许超过 1，由战斗 Volume 的 Bloom 处理，不额外创建灯光或粒子层。
            Color frontGlow = new Color(
                hotColor.r * 2.4f,
                hotColor.g * 2.4f,
                hotColor.b * 2.4f,
                1f);
            Color frontFalloff = new Color(
                hotColor.r * 1.35f,
                hotColor.g * 1.35f,
                hotColor.b * 1.35f,
                1f);
            var gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(frontGlow, 0f),
                    new GradientColorKey(frontFalloff, 0.16f),
                    new GradientColorKey(hotColor, 0.32f),
                    new GradientColorKey(new Color(1f, 0.55f, 0.18f), 0.45f),
                    new GradientColorKey(new Color(0.85f, 0.22f, 0.05f), 1f),
                },
                new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(0.6f, 0.4f),
                    new GradientAlphaKey(0f, 1f),
                });

            var glowGradient = new Gradient();
            glowGradient.SetKeys(
                new[]
                {
                    new GradientColorKey(frontGlow, 0f),
                    new GradientColorKey(frontFalloff, 0.18f),
                    new GradientColorKey(hotColor, 0.42f),
                    new GradientColorKey(new Color(1f, 0.42f, 0.12f), 1f),
                },
                new[]
                {
                    new GradientAlphaKey(0.5f, 0f),
                    new GradientAlphaKey(0.28f, 0.42f),
                    new GradientAlphaKey(0f, 1f),
                });

            var glowObject = new GameObject("Glow");
            glowObject.transform.SetParent(streakObject.transform, false);
            var glowTrail = glowObject.AddComponent<TrailRenderer>();
            streakGlowTrail = glowTrail;
            ConfigureTrailRenderer(
                glowTrail,
                streakSeconds,
                trailWidth * 1.75f,
                columnWidthCurve,
                glowGradient,
                sortingOrder: 0);
            trailGlowMaterial ??= MechEffectResources.CreateAdditiveTrailMaterial(
                "AC6 Tracer Streak Glow (Runtime)",
                new Color(1.4f, 0.82f, 0.38f, 1f));
            glowTrail.sharedMaterial = trailGlowMaterial;

            var coreObject = new GameObject("Core");
            coreObject.transform.SetParent(streakObject.transform, false);
            var trail = coreObject.AddComponent<TrailRenderer>();
            streakTrail = trail;
            ConfigureTrailRenderer(
                trail,
                streakSeconds,
                trailWidth,
                columnWidthCurve,
                gradient,
                sortingOrder: 1);

            trailMaterial ??= MechEffectResources.CreateAdditiveTrailMaterial(
                "AC6 Tracer Streak (Runtime)",
                new Color(2.6f, 1.5f, 0.7f, 1f));
            trail.sharedMaterial = trailMaterial;
        }

        private static void ConfigureTrailRenderer(
            TrailRenderer trail,
            float lifetimeSeconds,
            float width,
            AnimationCurve widthCurve,
            Gradient colorGradient,
            int sortingOrder)
        {
            trail.time = lifetimeSeconds;
            trail.widthCurve = widthCurve;
            trail.widthMultiplier = width;
            trail.colorGradient = colorGradient;
            trail.minVertexDistance = 0.35f;
            trail.autodestruct = false;
            trail.alignment = LineAlignment.View;
            trail.textureMode = LineTextureMode.Stretch;
            trail.numCornerVertices = 2;
            trail.numCapVertices = 3;
            trail.sortingOrder = sortingOrder;
            trail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            trail.receiveShadows = false;
        }

        /// <summary>
        /// 弹体销毁（命中或超出射程）时把亮痕留在原地：停止延长，已有的段按
        /// <c>TrailRenderer.time</c> 自然老化淡出，随后回收这个游离物体。
        /// </summary>
        private void OnDestroy()
        {
            if (streakObject == null || streakTrail == null)
            {
                return;
            }

            // 场景卸载/退出播放时不要再重新挂接，否则会留下游离物体并触发告警。
            if (!Application.isPlaying || !gameObject.scene.isLoaded)
            {
                return;
            }

            streakObject.transform.SetParent(null, true);
            streakTrail.emitting = false;
            if (streakGlowTrail != null)
            {
                streakGlowTrail.emitting = false;
            }
            Destroy(streakObject, streakLifetime + 0.05f);
            streakObject = null;
            streakTrail = null;
            streakGlowTrail = null;
        }

        private void BuildHead(float headSize)
        {
            var child = new GameObject("TracerHead");
            child.transform.SetParent(transform, false);

            var system = child.AddComponent<ParticleSystem>();
            system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = system.main;
            // 本地空间 + 零初速：粒子始终贴在弹体上，构成一个稳定的炽亮弹头，
            // 而不是被 1500 m/s 的弹速甩成一串散点。
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.playOnAwake = true;
            main.loop = true;
            main.startLifetime = 0.04f;
            main.startSpeed = 0f;
            main.startSize = new ParticleSystem.MinMaxCurve(headSize * 0.85f, headSize * 1.15f);
            main.startColor = Color.white;
            main.gravityModifier = 0f;
            main.maxParticles = 8;

            var emission = system.emission;
            emission.rateOverTime = 55f;

            var shape = system.shape;
            shape.enabled = false;

            var thermal = child.AddComponent<MechEffectThermalAppearance>();
            thermal.Configure(MechEffectThermalAppearance.Signature.HotSaturating);

            headMaterial ??= MechEffectResources.CreateAdditiveParticleMaterial(
                "AC6 Tracer Head (Runtime)",
                new Color(6.48f, 4.59f, 2.295f, 1f));
            var renderer = child.GetComponent<ParticleSystemRenderer>();
            MechEffectResources.ConfigureUnlitRenderer(renderer, headMaterial);
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            thermal.Track(renderer);

            system.Play();
        }
    }
}
