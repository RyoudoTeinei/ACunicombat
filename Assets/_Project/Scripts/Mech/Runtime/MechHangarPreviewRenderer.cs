using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace ACunicombat.Mechs
{
    [DisallowMultipleComponent]
    public sealed class MechHangarPreviewRenderer : MonoBehaviour
    {
        private const int PreviewLayer = 31;
        private const float DefaultZoom = 0.62f;
        private const float MinimumZoom = 0.28f;
        private const float MaximumZoom = 1.35f;
        private static readonly Vector3 PreviewOrigin = new Vector3(50000f, 50000f, 50000f);

        private GameObject previewStage;
        private GameObject contentRoot;
        private Camera previewCamera;
        private RenderTexture targetTexture;
        private VolumeProfile previewVolumeProfile;
        private ReflectionProbe hangarReflectionProbe;
        private Cubemap hangarReflectionCubemap;
        private Light overheadBankLight;
        private Light leftBankLight;
        private Light rearBankLight;
        private Texture2D staticPreviewImage;
        private Bounds contentBounds;
        // 轨道视角分成"目标"和"当前"两套。鼠标只写目标值，当前值每帧向目标做指数阻尼。
        // 直接把 Event.delta 累加到相机角度会抖：每帧的鼠标位移量本身是不均匀的
        // （12px、30px、8px、25px…），乘上灵敏度就是每帧转 5 度、13 度、3 度、11 度，
        // 转速忽快忽慢，加上没有运动模糊，看起来就是"整个在抖、每帧是一张朝向不同的图"。
        private float orbitYaw = -18f;
        private float orbitPitch = 7f;
        private float zoom = DefaultZoom;
        private float orbitYawTarget = -18f;
        private float orbitPitchTarget = 7f;
        private float zoomTarget = DefaultZoom;
        private int lastSmoothedFrame = -1;

        [Header("轨道视角手感")]
        [Tooltip("水平灵敏度：度 / 鼠标像素")]
        [SerializeField] private float orbitYawSensitivity = 0.45f;
        [Tooltip("垂直灵敏度：度 / 鼠标像素")]
        [SerializeField] private float orbitPitchSensitivity = 0.35f;
        [Tooltip("滚轮缩放灵敏度")]
        [SerializeField] private float zoomSensitivity = 0.08f;
        [Tooltip("跟随目标角度的时间常数（秒）。越小越跟手，越大越顺滑。0 = 关闭平滑。")]
        [SerializeField] private float orbitSmoothingTime = 0.07f;
        private bool hasRenderableContent;
        private MechAppearanceSettings appearance = new MechAppearanceSettings();

        public void SetAppearance(MechAppearanceSettings value)
        {
            appearance = value?.Clone() ?? new MechAppearanceSettings();
            if (contentRoot != null)
            {
                MechAppearanceApplicator.Apply(contentRoot, appearance);
            }
        }

        public void SetRenderingEnabled(bool value)
        {
            if (previewStage != null)
            {
                previewStage.SetActive(value);
            }
        }

        public void ShowLoadout(MechLoadoutDefinition loadout)
        {
            ClearContent();
            if (loadout == null)
            {
                return;
            }

            EnsureStage();
            contentRoot = new GameObject("HangarMechPreview")
            {
                hideFlags = HideFlags.DontSave
            };
            contentRoot.SetActive(false);
            contentRoot.transform.SetParent(previewStage.transform, false);

            var previewAssembler = contentRoot.AddComponent<MechAssembler>();
            previewAssembler.ConfigureRuntimePreview();
            previewAssembler.Loadout = loadout;
            previewAssembler.Assemble();
            MechAppearanceApplicator.Apply(contentRoot, appearance);
            PrepareContent(contentRoot);
            contentRoot.SetActive(true);
            FrameContent();
        }

        public void ShowPart(MechPartDefinition part)
        {
            ClearContent();
            if (part == null)
            {
                return;
            }

            staticPreviewImage = part.PreviewImage;
            if (part.PreviewModelPrefab == null)
            {
                return;
            }

            EnsureStage();
            contentRoot = new GameObject("HangarPartPreview")
            {
                hideFlags = HideFlags.DontSave
            };
            contentRoot.SetActive(false);
            contentRoot.transform.SetParent(previewStage.transform, false);

            var instance = Instantiate(part.PreviewModelPrefab, contentRoot.transform);
            instance.name = part.DisplayName + "_Preview";
            instance.transform.localPosition = part.LocalPosition;
            instance.transform.localRotation = Quaternion.Euler(part.LocalEulerAngles);
            instance.transform.localScale = part.LocalScale;
            MechAssembler.ConfigureStandaloneVisual(instance, part);
            MechAppearanceApplicator.Apply(contentRoot, appearance);
            PrepareContent(contentRoot);
            contentRoot.SetActive(true);
            FrameContent();
        }

        public void Draw(Rect rect)
        {
            if (rect.width < 8f || rect.height < 8f)
            {
                return;
            }

            EnsureStage();
            EnsureTarget(Mathf.RoundToInt(rect.width), Mathf.RoundToInt(rect.height));
            if (hasRenderableContent)
            {
                HandleOrbitInput(rect);
            }
            SmoothOrbitTowardsTarget();
            ApplyCameraPose();
            if (hasRenderableContent)
            {
                GUI.DrawTexture(rect, targetTexture, ScaleMode.StretchToFill, false);
            }
            else if (staticPreviewImage != null)
            {
                GUI.DrawTexture(rect, staticPreviewImage, ScaleMode.ScaleToFit, true);
            }
            else
            {
                GUI.DrawTexture(rect, targetTexture, ScaleMode.StretchToFill, false);
            }

            if (!hasRenderableContent && staticPreviewImage == null)
            {
                var style = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 15,
                    normal = { textColor = new Color(0.56f, 0.68f, 0.71f) }
                };
                GUI.Label(rect, "该部件没有可显示的外部模型", style);
            }
        }

        private void EnsureStage()
        {
            if (previewStage != null)
            {
                return;
            }

            previewStage = new GameObject("_MechHangarPreviewStage")
            {
                hideFlags = HideFlags.HideAndDontSave,
                layer = PreviewLayer
            };
            previewStage.transform.position = PreviewOrigin;

            var cameraObject = new GameObject("PreviewCamera")
            {
                hideFlags = HideFlags.HideAndDontSave,
                layer = PreviewLayer
            };
            cameraObject.transform.SetParent(previewStage.transform, false);
            previewCamera = cameraObject.AddComponent<Camera>();
            previewCamera.clearFlags = CameraClearFlags.SolidColor;
            previewCamera.backgroundColor = new Color(0.055f, 0.070f, 0.082f, 1f);
            previewCamera.cullingMask = 1 << PreviewLayer;
            previewCamera.nearClipPlane = 0.03f;
            previewCamera.farClipPlane = 5000f;
            previewCamera.fieldOfView = 34f;
            previewCamera.allowHDR = true;
            previewCamera.allowMSAA = true;
            previewCamera.enabled = false;

            ConfigurePostProcessing(cameraObject);
            CreateDirectionalLight(
                "PreviewKey",
                new Color(1f, 0.97f, 0.92f),
                1.85f,
                new Vector3(28f, -38f, 0f),
                LightShadows.Soft);
            CreateDirectionalLight(
                "PreviewFill",
                new Color(0.82f, 0.87f, 0.91f),
                0.42f,
                new Vector3(14f, 142f, 8f),
                LightShadows.None);
            CreateDirectionalLight(
                "PreviewRim",
                new Color(0.86f, 0.90f, 0.94f),
                0.68f,
                new Vector3(-32f, 205f, 0f),
                LightShadows.None);
            overheadBankLight = CreateSpotLight(
                "HangarOverheadBank",
                new Color(1f, 0.94f, 0.84f),
                82f,
                58f,
                LightShadows.Soft);
            leftBankLight = CreateSpotLight(
                "HangarLeftBank",
                new Color(0.76f, 0.86f, 1f),
                74f,
                50f,
                LightShadows.None);
            rearBankLight = CreateSpotLight(
                "HangarRearBank",
                new Color(0.66f, 0.78f, 1f),
                68f,
                46f,
                LightShadows.None);
            CreateHangarReflectionEnvironment();
        }

        private void ConfigurePostProcessing(GameObject cameraObject)
        {
            var cameraData = previewCamera.GetUniversalAdditionalCameraData();
            cameraData.renderPostProcessing = true;
            cameraData.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            cameraData.antialiasingQuality = AntialiasingQuality.High;
            cameraData.volumeLayerMask = 1 << PreviewLayer;
            cameraData.volumeTrigger = cameraObject.transform;

            previewVolumeProfile = ScriptableObject.CreateInstance<VolumeProfile>();
            previewVolumeProfile.name = "MechHangarPreviewProfile";
            previewVolumeProfile.hideFlags = HideFlags.HideAndDontSave;

            var tonemapping = previewVolumeProfile.Add<Tonemapping>(true);
            tonemapping.mode.Override(TonemappingMode.ACES);

            var color = previewVolumeProfile.Add<ColorAdjustments>(true);
            color.postExposure.Override(-0.05f);
            color.contrast.Override(5f);
            color.saturation.Override(-2f);
            color.colorFilter.Override(new Color(1f, 0.99f, 0.975f, 1f));

            var volumeObject = new GameObject("PreviewPostProcessing")
            {
                hideFlags = HideFlags.HideAndDontSave,
                layer = PreviewLayer
            };
            volumeObject.transform.SetParent(previewStage.transform, false);
            var volume = volumeObject.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 1000f;
            volume.sharedProfile = previewVolumeProfile;
        }

        private void CreateDirectionalLight(
            string objectName,
            Color color,
            float intensity,
            Vector3 eulerAngles,
            LightShadows shadows)
        {
            var lightObject = new GameObject(objectName)
            {
                hideFlags = HideFlags.HideAndDontSave,
                layer = PreviewLayer
            };
            lightObject.transform.SetParent(previewStage.transform, false);
            lightObject.transform.localRotation = Quaternion.Euler(eulerAngles);
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = color;
            light.intensity = intensity;
            light.cullingMask = 1 << PreviewLayer;
            light.shadows = shadows;
            if (shadows != LightShadows.None)
            {
                light.shadowStrength = 0.78f;
                light.shadowBias = 0.03f;
                light.shadowNormalBias = 0.22f;
                light.shadowResolution = LightShadowResolution.High;
            }
        }

        private Light CreateSpotLight(
            string objectName,
            Color color,
            float spotAngle,
            float innerSpotAngle,
            LightShadows shadows)
        {
            var lightObject = new GameObject(objectName)
            {
                hideFlags = HideFlags.HideAndDontSave,
                layer = PreviewLayer
            };
            lightObject.transform.SetParent(previewStage.transform, false);
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Spot;
            light.color = color;
            light.spotAngle = spotAngle;
            light.innerSpotAngle = innerSpotAngle;
            light.cullingMask = 1 << PreviewLayer;
            light.shadows = shadows;
            if (shadows != LightShadows.None)
            {
                light.shadowStrength = 0.42f;
                light.shadowBias = 0.025f;
                light.shadowNormalBias = 0.18f;
                light.shadowResolution = LightShadowResolution.Medium;
            }

            return light;
        }

        private void CreateHangarReflectionEnvironment()
        {
            hangarReflectionCubemap = CreateHangarReflectionCubemap(32);
            var probeObject = new GameObject("HangarReflectionProbe")
            {
                hideFlags = HideFlags.HideAndDontSave,
                layer = PreviewLayer
            };
            probeObject.transform.SetParent(previewStage.transform, false);
            hangarReflectionProbe = probeObject.AddComponent<ReflectionProbe>();
            hangarReflectionProbe.mode = ReflectionProbeMode.Custom;
            hangarReflectionProbe.customBakedTexture = hangarReflectionCubemap;
            hangarReflectionProbe.intensity = 1.05f;
            hangarReflectionProbe.boxProjection = true;
            hangarReflectionProbe.importance = 100;
            hangarReflectionProbe.blendDistance = 0.5f;
            hangarReflectionProbe.cullingMask = 1 << PreviewLayer;
        }

        private static Cubemap CreateHangarReflectionCubemap(int size)
        {
            var cubemap = new Cubemap(size, TextureFormat.RGBAHalf, true)
            {
                name = "HangarLightingReflection",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Trilinear,
                wrapMode = TextureWrapMode.Clamp
            };

            var faces = new[]
            {
                CubemapFace.PositiveX,
                CubemapFace.NegativeX,
                CubemapFace.PositiveY,
                CubemapFace.NegativeY,
                CubemapFace.PositiveZ,
                CubemapFace.NegativeZ
            };
            var overheadDirection = new Vector3(-0.12f, 0.98f, -0.16f).normalized;
            var leftBankDirection = new Vector3(-0.82f, 0.35f, -0.45f).normalized;
            var rearBankDirection = new Vector3(0.66f, 0.48f, 0.57f).normalized;

            for (var faceIndex = 0; faceIndex < faces.Length; faceIndex++)
            {
                var face = faces[faceIndex];
                var pixels = new Color[size * size];
                for (var y = 0; y < size; y++)
                {
                    for (var x = 0; x < size; x++)
                    {
                        var direction = CubemapTexelDirection(face, x, y, size);
                        var overhead = SoftPanel(direction, overheadDirection, 0.08f, 1.8f);
                        var left = SoftPanel(direction, leftBankDirection, 0.38f, 2.8f);
                        var rear = SoftPanel(direction, rearBankDirection, 0.46f, 3.4f);
                        var floorBounce = Mathf.Pow(Mathf.Clamp01(-direction.y), 1.5f);
                        var color = new Color(0.085f, 0.100f, 0.115f, 1f);
                        color += new Color(0.95f, 0.90f, 0.80f, 0f) * overhead;
                        color += new Color(0.32f, 0.40f, 0.50f, 0f) * left;
                        color += new Color(0.24f, 0.31f, 0.43f, 0f) * rear;
                        color += new Color(0.065f, 0.075f, 0.085f, 0f) * floorBounce;
                        color.a = 1f;
                        pixels[y * size + x] = color;
                    }
                }

                cubemap.SetPixels(pixels, face);
            }

            cubemap.Apply(true, true);
            return cubemap;
        }

        private static float SoftPanel(Vector3 direction, Vector3 panelDirection, float threshold, float power)
        {
            var normalized = Mathf.InverseLerp(threshold, 1f, Vector3.Dot(direction, panelDirection));
            return Mathf.Pow(normalized, power);
        }

        private static Vector3 CubemapTexelDirection(CubemapFace face, int x, int y, int size)
        {
            var u = (2f * (x + 0.5f) / size) - 1f;
            var v = (2f * (y + 0.5f) / size) - 1f;
            switch (face)
            {
                case CubemapFace.PositiveX:
                    return new Vector3(1f, -v, -u).normalized;
                case CubemapFace.NegativeX:
                    return new Vector3(-1f, -v, u).normalized;
                case CubemapFace.PositiveY:
                    return new Vector3(u, 1f, v).normalized;
                case CubemapFace.NegativeY:
                    return new Vector3(u, -1f, -v).normalized;
                case CubemapFace.PositiveZ:
                    return new Vector3(u, -v, 1f).normalized;
                default:
                    return new Vector3(-u, -v, -1f).normalized;
            }
        }

        private void PrepareContent(GameObject root)
        {
            SetLayerRecursively(root.transform, PreviewLayer);
            var audioSources = root.GetComponentsInChildren<AudioSource>(true);
            for (var i = 0; i < audioSources.Length; i++)
            {
                audioSources[i].enabled = false;
            }

            var rigidbodies = root.GetComponentsInChildren<Rigidbody>(true);
            for (var i = 0; i < rigidbodies.Length; i++)
            {
                rigidbodies[i].isKinematic = true;
                rigidbodies[i].detectCollisions = false;
            }

            var colliders = root.GetComponentsInChildren<Collider>(true);
            for (var i = 0; i < colliders.Length; i++)
            {
                colliders[i].enabled = false;
            }

            var skinnedRenderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (var i = 0; i < skinnedRenderers.Length; i++)
            {
                skinnedRenderers[i].updateWhenOffscreen = true;
            }

            var renderers = root.GetComponentsInChildren<Renderer>(true);
            for (var i = 0; i < renderers.Length; i++)
            {
                renderers[i].reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
            }
        }

        private void FrameContent()
        {
            hasRenderableContent = TryCalculateBounds(contentRoot, out contentBounds);
            // 目标值必须一起重置，否则换部件后视角会从默认角度又滑回上一个部件的角度。
            orbitYaw = orbitYawTarget = -18f;
            orbitPitch = orbitPitchTarget = 7f;
            zoom = zoomTarget = DefaultZoom;
            UpdateHangarLighting();
            ApplyCameraPose();
        }

        private void UpdateHangarLighting()
        {
            if (!hasRenderableContent)
            {
                return;
            }

            var center = contentBounds.center;
            var radius = Mathf.Max(0.5f, contentBounds.extents.magnitude);
            PositionSpotLight(
                overheadBankLight,
                center + new Vector3(-0.18f, 1.72f, -0.34f) * radius,
                center,
                radius * radius * 1.6f,
                radius * 4.2f);
            PositionSpotLight(
                leftBankLight,
                center + new Vector3(-1.55f, 0.62f, -0.92f) * radius,
                center,
                radius * radius * 0.78f,
                radius * 4.5f);
            PositionSpotLight(
                rearBankLight,
                center + new Vector3(1.25f, 0.78f, 1.05f) * radius,
                center + Vector3.up * contentBounds.extents.y * 0.16f,
                radius * radius * 0.92f,
                radius * 4.2f);

            if (hangarReflectionProbe != null)
            {
                hangarReflectionProbe.transform.position = center;
                hangarReflectionProbe.size = Vector3.one * radius * 3.6f;
                hangarReflectionProbe.blendDistance = radius * 0.35f;
            }
        }

        private static void PositionSpotLight(
            Light light,
            Vector3 position,
            Vector3 target,
            float intensity,
            float range)
        {
            if (light == null)
            {
                return;
            }

            light.transform.SetPositionAndRotation(position, Quaternion.LookRotation(target - position, Vector3.up));
            light.intensity = intensity;
            light.range = range;
        }

        private void ApplyCameraPose()
        {
            if (previewCamera == null)
            {
                return;
            }

            var center = hasRenderableContent ? contentBounds.center : PreviewOrigin;
            var radius = hasRenderableContent ? Mathf.Max(0.5f, contentBounds.extents.magnitude) : 4f;
            var verticalFovRadians = previewCamera.fieldOfView * Mathf.Deg2Rad;
            var distance = radius / Mathf.Tan(verticalFovRadians * 0.5f) * 1.18f * zoom;
            var orbit = Quaternion.Euler(orbitPitch, orbitYaw, 0f);
            var viewDirection = orbit * Vector3.forward;
            previewCamera.transform.SetPositionAndRotation(
                center - viewDirection * distance,
                Quaternion.LookRotation(viewDirection, Vector3.up));
            previewCamera.nearClipPlane = Mathf.Max(0.03f, distance - radius * 1.8f);
            previewCamera.farClipPlane = Mathf.Max(previewCamera.nearClipPlane + 10f, distance + radius * 2.5f);
        }

        private void HandleOrbitInput(Rect rect)
        {
            var currentEvent = Event.current;
            if (currentEvent == null || !rect.Contains(currentEvent.mousePosition))
            {
                return;
            }

            if (currentEvent.type == EventType.MouseDrag && currentEvent.button == 0)
            {
                orbitYawTarget += currentEvent.delta.x * orbitYawSensitivity;
                orbitPitchTarget = Mathf.Clamp(
                    orbitPitchTarget + currentEvent.delta.y * orbitPitchSensitivity, -55f, 55f);
                currentEvent.Use();
            }
            else if (currentEvent.type == EventType.ScrollWheel)
            {
                zoomTarget = Mathf.Clamp(
                    zoomTarget * (1f + currentEvent.delta.y * zoomSensitivity),
                    MinimumZoom,
                    MaximumZoom);
                currentEvent.Use();
            }
        }

        /// <summary>
        /// 把当前视角向目标视角推进。必须每帧只跑一次：OnGUI 一帧会被调用多次
        /// （Layout、Repaint、以及每个输入事件各一次），逐次做阻尼会让实际转速随当帧
        /// 事件数量变化，本身就成了新的抖动来源。
        /// </summary>
        private void SmoothOrbitTowardsTarget()
        {
            if (lastSmoothedFrame == Time.frameCount)
            {
                return;
            }

            lastSmoothedFrame = Time.frameCount;

            if (orbitSmoothingTime <= 0f)
            {
                orbitYaw = orbitYawTarget;
                orbitPitch = orbitPitchTarget;
                zoom = zoomTarget;
                return;
            }

            // 指数趋近，写成 1 - exp(-dt / tau) 的形式以保证与帧率无关。
            // unscaledDeltaTime：机库里暂停或改 timeScale 时视角操作仍应正常。
            var t = 1f - Mathf.Exp(-Time.unscaledDeltaTime / orbitSmoothingTime);
            orbitYaw = Mathf.Lerp(orbitYaw, orbitYawTarget, t);
            orbitPitch = Mathf.Lerp(orbitPitch, orbitPitchTarget, t);
            zoom = Mathf.Lerp(zoom, zoomTarget, t);
        }

        private void EnsureTarget(int width, int height)
        {
            width = Mathf.Clamp(width, 256, 1600);
            height = Mathf.Clamp(height, 256, 1200);
            if (targetTexture != null && targetTexture.width == width && targetTexture.height == height)
            {
                return;
            }

            DestroyTarget();
            targetTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGBHalf)
            {
                name = "MechHangarPreview",
                hideFlags = HideFlags.HideAndDontSave,
                // 之前是 1，等于 previewCamera.allowMSAA 完全没生效（URP 渲染到自定义 RT 时
                // 用的是 RT 描述符的采样数，不是管线资产上的）。部件边缘和细密的分模线在
                // 转动时会逐帧重新走样，这也是"每帧像换了一张图"观感的一部分。
                antiAliasing = 4,
                useMipMap = false,
                autoGenerateMips = false
            };
            targetTexture.Create();
            previewCamera.targetTexture = targetTexture;
            previewCamera.enabled = true;
        }

        private void ClearContent()
        {
            hasRenderableContent = false;
            staticPreviewImage = null;
            if (contentRoot == null)
            {
                return;
            }

            contentRoot.SetActive(false);
            Destroy(contentRoot);
            contentRoot = null;
        }

        private void OnDestroy()
        {
            DestroyTarget();
            if (previewStage != null)
            {
                Destroy(previewStage);
            }

            if (previewVolumeProfile != null)
            {
                Destroy(previewVolumeProfile);
                previewVolumeProfile = null;
            }

            if (hangarReflectionCubemap != null)
            {
                Destroy(hangarReflectionCubemap);
                hangarReflectionCubemap = null;
            }
        }

        private void DestroyTarget()
        {
            if (previewCamera != null)
            {
                previewCamera.targetTexture = null;
            }

            if (targetTexture != null)
            {
                targetTexture.Release();
                Destroy(targetTexture);
                targetTexture = null;
            }
        }

        private static bool TryCalculateBounds(GameObject root, out Bounds bounds)
        {
            bounds = default;
            if (root == null)
            {
                return false;
            }

            var renderers = root.GetComponentsInChildren<Renderer>(true);
            var found = false;
            for (var i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer == null || renderer is ParticleSystemRenderer || renderer is TrailRenderer || renderer is LineRenderer)
                {
                    continue;
                }

                if (!found)
                {
                    bounds = renderer.bounds;
                    found = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            return found && IsFinite(bounds.center) && IsFinite(bounds.extents);
        }

        private static bool IsFinite(Vector3 value)
        {
            return float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
        }

        private static void SetLayerRecursively(Transform root, int layer)
        {
            root.gameObject.layer = layer;
            for (var i = 0; i < root.childCount; i++)
            {
                SetLayerRecursively(root.GetChild(i), layer);
            }
        }
    }
}
