using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// Runtime-only thrust diagnostics. Red cones show every allocated nozzle force on an external
    /// display shell; the amber cone shows their resultant above the mech. Visualization never feeds physics.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MechThrustVisualizer : MonoBehaviour
    {
        [SerializeField] private MechFlightMotionController motion;
        [SerializeField] private bool visible;
        [SerializeField] private bool showResultantForce = true;
        [SerializeField] private Color nozzleConeColor = new(1f, 0.035f, 0.02f, 0.82f);
        [SerializeField] private Color resultantConeColor = new(1f, 0.62f, 0.04f, 0.72f);
        [SerializeField] private float minimumVisibleForceKn = 5f;
        [SerializeField] private float referenceForceKn = 1000f;
        [SerializeField] private float referenceLength = 9f;
        [SerializeField] private float minimumLength = 0.4f;
        [SerializeField] private float maximumLength = 24f;
        [SerializeField] [Range(0.05f, 0.5f)] private float radiusToLength = 0.2f;
        [Header("外置诊断布局")]
        [SerializeField] private Vector3 diagnosticHalfExtents = new(5.5f, 6.5f, 4.5f);
        [SerializeField] private float displayShellPadding = 0.75f;
        [SerializeField] private float sideLaneOffset = 0.65f;
        [SerializeField] private float roleLaneOffset = 0.8f;
        [SerializeField] private float resultantHeightPadding = 2.5f;
        [SerializeField] [Range(0.05f, 1f)] private float nozzleVisualScale = 0.28f;
        [SerializeField] [Range(0.05f, 1f)] private float resultantVisualScale = 0.34f;

        private readonly Dictionary<string, ConeMarker> channelMarkers = new();
        private Transform markerRoot;
        private Material nozzleMaterial;
        private Material resultantMaterial;
        private Mesh coneMesh;
        private ConeMarker resultantMarker;
        private Vector3 displayCenterLocal;
        private Vector3 displayExtentsLocal = new(2f, 4f, 2f);
        private Vector3 resultantOriginLocal = new(0f, 6.5f, 0f);

        public bool Visible => visible;

        public void Configure(MechFlightMotionController controller)
        {
            motion = controller;
            if (!Application.isPlaying)
            {
                return;
            }

            motion?.RefreshThrusterLayout();
            EnsureResources();
            ResolveDisplayLayout();
            SynchronizeChannelMarkers();
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
                ResolveDisplayLayout();
                SynchronizeChannelMarkers();
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
            ApplyVisibility();
        }

        private void LateUpdate()
        {
            if (!visible || motion == null)
            {
                return;
            }

            SynchronizeChannelMarkers();
            var resultantForce = Vector3.zero;
            var channels = motion.ThrusterChannels;
            for (var i = 0; i < channels.Count; i++)
            {
                var channel = channels[i];
                if (!channelMarkers.TryGetValue(channel.Id, out var marker))
                {
                    continue;
                }

                UpdateMarker(
                    marker,
                    ResolveNozzleDisplayOrigin(channel, channel.CurrentForceNewtons),
                    channel.CurrentForceNewtons,
                    nozzleVisualScale);
                resultantForce += channel.CurrentForceNewtons;
            }

            if (resultantMarker != null)
            {
                if (showResultantForce)
                {
                    UpdateMarker(
                        resultantMarker,
                        transform.TransformPoint(resultantOriginLocal),
                        resultantForce,
                        resultantVisualScale);
                }
                else
                {
                    resultantMarker.GameObject.SetActive(false);
                }
            }
        }

        private void UpdateMarker(
            ConeMarker marker,
            Vector3 origin,
            Vector3 forceNewtons,
            float visualScale)
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
            var length = Mathf.Clamp(
                referenceLength * normalizedMagnitude * Mathf.Max(0.01f, visualScale),
                minimumLength,
                maximumLength * Mathf.Max(0.01f, visualScale));
            var radius = Mathf.Max(0.08f, length * radiusToLength);
            marker.Transform.SetPositionAndRotation(
                origin,
                Quaternion.FromToRotation(Vector3.forward, forceNewtons.normalized));
            marker.Transform.localScale = new Vector3(radius, radius, length);
        }

        private Vector3 ResolveNozzleDisplayOrigin(
            MechThrusterChannelState channel,
            Vector3 forceNewtons)
        {
            if (forceNewtons.sqrMagnitude <= Mathf.Epsilon)
            {
                return transform.TransformPoint(displayCenterLocal);
            }

            var localDirection = transform.InverseTransformDirection(forceNewtons.normalized);
            var paddedExtents = displayExtentsLocal + Vector3.one * Mathf.Max(0f, displayShellPadding);
            var shellDistance = float.PositiveInfinity;
            if (Mathf.Abs(localDirection.x) > 0.0001f)
            {
                shellDistance = Mathf.Min(shellDistance, paddedExtents.x / Mathf.Abs(localDirection.x));
            }

            if (Mathf.Abs(localDirection.y) > 0.0001f)
            {
                shellDistance = Mathf.Min(shellDistance, paddedExtents.y / Mathf.Abs(localDirection.y));
            }

            if (Mathf.Abs(localDirection.z) > 0.0001f)
            {
                shellDistance = Mathf.Min(shellDistance, paddedExtents.z / Mathf.Abs(localDirection.z));
            }

            if (float.IsInfinity(shellDistance))
            {
                shellDistance = paddedExtents.magnitude;
            }

            var sideSign = channel.Side == MechThrusterSide.Left
                ? -1f
                : channel.Side == MechThrusterSide.Right
                    ? 1f
                    : 0f;
            var laneOffset = Vector3.right * (sideSign * sideLaneOffset)
                             + ResolveRoleLaneOffset(channel.Role);
            return transform.TransformPoint(
                displayCenterLocal + localDirection * shellDistance + laneOffset);
        }

        private Vector3 ResolveRoleLaneOffset(MechThrusterRole role)
        {
            return role switch
            {
                MechThrusterRole.Main => Vector3.up * roleLaneOffset,
                MechThrusterRole.Reverse => Vector3.down * roleLaneOffset,
                MechThrusterRole.ShoulderLift => Vector3.forward * roleLaneOffset,
                MechThrusterRole.LegLift => Vector3.back * roleLaneOffset,
                MechThrusterRole.Descent => Vector3.up * roleLaneOffset + Vector3.back * roleLaneOffset,
                MechThrusterRole.ShoulderRcs => Vector3.up * roleLaneOffset + Vector3.forward * roleLaneOffset,
                MechThrusterRole.LegRcs => Vector3.down * roleLaneOffset + Vector3.back * roleLaneOffset,
                _ => Vector3.zero,
            };
        }

        private void ResolveDisplayLayout()
        {
            displayCenterLocal = motion != null
                ? transform.InverseTransformPoint(motion.FixedControlCenterWorld)
                : Vector3.up * 2f;
            displayExtentsLocal = new Vector3(
                Mathf.Max(0.5f, Mathf.Abs(diagnosticHalfExtents.x)),
                Mathf.Max(0.5f, Mathf.Abs(diagnosticHalfExtents.y)),
                Mathf.Max(0.5f, Mathf.Abs(diagnosticHalfExtents.z)));
            resultantOriginLocal = new Vector3(
                displayCenterLocal.x,
                displayCenterLocal.y + displayExtentsLocal.y + Mathf.Max(0f, resultantHeightPadding),
                displayCenterLocal.z);
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
            nozzleMaterial ??= BuildMaterial(nozzleConeColor, "Runtime Nozzle Force Material");
            resultantMaterial ??= BuildMaterial(resultantConeColor, "Runtime Resultant Force Material");
            resultantMarker ??= CreateMarker("Resultant Force", resultantMaterial);
        }

        private void SynchronizeChannelMarkers()
        {
            if (motion == null)
            {
                return;
            }

            var channels = motion.ThrusterChannels;
            for (var i = 0; i < channels.Count; i++)
            {
                var channel = channels[i];
                if (!channelMarkers.ContainsKey(channel.Id))
                {
                    channelMarkers.Add(
                        channel.Id,
                        CreateMarker($"{channel.Role} {channel.Side} [{channel.Id}]", nozzleMaterial));
                }
            }

            if (channelMarkers.Count == channels.Count)
            {
                return;
            }

            var staleIds = new List<string>();
            foreach (var pair in channelMarkers)
            {
                var found = false;
                for (var i = 0; i < channels.Count; i++)
                {
                    if (channels[i].Id == pair.Key)
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    staleIds.Add(pair.Key);
                }
            }

            for (var i = 0; i < staleIds.Count; i++)
            {
                var id = staleIds[i];
                if (channelMarkers.TryGetValue(id, out var marker) && marker.GameObject != null)
                {
                    Destroy(marker.GameObject);
                }

                channelMarkers.Remove(id);
            }
        }

        private ConeMarker CreateMarker(string markerName, Material material)
        {
            var go = new GameObject(markerName, typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(markerRoot, false);
            go.GetComponent<MeshFilter>().sharedMesh = coneMesh;
            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            return new ConeMarker(go);
        }

        private void ApplyVisibility()
        {
            if (markerRoot != null)
            {
                markerRoot.gameObject.SetActive(visible);
            }
        }

        private static Material BuildMaterial(Color color, string materialName)
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
                name = materialName,
                hideFlags = HideFlags.HideAndDontSave,
                renderQueue = (int)RenderQueue.Transparent,
            };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
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
            if (nozzleMaterial != null) Destroy(nozzleMaterial);
            if (resultantMaterial != null) Destroy(resultantMaterial);
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
