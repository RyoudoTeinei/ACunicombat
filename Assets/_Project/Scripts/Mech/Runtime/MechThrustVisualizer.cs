using UnityEngine;
using UnityEngine.Rendering;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// Runtime-only force visualization. Red cones point along the force vector;
    /// their length and radius scale with force magnitude. It never feeds back into physics.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MechThrustVisualizer : MonoBehaviour
    {
        [SerializeField] private MechFlightMotionController motion;
        [SerializeField] private bool visible;
        [SerializeField] private Color coneColor = new(1f, 0.035f, 0.02f, 0.82f);
        [SerializeField] private float minimumVisibleForceKn = 5f;
        [SerializeField] private float referenceForceKn = 1000f;
        [SerializeField] private float referenceLength = 9f;
        [SerializeField] private float minimumLength = 0.4f;
        [SerializeField] private float maximumLength = 24f;
        [SerializeField] [Range(0.05f, 0.5f)] private float radiusToLength = 0.2f;

        private Transform markerRoot;
        private Material runtimeMaterial;
        private Mesh coneMesh;
        private ConeMarker totalMarker;
        private ConeMarker leftShoulderMarker;
        private ConeMarker rightShoulderMarker;
        private ConeMarker leftLegMarker;
        private ConeMarker rightLegMarker;
        private Vector3 totalOriginLocal;
        private Vector3 leftShoulderOriginLocal;
        private Vector3 rightShoulderOriginLocal;
        private Vector3 leftLegOriginLocal;
        private Vector3 rightLegOriginLocal;

        public bool Visible => visible;

        public void Configure(MechFlightMotionController controller)
        {
            motion = controller;
            if (!Application.isPlaying)
            {
                return;
            }

            EnsureResources();
            ResolveOrigins();
            ApplyVisibility();
        }

        public void ToggleVisible()
        {
            SetVisible(!visible);
        }

        public void SetVisible(bool value)
        {
            visible = value;
            EnsureResources();
            if (visible)
            {
                ResolveOrigins();
            }
            ApplyVisibility();
        }

        private void Awake()
        {
            if (motion == null)
            {
                motion = GetComponent<MechFlightMotionController>();
            }

            EnsureResources();
            ResolveOrigins();
            ApplyVisibility();
        }

        private void LateUpdate()
        {
            if (!visible || motion == null)
            {
                return;
            }

            var shoulderForce = motion.CurrentShoulderLiftForceNewtons;
            var legForce = motion.CurrentLegLiftForceNewtons;
            var quickBoostForce = motion.CurrentQuickBoostAcceleration * motion.CurrentMassKg;
            var totalForce = motion.CurrentControlForceNewtons + quickBoostForce;

            UpdateMarker(totalMarker, transform.TransformPoint(totalOriginLocal), totalForce);
            UpdateMarker(leftShoulderMarker, transform.TransformPoint(leftShoulderOriginLocal), shoulderForce * 0.5f);
            UpdateMarker(rightShoulderMarker, transform.TransformPoint(rightShoulderOriginLocal), shoulderForce * 0.5f);
            UpdateMarker(leftLegMarker, transform.TransformPoint(leftLegOriginLocal), legForce * 0.5f);
            UpdateMarker(rightLegMarker, transform.TransformPoint(rightLegOriginLocal), legForce * 0.5f);
        }

        private void UpdateMarker(ConeMarker marker, Vector3 origin, Vector3 forceNewtons)
        {
            if (marker == null)
            {
                return;
            }

            var forceKn = forceNewtons.magnitude / 1000f;
            var show = forceKn >= minimumVisibleForceKn;
            marker.GameObject.SetActive(show);
            if (!show)
            {
                return;
            }

            var normalizedMagnitude = Mathf.Sqrt(forceKn / Mathf.Max(1f, referenceForceKn));
            var length = Mathf.Clamp(referenceLength * normalizedMagnitude, minimumLength, maximumLength);
            var radius = Mathf.Max(0.08f, length * radiusToLength);
            marker.Transform.SetPositionAndRotation(
                origin,
                Quaternion.FromToRotation(Vector3.forward, forceNewtons.normalized));
            marker.Transform.localScale = new Vector3(radius, radius, length);
        }

        private void EnsureResources()
        {
            if (markerRoot == null)
            {
                var root = new GameObject("__ThrustForceVisualization");
                root.transform.SetParent(transform, false);
                markerRoot = root.transform;
            }

            coneMesh ??= BuildConeMesh(24);
            runtimeMaterial ??= BuildMaterial();
            totalMarker ??= CreateMarker("Total Force");
            leftShoulderMarker ??= CreateMarker("Left Shoulder Force");
            rightShoulderMarker ??= CreateMarker("Right Shoulder Force");
            leftLegMarker ??= CreateMarker("Left Leg Force");
            rightLegMarker ??= CreateMarker("Right Leg Force");
        }

        private ConeMarker CreateMarker(string markerName)
        {
            var go = new GameObject(markerName, typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(markerRoot, false);
            go.GetComponent<MeshFilter>().sharedMesh = coneMesh;
            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = runtimeMaterial;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            return new ConeMarker(go);
        }

        private void ResolveOrigins()
        {
            var coreBounds = TryGetPartBounds(MechPartSlot.Core, out var resolvedCore)
                ? resolvedCore
                : ResolveAllBounds();
            var legBounds = TryGetPartBounds(MechPartSlot.Legs, out var resolvedLegs)
                ? resolvedLegs
                : coreBounds;
            var logicalRight = motion != null ? motion.LogicalRotation * Vector3.right : transform.right;
            var shoulderHalfWidth = Mathf.Max(0.6f, coreBounds.extents.x * 0.65f);
            var legHalfWidth = Mathf.Max(0.35f, legBounds.extents.x * 0.28f);
            var shoulderCenter = coreBounds.center + Vector3.up * (coreBounds.extents.y * 0.25f);
            var legCenter = legBounds.center - Vector3.up * (legBounds.extents.y * 0.55f);

            totalOriginLocal = transform.InverseTransformPoint(coreBounds.center);
            leftShoulderOriginLocal = transform.InverseTransformPoint(shoulderCenter - logicalRight * shoulderHalfWidth);
            rightShoulderOriginLocal = transform.InverseTransformPoint(shoulderCenter + logicalRight * shoulderHalfWidth);
            leftLegOriginLocal = transform.InverseTransformPoint(legCenter - logicalRight * legHalfWidth);
            rightLegOriginLocal = transform.InverseTransformPoint(legCenter + logicalRight * legHalfWidth);
        }

        private bool TryGetPartBounds(MechPartSlot slot, out Bounds bounds)
        {
            var parts = GetComponentsInChildren<MechAssembledPart>(true);
            for (var i = 0; i < parts.Length; i++)
            {
                if (parts[i].Slot == slot && TryGetRendererBounds(parts[i].gameObject, out bounds))
                {
                    return true;
                }
            }

            bounds = default;
            return false;
        }

        private Bounds ResolveAllBounds()
        {
            return TryGetRendererBounds(gameObject, out var bounds)
                ? bounds
                : new Bounds(transform.position + Vector3.up * 2f, new Vector3(4f, 8f, 4f));
        }

        private bool TryGetRendererBounds(GameObject root, out Bounds bounds)
        {
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            var initialized = false;
            bounds = default;
            for (var i = 0; i < renderers.Length; i++)
            {
                if (markerRoot != null && renderers[i].transform.IsChildOf(markerRoot))
                {
                    continue;
                }

                if (!initialized)
                {
                    bounds = renderers[i].bounds;
                    initialized = true;
                }
                else
                {
                    bounds.Encapsulate(renderers[i].bounds);
                }
            }

            return initialized;
        }

        private void ApplyVisibility()
        {
            if (markerRoot != null)
            {
                markerRoot.gameObject.SetActive(visible);
            }
        }

        private Material BuildMaterial()
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit")
                         ?? Shader.Find("Sprites/Default")
                         ?? Shader.Find("Unlit/Color");
            if (shader == null)
            {
                return null;
            }

            var material = new Material(shader)
            {
                name = "Runtime Thrust Force Material",
                hideFlags = HideFlags.HideAndDontSave,
                renderQueue = (int)RenderQueue.Transparent,
            };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", coneColor);
            if (material.HasProperty("_Color")) material.SetColor("_Color", coneColor);
            if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1f);
            if (material.HasProperty("_ZWrite")) material.SetFloat("_ZWrite", 0f);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            return material;
        }

        private static Mesh BuildConeMesh(int sides)
        {
            sides = Mathf.Max(3, sides);
            var vertices = new Vector3[sides + 2];
            var triangles = new int[sides * 6];
            vertices[0] = Vector3.forward;
            vertices[1] = Vector3.zero;
            for (var i = 0; i < sides; i++)
            {
                var angle = Mathf.PI * 2f * i / sides;
                vertices[i + 2] = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f);
                var next = (i + 1) % sides;
                var triangle = i * 6;
                triangles[triangle] = 0;
                triangles[triangle + 1] = i + 2;
                triangles[triangle + 2] = next + 2;
                triangles[triangle + 3] = 1;
                triangles[triangle + 4] = next + 2;
                triangles[triangle + 5] = i + 2;
            }

            var mesh = new Mesh
            {
                name = "Runtime Thrust Force Cone",
                hideFlags = HideFlags.HideAndDontSave,
                vertices = vertices,
                triangles = triangles,
            };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private void OnDestroy()
        {
            if (runtimeMaterial != null) Destroy(runtimeMaterial);
            if (coneMesh != null) Destroy(coneMesh);
        }

        private sealed class ConeMarker
        {
            public ConeMarker(GameObject gameObject)
            {
                GameObject = gameObject;
                Transform = gameObject.transform;
            }

            public GameObject GameObject { get; }
            public Transform Transform { get; }
        }
    }
}
