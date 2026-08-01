using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// Camera-relative visual reference field for deep space. Particles remain fixed in world
    /// space until they leave the camera-centred volume, then wrap to the opposite side. This
    /// preserves translational parallax without allowing old particles to consume the pool far
    /// behind a fast-moving player.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MechSpaceDustField : MonoBehaviour
    {
        private const float PersistentParticleLifetime = 100000f;

        [SerializeField] private Transform followTarget;

        [Header("空间范围")]
        [SerializeField] private Vector3 boxHalfExtents = new(58f, 46f, 82f);

        [Header("基础尘埃")]
        [SerializeField, Min(1)] private int speckCount = 420;
        [SerializeField] private Vector2 speckSizeRange = new(0.018f, 0.085f);
        [SerializeField] private Color speckTint = new(0.78f, 0.86f, 0.96f, 0.24f);

        [Header("高速飞线")]
        [SerializeField, Min(1)] private int streakCount = 180;
        [SerializeField] private Vector2 streakSizeRange = new(0.012f, 0.045f);
        [SerializeField] private Color streakTint = new(0.76f, 0.88f, 1f, 0.32f);
        [SerializeField, Min(0f)] private float streakStartSpeed = 110f;
        [SerializeField, Min(0.01f)] private float streakFullSpeed = 720f;
        [SerializeField, Min(0f)] private float streakCameraVelocityScale = 0.006f;
        [SerializeField, Min(0f)] private float streakLengthScale = 1.35f;

        [Header("翻滚碎片")]
        [SerializeField, Min(0)] private int debrisCount = 42;
        [SerializeField] private Vector2 debrisSizeRange = new(0.06f, 0.34f);
        [SerializeField] private Vector2 debrisTumbleRange = new(18f, 95f);
        [SerializeField] private Color debrisDark = new(0.08f, 0.1f, 0.13f, 1f);
        [SerializeField] private Color debrisLight = new(0.55f, 0.62f, 0.7f, 1f);

        private readonly System.Random random = new(0x0AC6D057);

        private ParticleSystem speckSystem;
        private ParticleSystem streakSystem;
        private ParticleSystem debrisSystem;
        private ParticleSystemRenderer streakRenderer;
        private ParticleSystem.Particle[] speckParticles;
        private ParticleSystem.Particle[] streakParticles;
        private ParticleSystem.Particle[] debrisParticles;
        private Material speckMaterial;
        private Material streakMaterial;
        private Material debrisMaterial;
        private Texture2D softParticleTexture;
        private Mesh debrisMesh;
        private Vector3 previousTargetPosition;
        private bool hasPreviousTargetPosition;

        public void Configure(Transform target)
        {
            followTarget = target;
            EnsureResources();
            ResetTrackingPosition();
        }

        public void Tick()
        {
            if (followTarget == null)
            {
                return;
            }

            EnsureResources();
            if (speckSystem == null)
            {
                return;
            }

            var targetPosition = followTarget.position;
            var deltaTime = Mathf.Max(Time.deltaTime, 0.0001f);
            var speed = hasPreviousTargetPosition
                ? Vector3.Distance(targetPosition, previousTargetPosition) / deltaTime
                : 0f;

            transform.SetPositionAndRotation(targetPosition, Quaternion.identity);
            WrapParticles(speckSystem, speckParticles, targetPosition);
            WrapParticles(streakSystem, streakParticles, targetPosition);
            WrapParticles(debrisSystem, debrisParticles, targetPosition);
            UpdateStreakAppearance(speed);

            previousTargetPosition = targetPosition;
            hasPreviousTargetPosition = true;
        }

        private void Awake()
        {
            EnsureResources();
            ResetTrackingPosition();
        }

        private void OnEnable()
        {
            ResetTrackingPosition();
        }

        private void OnDestroy()
        {
            DestroyRuntimeObject(speckMaterial);
            DestroyRuntimeObject(streakMaterial);
            DestroyRuntimeObject(debrisMaterial);
            DestroyRuntimeObject(softParticleTexture);
            DestroyRuntimeObject(debrisMesh);
        }

        private void OnValidate()
        {
            boxHalfExtents = new Vector3(
                Mathf.Max(1f, boxHalfExtents.x),
                Mathf.Max(1f, boxHalfExtents.y),
                Mathf.Max(1f, boxHalfExtents.z));
            speckSizeRange = OrderedPositiveRange(speckSizeRange, 0.001f);
            streakSizeRange = OrderedPositiveRange(streakSizeRange, 0.001f);
            debrisSizeRange = OrderedPositiveRange(debrisSizeRange, 0.001f);
            debrisTumbleRange = OrderedPositiveRange(debrisTumbleRange, 0f);
            streakFullSpeed = Mathf.Max(streakStartSpeed + 0.01f, streakFullSpeed);
        }

        private void ResetTrackingPosition()
        {
            if (followTarget == null)
            {
                hasPreviousTargetPosition = false;
                return;
            }

            previousTargetPosition = followTarget.position;
            hasPreviousTargetPosition = true;
        }

        private void EnsureResources()
        {
            if (!Application.isPlaying || speckSystem != null)
            {
                return;
            }

            var particleShader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (particleShader == null)
            {
                Debug.LogError("[AC6SpaceDust] URP Particles/Unlit shader was not found.", this);
                return;
            }

            transform.rotation = Quaternion.identity;
            var centre = followTarget != null ? followTarget.position : transform.position;
            transform.position = centre;
            softParticleTexture = CreateSoftParticleTexture();

            speckMaterial = CreateParticleMaterial(
                particleShader,
                "AC6 Space Specks (Runtime)",
                speckTint,
                softParticleTexture,
                additive: true);
            streakMaterial = CreateParticleMaterial(
                particleShader,
                "AC6 Speed Streaks (Runtime)",
                streakTint,
                softParticleTexture,
                additive: true);

            var litParticleShader =
                Shader.Find("Universal Render Pipeline/Particles/Lit") ?? particleShader;
            debrisMaterial = CreateParticleMaterial(
                litParticleShader,
                "AC6 Floating Debris (Runtime)",
                Color.white,
                null,
                additive: false);
            if (debrisMaterial.HasProperty("_Metallic"))
            {
                debrisMaterial.SetFloat("_Metallic", 0.55f);
            }
            if (debrisMaterial.HasProperty("_Smoothness"))
            {
                debrisMaterial.SetFloat("_Smoothness", 0.38f);
            }

            speckSystem = CreateParticleSystem("Specks", speckCount, speckMaterial, centre);
            var speckRenderer = speckSystem.GetComponent<ParticleSystemRenderer>();
            speckRenderer.renderMode = ParticleSystemRenderMode.Billboard;
            speckRenderer.minParticleSize = 0f;
            speckRenderer.maxParticleSize = 0.018f;
            speckParticles = CreateParticlePool(
                speckSystem,
                speckCount,
                centre,
                speckSizeRange,
                (_, _) => Color.white,
                tumble: false);

            streakSystem = CreateParticleSystem("Speed Streaks", streakCount, streakMaterial, centre);
            streakRenderer = streakSystem.GetComponent<ParticleSystemRenderer>();
            streakRenderer.renderMode = ParticleSystemRenderMode.Stretch;
            streakRenderer.velocityScale = 0f;
            streakRenderer.cameraVelocityScale = 0f;
            streakRenderer.lengthScale = streakLengthScale;
            streakRenderer.minParticleSize = 0f;
            streakRenderer.maxParticleSize = 0.028f;
            streakParticles = CreateParticlePool(
                streakSystem,
                streakCount,
                centre,
                streakSizeRange,
                (_, _) => Color.white,
                tumble: false);
            streakRenderer.enabled = false;

            debrisMesh = CreateDebrisMesh();
            debrisSystem = CreateParticleSystem("Floating Debris", debrisCount, debrisMaterial, centre);
            var debrisMain = debrisSystem.main;
            debrisMain.startRotation3D = true;
            var debrisRenderer = debrisSystem.GetComponent<ParticleSystemRenderer>();
            debrisRenderer.renderMode = ParticleSystemRenderMode.Mesh;
            debrisRenderer.mesh = debrisMesh;
            debrisRenderer.enableGPUInstancing = true;
            debrisParticles = CreateParticlePool(
                debrisSystem,
                debrisCount,
                centre,
                debrisSizeRange,
                (index, count) => Color.Lerp(
                    debrisDark,
                    debrisLight,
                    0.2f + 0.8f * Hash01(index, count)),
                tumble: true);

            Debug.Log(
                $"[AC6SpaceDust] Initialized {speckCount} specks, {streakCount} speed streaks " +
                $"and {debrisCount} debris particles.",
                this);
        }

        private ParticleSystem CreateParticleSystem(
            string objectName,
            int particleCount,
            Material sharedMaterial,
            Vector3 centre)
        {
            var particleObject = new GameObject(objectName);
            particleObject.transform.SetParent(transform, false);
            particleObject.transform.position = centre;

            var particleSystem = particleObject.AddComponent<ParticleSystem>();
            particleSystem.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = particleSystem.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.playOnAwake = false;
            main.loop = true;
            main.maxParticles = Mathf.Max(1, particleCount);
            main.startLifetime = PersistentParticleLifetime;
            main.startSpeed = 0f;
            main.gravityModifier = 0f;
            main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;

            var emission = particleSystem.emission;
            emission.enabled = false;
            var shape = particleSystem.shape;
            shape.enabled = false;

            var renderer = particleObject.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = sharedMaterial;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

            particleSystem.Play();
            return particleSystem;
        }

        private ParticleSystem.Particle[] CreateParticlePool(
            ParticleSystem particleSystem,
            int count,
            Vector3 centre,
            Vector2 sizeRange,
            Func<int, int, Color> colorFactory,
            bool tumble)
        {
            count = Mathf.Max(0, count);
            var particles = new ParticleSystem.Particle[count];
            for (var i = 0; i < count; i++)
            {
                var particle = new ParticleSystem.Particle
                {
                    position = RandomPointInVolume(centre),
                    velocity = Vector3.zero,
                    startLifetime = PersistentParticleLifetime,
                    remainingLifetime = PersistentParticleLifetime,
                    startSize = RandomRange(sizeRange.x, sizeRange.y),
                    startColor = colorFactory(i, count),
                    randomSeed = unchecked((uint)random.Next(1, int.MaxValue)),
                };

                if (tumble)
                {
                    particle.rotation3D = RandomEuler();
                    particle.angularVelocity3D = RandomAngularVelocity();
                }

                particles[i] = particle;
            }

            particleSystem.SetParticles(particles, particles.Length);
            return particles;
        }

        private void WrapParticles(
            ParticleSystem particleSystem,
            ParticleSystem.Particle[] particles,
            Vector3 centre)
        {
            if (particleSystem == null || particles == null || particles.Length == 0)
            {
                return;
            }

            var count = particleSystem.GetParticles(particles);
            var changed = false;
            for (var i = 0; i < count; i++)
            {
                var wrapped = WrapPosition(particles[i].position, centre);
                if ((wrapped - particles[i].position).sqrMagnitude <= 0.000001f)
                {
                    continue;
                }

                particles[i].position = wrapped;
                particles[i].remainingLifetime = PersistentParticleLifetime;
                changed = true;
            }

            if (changed)
            {
                particleSystem.SetParticles(particles, count);
            }
        }

        private void UpdateStreakAppearance(float speed)
        {
            if (streakRenderer == null || streakMaterial == null)
            {
                return;
            }

            var speed01 = Mathf.SmoothStep(
                0f,
                1f,
                Mathf.InverseLerp(streakStartSpeed, streakFullSpeed, speed));
            streakRenderer.enabled = speed01 > 0.005f;
            streakRenderer.cameraVelocityScale = streakCameraVelocityScale * speed01;
            streakRenderer.lengthScale = Mathf.Lerp(0.45f, streakLengthScale, speed01);

            var color = streakTint;
            color.a *= speed01;
            SetMaterialColor(streakMaterial, color);
        }

        private Vector3 WrapPosition(Vector3 position, Vector3 centre)
        {
            return new Vector3(
                WrapAxis(position.x, centre.x, boxHalfExtents.x),
                WrapAxis(position.y, centre.y, boxHalfExtents.y),
                WrapAxis(position.z, centre.z, boxHalfExtents.z));
        }

        private Vector3 RandomPointInVolume(Vector3 centre)
        {
            return centre + new Vector3(
                RandomRange(-boxHalfExtents.x, boxHalfExtents.x),
                RandomRange(-boxHalfExtents.y, boxHalfExtents.y),
                RandomRange(-boxHalfExtents.z, boxHalfExtents.z));
        }

        private Vector3 RandomEuler()
        {
            return new Vector3(
                RandomRange(0f, 360f),
                RandomRange(0f, 360f),
                RandomRange(0f, 360f));
        }

        private Vector3 RandomAngularVelocity()
        {
            var magnitude = RandomRange(debrisTumbleRange.x, debrisTumbleRange.y);
            var direction = new Vector3(
                RandomRange(-1f, 1f),
                RandomRange(-1f, 1f),
                RandomRange(-1f, 1f));
            return direction.sqrMagnitude > 0.0001f
                ? direction.normalized * magnitude
                : Vector3.up * magnitude;
        }

        private float RandomRange(float minimum, float maximum)
        {
            return Mathf.Lerp(minimum, maximum, (float)random.NextDouble());
        }

        private static float WrapAxis(float value, float centre, float extent)
        {
            var width = extent * 2f;
            var offset = value - centre;
            if (offset >= -extent && offset <= extent)
            {
                return value;
            }

            return centre + Mathf.Repeat(offset + extent, width) - extent;
        }

        private static float Hash01(int index, int count)
        {
            if (count <= 1)
            {
                return 0.5f;
            }

            var value = unchecked((uint)(index * 747796405) + 2891336453u);
            value = ((value >> ((int)(value >> 28) + 4)) ^ value) * 277803737u;
            value = (value >> 22) ^ value;
            return (value & 0x00FFFFFFu) / 16777215f;
        }

        private static Vector2 OrderedPositiveRange(Vector2 range, float minimum)
        {
            var low = Mathf.Max(minimum, Mathf.Min(range.x, range.y));
            var high = Mathf.Max(low, Mathf.Max(range.x, range.y));
            return new Vector2(low, high);
        }

        private static Material CreateParticleMaterial(
            Shader shader,
            string materialName,
            Color color,
            Texture texture,
            bool additive)
        {
            var material = new Material(shader)
            {
                name = materialName,
                hideFlags = HideFlags.DontSave,
            };

            material.SetFloat("_Surface", additive ? 1f : 0f);
            material.SetFloat("_Blend", additive ? 2f : 0f);
            material.SetInt("_SrcBlend", additive ? (int)BlendMode.SrcAlpha : (int)BlendMode.One);
            material.SetInt("_DstBlend", additive ? (int)BlendMode.One : (int)BlendMode.Zero);
            material.SetInt("_SrcBlendAlpha", (int)BlendMode.One);
            material.SetInt("_DstBlendAlpha", additive ? (int)BlendMode.One : (int)BlendMode.Zero);
            material.SetInt("_ZWrite", additive ? 0 : 1);
            if (material.HasProperty("_Cull"))
            {
                material.SetInt("_Cull", (int)CullMode.Off);
            }

            if (additive)
            {
                material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                material.renderQueue = (int)RenderQueue.Transparent;
                material.SetOverrideTag("RenderType", "Transparent");
                material.SetShaderPassEnabled("DepthOnly", false);
                material.SetShaderPassEnabled("ShadowCaster", false);
            }
            else
            {
                material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
                material.renderQueue = (int)RenderQueue.Geometry;
                material.SetOverrideTag("RenderType", "Opaque");
            }

            if (texture != null && material.HasProperty("_BaseMap"))
            {
                material.SetTexture("_BaseMap", texture);
            }
            SetMaterialColor(material, color);
            return material;
        }

        private static void SetMaterialColor(Material material, Color color)
        {
            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", color);
            }
            else if (material.HasProperty("_Color"))
            {
                material.SetColor("_Color", color);
            }
        }

        private static Texture2D CreateSoftParticleTexture()
        {
            const int resolution = 64;
            var texture = new Texture2D(
                resolution,
                resolution,
                TextureFormat.RGBA32,
                false,
                true)
            {
                name = "AC6 Soft Particle (Runtime)",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.DontSave,
            };

            var pixels = new Color32[resolution * resolution];
            for (var y = 0; y < resolution; y++)
            {
                for (var x = 0; x < resolution; x++)
                {
                    var uv = new Vector2(
                        (x + 0.5f) / resolution * 2f - 1f,
                        (y + 0.5f) / resolution * 2f - 1f);
                    var radial = Mathf.Clamp01(1f - uv.magnitude);
                    var alpha = radial * radial * (3f - 2f * radial);
                    pixels[y * resolution + x] = new Color(1f, 1f, 1f, alpha);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        private static Mesh CreateDebrisMesh()
        {
            var mesh = new Mesh
            {
                name = "AC6 Debris Shard (Runtime)",
                hideFlags = HideFlags.DontSave,
                vertices = new[]
                {
                    new Vector3(-0.55f, -0.18f, -0.3f),
                    new Vector3(0.62f, -0.12f, -0.22f),
                    new Vector3(0.18f, 0.44f, -0.08f),
                    new Vector3(-0.1f, 0.08f, 0.52f),
                },
                triangles = new[]
                {
                    0, 2, 1,
                    0, 1, 3,
                    1, 2, 3,
                    2, 0, 3,
                },
            };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void DestroyRuntimeObject(UnityEngine.Object runtimeObject)
        {
            if (runtimeObject == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Destroy(runtimeObject);
            }
            else
            {
                DestroyImmediate(runtimeObject);
            }
        }
    }
}
