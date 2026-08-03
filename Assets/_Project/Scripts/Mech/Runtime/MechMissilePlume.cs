using UnityEngine;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// 导弹主发动机的真空羽焰。由 <c>MechTestProjectile.SetMotorVisual</c> 驱动——
    /// 冷发射 / RCS 转向 / 点火 / 燃尽的阶段判定早已就绪，本组件只负责"烧起来长什么样"。
    ///
    /// 形态依据 `01_待开发事项/画面与渲染升级计划.md` 阶段 5c：
    /// - <b>欠膨胀自由膨胀</b>。出口压远高于环境压（真空为 0），羽流一离开喷口就急剧张开，
    ///   视觉是<b>极短极亮的核心 + 极宽极暗的泛光罩</b>，几何是渐扩旋成体而非直锥。
    /// - <b>没有马赫环</b>。激波菱形来自流场被环境压再压缩，硬真空无环境压、无再压缩，
    ///   因此不存在——那是大气发动机的特征，不要放进太空羽焰。
    /// - <b>固体发动机</b>。铝掺杂推进剂产生氧化铝凝相颗粒，羽焰明亮致密偏白橙，
    ///   并拖出大量烟；烟在真空中弹道式膨胀且不消散，只持续变稀。
    ///
    /// 喷口点光源：Forward+ 的集群光循环取消了逐物体附加光源上限（升级计划 5d），
    /// 导弹掠过时把机体真正照亮，这个动态照明比羽焰本身更能卖出"有东西烧着飞过去"。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MechMissilePlume : MonoBehaviour
    {
        private static Material coreMaterial;
        private static Material shroudMaterial;
        private static Material sootMaterial;

        private ParticleSystem coreSystem;
        private ParticleSystem shroudSystem;
        private ParticleSystem sootSystem;
        private Light nozzleLight;
        private float nozzleLightIntensity;
        private bool burning;

        /// <summary>
        /// 在导弹本体上构建羽焰。喷口位置由实际挂载的弹体模型量出，
        /// <paramref name="fallbackNozzleOffset"/> 只在没有可测模型时兜底（单位：米）。
        /// </summary>
        public void Build(float fallbackNozzleOffset, float scale)
        {
            if (!Application.isPlaying || coreSystem != null)
            {
                return;
            }

            scale = Mathf.Max(0.1f, scale);
            var nozzleOffset = MeasureTailOffsetMeters(fallbackNozzleOffset);

            var root = new GameObject("Missile_Plume");
            root.transform.SetParent(transform, false);
            // 羽焰朝弹体尾部喷出。
            root.transform.localRotation = Quaternion.LookRotation(Vector3.back);
            root.transform.localScale = new Vector3(
                SafeReciprocal(transform.localScale.x),
                SafeReciprocal(transform.localScale.y),
                SafeReciprocal(transform.localScale.z));
            // 弹体 localScale 是非均匀的 (0.18, 0.18, 0.8)，所以世界米不能直接当 localPosition 用；
            // 经弹体自身的逆变换换算后，倒数缩放的 root 内 1 单位才等于 1 世界米。
            root.transform.localPosition =
                transform.InverseTransformVector(-transform.forward * nozzleOffset);

            var thermal = root.AddComponent<MechEffectThermalAppearance>();
            thermal.Configure(MechEffectThermalAppearance.Signature.HotSaturating);

            thermal.Track(BuildCore(root.transform, scale));
            thermal.Track(BuildShroud(root.transform, scale));
            thermal.Track(BuildSoot(root.transform, scale));
            BuildNozzleLight(root.transform, scale);

            SetBurning(false);
        }

        /// <summary>由 <c>SetMotorVisual</c> 调用；点火与熄火的时机完全由弹道状态机决定。</summary>
        public void SetBurning(bool active)
        {
            burning = active;

            SetEmitting(coreSystem, active);
            SetEmitting(shroudSystem, active);
            SetEmitting(sootSystem, active);

            if (nozzleLight != null)
            {
                nozzleLight.enabled = active;
            }
        }

        private void LateUpdate()
        {
            if (nozzleLight == null || !burning)
            {
                return;
            }

            // 固体发动机不可节流，燃烧室压力的随机脉动是它唯一的亮度变化来源。
            var flicker = 0.9f + 0.1f * Mathf.PerlinNoise(Time.time * 26f, 0.37f);
            nozzleLight.intensity = nozzleLightIntensity * flicker;
        }

        private ParticleSystemRenderer BuildCore(Transform parent, float scale)
        {
            coreSystem = CreateChildSystem(
                parent,
                "Core",
                ParticleSystemSimulationSpace.Local,
                out var renderer);

            var main = coreSystem.main;
            // 核心极短：欠膨胀羽流的高亮区只存在于喷口紧后方很短一段。
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.035f, 0.07f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(scale * 26f, scale * 40f);
            main.startSize = new ParticleSystem.MinMaxCurve(scale * 0.42f, scale * 0.62f);
            main.maxParticles = 420;

            // 排放率要足够高，相邻粒子才会互相重叠成连续火焰而不是可数的亮点。
            var emission = coreSystem.emission;
            emission.rateOverTime = 520f;

            var shape = coreSystem.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 9f;
            shape.radius = scale * 0.1f;

            var sizeOverLifetime = coreSystem.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(
                1f,
                new AnimationCurve(
                    new Keyframe(0f, 0.55f),
                    new Keyframe(0.4f, 1f),
                    new Keyframe(1f, 0.72f)));

            var colorOverLifetime = coreSystem.colorOverLifetime;
            colorOverLifetime.enabled = true;
            colorOverLifetime.color = new ParticleSystem.MinMaxGradient(
                BuildGradient(
                    new[]
                    {
                        (0f, new Color(1f, 0.98f, 0.95f)),
                        (0.5f, new Color(1f, 0.93f, 0.76f)),
                        (1f, new Color(1f, 0.78f, 0.45f)),
                    },
                    new[] { (0f, 1f), (0.55f, 0.9f), (1f, 0f) }));

            coreMaterial ??= MechEffectResources.CreateAdditiveParticleMaterial(
                "AC6 Missile Plume Core (Runtime)",
                new Color(5.6f, 4.9f, 3.9f, 1f));
            MechEffectResources.ConfigureUnlitRenderer(renderer, coreMaterial);
            // 沿流向拉伸：燃气是流动的柱体，圆点即使叠得再密也读作颗粒。
            // 本地空间下粒子速度只有排气速度、不含弹体速度，所以拉伸长度不会随导弹加速失控
            // ——这是火焰层用本地空间的又一个附带好处。
            renderer.renderMode = ParticleSystemRenderMode.Stretch;
            renderer.lengthScale = 2.6f;
            renderer.velocityScale = 0.01f;
            return renderer;
        }

        private ParticleSystemRenderer BuildShroud(Transform parent, float scale)
        {
            shroudSystem = CreateChildSystem(
                parent,
                "Shroud",
                ParticleSystemSimulationSpace.Local,
                out var renderer);

            var main = shroudSystem.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.1f, 0.18f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(scale * 12f, scale * 22f);
            main.startSize = new ParticleSystem.MinMaxCurve(scale * 0.5f, scale * 0.9f);
            main.maxParticles = 320;

            var emission = shroudSystem.emission;
            emission.rateOverTime = 300f;

            // 泛光罩才是真空羽焰的辨识特征：张角远大于大气发动机的窄锥。
            var shape = shroudSystem.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 47f;
            shape.radius = scale * 0.16f;

            // 渐扩旋成体：尺寸沿寿命持续放大，越远越宽越暗。
            var sizeOverLifetime = shroudSystem.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(
                1f,
                new AnimationCurve(
                    new Keyframe(0f, 0.5f),
                    new Keyframe(1f, 2.9f)));

            var colorOverLifetime = shroudSystem.colorOverLifetime;
            colorOverLifetime.enabled = true;
            colorOverLifetime.color = new ParticleSystem.MinMaxGradient(
                BuildGradient(
                    new[]
                    {
                        (0f, new Color(1f, 0.86f, 0.6f)),
                        (0.45f, new Color(1f, 0.62f, 0.3f)),
                        (1f, new Color(0.6f, 0.26f, 0.1f)),
                    },
                    new[] { (0f, 0.5f), (0.3f, 0.34f), (1f, 0f) }));

            shroudMaterial ??= MechEffectResources.CreateAdditiveParticleMaterial(
                "AC6 Missile Plume Shroud (Runtime)",
                new Color(1.7f, 1.15f, 0.72f, 1f));
            MechEffectResources.ConfigureUnlitRenderer(renderer, shroudMaterial);
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            return renderer;
        }

        private ParticleSystemRenderer BuildSoot(Transform parent, float scale)
        {
            sootSystem = CreateChildSystem(
                parent,
                "AluminaSmoke",
                ParticleSystemSimulationSpace.World,
                out var renderer);

            var main = sootSystem.main;
            // 氧化铝凝相颗粒被留在弹道上，寿命远长于燃气本身。
            // 但寿命不能太长：导弹 ~300 m/s 时 3 s 就是 900 m 的烟柱，粒子会稀疏到成为
            // 一串可数的点——同样是烟花观感。缩短寿命并大幅提高排放率，让相邻烟团重叠。
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.9f, 2f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(scale * 2f, scale * 6f);
            main.startSize = new ParticleSystem.MinMaxCurve(scale * 0.5f, scale * 0.95f);
            main.maxParticles = 700;

            var emission = sootSystem.emission;
            emission.rateOverTime = 240f;

            var shape = sootSystem.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 22f;
            shape.radius = scale * 0.12f;

            // 真空中没有风、没有扩散介质：烟云弹道式膨胀，不消散，只持续变稀。
            var sizeOverLifetime = sootSystem.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(
                1f,
                new AnimationCurve(
                    new Keyframe(0f, 0.6f),
                    new Keyframe(1f, 4.2f)));

            var colorOverLifetime = sootSystem.colorOverLifetime;
            colorOverLifetime.enabled = true;
            colorOverLifetime.color = new ParticleSystem.MinMaxGradient(
                BuildGradient(
                    new[]
                    {
                        (0f, new Color(0.95f, 0.82f, 0.66f)),
                        (0.25f, new Color(0.72f, 0.68f, 0.64f)),
                        (1f, new Color(0.42f, 0.42f, 0.44f)),
                    },
                    new[] { (0f, 0.3f), (0.2f, 0.22f), (1f, 0f) }));

            sootMaterial ??= MechEffectResources.CreateAdditiveParticleMaterial(
                "AC6 Missile Alumina Smoke (Runtime)",
                new Color(0.62f, 0.58f, 0.54f, 1f));
            MechEffectResources.ConfigureUnlitRenderer(renderer, sootMaterial);
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            return renderer;
        }

        private void BuildNozzleLight(Transform parent, float scale)
        {
            var lightObject = new GameObject("NozzleLight");
            lightObject.transform.SetParent(parent, false);

            nozzleLight = lightObject.AddComponent<Light>();
            nozzleLight.type = LightType.Point;
            nozzleLight.color = new Color(1f, 0.79f, 0.52f);
            nozzleLight.range = Mathf.Lerp(18f, 60f, Mathf.InverseLerp(0.3f, 1.5f, scale));
            nozzleLight.shadows = LightShadows.None;
            nozzleLightIntensity = Mathf.Lerp(35f, 140f, Mathf.InverseLerp(0.3f, 1.5f, scale));
            nozzleLight.intensity = nozzleLightIntensity;
            nozzleLight.enabled = false;
        }

        private static ParticleSystem CreateChildSystem(
            Transform parent,
            string childName,
            ParticleSystemSimulationSpace space,
            out ParticleSystemRenderer renderer)
        {
            var child = new GameObject(childName);
            child.transform.SetParent(parent, false);

            var system = child.AddComponent<ParticleSystem>();
            system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = system.main;
            // 模拟空间按层区分，这是"羽焰"与"烟花"的分界：
            // 导弹以数百 m/s 飞行，世界空间粒子一生成就被甩在身后，短寿命的火焰层会散成
            // 一串互不相连的亮点——正是烟花观感的来源。而燃气相对喷口以 ~2000 m/s 排出、
            // 一两米内就稀释掉，物理上可见火焰本来就是"贴在喷口上"的，所以火焰层用本地空间。
            // 只有烟真正被留在弹道上，那一层才该用世界空间。
            main.simulationSpace = space;
            main.playOnAwake = false;
            main.loop = true;
            main.gravityModifier = 0f;
            main.startColor = Color.white;

            renderer = child.GetComponent<ParticleSystemRenderer>();
            system.Play();
            return system;
        }

        private static void SetEmitting(ParticleSystem system, bool active)
        {
            if (system == null)
            {
                return;
            }

            if (active)
            {
                system.Play(false);
            }
            else
            {
                // 只停排放：已经喷出去的燃气与烟应当继续走完自己的寿命。
                system.Stop(false, ParticleSystemStopBehavior.StopEmitting);
            }
        }

        private static Gradient BuildGradient(
            (float time, Color color)[] colorKeys,
            (float time, float alpha)[] alphaKeys)
        {
            var gradient = new Gradient();
            var colors = new GradientColorKey[colorKeys.Length];
            for (var i = 0; i < colorKeys.Length; i++)
            {
                colors[i] = new GradientColorKey(colorKeys[i].color, colorKeys[i].time);
            }

            var alphas = new GradientAlphaKey[alphaKeys.Length];
            for (var i = 0; i < alphaKeys.Length; i++)
            {
                alphas[i] = new GradientAlphaKey(alphaKeys[i].alpha, alphaKeys[i].time);
            }

            gradient.SetKeys(colors, alphas);
            return gradient;
        }

        /// <summary>
        /// 量出弹体尾端相对弹体原点、沿 -forward 的距离（世界米）。
        /// 实际挂载的模型被缩放到 4 m / 3.6 m 并以包围盒中心对齐原点
        /// （<c>MechTestWeaponSystem.TryAttachMissileVisual</c>），所以尾端位置不是常量；
        /// 写死偏移会让羽焰从弹体中段喷出来。
        /// </summary>
        private float MeasureTailOffsetMeters(float fallback)
        {
            var renderers = GetComponentsInChildren<Renderer>(true);
            var origin = transform.position;
            var forward = transform.forward;
            var tail = 0f;
            var measured = false;

            for (var i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                // 只量实体弹身；拖尾与粒子的包围盒会跟着效果本身扩张。
                if (renderer is not MeshRenderer && renderer is not SkinnedMeshRenderer)
                {
                    continue;
                }

                var bounds = renderer.bounds;
                var center = bounds.center;
                var extents = bounds.extents;
                for (var corner = 0; corner < 8; corner++)
                {
                    var point = center + new Vector3(
                        (corner & 1) == 0 ? -extents.x : extents.x,
                        (corner & 2) == 0 ? -extents.y : extents.y,
                        (corner & 4) == 0 ? -extents.z : extents.z);
                    var along = Vector3.Dot(point - origin, forward);
                    if (!measured || along < tail)
                    {
                        tail = along;
                        measured = true;
                    }
                }
            }

            // tail 为负值（在原点后方）；返回正的"向后多远"。
            return measured && tail < -0.05f ? -tail : Mathf.Max(0.05f, fallback);
        }

        private static float SafeReciprocal(float value) =>
            Mathf.Abs(value) > 0.0001f ? 1f / value : 1f;
    }
}
