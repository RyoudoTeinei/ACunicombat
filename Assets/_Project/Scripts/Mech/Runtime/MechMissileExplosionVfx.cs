using System.Collections.Generic;
using UnityEngine;
using UnityEngine.VFX;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// 导弹爆炸的 GPU 特效层。VFX Graph 负责连续帧混合的一次性火球、
    /// 被火光染亮的烟尘与短促碎屑；点光源、远距离闪光和物理碎片由
    /// <see cref="MechImpactEffect"/> 负责。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MechMissileExplosionVfx : MonoBehaviour
    {
        private const string FireballAssetPath = "Effects/VFX/Flames";
        private const string SmokeAssetPath = "Effects/VFX/Smoke";
        private const string SparksAssetPath = "Effects/VFX/Sparks";
        private const string FireballTexturePath = "Effects/MissileExplosionFlipbook";
        private const string SmokeTexturePath = "Effects/VFX/Textures/WispySmoke03b_8x8";

        private VisualEffect fireball;
        private readonly List<VisualEffect> smoke = new();
        private VisualEffect sparks;
        private float age;
        private bool fireballStopped;
        private bool smokeStopped;
        private bool sparksStopped;

        public static bool TryAttach(GameObject root, float scale)
        {
            if (root == null || !SystemInfo.supportsComputeShaders)
            {
                return false;
            }

            var fireballAsset = Resources.Load<VisualEffectAsset>(FireballAssetPath);
            var smokeAsset = Resources.Load<VisualEffectAsset>(SmokeAssetPath);
            var sparksAsset = Resources.Load<VisualEffectAsset>(SparksAssetPath);
            if (fireballAsset == null || smokeAsset == null || sparksAsset == null)
            {
                return false;
            }

            try
            {
                var controller = root.AddComponent<MechMissileExplosionVfx>();
                controller.Build(fireballAsset, smokeAsset, sparksAsset, Mathf.Max(1f, scale));
                return true;
            }
            catch (System.Exception exception)
            {
                Debug.LogWarning($"[MissileExplosionVFX] GPU effect setup failed; using particle fallback. {exception.Message}");
                var controller = root.GetComponent<MechMissileExplosionVfx>();
                if (controller != null)
                {
                    Destroy(controller);
                }
                return false;
            }
        }

        private void Build(
            VisualEffectAsset fireballAsset,
            VisualEffectAsset smokeAsset,
            VisualEffectAsset sparksAsset,
            float scale)
        {
            var fireballTexture = Resources.Load<Texture2D>(FireballTexturePath);
            var smokeTexture = Resources.Load<Texture2D>(SmokeTexturePath);

            fireball = CreateEffect("Fireball_Core", fireballAsset);
            ConfigureFireball(fireball, fireballTexture, scale);
            StartEffect(fireball);

            CreateSmokeLobe(
                smokeAsset,
                smokeTexture,
                "Smoke_A",
                new Vector3(0.05f, 0.02f, 0f) * scale,
                Quaternion.Euler(18f, 31f, -14f),
                scale * 0.92f,
                15f);
            CreateSmokeLobe(
                smokeAsset,
                smokeTexture,
                "Smoke_B",
                new Vector3(-0.12f, 0.08f, 0.09f) * scale,
                Quaternion.Euler(-22f, -41f, 28f),
                scale * 0.73f,
                11f);

            sparks = CreateEffect("HotFragments", sparksAsset);
            ConfigureSparks(sparks, scale);
            StartEffect(sparks);
        }

        private void Update()
        {
            age += Time.deltaTime;

            if (!sparksStopped && age >= 0.095f)
            {
                StopEffect(sparks);
                sparksStopped = true;
            }

            // The source graph uses a rate spawner. Stop it almost immediately after the
            // first particle is emitted; that particle then plays the blended 4x4 blast
            // sequence for its complete lifetime.
            if (!fireballStopped && age >= 0.035f)
            {
                StopEffect(fireball);
                fireballStopped = true;
            }

            if (!smokeStopped && age >= 0.2f)
            {
                StopEffects(smoke);
                smokeStopped = true;
            }

            // 必须晚于 MechImpactEffect.totalLifetime（余烬寿命上界 2.9 + 拖尾 0.26 + 0.2
            // ≈ 3.36 s）。两者挂在同一个 root 上，谁先到点谁销毁，早于它就会把余烬截断。
            if (age >= 3.6f)
            {
                Destroy(gameObject);
            }
        }

        private static void ConfigureFireball(VisualEffect effect, Texture texture, float scale)
        {
            // 真空：没有助燃剂，火球短促。图集 4x4 共 16 帧中，前 8 帧是闪光与火球，
            // 后 8 帧是大气层里的浓烟消散——真空里不该出现，所以只播前 8 帧就结束。
            const float lifetime = 0.34f;
            const float playedFrames = 8f;
            SetFloat(effect, "Spawn Rate", 31f);
            SetFloat(effect, "Initial Size", scale * 3.15f);
            SetFloat(effect, "Lifetime", lifetime);
            SetBool(effect, "Use Flipbook", true);
            SetVector2(effect, "Flipbook Size", new Vector2(4f, 4f));
            SetFloat(effect, "Flipbook Animation Speed", playedFrames / lifetime);
            SetTexture(effect, "Texture", texture);
            SetVector3(effect, "Effect Bounds_center", Vector3.zero);
            SetVector3(effect, "Effect Bounds_size", Vector3.one * Mathf.Max(12f, scale * 7f));
            SetFloat(effect, "Wind Drag Intensity", 0f);
            SetVector3(effect, "Wind Direction", Vector3.zero);
            SetGradient(effect, "Fire Colors", CreateExplosionTemperatureGradient());
            SetGradient(effect, "Color Over Life", CreateExplosionLifeGradient());
            SetCurve(effect, "Size Scale over Life", CreateExplosionExpansionCurve());
        }

        private void CreateSmokeLobe(
            VisualEffectAsset asset,
            Texture texture,
            string objectName,
            Vector3 localOffset,
            Quaternion localRotation,
            float size,
            float spawnRate)
        {
            var effect = CreateEffect(objectName, asset, localOffset, localRotation);
            SetFloat(effect, "Spawn Rate", spawnRate);
            // 真空里没有持续存在的烟：装药与结构汽化后的那点物质迅速膨胀稀薄化即不可见。
            // 2.35 s 是大气层内的时长感，会在爆炸早就结束后仍留一团挂在画面上。
            SetFloat(effect, "LifeTime", 0.62f);
            SetFloat(effect, "Drag", 0f);
            SetFloat(effect, "Initial Velocity Divergence", 1.25f);
            // 原本是 Vector3.up——那是浮力，真空里不成立（烟不会上升）。改为沿各自
            // 叶瓣的本地前向，两瓣朝向不同，合起来读作向外膨胀而不是向上飘。
            SetVector3(effect, "Initial Velocity", Vector3.forward * size * 1.15f);
            SetVector3(effect, "Global Force", Vector3.zero);
            SetBool(effect, "Use Flipbook", true);
            SetVector2(effect, "Flipbook Size", new Vector2(8f, 8f));
            SetFloat(effect, "Flipbook Framerate", 32f);
            SetTexture(effect, "Main Texture", texture);
            SetVector3(effect, "Bounds_center", Vector3.zero);
            SetVector3(effect, "Bounds_size", Vector3.one * Mathf.Max(12f, size * 10f));
            SetCurve(effect, "Size Over Life", CreateSmokeExpansionCurve(size));
            SetGradient(effect, "Color Over Life", CreateIlluminatedSmokeGradient());
            smoke.Add(effect);
            StartEffect(effect);
        }

        private void ConfigureSparks(VisualEffect effect, float scale)
        {
            SetFloat(effect, "Spawn Rate", 115f);
            SetVector2(effect, "Loop Duration Min/Max", new Vector2(0.055f, 0.09f));
            SetVector2(effect, "Burst Count Min/Max", new Vector2(8f, 14f));
            SetVector2(effect, "Lifetime Min/Max", new Vector2(0.18f, 0.52f));
            SetVector2(effect, "Size Min/Max", new Vector2(scale * 0.008f, scale * 0.026f));
            SetVector3(effect, "Initial Velocity", Vector3.forward * scale * 1.2f);
            SetFloat(effect, "Initial Velocity Divergence", 3.05f);
            SetFloat(effect, "Drag", 0f);
            SetVector3(effect, "Gravity Vector", Vector3.zero);
            SetFloat(effect, "Turbulence Intensity", 0f);
            SetFloat(effect, "Sparkle Width", 0.045f);
            SetFloat(effect, "Density", 0.68f);
            SetVector3(effect, "Bounds_center", Vector3.zero);
            SetVector3(effect, "Bounds_size", Vector3.one * Mathf.Max(14f, scale * 12f));
            SetGradient(effect, "Color Over Life", CreateSparkGradient());
            // 三个 .vfx 资产内部都没有烘进任何贴图默认值（已核查其 guid 引用全为
            // VFX Graph 内部节点脚本），贴图只能由此处注入。火球设了 "Texture"、烟设了
            // "Main Texture"，唯独火星漏了 "Sparkle Texture"——无贴图的面片输出会渲成
            // 实心色块而不是亮点。软圆点贴图正是火星要的形状。
            SetTexture(effect, "Sparkle Texture", MechEffectResources.SoftParticleTexture);
            SetGradient(effect, "Sparkle Fire Gradient", CreateSparkGradient());
        }

        private VisualEffect CreateEffect(
            string objectName,
            VisualEffectAsset asset,
            Vector3 localOffset = default,
            Quaternion localRotation = default)
        {
            var child = new GameObject(objectName);
            child.layer = gameObject.layer;
            child.transform.SetParent(transform, false);
            child.transform.localPosition = localOffset;
            child.transform.localRotation = localRotation == default ? Quaternion.identity : localRotation;

            var effect = child.AddComponent<VisualEffect>();
            effect.enabled = false;
            effect.visualEffectAsset = asset;
            effect.playRate = 1f;
            return effect;
        }

        private static void StartEffect(VisualEffect effect)
        {
            if (effect == null)
            {
                return;
            }

            effect.enabled = true;
            effect.Reinit();
            effect.Play();
        }

        private static void StopEffects(List<VisualEffect> effects)
        {
            for (var i = 0; i < effects.Count; i++)
            {
                StopEffect(effects[i]);
            }
        }

        private static void StopEffect(VisualEffect effect)
        {
            if (effect != null)
            {
                effect.Stop();
            }
        }

        private static void SetFloat(VisualEffect effect, string name, float value)
        {
            if (effect.HasFloat(name))
            {
                effect.SetFloat(name, value);
            }
        }

        private static void SetBool(VisualEffect effect, string name, bool value)
        {
            if (effect.HasBool(name))
            {
                effect.SetBool(name, value);
            }
        }

        private static void SetVector2(VisualEffect effect, string name, Vector2 value)
        {
            if (effect.HasVector2(name))
            {
                effect.SetVector2(name, value);
            }
        }

        private static void SetVector3(VisualEffect effect, string name, Vector3 value)
        {
            if (effect.HasVector3(name))
            {
                effect.SetVector3(name, value);
            }
        }

        private static void SetTexture(VisualEffect effect, string name, Texture value)
        {
            if (value != null && effect.HasTexture(name))
            {
                effect.SetTexture(name, value);
            }
        }

        private static void SetGradient(VisualEffect effect, string name, Gradient value)
        {
            if (effect.HasGradient(name))
            {
                effect.SetGradient(name, value);
            }
        }

        private static void SetCurve(VisualEffect effect, string name, AnimationCurve value)
        {
            if (effect.HasAnimationCurve(name))
            {
                effect.SetAnimationCurve(name, value);
            }
        }

        private static Gradient CreateIlluminatedSmokeGradient()
        {
            var gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(2.2f, 0.72f, 0.16f), 0f),
                    new GradientColorKey(new Color(0.45f, 0.12f, 0.035f), 0.23f),
                    new GradientColorKey(new Color(0.12f, 0.105f, 0.095f), 0.58f),
                    new GradientColorKey(new Color(0.035f, 0.035f, 0.04f), 1f),
                },
                new[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(0.72f, 0.09f),
                    new GradientAlphaKey(0.45f, 0.54f),
                    new GradientAlphaKey(0f, 1f),
                });
            return gradient;
        }

        private static Gradient CreateExplosionTemperatureGradient()
        {
            var gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(Color.black, 0f),
                    new GradientColorKey(new Color(0.16f, 0.002f, 0f), 0.12f),
                    new GradientColorKey(new Color(1.15f, 0.08f, 0.004f), 0.34f),
                    new GradientColorKey(new Color(3.4f, 0.78f, 0.05f), 0.62f),
                    new GradientColorKey(new Color(5.6f, 2.9f, 0.5f), 0.84f),
                    new GradientColorKey(new Color(6.5f, 5.7f, 4.6f), 1f),
                },
                new[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(1f, 0.16f),
                    new GradientAlphaKey(1f, 0.86f),
                    new GradientAlphaKey(0f, 1f),
                });
            return gradient;
        }

        private static Gradient CreateExplosionLifeGradient()
        {
            var gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(3.8f, 3.25f, 2.55f), 0f),
                    new GradientColorKey(new Color(2.5f, 1.15f, 0.28f), 0.42f),
                    new GradientColorKey(new Color(0.72f, 0.18f, 0.035f), 0.78f),
                    new GradientColorKey(new Color(0.18f, 0.055f, 0.018f), 1f),
                },
                new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(1f, 0.82f),
                    new GradientAlphaKey(0f, 1f),
                });
            return gradient;
        }

        private static AnimationCurve CreateExplosionExpansionCurve()
        {
            return new AnimationCurve(
                new Keyframe(0f, 0.13f),
                new Keyframe(0.055f, 0.48f),
                new Keyframe(0.2f, 0.84f),
                new Keyframe(0.55f, 1f),
                new Keyframe(1f, 1.06f));
        }

        private static Gradient CreateSparkGradient()
        {
            var gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(5.5f, 4.2f, 2.2f), 0f),
                    new GradientColorKey(new Color(2.7f, 0.62f, 0.07f), 0.48f),
                    new GradientColorKey(new Color(0.22f, 0.015f, 0.002f), 1f),
                },
                new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(0.8f, 0.68f),
                    new GradientAlphaKey(0f, 1f),
                });
            return gradient;
        }

        private static AnimationCurve CreateSmokeExpansionCurve(float size)
        {
            return new AnimationCurve(
                new Keyframe(0f, size * 0.22f),
                new Keyframe(0.14f, size * 0.72f),
                new Keyframe(0.52f, size * 1.42f),
                new Keyframe(1f, size * 2.05f));
        }
    }
}
