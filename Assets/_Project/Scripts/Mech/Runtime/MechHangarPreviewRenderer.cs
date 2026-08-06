using UnityEngine;

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
            previewCamera.backgroundColor = new Color(0.025f, 0.045f, 0.066f, 1f);
            previewCamera.cullingMask = 1 << PreviewLayer;
            previewCamera.nearClipPlane = 0.03f;
            previewCamera.farClipPlane = 5000f;
            previewCamera.fieldOfView = 34f;
            previewCamera.allowHDR = true;
            previewCamera.allowMSAA = true;
            previewCamera.enabled = false;

            CreateDirectionalLight("PreviewKey", new Color(1f, 0.97f, 0.91f), 2.3f, new Vector3(28f, -38f, 0f));
            CreateDirectionalLight("PreviewFill", new Color(0.62f, 0.70f, 0.78f), 0.55f, new Vector3(14f, 142f, 8f));
            CreateDirectionalLight("PreviewRim", new Color(0.78f, 0.86f, 0.91f), 0.95f, new Vector3(-32f, 205f, 0f));
        }

        private void CreateDirectionalLight(string objectName, Color color, float intensity, Vector3 eulerAngles)
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
            light.shadows = LightShadows.None;
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
        }

        private void FrameContent()
        {
            hasRenderableContent = TryCalculateBounds(contentRoot, out contentBounds);
            orbitYaw = -18f;
            orbitPitch = 7f;
            zoom = DefaultZoom;
            ApplyCameraPose();
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
            targetTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
            {
                name = "MechHangarPreview",
                hideFlags = HideFlags.HideAndDontSave,
                antiAliasing = 2,
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
