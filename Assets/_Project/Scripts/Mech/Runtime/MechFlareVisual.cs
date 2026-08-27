using UnityEngine;
using UnityEngine.Rendering;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// 热诱视觉的惯性运动与燃烧产物。热诱和每一团烟都在世界空间中独立滑行。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MechFlareVisual : MonoBehaviour
    {
        private Vector3 velocity;
        private float flareLifetimeRemaining;
        private float smokeEmissionRate;
        private float smokeLifetime;
        private float relativeExhaustSpeed;
        private float expansionSpeed;
        private float smokeEmissionAccumulator;
        private ParticleSystem flareCore;
        private ParticleSystem smoke;

        public void Configure(
            Vector3 initialVelocity,
            float flareLifetimeSeconds,
            float smokeEmissionRatePerSecond,
            float smokeLifetimeSeconds,
            float smokeRelativeExhaustMetersPerSecond,
            float smokeExpansionMetersPerSecond,
            Material flareMaterial,
            Material smokeMaterial)
        {
            velocity = initialVelocity;
            flareLifetimeRemaining = Mathf.Max(0.5f, flareLifetimeSeconds);
            smokeEmissionRate = Mathf.Max(1f, smokeEmissionRatePerSecond);
            smokeLifetime = Mathf.Max(0.1f, smokeLifetimeSeconds);
            relativeExhaustSpeed = Mathf.Max(0f, smokeRelativeExhaustMetersPerSecond);
            expansionSpeed = Mathf.Max(0f, smokeExpansionMetersPerSecond);

            flareCore = BuildFlareCore(flareMaterial);
            smoke = BuildSmokeSystem(smokeMaterial);
            flareCore.Play(withChildren: false);
        }

        private void Update()
        {
            var deltaTime = Mathf.Min(Time.deltaTime, flareLifetimeRemaining);
            if (deltaTime <= 0f)
            {
                return;
            }

            var previousPosition = transform.position;
            var nextPosition = previousPosition + velocity * deltaTime;
            EmitSmoke(previousPosition, nextPosition, deltaTime);
            transform.position = nextPosition;
            flareLifetimeRemaining -= deltaTime;

            if (flareLifetimeRemaining > 0f)
            {
                return;
            }

            flareCore.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            enabled = false;
            Destroy(gameObject, smokeLifetime + 0.2f);
        }

        private void EmitSmoke(Vector3 from, Vector3 to, float deltaTime)
        {
            smokeEmissionAccumulator += smokeEmissionRate * deltaTime;
            var emissionCount = Mathf.FloorToInt(smokeEmissionAccumulator);
            smokeEmissionAccumulator -= emissionCount;
            if (emissionCount <= 0)
            {
                return;
            }

            var travelDirection = velocity.sqrMagnitude > 0.001f
                ? velocity.normalized
                : transform.forward;
            for (var index = 0; index < emissionCount; index++)
            {
                var progress = (index + 0.5f) / emissionCount;
                var smokePosition = Vector3.Lerp(from, to, progress) +
                                    Random.insideUnitSphere * 0.08f;
                var smokeVelocity = velocity -
                                    travelDirection * relativeExhaustSpeed +
                                    Random.insideUnitSphere * expansionSpeed;
                smoke.Emit(new ParticleSystem.EmitParams
                {
                    position = smokePosition,
                    velocity = smokeVelocity,
                    startLifetime = smokeLifetime * Random.Range(0.82f, 1.12f),
                    startSize = Random.Range(0.2f, 0.38f),
                    rotation = Random.Range(0f, 360f),
                }, 1);
            }
        }

        private ParticleSystem BuildFlareCore(Material material)
        {
            var root = new GameObject("Hot Core");
            root.transform.SetParent(transform, false);
            var particles = root.AddComponent<ParticleSystem>();
            particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = particles.main;
            main.loop = true;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.maxParticles = 16;
            main.startLifetime = 0.08f;
            main.startSpeed = 0f;
            main.startSize = new ParticleSystem.MinMaxCurve(0.34f, 0.5f);
            main.startColor = Color.white;
            main.gravityModifier = 0f;

            var emission = particles.emission;
            emission.rateOverTime = 90f;
            var shape = particles.shape;
            shape.enabled = false;

            var color = particles.colorOverLifetime;
            color.enabled = true;
            color.color = MechEffectResources.CreateBlackbodyCoolingGradient();

            var renderer = root.GetComponent<ParticleSystemRenderer>();
            MechEffectResources.ConfigureUnlitRenderer(renderer, material);
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return particles;
        }

        private ParticleSystem BuildSmokeSystem(Material material)
        {
            var root = new GameObject("Independent Smoke Puffs");
            root.transform.SetParent(transform, false);
            var particles = root.AddComponent<ParticleSystem>();
            particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = particles.main;
            main.loop = false;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = Mathf.Max(64, Mathf.CeilToInt(smokeEmissionRate * smokeLifetime * 1.4f));
            main.startSpeed = 0f;
            main.gravityModifier = 0f;

            var emission = particles.emission;
            emission.enabled = false;

            var size = particles.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(
                1f,
                new AnimationCurve(
                    new Keyframe(0f, 0.55f),
                    new Keyframe(0.3f, 1f),
                    new Keyframe(1f, 1.65f)));

            var smokeGradient = new Gradient();
            smokeGradient.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(0.82f, 0.79f, 0.74f), 0f),
                    new GradientColorKey(new Color(0.5f, 0.52f, 0.55f), 1f),
                },
                new[]
                {
                    new GradientAlphaKey(0.3f, 0f),
                    new GradientAlphaKey(0.2f, 0.28f),
                    new GradientAlphaKey(0.08f, 0.68f),
                    new GradientAlphaKey(0f, 1f),
                });
            var color = particles.colorOverLifetime;
            color.enabled = true;
            color.color = new ParticleSystem.MinMaxGradient(smokeGradient);

            var renderer = root.GetComponent<ParticleSystemRenderer>();
            MechEffectResources.ConfigureUnlitRenderer(renderer, material);
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            particles.Play(withChildren: false);
            return particles;
        }
    }
}
