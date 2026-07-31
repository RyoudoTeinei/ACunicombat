using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace ACunicombat.Mechs
{
    public enum MechThermalVisionMode
    {
        Off = 0,

        // 1 曾是 WhiteHot，已移除。刻意留空不复用：旧场景里序列化的 1 会落在这个洞里，
        // SetMode/Awake 统一归一到 BlackHot，而不是被误读成另一个有效模式。
        BlackHot = 2,
    }

    /// <summary>
    /// 热成像视野编排（`~ 键在 关闭↔黑热 之间切换）：
    ///   - 开关渲染侧的两个 URP feature（AC6ThermalObjects 重画 + AC6ThermalScreen 后处理），
    ///     feature 由 AC6ThermalVisionInstaller 一次性装进激活的 Renderer 资产；
    ///   - CPU 端 AGC：遍历 MechThermalSignature/MechThermalPointEmitter 注册表求当前
    ///     场景温度域，换算成辐射亮度窗口推给 shader 全局量——不做 GPU 亮度回读；
    ///   - 热视野开启期间周期性给未标记的试验目标/导弹自动补挂点热源，
    ///     现有场景无需重建即可全员可见。
    /// </summary>
    public sealed class MechThermalVisionController : MonoBehaviour
    {
        private const string ObjectsFeatureName = "AC6ThermalObjects";
        private const string ScreenFeatureName = "AC6ThermalScreen";

        /// <summary>
        /// 场景构建器与运行时共用的名字，供 serialized 引用为空时按名查找。
        /// </summary>
        public const string ThermalVolumeObjectName = "AC6 Thermal Post";

        private static readonly int WindowId = Shader.PropertyToID("_AC6ThermalWindow");
        private static readonly int ScreenParamsId = Shader.PropertyToID("_AC6ThermalScreenParams");
        private static readonly int ClarityParamsId = Shader.PropertyToID("_AC6ThermalClarityParams");

        [Tooltip("Installer 会把两个 feature 资产引用填进来；为空时按名字反射查找激活管线。")]
        [SerializeField] private ScriptableRendererFeature thermalObjectsFeature;
        [SerializeField] private ScriptableRendererFeature thermalScreenFeature;

        [Tooltip(
            "观瞄模式专用后处理 Volume（高 priority，反盖战斗 Volume）。" +
            "热像屏幕 pass 注入在后处理之前，若让 ACES 与调色作用其上，" +
            "AGC 标定好的灰阶会被重新映射。为空时按名字查找。")]
        [SerializeField] private Volume thermalVolume;

        [Header("AGC / 显示映射")]
        [Tooltip("未挂热源物体的默认温度，须与 AC6ThermalRadiance 材质的 _ThermalTemp 默认值一致。")]
        [SerializeField] private float defaultSceneTemperatureK = 240f;
        [SerializeField] private float agcSmoothingSeconds = 0.35f;
        [Tooltip("窗口下限相对最冷物体辐射的余量系数（<1 保留冷端灰阶）。")]
        [SerializeField] private float windowLowMargin = 0.85f;
        [SerializeField] private float windowHighMargin = 1.05f;
        [Tooltip("AGC 窗口允许的辐射亮度动态范围上限。极热源（导弹 620K、AB 推进器 ~1400K）" +
                 "相对 240K 冷物体的辐射比可达千倍，线性映射会把中温机体压到固定天空背景灰" +
                 "之下而在太空中隐形；超过上限的热源饱和为纯白。")]
        [Min(1f)] [SerializeField] private float agcMaxWindowRadianceRatio = 32f;
        [Tooltip("AGC 至少保留到该温度的显示余量，防止场景中只有冷机体时全部过曝。")]
        [Min(250f)] [SerializeField] private float agcMinimumHighTemperatureK = 360f;
        [Tooltip("只对视野内目标的面积主导温度取该百分位；推进器和导弹热点不参与计量。")]
        [Range(0.5f, 1f)] [SerializeField] private float agcHighPercentile = 0.85f;
        [Tooltip("归一化辐射的暗部软压缩强度。0 为线性，数值越大越能保留冷目标。")]
        [Range(0f, 63f)] [SerializeField] private float agcShadowCompression = 31f;
        [Tooltip("热像高光响应。较低值让机体保留更多浅灰层次，极热热点仍可达到纯白。")]
        [Range(0.5f, 3f)] [SerializeField] private float thermalHighlightResponse = 1.3f;

        [Header("传感器质感")]
        [Range(1f, 4f)] [SerializeField] private float detectorPixelSize = 1.25f;
        [Range(0f, 0.05f)] [SerializeField] private float noiseAmplitude = 0.008f;
        [Range(0f, 0.2f)] [SerializeField] private float backgroundLevel = 0.025f;
        [Range(0f, 1f)] [SerializeField] private float sharpness = 0.38f;
        [Range(0.8f, 1.5f)] [SerializeField] private float contrast = 1.1f;
        [Range(0f, 1f)] [SerializeField] private float backgroundNoiseScale = 0.08f;

        [Header("音频")]
        [Tooltip("从关闭切入热成像时播放一次；为空时加载 Resources/Audio/Thermal/thermal_imager_power_on。")]
        [SerializeField] private AudioClip startupClip;
        [Range(0f, 1f)] [SerializeField] private float startupVolume = 0.65f;
        [Tooltip("热成像开启期间循环播放；为空时加载 Resources/Audio/Thermal/thermal_sensor_loop。")]
        [SerializeField] private AudioClip backgroundLoopClip;
        [Range(0f, 1f)] [SerializeField] private float backgroundLoopVolume = 0.35f;

        [Header("自动标记")]
        [SerializeField] private float autoTagIntervalSeconds = 0.5f;
        [SerializeField] private float targetTemperatureK = 330f;
        [SerializeField] private float projectileTemperatureK = 620f;

        private const float NominalEmissivity = 0.92f;

        private MechThermalVisionMode mode = MechThermalVisionMode.Off;
        private float windowLow;
        private float windowHigh;
        private float nextAutoTagTime;
        private bool featuresResolved;
        private bool warnedMissingFeatures;
        private Camera meteringCamera;
        private AudioSource startupAudioSource;
        private AudioSource backgroundAudioSource;
        private readonly List<float> agcMeteringSamples = new();

        public MechThermalVisionMode Mode => mode;

        public void CycleMode()
        {
            SetMode(
                mode == MechThermalVisionMode.Off
                    ? MechThermalVisionMode.BlackHot
                    : MechThermalVisionMode.Off);
        }

        public void SetMode(MechThermalVisionMode next)
        {
            // 只剩黑热一种显示模式；任何非 Off 的取值（含旧场景残留的 WhiteHot=1）都归一到它。
            if (next != MechThermalVisionMode.Off)
            {
                next = MechThermalVisionMode.BlackHot;
            }

            ResolveFeatures();
            if (next != MechThermalVisionMode.Off &&
                (thermalObjectsFeature == null || thermalScreenFeature == null))
            {
                if (!warnedMissingFeatures)
                {
                    warnedMissingFeatures = true;
                    Debug.LogWarning(
                        "[MechThermalVision] Renderer feature 未安装。先执行 " +
                        "Tools > ACunicombat > AC6 > Install Thermal Vision。", this);
                }

                return;
            }

            var wasOff = mode == MechThermalVisionMode.Off;
            mode = next;
            var on = mode != MechThermalVisionMode.Off;
            MechThermalSignature.RendererTemperatureOutputEnabled = on;
            thermalObjectsFeature?.SetActive(on);
            thermalScreenFeature?.SetActive(on);

            // 观瞄显示链路整体切换，而不是在仪表读数上叠一层电影调色。
            ResolveThermalVolume();
            if (thermalVolume != null)
            {
                thermalVolume.enabled = on;
            }
            if (on)
            {
                AutoTagUntaggedHeatSources();
                CalculateAgcTarget(out windowLow, out windowHigh);
                nextAutoTagTime = Time.unscaledTime + autoTagIntervalSeconds;
                PushGlobals();
            }

            if (wasOff && on && startupClip != null && startupAudioSource != null)
            {
                startupAudioSource.PlayOneShot(startupClip, startupVolume);
            }

            if (on)
            {
                if (backgroundAudioSource != null &&
                    backgroundLoopClip != null &&
                    !backgroundAudioSource.isPlaying)
                {
                    backgroundAudioSource.Play();
                }
            }
            else
            {
                backgroundAudioSource?.Stop();
            }

            Debug.Log($"[MechThermalVision] Mode: {mode}.", this);
        }

        private void Awake()
        {
            // 未经 SetMode 就直接进场的旧序列化值在这里归一，避免 PushGlobals 推出白热符号。
            if (mode != MechThermalVisionMode.Off)
            {
                mode = MechThermalVisionMode.BlackHot;
            }

            if (startupClip == null)
            {
                startupClip = Resources.Load<AudioClip>(
                    "Audio/Thermal/thermal_imager_power_on");
            }
            if (backgroundLoopClip == null)
            {
                backgroundLoopClip = Resources.Load<AudioClip>(
                    "Audio/Thermal/thermal_sensor_loop");
            }
            startupClip?.LoadAudioData();
            backgroundLoopClip?.LoadAudioData();

            startupAudioSource = gameObject.AddComponent<AudioSource>();
            startupAudioSource.playOnAwake = false;
            startupAudioSource.loop = false;
            startupAudioSource.spatialBlend = 0f;

            backgroundAudioSource = gameObject.AddComponent<AudioSource>();
            backgroundAudioSource.playOnAwake = false;
            backgroundAudioSource.loop = true;
            backgroundAudioSource.spatialBlend = 0f;
            backgroundAudioSource.clip = backgroundLoopClip;
            backgroundAudioSource.volume = backgroundLoopVolume;

            ResolveFeatures();
            MechThermalSignature.RendererTemperatureOutputEnabled = false;
            // 场景可能带着上次运行留下的激活状态；开局强制与 Off 一致。
            thermalObjectsFeature?.SetActive(false);
            thermalScreenFeature?.SetActive(false);
        }

        private void OnDisable()
        {
            backgroundAudioSource?.Stop();
            MechThermalSignature.RendererTemperatureOutputEnabled = false;
            thermalObjectsFeature?.SetActive(false);
            thermalScreenFeature?.SetActive(false);
        }

        private void Update()
        {
            if (mode == MechThermalVisionMode.Off)
            {
                return;
            }

            UpdateAgcWindow(Time.deltaTime);
            PushGlobals();

            if (Time.unscaledTime >= nextAutoTagTime)
            {
                nextAutoTagTime = Time.unscaledTime + autoTagIntervalSeconds;
                AutoTagUntaggedHeatSources();
            }
        }

        private static float Radiance(float kelvin)
        {
            var t = kelvin * 0.001f;
            return NominalEmissivity * t * t * t * t;
        }

        private void UpdateAgcWindow(float dt)
        {
            CalculateAgcTarget(out var targetLow, out var targetHigh);
            var blend = 1f - Mathf.Exp(-dt / Mathf.Max(0.02f, agcSmoothingSeconds));
            windowLow = Mathf.Lerp(windowLow, targetLow, blend);
            windowHigh = Mathf.Lerp(windowHigh, targetHigh, blend);
        }

        private void CalculateAgcTarget(out float targetLow, out float targetHigh)
        {
            targetLow = Radiance(defaultSceneTemperatureK) * windowLowMargin;
            agcMeteringSamples.Clear();

            var camera = ResolveMeteringCamera();
            var signatures = MechThermalSignature.Registry;
            for (var i = 0; i < signatures.Count; i++)
            {
                var signature = signatures[i];
                if (signature == null ||
                    IsOwnship(signature) ||
                    !IsInsideMeteringFrustum(signature.transform.position, camera))
                {
                    continue;
                }

                // Armor occupies most target pixels. Hot nozzles and weapon impulses
                // should saturate instead of changing exposure for the entire image.
                agcMeteringSamples.Add(Radiance(
                    signature.GetChannelTemperature(MechThermalChannel.Skin)));
            }

            var emitters = MechThermalPointEmitter.Registry;
            for (var i = 0; i < emitters.Count; i++)
            {
                var emitter = emitters[i];
                if (emitter == null ||
                    emitter.GetComponent<MechTestProjectile>() != null ||
                    IsOwnship(emitter) ||
                    !IsInsideMeteringFrustum(emitter.transform.position, camera))
                {
                    continue;
                }

                agcMeteringSamples.Add(Radiance(emitter.TemperatureK));
            }

            var meteredHigh = Radiance(Mathf.Max(
                defaultSceneTemperatureK,
                agcMinimumHighTemperatureK));
            if (agcMeteringSamples.Count > 0)
            {
                agcMeteringSamples.Sort();
                meteredHigh = Mathf.Max(
                    meteredHigh,
                    PercentileSorted(agcMeteringSamples, agcHighPercentile));
            }

            targetHigh = Mathf.Max(
                meteredHigh * windowHighMargin,
                targetLow + 1e-4f);
            targetHigh = Mathf.Min(
                targetHigh,
                targetLow * Mathf.Max(1f, agcMaxWindowRadianceRatio) + 1e-4f);
        }

        private Camera ResolveMeteringCamera()
        {
            if (meteringCamera != null && meteringCamera.isActiveAndEnabled)
            {
                return meteringCamera;
            }

            meteringCamera = GetComponentInChildren<Camera>();
            if (meteringCamera == null)
            {
                meteringCamera = GetComponentInParent<Camera>();
            }
            if (meteringCamera == null)
            {
                meteringCamera = Camera.main;
            }

            return meteringCamera;
        }

        private bool IsOwnship(Component source)
        {
            var sourceTransform = source.transform;
            return sourceTransform == transform ||
                   sourceTransform.IsChildOf(transform) ||
                   transform.IsChildOf(sourceTransform);
        }

        private static bool IsInsideMeteringFrustum(Vector3 worldPosition, Camera camera)
        {
            if (camera == null)
            {
                return true;
            }

            const float viewportPadding = 0.1f;
            var viewport = camera.WorldToViewportPoint(worldPosition);
            return viewport.z >= camera.nearClipPlane &&
                   viewport.z <= camera.farClipPlane &&
                   viewport.x >= -viewportPadding &&
                   viewport.x <= 1f + viewportPadding &&
                   viewport.y >= -viewportPadding &&
                   viewport.y <= 1f + viewportPadding;
        }

        private static float PercentileSorted(List<float> sortedValues, float percentile)
        {
            if (sortedValues.Count == 1)
            {
                return sortedValues[0];
            }

            var position = Mathf.Clamp01(percentile) * (sortedValues.Count - 1);
            var lower = Mathf.FloorToInt(position);
            var upper = Mathf.Min(lower + 1, sortedValues.Count - 1);
            return Mathf.Lerp(sortedValues[lower], sortedValues[upper], position - lower);
        }

        private void PushGlobals()
        {
            Shader.SetGlobalVector(WindowId, new Vector4(
                windowLow,
                1f / Mathf.Max(1e-5f, windowHigh - windowLow),
                Mathf.Max(0f, agcShadowCompression),
                Mathf.Max(0.01f, thermalHighlightResponse)));
            Shader.SetGlobalVector(ScreenParamsId, new Vector4(
                detectorPixelSize,
                noiseAmplitude,
                backgroundLevel,
                // AC6ThermalScreen 用 w 的符号选择极性，负号即黑热。shader 仍保留两种极性，
                // 但白热被砍掉后这里只可能推负号（PushGlobals 只在开启时到达）。
                -1f));
            Shader.SetGlobalVector(ClarityParamsId, new Vector4(
                sharpness,
                contrast,
                backgroundNoiseScale,
                0f));
        }

        private void AutoTagUntaggedHeatSources()
        {
            var targets = FindObjectsByType<CombatTestTarget>();
            for (var i = 0; i < targets.Length; i++)
            {
                EnsureEmitter(targets[i].gameObject, targetTemperatureK);
            }

            var projectiles = FindObjectsByType<MechTestProjectile>();
            for (var i = 0; i < projectiles.Length; i++)
            {
                EnsureEmitter(projectiles[i].gameObject, projectileTemperatureK);
            }
        }

        private static void EnsureEmitter(GameObject go, float temperatureK)
        {
            if (go.GetComponent<MechThermalSignature>() != null ||
                go.GetComponent<MechThermalPointEmitter>() != null)
            {
                return;
            }

            go.AddComponent<MechThermalPointEmitter>().SetTemperature(temperatureK);
        }

        /// <summary>
        /// 由场景构建器在生成观瞄 Volume 后回填引用，避免运行时依赖按名查找。
        /// </summary>
        public void BindThermalVolume(Volume volume)
        {
            thermalVolume = volume;
        }

        private void ResolveThermalVolume()
        {
            if (thermalVolume != null)
            {
                return;
            }

            var volumeObject = GameObject.Find(ThermalVolumeObjectName);
            if (volumeObject != null)
            {
                thermalVolume = volumeObject.GetComponent<Volume>();
            }
        }

        /// <summary>
        /// serialized 引用为空时按名字在激活的 URP 管线里找 feature。
        /// m_RendererDataList 是 internal 序列化字段，只能反射；rendererFeatures 是公开属性。
        /// </summary>
        private void ResolveFeatures()
        {
            if (featuresResolved && thermalObjectsFeature != null && thermalScreenFeature != null)
            {
                return;
            }

            featuresResolved = true;
            if (thermalObjectsFeature != null && thermalScreenFeature != null)
            {
                return;
            }

            if (GraphicsSettings.currentRenderPipeline is not UniversalRenderPipelineAsset pipeline)
            {
                return;
            }

            var field = typeof(UniversalRenderPipelineAsset).GetField(
                "m_RendererDataList", BindingFlags.NonPublic | BindingFlags.Instance);
            if (field?.GetValue(pipeline) is not ScriptableRendererData[] dataList)
            {
                return;
            }

            foreach (var data in dataList)
            {
                if (data == null)
                {
                    continue;
                }

                foreach (var feature in data.rendererFeatures)
                {
                    if (feature == null)
                    {
                        continue;
                    }

                    if (thermalObjectsFeature == null && feature.name == ObjectsFeatureName)
                    {
                        thermalObjectsFeature = feature;
                    }
                    else if (thermalScreenFeature == null && feature.name == ScreenFeatureName)
                    {
                        thermalScreenFeature = feature;
                    }
                }
            }
        }
    }
}
