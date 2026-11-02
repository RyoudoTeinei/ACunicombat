using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// GPU replacement for the per-missile Shuriken plume layers. A missile plume still
    /// configures its ParticleSystems as usual; they are handed here, their settings are
    /// read back (lifetime, speed, size, rate, cone, curves, tint, stretch) and the systems
    /// are deactivated. All emitters of a layer share one GPU particle ring that a compute
    /// shader spawns and advances; each layer is one procedural draw. The CPU cost per
    /// missile is a transform read and a few floats per frame.
    /// </summary>
    [DefaultExecutionOrder(10000)]
    [AddComponentMenu("")]
    public sealed class MechGpuPlumeSystem : MonoBehaviour
    {
        /// <summary>Use GPU plumes when the platform supports compute shaders.</summary>
        public static bool Enabled = true;

        public static bool Supported =>
            SystemInfo.supportsComputeShaders && Shaders.Compute != null && Shaders.Render != null;

        private const int CurveSamples = 64;
        private const int LayerCount = 3;

        private static MechGpuPlumeSystem instance;
        private static Material thermalOverride;

        private struct EmitterData
        {
            public Vector3 Position; public float Scale;
            public Vector3 Forward; public float Burning;   // 1 burning, 0 idle, -1 removed
            public Vector3 Up; public float Pad0;
            public Vector3 Previous; public float Pad1;     // last frame's nozzle position
        }

        private sealed class EmitterState
        {
            public Transform Nozzle;
            public Behaviour Owner;
            public float Scale;
            public bool Burning, Removed, HasPrevious;
            public Vector3 Previous;
            public readonly float[] Carry = new float[LayerCount];
        }

        private sealed class Layer
        {
            public bool Configured, WorldSpace, Stretch;
            public float LifetimeMin, LifetimeMax, SpeedMin, SpeedMax, SizeMin, SizeMax;
            public float ConeTan, ConeRadius, Rate, LengthScale, VelocityScale, ReferenceScale = 1;
            public int MaxPerEmitter;
            public Color Tint;
            public Texture Texture;
            public ComputeBuffer Particles;
            public int Capacity, Head;
            public Material Material;
            public MaterialPropertyBlock Properties;
        }

        private static class Shaders
        {
            private static ComputeShader compute; private static Shader render; private static bool loaded;
            public static ComputeShader Compute { get { Load(); return compute; } }
            public static Shader Render { get { Load(); return render; } }
            private static void Load()
            {
                if (loaded) return; loaded = true;
                compute = Resources.Load<ComputeShader>("Shaders/MechGpuPlume");
                render = Resources.Load<Shader>("Shaders/MechGpuPlume");
            }
        }

        private readonly List<EmitterState> emitters = new();
        private readonly Stack<int> freeSlots = new();
        private readonly List<int> pendingFree = new();
        private readonly Layer[] layers = { new(), new(), new() };
        private EmitterData[] emitterData = new EmitterData[64];
        private ComputeBuffer emitterBuffer;
        private ComputeBuffer requestBuffer;
        private uint[] requests = new uint[256];
        private Texture2D curves;
        private ComputeShader compute;
        private int spawnKernel, advanceKernel;
        private uint frameSeed;

        public static MechGpuPlumeSystem Instance
        {
            get
            {
                if (instance == null && Application.isPlaying)
                {
                    var host = new GameObject("GPU missile plumes") { hideFlags = HideFlags.HideInHierarchy };
                    instance = host.AddComponent<MechGpuPlumeSystem>();
                }
                return instance;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() { instance = null; }

        /// <summary>
        /// Takes over the given Shuriken layers (core, shroud, smoke; any may be null). Returns
        /// a handle for SetBurning/Unregister. The systems are read once and deactivated.
        /// </summary>
        public int Register(Behaviour owner, Transform nozzle, float scale, ParticleSystem core, ParticleSystem shroud, ParticleSystem smoke)
        {
            var systems = new[] { core, shroud, smoke };
            for (int i = 0; i < LayerCount; i++)
            {
                if (systems[i] == null) continue;
                if (!layers[i].Configured) Capture(layers[i], i, systems[i], scale);
                systems[i].Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                systems[i].gameObject.SetActive(false);
            }
            int slot;
            if (freeSlots.Count > 0) slot = freeSlots.Pop();
            else { slot = emitters.Count; emitters.Add(null); }
            emitters[slot] = new EmitterState { Owner = owner, Nozzle = nozzle, Scale = scale };
            return slot;
        }

        public void SetBurning(int handle, bool burning)
        {
            if ((uint)handle < (uint)emitters.Count && emitters[handle] != null) emitters[handle].Burning = burning;
        }

        /// <summary>Like destroying the Shuriken systems: this emitter's particles vanish.</summary>
        public void Unregister(int handle)
        {
            if ((uint)handle >= (uint)emitters.Count || emitters[handle] == null || emitters[handle].Removed) return;
            emitters[handle].Removed = true;
            pendingFree.Add(handle);
        }

        private void Capture(Layer layer, int index, ParticleSystem system, float scale)
        {
            var main = system.main;
            var shape = system.shape;
            var emission = system.emission;
            var renderer = system.GetComponent<ParticleSystemRenderer>();
            layer.Configured = true;
            layer.ReferenceScale = Mathf.Max(1e-3f, scale);
            layer.WorldSpace = main.simulationSpace == ParticleSystemSimulationSpace.World;
            layer.LifetimeMin = main.startLifetime.constantMin; layer.LifetimeMax = main.startLifetime.constantMax;
            layer.SpeedMin = main.startSpeed.constantMin; layer.SpeedMax = main.startSpeed.constantMax;
            layer.SizeMin = main.startSize.constantMin; layer.SizeMax = main.startSize.constantMax;
            if (main.startLifetime.mode == ParticleSystemCurveMode.Constant) layer.LifetimeMin = layer.LifetimeMax = main.startLifetime.constant;
            if (main.startSpeed.mode == ParticleSystemCurveMode.Constant) layer.SpeedMin = layer.SpeedMax = main.startSpeed.constant;
            if (main.startSize.mode == ParticleSystemCurveMode.Constant) layer.SizeMin = layer.SizeMax = main.startSize.constant;
            layer.MaxPerEmitter = main.maxParticles;
            layer.Rate = emission.rateOverTime.constant;
            layer.ConeTan = shape.enabled ? Mathf.Tan(shape.angle * Mathf.Deg2Rad) : 0;
            layer.ConeRadius = shape.enabled ? shape.radius : 0;
            layer.Stretch = renderer != null && renderer.renderMode == ParticleSystemRenderMode.Stretch;
            layer.LengthScale = renderer != null ? renderer.lengthScale : 1;
            layer.VelocityScale = renderer != null ? renderer.velocityScale : 0;
            var material = renderer != null ? renderer.sharedMaterial : null;
            layer.Tint = material != null && material.HasProperty("_BaseColor") ? material.GetColor("_BaseColor") : Color.white;
            layer.Texture = material != null ? material.mainTexture : null;
            BakeCurves(index, system);
        }

        private void BakeCurves(int index, ParticleSystem system)
        {
            if (curves == null)
            {
                curves = new Texture2D(CurveSamples, LayerCount * 2, TextureFormat.RGBAHalf, false, true)
                { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
                var white = new Color[CurveSamples * LayerCount * 2];
                for (int i = 0; i < white.Length; i++) white[i] = Color.white;
                curves.SetPixels(white);
            }
            var colour = system.colorOverLifetime;
            var size = system.sizeOverLifetime;
            for (int x = 0; x < CurveSamples; x++)
            {
                float t = x / (CurveSamples - 1f);
                var c = colour.enabled ? colour.color.Evaluate(t) : Color.white;
                // Shuriken applies particle colours in the active colour space (renderer default).
                if (QualitySettings.activeColorSpace == ColorSpace.Linear) { var a = c.a; c = c.linear; c.a = a; }
                float s = size.enabled ? size.size.Evaluate(t) : 1f;
                curves.SetPixel(x, index, c);
                curves.SetPixel(x, index + LayerCount, new Color(s, s, s, 1));
            }
            curves.Apply(false, false);
        }

        private void OnEnable()
        {
            compute = Shaders.Compute;
            if (compute != null) { spawnKernel = compute.FindKernel("Spawn"); advanceKernel = compute.FindKernel("Advance"); }
        }

        private void LateUpdate()
        {
            if (compute == null || Shaders.Render == null) return;
            float dt = Time.deltaTime;
            int count = emitters.Count;
            if (count == 0) return;
            if (emitterData.Length < count) emitterData = new EmitterData[Mathf.NextPowerOfTwo(count)];
            EnsureBuffer(ref emitterBuffer, emitterData.Length, System.Runtime.InteropServices.Marshal.SizeOf<EmitterData>());
            for (int i = 0; i < count; i++)
            {
                var e = emitters[i];
                if (e == null || e.Removed || e.Nozzle == null || e.Owner == null)
                {
                    emitterData[i].Burning = -1;
                    if (e != null && !e.Removed) Unregister(i);
                    continue;
                }
                if (!e.Owner.isActiveAndEnabled)
                {
                    // A deactivated missile hides its Shuriken children with their particles.
                    emitterData[i].Burning = -1; e.Carry[0] = e.Carry[1] = e.Carry[2] = 0; e.HasPrevious = false;
                    continue;
                }
                var position = e.Nozzle.position;
                // Spawns are spread along the nozzle path since last frame, like Shuriken's
                // sub-frame emission; a jump far beyond any flight speed is a teleport.
                var previous = e.HasPrevious ? e.Previous : position;
                if ((position - previous).sqrMagnitude > Mathf.Pow(Mathf.Max(50f, 3000f * dt), 2)) previous = position;
                emitterData[i] = new EmitterData
                {
                    Position = position, Scale = e.Scale, Forward = e.Nozzle.forward, Up = e.Nozzle.up,
                    Burning = e.Burning ? 1 : 0, Previous = previous,
                };
                e.Previous = position; e.HasPrevious = true;
            }
            emitterBuffer.SetData(emitterData, 0, 0, count);
            frameSeed++;
            bool thermal = MechThermalSignature.RendererTemperatureOutputEnabled;
            for (int l = 0; l < LayerCount; l++)
            {
                var layer = layers[l];
                if (!layer.Configured) continue;
                Simulate(layer, l, count, dt);
                Draw(layer, l, thermal);
            }
            // Removed emitters were killed by this frame's Advance: their slots are reusable.
            foreach (var slot in pendingFree) { emitters[slot] = null; freeSlots.Push(slot); }
            pendingFree.Clear();
        }

        private void Simulate(Layer layer, int index, int emitterCount, float dt)
        {
            int perEmitter = Mathf.Min(layer.MaxPerEmitter, Mathf.CeilToInt(layer.Rate * layer.LifetimeMax * 1.25f) + 8);
            int capacity = Mathf.NextPowerOfTwo(Mathf.Max(1024, perEmitter * emitterCount));
            if (layer.Particles == null || layer.Capacity < capacity)
            {
                layer.Particles?.Release();
                layer.Particles = new ComputeBuffer(capacity, System.Runtime.InteropServices.Marshal.SizeOf<GpuParticle>());
                layer.Particles.SetData(new GpuParticle[capacity]);
                layer.Capacity = capacity; layer.Head = 0;
            }
            // Spawn list: one request per burning emitter, exact rate*dt with fractional carry.
            int requestCount = 0, total = 0;
            for (int i = 0; i < emitterCount; i++)
            {
                var e = emitters[i];
                if (e == null || e.Removed || !e.Burning || e.Nozzle == null || e.Owner == null || !e.Owner.isActiveAndEnabled) { if (e != null) e.Carry[index] = 0; continue; }
                float wanted = layer.Rate * dt + e.Carry[index];
                int spawn = (int)wanted; e.Carry[index] = wanted - spawn;
                if (spawn <= 0) continue;
                spawn = Mathf.Min(spawn, perEmitter);
                if (requests.Length < (requestCount + 1) * 4) System.Array.Resize(ref requests, requests.Length * 2);
                requests[requestCount * 4] = (uint)i;
                requests[requestCount * 4 + 1] = (uint)((layer.Head + total) % layer.Capacity);
                requests[requestCount * 4 + 2] = (uint)spawn;
                requests[requestCount * 4 + 3] = (uint)total;
                requestCount++; total += spawn;
            }
            layer.Head = (layer.Head + total) % layer.Capacity;

            compute.SetFloat("_DeltaTime", dt);
            compute.SetInt("_Capacity", layer.Capacity);
            compute.SetBuffer(advanceKernel, "_Particles", layer.Particles);
            compute.SetBuffer(advanceKernel, "_Emitters", emitterBuffer);
            compute.Dispatch(advanceKernel, (layer.Capacity + 63) / 64, 1, 1);
            if (total == 0) return;

            EnsureBuffer(ref requestBuffer, requests.Length / 4, 16);
            requestBuffer.SetData(requests, 0, 0, requestCount * 4);
            float scaleRatio = 1f / layer.ReferenceScale;
            compute.SetVector("_Lifetime", new Vector4(layer.LifetimeMin, layer.LifetimeMax));
            compute.SetVector("_Speed", new Vector4(layer.SpeedMin * scaleRatio, layer.SpeedMax * scaleRatio));
            compute.SetVector("_Size", new Vector4(layer.SizeMin * scaleRatio, layer.SizeMax * scaleRatio));
            compute.SetVector("_Cone", new Vector4(layer.ConeTan, layer.ConeRadius * scaleRatio, layer.WorldSpace ? 1 : 0, 0));
            compute.SetInt("_RequestCount", requestCount);
            compute.SetInt("_TotalSpawn", total);
            compute.SetInt("_FrameSeed", (int)(frameSeed * 3 + (uint)index));
            compute.SetBuffer(spawnKernel, "_Particles", layer.Particles);
            compute.SetBuffer(spawnKernel, "_Emitters", emitterBuffer);
            compute.SetBuffer(spawnKernel, "_Requests", requestBuffer);
            compute.Dispatch(spawnKernel, (total + 63) / 64, 1, 1);
        }

        private void Draw(Layer layer, int index, bool thermal)
        {
            if (layer.Particles == null) return;
            if (layer.Material == null)
            {
                layer.Material = new Material(Shaders.Render) { hideFlags = HideFlags.HideAndDontSave, renderQueue = (int)RenderQueue.Transparent };
                layer.Properties = new MaterialPropertyBlock();
            }
            var props = layer.Properties;
            props.SetBuffer("_Particles", layer.Particles);
            props.SetBuffer("_Emitters", emitterBuffer);
            props.SetTexture("_Curves", curves);
            if (layer.Texture != null) props.SetTexture("_BaseMap", layer.Texture);
            // Thermal view: hot plumes render as saturated white, like MechEffectThermalAppearance.
            props.SetColor("_Tint", thermal ? new Color(1.6f, 1.6f, 1.6f, 1f) : layer.Tint);
            props.SetFloat("_CurveRow", index);
            props.SetFloat("_WorldSpace", layer.WorldSpace ? 1 : 0);
            props.SetFloat("_Stretch", layer.Stretch ? 1 : 0);
            props.SetFloat("_LengthScale", layer.LengthScale);
            props.SetFloat("_VelocityScale", layer.VelocityScale);
            var rp = new RenderParams(layer.Material)
            {
                worldBounds = new Bounds(Vector3.zero, Vector3.one * 1e7f),
                matProps = props,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
            };
            Graphics.RenderPrimitives(rp, MeshTopology.Triangles, layer.Capacity * 6);
        }

        private static void EnsureBuffer(ref ComputeBuffer buffer, int count, int stride)
        {
            if (buffer != null && buffer.count >= count) return;
            buffer?.Release();
            buffer = new ComputeBuffer(Mathf.Max(1, count), stride);
        }

        private void OnDestroy()
        {
            emitterBuffer?.Release(); requestBuffer?.Release();
            foreach (var layer in layers) { layer.Particles?.Release(); if (layer.Material != null) Destroy(layer.Material); }
            if (curves != null) Destroy(curves);
            if (instance == this) instance = null;
        }

        private struct GpuParticle
        {
            public Vector3 Position; public float Age;
            public Vector3 Velocity; public float Lifetime;
            public float Size; public uint Emitter; public float Seed; public float Pad;
        }
    }
}
