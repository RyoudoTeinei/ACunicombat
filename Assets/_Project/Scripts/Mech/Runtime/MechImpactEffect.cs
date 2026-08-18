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
        private static Material fireballMaterial;
        private static Material smokeMaterial;
        private static Material sparkMaterial;
        private static Material debrisMaterial;
        private static Material emberMaterial;
        private static Material emberTrailMaterial;
        private static Material detonationHaloMaterial;

        private Light impactLight;
        private float lightPeakIntensity;
        private float lightLifetime = LightSeconds;
        private bool explosionLight;
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

        /// <summary>
        /// 生成导弹战斗部爆炸。与表面命中特效不同，爆炸产物从引爆点向所有方向膨胀。
        /// </summary>
        public static GameObject SpawnExplosion(
            Vector3 position,
            Vector3 travelDirection,
            float scale,
            int renderLayer = -1)
        {
            if (!Application.isPlaying)
            {
                return null;
            }

            var axis = travelDirection.sqrMagnitude > 0.0001f
                ? travelDirection.normalized
                : Vector3.forward;
            scale = Mathf.Max(1f, scale);

            var rotation = Quaternion.LookRotation(axis);
            var root = new GameObject("AC6_MissileExplosion");
            if (renderLayer >= 0 && renderLayer <= 31)
            {
                root.layer = renderLayer;
            }
            root.transform.SetPositionAndRotation(position, rotation);
            var gpuVfxSpawned = MechMissileExplosionVfx.TryAttach(root, scale);
            root.AddComponent<MechImpactEffect>().BuildExplosion(scale, gpuVfxSpawned);
            return root;
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

        private void BuildExplosion(float scale, bool gpuVfxSpawned)
        {
            var thermal = gameObject.AddComponent<MechEffectThermalAppearance>();
            thermal.Configure(MechEffectThermalAppearance.Signature.HotSaturating);

            var debrisLifetime = Mathf.Lerp(1.15f, 2.1f, Mathf.InverseLerp(1f, 4f, scale));
            const float smokeLifetime = 2.35f;
            // 与 BuildVacuumEmbers 里 startLifetime 的上界 + 拖尾寿命上界保持一致。
            const float emberLifetime = 2.9f + 0.26f;

            thermal.Track(BuildFlash(scale * 1.05f, distanceReadable: true));
            // 起爆瞬间的辉光层。单靠核心闪光做不出"闪一下"的冲击感：核心只有几米，
            // 在屏幕上占不了多少面积；真正制造冲击的是这一层大而极短、低 alpha 的
            // 光晕，它的作用是把 Bloom 一次性冲开，让整个画面亮一下再立刻收回。
            thermal.Track(BuildDetonationHalo(scale));
            if (!gpuVfxSpawned)
            {
                // GPU flipbook blending is the primary path. The classic particle version
                // exists only for hardware without compute/VFX Graph support.
                thermal.Track(BuildFireballCluster(scale));
                thermal.Track(BuildExplosionSmoke(scale, smokeLifetime));
                thermal.Track(BuildExplosionSparks(scale));
            }
            // 余烬两条路径都要建：它不是无 compute 硬件的兜底，而是真空爆炸的视觉主体。
            // 火球在真空里没有助燃剂、约 0.3 s 就灭了，之后画面里还在动的就只剩它和碎片。
            thermal.Track(BuildVacuumEmbers(scale));
            BuildDebris(scale, debrisLifetime, radial: true);
            BuildLight(scale, explosion: true);

            totalLifetime = Mathf.Max(
                gpuVfxSpawned ? 3.35f : Mathf.Max(debrisLifetime, smokeLifetime) + 0.35f,
                emberLifetime + 0.2f);
        }

        private ParticleSystemRenderer BuildFireballCluster(float scale)
        {
            var system = CreateChildSystem("FireballCluster", out var renderer);
            var usesFlipbook = MechEffectResources.MissileExplosionFlipbook != null;

            var main = system.main;
            main.duration = usesFlipbook ? 0.04f : 0.3f;
            main.loop = false;
            main.startLifetime = usesFlipbook
                ? new ParticleSystem.MinMaxCurve(0.72f)
                : new ParticleSystem.MinMaxCurve(0.62f, 1.05f);
            main.startSpeed = usesFlipbook
                ? new ParticleSystem.MinMaxCurve(0f)
                : new ParticleSystem.MinMaxCurve(scale * 0.22f, scale * 0.72f);
            main.startSize = usesFlipbook
                ? new ParticleSystem.MinMaxCurve(scale * 3.15f)
                : new ParticleSystem.MinMaxCurve(scale * 0.72f, scale * 1.42f);
            main.startRotation = usesFlipbook
                ? new ParticleSystem.MinMaxCurve(-0.1f, 0.1f)
                : new ParticleSystem.MinMaxCurve(-Mathf.PI, Mathf.PI);
            main.startColor = Color.white;
            main.gravityModifier = 0f;

            var emission = system.emission;
            emission.rateOverTime = 0f;
            if (usesFlipbook)
            {
                emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 1) });
            }
            else
            {
                emission.SetBursts(new[]
                {
                    new ParticleSystem.Burst(0f, 5),
                    new ParticleSystem.Burst(0.045f, 6),
                    new ParticleSystem.Burst(0.105f, 5),
                    new ParticleSystem.Burst(0.18f, 4),
                });
            }

            var shape = system.shape;
            shape.enabled = !usesFlipbook;
            if (!usesFlipbook)
            {
                shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.radius = scale * 0.16f;
            }

            var sizeOverLifetime = system.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(
                1f,
                usesFlipbook
                    ? new AnimationCurve(
                        new Keyframe(0f, 0.16f),
                        new Keyframe(0.08f, 0.52f),
                        new Keyframe(0.28f, 0.88f),
                        new Keyframe(0.62f, 1f),
                        new Keyframe(1f, 1.06f))
                    : new AnimationCurve(
                        new Keyframe(0f, 0.12f),
                        new Keyframe(0.08f, 0.58f),
                        new Keyframe(0.38f, 1.02f),
                        new Keyframe(0.72f, 1.3f),
                        new Keyframe(1f, 1.42f)));

            var colorOverLifetime = system.colorOverLifetime;
            colorOverLifetime.enabled = true;
            colorOverLifetime.color = usesFlipbook
                ? CreateFlipbookVisibilityGradient()
                : MechEffectResources.CreateBlackbodyCoolingGradient(0.92f);

            var textureSheet = system.textureSheetAnimation;
            textureSheet.enabled = usesFlipbook;
            if (usesFlipbook)
            {
                textureSheet.mode = ParticleSystemAnimationMode.Grid;
                textureSheet.animation = ParticleSystemAnimationType.WholeSheet;
                textureSheet.numTilesX = 4;
                textureSheet.numTilesY = 4;
                textureSheet.cycleCount = 1;
                textureSheet.frameOverTime = new ParticleSystem.MinMaxCurve(
                    1f,
                    new AnimationCurve(
                        new Keyframe(0f, 0f),
                        new Keyframe(0.1f, 0.1f),
                        new Keyframe(0.52f, 0.52f),
                        new Keyframe(1f, 1f)));
            }

            fireballMaterial ??= MechEffectResources.CreateFireballParticleMaterial(
                "AC6 Missile Fireball Cluster (Runtime)",
                new Color(1.55f, 1.28f, 1.05f, 1f));
            MechEffectResources.ConfigureUnlitRenderer(renderer, fireballMaterial);
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.sortingFudge = -0.5f;

            system.Play();
            return renderer;
        }

        private static Gradient CreateFlipbookVisibilityGradient()
        {
            var gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(Color.white, 0f),
                    new GradientColorKey(Color.white, 0.72f),
                    new GradientColorKey(new Color(0.76f, 0.72f, 0.7f), 1f),
                },
                new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(1f, 0.8f),
                    new GradientAlphaKey(0f, 1f),
                });
            return gradient;
        }

        private void Update()
        {
            elapsed += Time.deltaTime;

            if (impactLight != null)
            {
                if (elapsed >= lightLifetime)
                {
                    Destroy(impactLight.gameObject);
                    impactLight = null;
                }
                else
                {
                    var normalizedAge = Mathf.Clamp01(elapsed / lightLifetime);
                    if (explosionLight)
                    {
                        // 闪光先保持极短峰值，再随火团冷却平滑下降，让周围烟尘和机体
                        // 有一段可读的受光过程，而不是只亮一帧。
                        var decayAge = Mathf.Clamp01((normalizedAge - 0.055f) / 0.945f);
                        impactLight.intensity = lightPeakIntensity * Mathf.Pow(1f - decayAge, 1.65f);
                    }
                    else
                    {
                        var remaining = 1f - normalizedAge;
                        impactLight.intensity = lightPeakIntensity * remaining * remaining;
                    }
                }
            }

            if (elapsed >= totalLifetime)
            {
                Destroy(gameObject);
            }
        }

        private ParticleSystemRenderer BuildFlash(float scale, bool distanceReadable = false)
        {
            var system = CreateChildSystem("Flash", out var renderer);

            if (distanceReadable)
            {
                var camera = Camera.main;
                if (camera != null)
                {
                    var distance = Vector3.Distance(camera.transform.position, transform.position);
                    // 保证远处爆炸仍至少形成约 2-3 像素的白闪；只影响不足 0.1 秒的光学辉光，
                    // 不改变随后火球和碎片的物理尺寸。
                    scale = Mathf.Max(scale, distance * 0.0018f);
                }
            }

            var main = system.main;
            // 爆炸闪光要读作"一下"，不是"一团"。0.085 s 在 60fps 下约 5 帧，配合原先
            // 由小长大的尺寸曲线会摊成一个小火团；缩到 0.055 s 并让尺寸首帧即满。
            var flashSeconds = distanceReadable ? 0.055f : FlashSeconds;
            main.duration = flashSeconds;
            main.loop = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(flashSeconds * 0.65f, flashSeconds);
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
                    // 首帧即满：闪光是一次瞬时释放，没有"长大"的过程。之后略微外扩，
                    // 读作光球边缘被推开，而不是又生成了一团东西。
                    new Keyframe(0f, 1f),
                    new Keyframe(0.35f, 1.16f),
                    new Keyframe(1f, 1.3f)));

            var colorOverLifetime = system.colorOverLifetime;
            colorOverLifetime.enabled = true;
            // 不用 CreateBlackbodyCoolingGradient：它的 alpha 保持到 0.4 才开始下降，
            // 那是火球冷却的节奏。闪光要的是"满 → 立刻塌"。
            colorOverLifetime.color = CreateFlashDecayGradient();

            // HDR 峰值明确推过战斗 Volume 的 Bloom 阈值 1.1，闪光才会泛开并喂到镜头光晕。
            flashMaterial ??= MechEffectResources.CreateAdditiveParticleMaterial(
                "AC6 Impact Flash (Runtime)",
                new Color(4.2f, 3.6f, 2.8f, 1f));
            MechEffectResources.ConfigureUnlitRenderer(renderer, flashMaterial);
            renderer.renderMode = ParticleSystemRenderMode.Billboard;

            system.Play();
            return renderer;
        }

        private ParticleSystemRenderer BuildExplosionSparks(float scale)
        {
            var system = CreateChildSystem("HotFragmentSpray", out var renderer);

            var main = system.main;
            main.duration = 0.06f;
            main.loop = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.16f, 0.44f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(scale * 2.4f, scale * 6.5f);
            main.startSize = new ParticleSystem.MinMaxCurve(scale * 0.012f, scale * 0.032f);
            main.startColor = Color.white;
            main.gravityModifier = 0f;

            var emission = system.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[]
            {
                new ParticleSystem.Burst(0f, (short)Mathf.RoundToInt(5f + scale)),
            });

            var shape = system.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = scale * 0.08f;

            var sizeOverLifetime = system.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(
                1f,
                new AnimationCurve(
                    new Keyframe(0f, 1f),
                    new Keyframe(0.72f, 0.72f),
                    new Keyframe(1f, 0f)));

            var colorOverLifetime = system.colorOverLifetime;
            colorOverLifetime.enabled = true;
            colorOverLifetime.color = MechEffectResources.CreateBlackbodyCoolingGradient(1f);

            sparkMaterial ??= MechEffectResources.CreateAdditiveParticleMaterial(
                "AC6 Missile Hot Fragment Spray (Runtime)",
                new Color(4.6f, 3.5f, 2.2f, 1f));
            MechEffectResources.ConfigureUnlitRenderer(renderer, sparkMaterial);
            renderer.renderMode = ParticleSystemRenderMode.Stretch;
            renderer.velocityScale = 0.025f;
            renderer.lengthScale = 0.42f;

            system.Play();
            return renderer;
        }

        private ParticleSystemRenderer BuildExplosionSmoke(float scale, float lifetime)
        {
            var system = CreateChildSystem("IlluminatedDustCloud", out var renderer);

            var main = system.main;
            main.duration = 0.42f;
            main.loop = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(lifetime * 0.66f, lifetime);
            main.startSpeed = new ParticleSystem.MinMaxCurve(scale * 0.16f, scale * 0.58f);
            main.startSize = new ParticleSystem.MinMaxCurve(scale * 0.62f, scale * 1.32f);
            main.startRotation = new ParticleSystem.MinMaxCurve(-Mathf.PI, Mathf.PI);
            main.startColor = Color.white;
            main.gravityModifier = 0f;

            var emission = system.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[]
            {
                new ParticleSystem.Burst(0.12f, 7),
                new ParticleSystem.Burst(0.22f, 7),
                new ParticleSystem.Burst(0.34f, 5),
            });

            var shape = system.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = scale * 0.22f;

            var sizeOverLifetime = system.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(
                1f,
                new AnimationCurve(
                    new Keyframe(0f, 0.16f),
                    new Keyframe(0.12f, 0.72f),
                    new Keyframe(0.52f, 1.38f),
                    new Keyframe(1f, 1.82f)));

            var colorOverLifetime = system.colorOverLifetime;
            colorOverLifetime.enabled = true;
            colorOverLifetime.color = MechEffectResources.CreateExplosionSmokeGradient();

            smokeMaterial ??= MechEffectResources.CreateSmokeParticleMaterial(
                "AC6 Missile Illuminated Dust (Runtime)",
                Color.white);
            MechEffectResources.ConfigureUnlitRenderer(renderer, smokeMaterial);
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.sortingFudge = 0.35f;

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

        private void BuildDebris(float scale, float lifetime, bool radial = false)
        {
            var system = CreateChildSystem("Debris", out var renderer);

            var main = system.main;
            main.duration = 0.1f;
            main.loop = false;
            main.startLifetime = radial
                ? new ParticleSystem.MinMaxCurve(lifetime * 0.55f, lifetime)
                : new ParticleSystem.MinMaxCurve(lifetime * 0.45f, lifetime);
            // 战斗部破片在真空里既无阻力也无重力，初速就是全程速度。原值 scale*2.5~8
            // （scale 3.4 时只有 8.5~27 m/s）比表面命中的抛撒还慢，读起来像慢动作喷粉，
            // 而这里代表的是装药驱动的破片。抬到与"炸开"相称的量级；真实破片是千米级，
            // 取不到那么高是因为超过约 250 m/s 后碎片在一帧内的位移就超出了可读范围。
            main.startSpeed = radial
                ? new ParticleSystem.MinMaxCurve(scale * 18f, scale * 62f)
                : new ParticleSystem.MinMaxCurve(scale * 6f, scale * 22f);
            main.startSize = radial
                ? new ParticleSystem.MinMaxCurve(scale * 0.012f, scale * 0.045f)
                : new ParticleSystem.MinMaxCurve(scale * 0.05f, scale * 0.2f);
            main.startColor = new Color(0.62f, 0.66f, 0.72f, 1f);
            // 真空：无重力（本作场景）也无空气阻力，碎片保持初速直线飞行。
            main.gravityModifier = 0f;
            main.startRotation3D = true;
            main.startRotationX = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startRotationY = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startRotationZ = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);

            var emission = system.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[]
            {
                // 真空爆炸的视觉主体是碎片而不是火球——火球没有助燃剂几乎瞬灭，碎片却能一直飞。
                // 原来 radial 只发 8 + scale*1.5（scale 3.4 时 13 个），撑不起主体。
                new ParticleSystem.Burst(
                    0f,
                    (short)Mathf.RoundToInt(radial ? 26f + scale * 12f : 8f + scale * 18f)),
            });

            var shape = system.shape;
            shape.enabled = true;
            shape.shapeType = radial
                ? ParticleSystemShapeType.Sphere
                : ParticleSystemShapeType.Cone;
            if (!radial)
            {
                // 沿受击面法线的半球状抛撒。
                shape.angle = 74f;
            }
            shape.radius = scale * (radial ? 0.16f : 0.08f);

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

        /// <summary>
        /// 起爆辉光：一层远大于核心闪光、寿命极短、低 alpha 的加色光晕。
        /// 它不表现任何实体，唯一职责是在起爆那一两帧把 Bloom 冲开，制造"闪一下"的
        /// 冲击感——核心闪光只有几米宽，单独看只是个亮点，撑不起这个瞬间。
        /// 因为是纯光学现象，它不参与 <see cref="MechEffectThermalAppearance"/> 之外的
        /// 任何物理表达，也刻意不做冷却降色温：它在冷却发生之前就已经消失了。
        /// </summary>
        private ParticleSystemRenderer BuildDetonationHalo(float scale)
        {
            var system = CreateChildSystem("DetonationHalo", out var renderer);

            var main = system.main;
            const float haloSeconds = 0.09f;
            main.duration = haloSeconds;
            main.loop = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(haloSeconds * 0.8f, haloSeconds);
            main.startSpeed = 0f;
            // 远大于核心闪光（那层是 scale*1.6~2.4），才能占到足够的屏幕面积。
            main.startSize = new ParticleSystem.MinMaxCurve(scale * 5.2f, scale * 7.4f);
            main.startColor = Color.white;
            main.startRotation = new ParticleSystem.MinMaxCurve(-Mathf.PI, Mathf.PI);

            var emission = system.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 1) });

            var shape = system.shape;
            shape.enabled = false;

            var sizeOverLifetime = system.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(
                1f,
                new AnimationCurve(
                    new Keyframe(0f, 0.82f),
                    new Keyframe(0.3f, 1.12f),
                    new Keyframe(1f, 1.34f)));

            var colorOverLifetime = system.colorOverLifetime;
            colorOverLifetime.enabled = true;
            colorOverLifetime.color = CreateFlashDecayGradient();

            // alpha 压低：这层要的是"整片亮一下"，不是画一个可辨认的白盘。真正的
            // 亮度靠 HDR 值推过 Bloom 阈值来获得，而不是靠不透明度。
            detonationHaloMaterial ??= MechEffectResources.CreateAdditiveParticleMaterial(
                "AC6 Detonation Halo (Runtime)",
                new Color(3.4f, 2.9f, 2.3f, 0.5f));
            MechEffectResources.ConfigureUnlitRenderer(renderer, detonationHaloMaterial);
            renderer.renderMode = ParticleSystemRenderMode.Billboard;

            system.Play();
            return renderer;
        }

        /// <summary>
        /// 闪光专用衰减：满值起步、几乎立刻塌掉。
        /// 与 <c>CreateBlackbodyCoolingGradient</c> 的区别在节奏——后者的 alpha 保持到
        /// 生命的 40% 才开始下降，那是火团冷却的时间尺度；闪光要的是一次瞬时释放。
        /// </summary>
        private static Gradient CreateFlashDecayGradient()
        {
            var gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(1f, 0.99f, 0.97f), 0f),
                    new GradientColorKey(new Color(1f, 0.94f, 0.8f), 0.4f),
                    new GradientColorKey(new Color(1f, 0.78f, 0.48f), 1f),
                },
                new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(0.62f, 0.18f),
                    new GradientAlphaKey(0.14f, 0.55f),
                    new GradientAlphaKey(0f, 1f),
                });
            return gradient;
        }

        /// <summary>
        /// 真空爆炸的招牌层：炽热余烬。与 <see cref="BuildExplosionSparks"/> 的区别是它
        /// 不是火球的附属喷溅，而是爆炸结束之后仍在视野里飞的东西——寿命长一个量级，
        /// 逐粒子拖尾拉出亮线，颜色按黑体降温从白热冷到暗红。
        ///
        /// 之所以必须单独一层而不是给 <see cref="BuildDebris"/> 加发光：碎片走的是
        /// URP Particles/Lit 网格材质，靠翻滚反光表达"这是金属"（见该方法内注释），
        /// 给它叠自发光会把这个线索抹掉。两层各司其职：碎片是冷的金属块，余烬是热的。
        ///
        /// 大气层内这一层会因为阻力迅速减速并被烟尘遮蔽，所以只在真空里成立。
        /// </summary>
        private ParticleSystemRenderer BuildVacuumEmbers(float scale)
        {
            var system = CreateChildSystem("VacuumEmbers", out var renderer);

            var main = system.main;
            main.duration = 0.05f;
            main.loop = false;
            // 无阻力：一旦抛出就一直飞，寿命远长于火球（火球约 0.3 s 就没了）。
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.3f, 2.9f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(scale * 7f, scale * 34f);
            main.startSize = new ParticleSystem.MinMaxCurve(scale * 0.008f, scale * 0.026f);
            main.startColor = Color.white;
            main.gravityModifier = 0f;
            main.maxParticles = 192;

            var emission = system.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[]
            {
                new ParticleSystem.Burst(0f, (short)Mathf.RoundToInt(48f + scale * 22f)),
            });

            var shape = system.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = scale * 0.12f;

            // 只在最后收尾时缩掉，中途保持——真空里没有东西让它变小。
            var sizeOverLifetime = system.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(
                1f,
                new AnimationCurve(
                    new Keyframe(0f, 1f),
                    new Keyframe(0.82f, 0.85f),
                    new Keyframe(1f, 0f)));

            var colorOverLifetime = system.colorOverLifetime;
            colorOverLifetime.enabled = true;
            colorOverLifetime.color = MechEffectResources.CreateBlackbodyCoolingGradient(1f);

            // 逐粒子拖尾：真空中高速炽热碎片划出的亮线，是这套效果最抓眼的部分。
            var trails = system.trails;
            trails.enabled = true;
            trails.mode = ParticleSystemTrailMode.PerParticle;
            trails.ratio = 0.65f;                 // 不是每颗都拖，避免糊成一片
            trails.lifetime = new ParticleSystem.MinMaxCurve(0.12f, 0.26f);
            trails.dieWithParticles = true;
            trails.inheritParticleColor = true;   // 拖尾跟着黑体降温一起变暗
            trails.sizeAffectsWidth = true;
            trails.widthOverTrail = new ParticleSystem.MinMaxCurve(
                1f,
                new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(1f, 0f)));
            trails.colorOverTrail = new ParticleSystem.MinMaxGradient(
                new Color(1f, 1f, 1f, 1f),
                new Color(1f, 1f, 1f, 0f));

            emberMaterial ??= MechEffectResources.CreateAdditiveParticleMaterial(
                "AC6 Vacuum Ember (Runtime)",
                // >1 才会越过战斗 Volume 的 Bloom 阈值 1.1（见 AC6SpaceCombatVolume）。
                new Color(5.2f, 3.4f, 1.9f, 1f));
            emberTrailMaterial ??= MechEffectResources.CreateAdditiveTrailMaterial(
                "AC6 Vacuum Ember Trail (Runtime)",
                new Color(4.4f, 2.6f, 1.35f, 1f));

            MechEffectResources.ConfigureUnlitRenderer(renderer, emberMaterial);
            renderer.renderMode = ParticleSystemRenderMode.Stretch;
            renderer.velocityScale = 0.012f;
            renderer.lengthScale = 0.5f;
            renderer.trailMaterial = emberTrailMaterial;

            system.Play();
            return renderer;
        }

        private void BuildLight(float scale, bool explosion = false)
        {
            var lightObject = new GameObject("ImpactLight");
            lightObject.layer = gameObject.layer;
            lightObject.transform.SetParent(transform, false);

            impactLight = lightObject.AddComponent<Light>();
            impactLight.type = LightType.Point;
            impactLight.color = new Color(1f, 0.85f, 0.62f);
            impactLight.range = explosion
                ? Mathf.Lerp(55f, 120f, Mathf.InverseLerp(1f, 4f, scale))
                : Mathf.Lerp(12f, 45f, Mathf.InverseLerp(0.3f, 1.4f, scale));
            impactLight.shadows = LightShadows.None;
            // Forward+ 的集群光循环取消了逐物体附加光源上限，命中点光源现在能真正照到机体，
            // 这是 `AC6PartsApprox` 补上 _CLUSTER_LIGHT_LOOP 之后才解锁的能力。
            explosionLight = explosion;
            // 真空：没有大气散射把闪光摊开成持续辉光，装药烧尽即暗。0.82 s 是大气层内
            // 的时长感，会让爆炸读起来"软"；缩到 0.42 s 后闪光更像一次冲击而非一团火。
            lightLifetime = explosion ? 0.42f : LightSeconds;
            lightPeakIntensity = explosion
                ? Mathf.Lerp(1400f, 3600f, Mathf.InverseLerp(1f, 4f, scale))
                : Mathf.Lerp(90f, 420f, Mathf.InverseLerp(0.3f, 1.4f, scale));
            impactLight.intensity = lightPeakIntensity;
        }

        private ParticleSystem CreateChildSystem(
            string childName,
            out ParticleSystemRenderer renderer)
        {
            var child = new GameObject(childName);
            child.layer = gameObject.layer;
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
