using UnityEngine;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// 程序化受击特效：白热闪光 + 一帧性点光源 + 无阻力自转碎片 + 稀薄蒸气。
    ///
    /// 为什么不沿用 FFX 栈：<c>MechTestWeaponSystem.SpawnImpact</c> 的 FFX 分支只有步枪传了
    /// `impactFfxId`，火炮与全部导弹都没有，落到白色 Sphere 兜底；而且 FFX 资产来自
    /// `Tools/LocalOut`（.gitignore 内，机器本地、不可复现），内容也只有闪光、没有碎片。
    /// 本组件是可提交的基线，FFX 命中闪光在本机存在时仍会照常叠加。
    ///
    /// 真空取向（对应 `01_待开发事项/画面与渲染升级计划.md` 阶段 5c 的同类物理）：
    /// - 没有氧化剂就没有火焰。"火光"实为白热碎屑与蒸发金属蒸气的黑体辐射，
    ///   因此走白 → 黄白 → 暗橙的<b>降色温</b>曲线，而不是大气火焰的橙红。
    /// - 没有空气阻力。碎片直线匀速飞出、自转速率恒定，既不减速也不飘落。
    /// - 没有扩散介质。蒸气弹道式膨胀后只会持续变稀，不会"散掉"。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MechImpactEffect : MonoBehaviour
    {
        private const float FlashSeconds = 0.12f;
        private const float LightSeconds = 0.1f;

        private static Material flashMaterial;
        private static Material vaporMaterial;
        private static Material debrisMaterial;

        private Light impactLight;
        private float lightPeakIntensity;
        private float elapsed;
        private float totalLifetime;

        /// <summary>
        /// 在命中点生成一次性受击特效。<paramref name="normal"/> 为受击面法线（碎片喷射轴）。
        /// </summary>
        public static void Spawn(Vector3 position, Vector3 normal, float scale)
        {
            if (!Application.isPlaying)
            {
                return;
            }

            var axis = normal.sqrMagnitude > 0.0001f ? normal.normalized : Vector3.up;
            scale = Mathf.Max(0.15f, scale);

            var root = new GameObject("AC6_ImpactEffect");
            // 朝向受击面法线：碎片与蒸气都沿这个轴抛撒。
            root.transform.SetPositionAndRotation(
                position,
                Quaternion.LookRotation(axis));
            root.AddComponent<MechImpactEffect>().Build(scale);
        }

        private void Build(float scale)
        {
            var thermal = gameObject.AddComponent<MechEffectThermalAppearance>();
            thermal.Configure(MechEffectThermalAppearance.Signature.HotSaturating);

            var debrisLifetime = Mathf.Lerp(1.4f, 3.2f, Mathf.InverseLerp(0.3f, 1.4f, scale));
            var vaporLifetime = Mathf.Lerp(0.9f, 2.2f, Mathf.InverseLerp(0.3f, 1.4f, scale));

            thermal.Track(BuildFlash(scale));
            thermal.Track(BuildVapor(scale, vaporLifetime));
            BuildDebris(scale, debrisLifetime);
            BuildLight(scale);

            totalLifetime = Mathf.Max(debrisLifetime, vaporLifetime) + 0.3f;
        }

        private void Update()
        {
            elapsed += Time.deltaTime;

            if (impactLight != null)
            {
                if (elapsed >= LightSeconds)
                {
                    Destroy(impactLight.gameObject);
                    impactLight = null;
                }
                else
                {
                    // 冷却是辐射主导的快速衰减，不是线性熄灭。
                    var remaining = 1f - elapsed / LightSeconds;
                    impactLight.intensity = lightPeakIntensity * remaining * remaining;
                }
            }

            if (elapsed >= totalLifetime)
            {
                Destroy(gameObject);
            }
        }

        private ParticleSystemRenderer BuildFlash(float scale)
        {
            var system = CreateChildSystem("Flash", out var renderer);

            var main = system.main;
            main.duration = FlashSeconds;
            main.loop = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(FlashSeconds * 0.6f, FlashSeconds);
            main.startSpeed = 0f;
            main.startSize = new ParticleSystem.MinMaxCurve(scale * 1.6f, scale * 2.4f);
            main.startColor = Color.white;

            var emission = system.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 2) });

            var shape = system.shape;
            shape.enabled = false;

            var sizeOverLifetime = system.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(
                1f,
                new AnimationCurve(
                    new Keyframe(0f, 0.35f),
                    new Keyframe(0.25f, 1f),
                    new Keyframe(1f, 1.25f)));

            var colorOverLifetime = system.colorOverLifetime;
            colorOverLifetime.enabled = true;
            colorOverLifetime.color = MechEffectResources.CreateBlackbodyCoolingGradient();

            // HDR 峰值明确推过战斗 Volume 的 Bloom 阈值 1.1，闪光才会泛开并喂到镜头光晕。
            flashMaterial ??= MechEffectResources.CreateAdditiveParticleMaterial(
                "AC6 Impact Flash (Runtime)",
                new Color(4.2f, 3.6f, 2.8f, 1f));
            MechEffectResources.ConfigureUnlitRenderer(renderer, flashMaterial);
            renderer.renderMode = ParticleSystemRenderMode.Billboard;

            system.Play();
            return renderer;
        }

        private ParticleSystemRenderer BuildVapor(float scale, float lifetime)
        {
            var system = CreateChildSystem("Vapor", out var renderer);

            var main = system.main;
            main.duration = 0.1f;
            main.loop = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(lifetime * 0.6f, lifetime);
            main.startSpeed = new ParticleSystem.MinMaxCurve(scale * 1.5f, scale * 5f);
            main.startSize = new ParticleSystem.MinMaxCurve(scale * 0.5f, scale * 1.1f);
            main.startColor = Color.white;
            main.gravityModifier = 0f;

            var emission = system.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)Mathf.RoundToInt(10f + scale * 14f)) });

            var shape = system.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 62f;
            shape.radius = scale * 0.1f;

            // 真空中没有扩散介质：蒸气弹道式膨胀，只会越来越稀，不会消散。
            // 因此尺寸单调增长而 alpha 缓慢衰减，两者叠加读作"变稀"。
            var sizeOverLifetime = system.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(
                1f,
                new AnimationCurve(
                    new Keyframe(0f, 0.4f),
                    new Keyframe(1f, 3.1f)));

            var colorOverLifetime = system.colorOverLifetime;
            colorOverLifetime.enabled = true;
            colorOverLifetime.color = MechEffectResources.CreateBlackbodyCoolingGradient(0.34f);

            vaporMaterial ??= MechEffectResources.CreateAdditiveParticleMaterial(
                "AC6 Impact Vapor (Runtime)",
                new Color(1.35f, 1.05f, 0.8f, 1f));
            MechEffectResources.ConfigureUnlitRenderer(renderer, vaporMaterial);
            renderer.renderMode = ParticleSystemRenderMode.Billboard;

            system.Play();
            return renderer;
        }

        private void BuildDebris(float scale, float lifetime)
        {
            var system = CreateChildSystem("Debris", out var renderer);

            var main = system.main;
            main.duration = 0.1f;
            main.loop = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(lifetime * 0.45f, lifetime);
            main.startSpeed = new ParticleSystem.MinMaxCurve(scale * 6f, scale * 22f);
            main.startSize = new ParticleSystem.MinMaxCurve(scale * 0.05f, scale * 0.2f);
            main.startColor = new Color(0.62f, 0.66f, 0.72f, 1f);
            // 真空：无重力（本作场景）也无空气阻力，碎片保持初速直线飞行。
            main.gravityModifier = 0f;
            main.startRotation3D = true;
            main.startRotationX = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startRotationY = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startRotationZ = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);

            var emission = system.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)Mathf.RoundToInt(8f + scale * 18f)) });

            var shape = system.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            // 沿受击面法线的半球状抛撒。
            shape.angle = 74f;
            shape.radius = scale * 0.08f;

            // 恒定自转（无阻尼）。翻滚时各面对主光的朝向不断变化，产生的随机反光闪烁
            // 本身就是"这是一片金属"的视觉线索——比给碎片自发光更可信也更省。
            var rotationOverLifetime = system.rotationOverLifetime;
            rotationOverLifetime.enabled = true;
            rotationOverLifetime.separateAxes = true;
            rotationOverLifetime.x = new ParticleSystem.MinMaxCurve(-9f, 9f);
            rotationOverLifetime.y = new ParticleSystem.MinMaxCurve(-9f, 9f);
            rotationOverLifetime.z = new ParticleSystem.MinMaxCurve(-9f, 9f);

            debrisMaterial ??= MechEffectResources.CreateLitDebrisMaterial(
                "AC6 Impact Debris (Runtime)",
                new Color(0.58f, 0.62f, 0.68f, 1f));
            renderer.renderMode = ParticleSystemRenderMode.Mesh;
            renderer.mesh = MechEffectResources.DebrisShardMesh;
            renderer.enableGPUInstancing = true;
            renderer.sharedMaterial = debrisMaterial;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            system.Play();
        }

        private void BuildLight(float scale)
        {
            var lightObject = new GameObject("ImpactLight");
            lightObject.transform.SetParent(transform, false);

            impactLight = lightObject.AddComponent<Light>();
            impactLight.type = LightType.Point;
            impactLight.color = new Color(1f, 0.85f, 0.62f);
            impactLight.range = Mathf.Lerp(12f, 45f, Mathf.InverseLerp(0.3f, 1.4f, scale));
            impactLight.shadows = LightShadows.None;
            // Forward+ 的集群光循环取消了逐物体附加光源上限，命中点光源现在能真正照到机体，
            // 这是 `AC6PartsApprox` 补上 _CLUSTER_LIGHT_LOOP 之后才解锁的能力。
            lightPeakIntensity = Mathf.Lerp(90f, 420f, Mathf.InverseLerp(0.3f, 1.4f, scale));
            impactLight.intensity = lightPeakIntensity;
        }

        private ParticleSystem CreateChildSystem(
            string childName,
            out ParticleSystemRenderer renderer)
        {
            var child = new GameObject(childName);
            child.transform.SetParent(transform, false);

            var system = child.AddComponent<ParticleSystem>();
            system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = system.main;
            // 世界空间：碎片与蒸气一旦离开命中点就不该再跟着任何东西走。
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.playOnAwake = false;
            main.loop = false;
            main.maxParticles = 128;
            main.gravityModifier = 0f;

            renderer = child.GetComponent<ParticleSystemRenderer>();
            return system;
        }
    }
}
