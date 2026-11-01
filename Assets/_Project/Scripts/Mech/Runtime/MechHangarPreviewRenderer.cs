using UnityEngine;
using System.Collections.Generic;
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
        private static readonly Vector3 PreviewOrigin = new Vector3(0f, -256f, 0f);
        private static readonly HashSet<int> occupiedStages = new HashSet<int>();
        private int stageIndex = -1;

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
        private float orbitYaw = -18f;
        private float orbitPitch = 7f;
        private float zoom = DefaultZoom;
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

        private void OnEnable()
        {
            RenderPipelineManager.beginCameraRendering += HandleBeginCameraRendering;
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= HandleBeginCameraRendering;
        }

        private void HandleBeginCameraRendering(
            ScriptableRenderContext context,
            Camera renderingCamera)
        {
            if (renderingCamera == null)
            {
                return;
            }
            if (renderingCamera == previewCamera)
            {
                renderingCamera.cullingMask = 1 << PreviewLayer;
                return;
            }
            renderingCamera.cullingMask &= ~(1 << PreviewLayer);
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

        public void ShowStandaloneModel(
            GameObject modelPrefab,
            string displayName,
            Vector3 localEulerAngles)
        {
            ClearContent();
            if (modelPrefab == null)
            {
                return;
            }

            EnsureStage();
            contentRoot = new GameObject("HangarStandalonePreview")
            {
                hideFlags = HideFlags.DontSave
            };
            contentRoot.SetActive(false);
            contentRoot.transform.SetParent(previewStage.transform, false);

            var instance = Instantiate(modelPrefab, contentRoot.transform);
            instance.name = string.IsNullOrWhiteSpace(displayName)
                ? modelPrefab.name + "_Preview"
                : displayName + "_Preview";
            instance.transform.SetLocalPositionAndRotation(
                Vector3.zero,
                Quaternion.Euler(localEulerAngles));
            instance.transform.localScale = Vector3.one;
            PrepareContent(contentRoot);
            contentRoot.SetActive(true);

            if (TryCalculateBounds(contentRoot, out var initialBounds))
            {
                var localCenter = contentRoot.transform.InverseTransformPoint(initialBounds.center);
                instance.transform.localPosition -= localCenter;
            }

            FrameContent();
            zoom = 1.05f;
            ApplyCameraPose();
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

        private void LateUpdate()
        {
            // IMGUI receives drag events after the normal camera update phase. Applying the pose
            // inside Draw made the enabled preview camera render either the old or new pose
            // depending on event timing. Commit it once here so the next camera render always
            // observes one complete input state.
            if (previewStage != null && previewStage.activeInHierarchy)
            {
                ApplyCameraPose();
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
            stageIndex = 0;
            while (occupiedStages.Contains(stageIndex)) stageIndex++;
            occupiedStages.Add(stageIndex);
            previewStage.transform.position = PreviewOrigin + Vector3.right * (stageIndex * 4096f);

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
            orbitYaw = -18f;
            orbitPitch = 7f;
            zoom = DefaultZoom;
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
                orbitYaw += currentEvent.delta.x * 0.45f;
                orbitPitch = Mathf.Clamp(orbitPitch + currentEvent.delta.y * 0.35f, -55f, 55f);
                currentEvent.Use();
            }
            else if (currentEvent.type == EventType.ScrollWheel)
            {
                zoom = Mathf.Clamp(
                    zoom * (1f + currentEvent.delta.y * 0.08f),
                    MinimumZoom,
                    MaximumZoom);
                currentEvent.Use();
            }
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
                antiAliasing = 1,
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
                previewStage.SetActive(false);
                Destroy(previewStage);
            }
            if (stageIndex >= 0) occupiedStages.Remove(stageIndex);

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
