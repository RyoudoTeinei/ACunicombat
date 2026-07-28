using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace ACunicombat.Mechs
{
    public enum MechThermalVisionMode
    {
        Off = 0,
        WhiteHot = 1,
        BlackHot = 2,
    }

    /// <summary>
    /// 热成像视野编排（`~ 键循环 关闭→白热→黑热）：
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
        private static readonly int WindowId = Shader.PropertyToID("_AC6ThermalWindow");
        private static readonly int ScreenParamsId = Shader.PropertyToID("_AC6ThermalScreenParams");
        private static readonly int ClarityParamsId = Shader.PropertyToID("_AC6ThermalClarityParams");

        [Tooltip("Installer 会把两个 feature 资产引用填进来；为空时按名字反射查找激活管线。")]
        [SerializeField] private ScriptableRendererFeature thermalObjectsFeature;
        [SerializeField] private ScriptableRendererFeature thermalScreenFeature;

        [Header("AGC / 显示映射")]
        [Tooltip("未挂热源物体的默认温度，须与 AC6ThermalRadiance 材质的 _ThermalTemp 默认值一致。")]
        [SerializeField] private float defaultSceneTemperatureK = 240f;
        [SerializeField] private float agcSmoothingSeconds = 0.35f;
        [Tooltip("窗口下限相对最冷物体辐射的余量系数（<1 保留冷端灰阶）。")]
        [SerializeField] private float windowLowMargin = 0.85f;
        [SerializeField] private float windowHighMargin = 1.05f;

        [Header("传感器质感")]
        [Range(1f, 4f)] [SerializeField] private float detectorPixelSize = 1.25f;
        [Range(0f, 0.05f)] [SerializeField] private float noiseAmplitude = 0.008f;
        [Range(0f, 0.2f)] [SerializeField] private float backgroundLevel = 0.025f;
        [Range(0f, 1f)] [SerializeField] private float sharpness = 0.38f;
        [Range(0.8f, 1.5f)] [SerializeField] private float contrast = 1.1f;
        [Range(0f, 1f)] [SerializeField] private float backgroundNoiseScale = 0.08f;

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

        public MechThermalVisionMode Mode => mode;

        public void CycleMode()
        {
            SetMode(mode switch
            {
                MechThermalVisionMode.Off => MechThermalVisionMode.WhiteHot,
                MechThermalVisionMode.WhiteHot => MechThermalVisionMode.BlackHot,
                _ => MechThermalVisionMode.Off,
            });
        }

        public void SetMode(MechThermalVisionMode next)
        {
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

            mode = next;
            var on = mode != MechThermalVisionMode.Off;
            MechThermalSignature.RendererTemperatureOutputEnabled = on;
            thermalObjectsFeature?.SetActive(on);
            thermalScreenFeature?.SetActive(on);
            if (on)
            {
                // 切入瞬间先用一个合理窗口，避免 AGC 收敛前整屏过曝。
                windowLow = Radiance(defaultSceneTemperatureK) * windowLowMargin;
                windowHigh = Radiance(1200f);
                nextAutoTagTime = 0f;
                PushGlobals();
            }

            Debug.Log($"[MechThermalVision] Mode: {mode}.", this);
        }

        private void Awake()
        {
            ResolveFeatures();
            MechThermalSignature.RendererTemperatureOutputEnabled = false;
            // 场景可能带着上次运行留下的激活状态；开局强制与 Off 一致。
            thermalObjectsFeature?.SetActive(false);
            thermalScreenFeature?.SetActive(false);
        }

        private void OnDisable()
        {
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
            var minK = defaultSceneTemperatureK;
            var maxK = defaultSceneTemperatureK;

            var signatures = MechThermalSignature.Registry;
            for (var i = 0; i < signatures.Count; i++)
            {
                for (var c = 0; c < 4; c++)
                {
                    var k = signatures[i].GetChannelTemperature((MechThermalChannel)c);
                    if (k < minK) minK = k;
                    if (k > maxK) maxK = k;
                }
            }

            var emitters = MechThermalPointEmitter.Registry;
            for (var i = 0; i < emitters.Count; i++)
            {
                var k = emitters[i].TemperatureK;
                if (k < minK) minK = k;
                if (k > maxK) maxK = k;
            }

            var targetLow = Radiance(minK) * windowLowMargin;
            var targetHigh = Mathf.Max(Radiance(maxK) * windowHighMargin, targetLow + 1e-4f);
            var blend = 1f - Mathf.Exp(-dt / Mathf.Max(0.02f, agcSmoothingSeconds));
            windowLow = Mathf.Lerp(windowLow, targetLow, blend);
            windowHigh = Mathf.Lerp(windowHigh, targetHigh, blend);
        }

        private void PushGlobals()
        {
            Shader.SetGlobalVector(WindowId, new Vector4(
                windowLow, 1f / Mathf.Max(1e-5f, windowHigh - windowLow), 0f, 0f));
            Shader.SetGlobalVector(ScreenParamsId, new Vector4(
                detectorPixelSize,
                noiseAmplitude,
                backgroundLevel,
                mode == MechThermalVisionMode.BlackHot ? -1f : 1f));
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
