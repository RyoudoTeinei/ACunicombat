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
        private static Material headMaterial;
        private static Material trailMaterial;

        private GameObject streakObject;
        private TrailRenderer streakTrail;
        private float streakLifetime;

        /// <summary>
        /// <paramref name="streakSeconds"/> 决定亮痕长度：长度 ≈ 初速 × 该秒数。
        /// </summary>
        public void Build(
            Color hotColor,
            float headSize,
            float trailWidth,
            float streakSeconds)
        {
            BuildTrail(hotColor, trailWidth, streakSeconds);
            BuildHead(headSize);
        }

        private void BuildTrail(Color hotColor, float trailWidth, float streakSeconds)
        {
            // 亮痕单独挂在子物体上，命中时能与弹体分离后就地淡出。
            // 挂在弹体本体上时，Destroy(projectile) 会让整条 135 m 亮痕瞬间消失——
            // 已经在空中的光不会随弹头一起熄灭，那一下"啪"地不见是最明显的失真。
            streakObject = new GameObject("TracerStreak");
            streakObject.transform.SetParent(transform, false);
            streakSeconds = Mathf.Max(0.02f, streakSeconds);
            streakLifetime = streakSeconds;

            var trail = streakObject.AddComponent<TrailRenderer>();
            streakTrail = trail;
            trail.time = streakSeconds;
            // 亮痕是光而不是丝带：宽度从头部就快速收细，避免读成一条等宽的塑料条。
            trail.widthCurve = new AnimationCurve(
                new Keyframe(0f, 1f),
                new Keyframe(0.18f, 0.42f),
                new Keyframe(0.6f, 0.14f),
                new Keyframe(1f, 0f));
            trail.widthMultiplier = trailWidth;
            trail.minVertexDistance = 0.35f;
            trail.autodestruct = false;
            trail.alignment = LineAlignment.View;
            trail.textureMode = LineTextureMode.Stretch;

            // 曳光剂是燃烧的镁/锶复合物：亮端接近白，沿程冷却成橙红后熄灭。
            var gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(hotColor, 0f),
                    new GradientColorKey(new Color(1f, 0.55f, 0.18f), 0.45f),
                    new GradientColorKey(new Color(0.85f, 0.22f, 0.05f), 1f),
                },
                new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(0.6f, 0.4f),
                    new GradientAlphaKey(0f, 1f),
                });
            trail.colorGradient = gradient;

            trailMaterial ??= MechEffectResources.CreateAdditiveTrailMaterial(
                "AC6 Tracer Streak (Runtime)",
                new Color(2.6f, 1.5f, 0.7f, 1f));
            trail.sharedMaterial = trailMaterial;
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
            Destroy(streakObject, streakLifetime + 0.05f);
            streakObject = null;
            streakTrail = null;
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
            main.startLifetime = 0.05f;
            main.startSpeed = 0f;
            main.startSize = new ParticleSystem.MinMaxCurve(headSize * 0.85f, headSize * 1.15f);
            main.startColor = Color.white;
            main.gravityModifier = 0f;
            main.maxParticles = 16;

            var emission = system.emission;
            emission.rateOverTime = 90f;

            var shape = system.shape;
            shape.enabled = false;

            var thermal = child.AddComponent<MechEffectThermalAppearance>();
            thermal.Configure(MechEffectThermalAppearance.Signature.HotSaturating);

            headMaterial ??= MechEffectResources.CreateAdditiveParticleMaterial(
                "AC6 Tracer Head (Runtime)",
                new Color(9.5f, 6.8f, 3.4f, 1f));
            var renderer = child.GetComponent<ParticleSystemRenderer>();
            MechEffectResources.ConfigureUnlitRenderer(renderer, headMaterial);
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            thermal.Track(renderer);

            system.Play();
        }
    }
}
