using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace ACunicombat.Mechs
{
    [DefaultExecutionOrder(-1100)]
    [DisallowMultipleComponent]
    public sealed class MechSolarSystemMap : MonoBehaviour
    {
        private enum OrbitKind : byte
        {
            None,
            Planet,
            SmallBody,
            Moon,
        }

        private sealed class BodyDefinition
        {
            public string NodeName;
            public string ChineseName;
            public string EnglishName;
            public OrbitKind OrbitKind;
            public SolarOrbitalElements PlanetOrbit;
            public SolarSmallBodyOrbit SmallBodyOrbit;
            public double OrbitalPeriodDays;
            public double PhysicalRadiusKilometers;
            public float RotationHours;
            public float AxialTiltDegrees;
            public Color OrbitColor;
        }

        private sealed class BodyVisual
        {
            public BodyDefinition Definition;
            public Transform Transform;
            public float SourceRadius;
            public Mesh OrbitMesh;
            public MeshRenderer OrbitRenderer;
            public MaterialPropertyBlock OrbitProperties;
            public Vector3[] OrbitPoints;
            public Vector3[] OrbitVertices;
            public Vector3 PositionAu;
        }

        private const int MapLayer = 30;
        private const float AuToWorld = 1.2f;
        private const float MapCameraDistance = 240f;
        private const float TopBarHeight = 52f;
        private const float BottomBarHeight = 82f;
        private const int OrbitSegments = 512;
        private const int OrbitTubeSides = 6;
        private const float InnerSystemViewSize = 2.75f;
        private const float OuterPlanetsViewSize = 18f;
        private const float FullSystemViewSize = 105f;
        private const float MinimumViewSize = 0.015f;
        private const float MaximumViewSize = 140f;
        private const float ScrollZoomSensitivity = 0.0045f;
        private const string ModelResourcePath =
            "SolarSystem/solar_system_real_scale_2k_textures";

        private static readonly Vector3 MapOrigin = Vector3.zero;
        private static readonly Color Background = new Color(0.002f, 0.008f, 0.012f, 1f);
        private static readonly Color Panel = new Color(0.015f, 0.04f, 0.05f, 0.96f);
        private static readonly Color Line = new Color(0.18f, 0.72f, 0.78f, 0.8f);
        private static readonly Color TextMain = new Color(0.76f, 0.94f, 0.96f, 1f);
        private static readonly Color TextMuted = new Color(0.42f, 0.66f, 0.7f, 1f);
        private static readonly Color Selected = new Color(1f, 0.68f, 0.22f, 1f);
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        private static MechSolarSystemMap runtimeInstance;

        private readonly List<BodyVisual> bodies = new List<BodyVisual>(14);
        private readonly Dictionary<string, BodyVisual> bodyByName =
            new Dictionary<string, BodyVisual>(StringComparer.Ordinal);

        private GameObject mapRoot;
        private Camera mapCamera;
        private Light mapLight;
        private Material orbitMaterial;
        private InputAction mapToggleAction;
        private BodyVisual selectedBody;
        private double simulationJulianDate;
        private float daysPerRealSecond = 1f;
        private bool timeRunning = true;
        private bool visible;
        private bool ownsPause;
        private float previousTimeScale = 1f;
        private float previousFixedDeltaTime = 0.02f;
        private CursorLockMode previousCursorLockMode;
        private bool previousCursorVisible;
        private Vector3 focusPoint;
        private float cameraYaw = -18f;
        private float cameraPitch = 66f;
        private float orthographicSize = InnerSystemViewSize;
        private GUIStyle titleStyle;
        private GUIStyle labelStyle;
        private GUIStyle selectedLabelStyle;
        private GUIStyle detailStyle;
        private GUIStyle mutedStyle;
        private GUIStyle buttonStyle;

        public static bool IsAnyMapVisible { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            IsAnyMapVisible = false;
            runtimeInstance = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AddToPlayableMech()
        {
            MechPlayerRig[] rigs = FindObjectsByType<MechPlayerRig>(
                FindObjectsInactive.Exclude);
            foreach (MechPlayerRig rig in rigs)
            {
                if (EnsureAttached(rig)) break;
            }
        }

        internal static bool EnsureAttached(MechPlayerRig rig)
        {
            if (runtimeInstance != null)
            {
                return true;
            }
            if (rig == null)
            {
                return false;
            }

            MechSolarSystemMap map = rig.GetComponent<MechSolarSystemMap>() ??
                rig.gameObject.AddComponent<MechSolarSystemMap>();
            runtimeInstance = map;
            Debug.Log("[SolarSystemMap] Runtime map is ready.", rig);
            return true;
        }

        private void Awake()
        {
            if (runtimeInstance != null && runtimeInstance != this)
            {
                enabled = false;
                return;
            }
            runtimeInstance = this;
            mapToggleAction = new InputAction(
                "Solar System Map",
                InputActionType.Button,
                "<Keyboard>/m");
            mapToggleAction.performed += OnMapTogglePerformed;
            simulationJulianDate = SolarSystemOrbitMath.ToJulianDate(
                new DateTime(2101, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        }

        private void OnEnable()
        {
            mapToggleAction?.Enable();
            RenderPipelineManager.beginCameraRendering += HandleBeginCameraRendering;
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= HandleBeginCameraRendering;
            mapToggleAction?.Disable();
            SetVisible(false);
        }

        private void HandleBeginCameraRendering(
            ScriptableRenderContext context,
            Camera renderingCamera)
        {
            if (renderingCamera == null)
            {
                return;
            }
            if (renderingCamera == mapCamera)
            {
                renderingCamera.cullingMask = 1 << MapLayer;
                return;
            }
            renderingCamera.cullingMask &= ~(1 << MapLayer);
        }

        private void OnDestroy()
        {
            SetVisible(false);
            if (orbitMaterial != null)
            {
                Destroy(orbitMaterial);
            }
            foreach (BodyVisual body in bodies)
            {
                if (body.OrbitMesh != null)
                {
                    Destroy(body.OrbitMesh);
                }
            }
            if (mapRoot != null)
            {
                Destroy(mapRoot);
            }
            if (mapCamera != null)
            {
                Destroy(mapCamera.gameObject);
            }
            if (runtimeInstance == this)
            {
                runtimeInstance = null;
            }
            if (mapToggleAction != null)
            {
                mapToggleAction.performed -= OnMapTogglePerformed;
                mapToggleAction.Dispose();
                mapToggleAction = null;
            }
        }

        private void OnMapTogglePerformed(InputAction.CallbackContext context)
        {
            if (!isActiveAndEnabled)
            {
                return;
            }

            if (visible)
            {
                SetVisible(false);
                return;
            }

            MechAssemblyMenu.CloseAllForSolarMap();
            Debug.Log("[SolarSystemMap] M pressed; opening map.", this);
            SetVisible(true);
        }

        private void Update()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
            {
                return;
            }

            if (!visible) return;

            if (keyboard.escapeKey.wasPressedThisFrame)
            {
                SetVisible(false);
                return;
            }

            UpdateMapInput();
            if (timeRunning)
            {
                simulationJulianDate += daysPerRealSecond * Time.unscaledDeltaTime;
                UpdateBodyPositions();
            }
        }

        private void SetVisible(bool value)
        {
            if (visible == value)
            {
                return;
            }

            if (value)
            {
                if (!EnsureMapCreated())
                {
                    return;
                }

                previousTimeScale = Time.timeScale;
                previousFixedDeltaTime = Time.fixedDeltaTime;
                previousCursorLockMode = Cursor.lockState;
                previousCursorVisible = Cursor.visible;
                ownsPause = true;
                Time.timeScale = 0f;
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                visible = true;
                IsAnyMapVisible = true;
                mapRoot.SetActive(true);
                mapCamera.gameObject.SetActive(true);
                UpdateBodyPositions();
                UpdateCameraTransform();
                Debug.Log("[SolarSystemMap] Map opened.", this);
                return;
            }

            visible = false;
            IsAnyMapVisible = false;
            if (mapRoot != null)
            {
                mapRoot.SetActive(false);
            }
            if (mapCamera != null)
            {
                mapCamera.gameObject.SetActive(false);
            }
            if (ownsPause)
            {
                Time.timeScale = previousTimeScale;
                Time.fixedDeltaTime = previousFixedDeltaTime;
                Cursor.lockState = previousCursorLockMode;
                Cursor.visible = previousCursorVisible;
                ownsPause = false;
            }
        }

        private bool EnsureMapCreated()
        {
            if (mapRoot != null && mapCamera != null)
            {
                return true;
            }

            GameObject modelPrefab = Resources.Load<GameObject>(ModelResourcePath);
            if (modelPrefab == null)
            {
                Debug.LogError($"Solar-system map model was not found at Resources/{ModelResourcePath}.", this);
                return false;
            }

            mapRoot = new GameObject("Solar System Map Runtime");
            mapRoot.transform.position = MapOrigin;
            mapRoot.SetActive(false);

            GameObject model = Instantiate(modelPrefab, mapRoot.transform);
            model.name = "Solar System Surface Models";
            model.transform.localPosition = Vector3.zero;
            // Keep the imported root rotation and scale. The Sketchfab root and
            // GLTF_SceneRootNode contain inverse axis conversions; clearing only
            // the outer transform rotates every body 90 degrees off its orbit.

            Transform unusedSphere = FindDescendant(model.transform, "Sphere_15");
            if (unusedSphere != null)
            {
                unusedSphere.gameObject.SetActive(false);
            }

            Transform saturn = FindDescendant(model.transform, "Saturn_5");
            Transform saturnRing = FindDescendant(model.transform, "SaturnRing_14");
            if (saturn != null && saturnRing != null)
            {
                saturnRing.SetParent(saturn, false);
                saturnRing.localPosition = Vector3.zero;
                saturnRing.localRotation = Quaternion.identity;
                saturnRing.localScale = Vector3.one;
            }

            CreateCamera();
            CreateLight();
            BuildBodyVisuals(model.transform);
            BuildOrbitLines();
            selectedBody = bodyByName.TryGetValue("Earth_0", out BodyVisual earth)
                ? earth
                : bodies[0];
            mapRoot.SetActive(false);
            return true;
        }

        private void CreateCamera()
        {
            GameObject cameraObject = new GameObject("Solar System Map Camera", typeof(Camera));
            mapCamera = cameraObject.GetComponent<Camera>();
            mapCamera.orthographic = true;
            mapCamera.orthographicSize = orthographicSize;
            mapCamera.clearFlags = CameraClearFlags.SolidColor;
            mapCamera.backgroundColor = Background;
            mapCamera.cullingMask = 1 << MapLayer;
            mapCamera.nearClipPlane = 0.05f;
            mapCamera.farClipPlane = 600f;
            mapCamera.depth = 100f;
            mapCamera.allowHDR = true;
            mapCamera.allowMSAA = true;
            cameraObject.SetActive(false);
        }

        private void CreateLight()
        {
            GameObject lightObject = new GameObject("Solar System Map Light", typeof(Light));
            lightObject.transform.SetParent(mapRoot.transform, false);
            lightObject.transform.localRotation = Quaternion.Euler(42f, -34f, 0f);
            mapLight = lightObject.GetComponent<Light>();
            mapLight.type = LightType.Directional;
            mapLight.color = new Color(1f, 0.94f, 0.84f);
            mapLight.intensity = 1.35f;
            mapLight.cullingMask = 1 << MapLayer;
            mapLight.shadows = LightShadows.None;
            SetLayerRecursively(lightObject, MapLayer);
        }

        private void BuildBodyVisuals(Transform modelRoot)
        {
            bodies.Clear();
            bodyByName.Clear();
            foreach (BodyDefinition definition in CreateDefinitions())
            {
                Transform bodyTransform = FindDescendant(modelRoot, definition.NodeName);
                if (bodyTransform == null)
                {
                    Debug.LogWarning($"Solar-system body node missing: {definition.NodeName}", this);
                    continue;
                }

                SetLayerRecursively(bodyTransform.gameObject, MapLayer);
                BodyVisual visual = new BodyVisual
                {
                    Definition = definition,
                    Transform = bodyTransform,
                    SourceRadius = CalculateSourceRadius(bodyTransform),
                };
                bodies.Add(visual);
                bodyByName.Add(definition.NodeName, visual);
            }
        }

        private void BuildOrbitLines()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ??
                Shader.Find("Unlit/Color");
            orbitMaterial = new Material(shader)
            {
                name = "Solar System Orbit Lines Runtime"
            };
            if (orbitMaterial.HasProperty(BaseColorId))
            {
                orbitMaterial.SetColor(BaseColorId, Color.white);
            }
            if (orbitMaterial.HasProperty("_Color"))
            {
                orbitMaterial.SetColor("_Color", Color.white);
            }

            foreach (BodyVisual body in bodies)
            {
                if (body.Definition.OrbitKind == OrbitKind.None)
                {
                    continue;
                }

                GameObject lineObject = new GameObject(body.Definition.EnglishName + " Orbit");
                lineObject.transform.SetParent(mapRoot.transform, false);
                SetLayerRecursively(lineObject, MapLayer);
                MeshFilter meshFilter = lineObject.AddComponent<MeshFilter>();
                MeshRenderer meshRenderer = lineObject.AddComponent<MeshRenderer>();
                Mesh mesh = new Mesh
                {
                    name = body.Definition.EnglishName + " Orbit Tube"
                };
                mesh.MarkDynamic();
                meshFilter.sharedMesh = mesh;
                meshRenderer.sharedMaterial = orbitMaterial;
                meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                meshRenderer.receiveShadows = false;
                body.OrbitMesh = mesh;
                body.OrbitRenderer = meshRenderer;
                body.OrbitProperties = new MaterialPropertyBlock();
                body.OrbitPoints = new Vector3[OrbitSegments];
                body.OrbitVertices = new Vector3[OrbitSegments * OrbitTubeSides];
                mesh.vertices = body.OrbitVertices;
                mesh.triangles = BuildOrbitTubeTriangles();
            }
        }

        private static int[] BuildOrbitTubeTriangles()
        {
            int[] triangles = new int[OrbitSegments * OrbitTubeSides * 6];
            int triangleIndex = 0;
            for (int segment = 0; segment < OrbitSegments; segment++)
            {
                int nextSegment = (segment + 1) % OrbitSegments;
                for (int side = 0; side < OrbitTubeSides; side++)
                {
                    int nextSide = (side + 1) % OrbitTubeSides;
                    int current = segment * OrbitTubeSides + side;
                    int currentNext = segment * OrbitTubeSides + nextSide;
                    int next = nextSegment * OrbitTubeSides + side;
                    int nextNext = nextSegment * OrbitTubeSides + nextSide;
                    triangles[triangleIndex++] = current;
                    triangles[triangleIndex++] = next;
                    triangles[triangleIndex++] = currentNext;
                    triangles[triangleIndex++] = currentNext;
                    triangles[triangleIndex++] = next;
                    triangles[triangleIndex++] = nextNext;
                }
            }
            return triangles;
        }

        private void UpdateBodyPositions()
        {
            BodyVisual earth = bodyByName.TryGetValue("Earth_0", out BodyVisual value)
                ? value
                : null;

            foreach (BodyVisual body in bodies)
            {
                BodyDefinition definition = body.Definition;
                switch (definition.OrbitKind)
                {
                    case OrbitKind.None:
                        body.PositionAu = Vector3.zero;
                        break;
                    case OrbitKind.Planet:
                        body.PositionAu = SolarSystemOrbitMath.PositionAtJulianDate(
                            in definition.PlanetOrbit,
                            simulationJulianDate);
                        break;
                    case OrbitKind.SmallBody:
                        body.PositionAu = SolarSystemOrbitMath.PositionAtJulianDate(
                            in definition.SmallBodyOrbit,
                            simulationJulianDate);
                        break;
                    case OrbitKind.Moon:
                        Vector3 moonRelative = SolarSystemOrbitMath.PositionAtJulianDate(
                            in definition.SmallBodyOrbit,
                            simulationJulianDate);
                        body.PositionAu = (earth?.PositionAu ?? Vector3.zero) + moonRelative;
                        break;
                }

                body.Transform.localPosition = body.PositionAu * AuToWorld;
                ApplyBodyDisplayScale(body);
                UpdateBodyRotation(body);
                UpdateOrbitLine(body, earth);
            }
        }

        private void ApplyBodyDisplayScale(BodyVisual body)
        {
            if (mapCamera == null || body.SourceRadius <= 0f)
            {
                return;
            }

            float radiusRatio = Mathf.Max(
                1f,
                (float)(body.Definition.PhysicalRadiusKilometers / 469.7d));
            float screenRadiusPixels = 4.5f + Mathf.Log10(radiusRatio) * 3.1f;
            float worldRadius = screenRadiusPixels *
                (mapCamera.orthographicSize * 2f / Mathf.Max(1f, Screen.height));
            float scale = worldRadius / body.SourceRadius;
            body.Transform.localScale = Vector3.one * scale;
        }

        private void UpdateBodyRotation(BodyVisual body)
        {
            float hours = body.Definition.RotationHours;
            float spin = Mathf.Abs(hours) > 0.01f
                ? (float)((simulationJulianDate - SolarSystemOrbitMath.J2000JulianDate) *
                    24d / hours * 360d)
                : 0f;
            body.Transform.localRotation =
                Quaternion.AngleAxis(body.Definition.AxialTiltDegrees, Vector3.forward) *
                Quaternion.AngleAxis(spin, Vector3.up);
        }

        private void UpdateOrbitLine(BodyVisual body, BodyVisual earth)
        {
            MeshRenderer orbitRenderer = body.OrbitRenderer;
            Mesh orbitMesh = body.OrbitMesh;
            if (orbitRenderer == null || orbitMesh == null)
            {
                return;
            }

            bool moonInScale = body.Definition.OrbitKind != OrbitKind.Moon ||
                orthographicSize <= 0.55f;
            float orbitExtent = (float)(GetSemiMajorAxis(body.Definition) *
                (1d + GetEccentricity(body.Definition))) * AuToWorld;
            float visibleExtent = orthographicSize * 2.25f +
                new Vector2(focusPoint.x, focusPoint.z).magnitude;
            bool orbitInScale = orbitExtent <= visibleExtent || body == selectedBody;
            orbitRenderer.enabled = moonInScale && orbitInScale;
            body.Transform.gameObject.SetActive(moonInScale);
            if (!moonInScale || !orbitInScale)
            {
                return;
            }

            SolarResolvedOrbit orbit;
            if (body.Definition.OrbitKind == OrbitKind.Planet)
            {
                orbit = SolarSystemOrbitMath.ResolveOrbit(
                    in body.Definition.PlanetOrbit,
                    simulationJulianDate);
            }
            else
            {
                orbit = SolarSystemOrbitMath.ResolveOrbit(in body.Definition.SmallBodyOrbit);
            }

            Vector3 center = body.Definition.OrbitKind == OrbitKind.Moon && earth != null
                ? earth.PositionAu * AuToWorld
                : Vector3.zero;
            for (int index = 0; index < OrbitSegments; index++)
            {
                double anomaly = Math.PI * 2d * index / OrbitSegments;
                Vector3 point = SolarSystemOrbitMath.PositionAtEccentricAnomaly(
                    in orbit,
                    anomaly) * AuToWorld;
                body.OrbitPoints[index] = center + point;
            }

            float worldPerPixel = orthographicSize * 2f / Mathf.Max(1f, Screen.height);
            float radius = worldPerPixel * (body == selectedBody ? 1.2f : 0.78f);
            BuildOrbitTubeVertices(body.OrbitPoints, body.OrbitVertices, radius);
            orbitMesh.vertices = body.OrbitVertices;
            orbitMesh.RecalculateBounds();

            Color color = Color.Lerp(
                new Color(0.055f, 0.19f, 0.22f, 1f),
                body.Definition.OrbitColor,
                0.18f);
            if (body == selectedBody)
            {
                color = new Color(Selected.r, Selected.g * 0.82f, Selected.b * 0.72f, 1f);
            }
            body.OrbitProperties.Clear();
            body.OrbitProperties.SetColor(BaseColorId, color);
            body.OrbitProperties.SetColor(ColorId, color);
            orbitRenderer.SetPropertyBlock(body.OrbitProperties);
        }

        private static void BuildOrbitTubeVertices(
            Vector3[] points,
            Vector3[] vertices,
            float radius)
        {
            for (int index = 0; index < points.Length; index++)
            {
                Vector3 previous = points[(index - 1 + points.Length) % points.Length];
                Vector3 next = points[(index + 1) % points.Length];
                Vector3 tangent = (next - previous).normalized;
                Vector3 axisA = Vector3.Cross(tangent, Vector3.up);
                if (axisA.sqrMagnitude < 0.0001f)
                {
                    axisA = Vector3.Cross(tangent, Vector3.forward);
                }
                axisA.Normalize();
                Vector3 axisB = Vector3.Cross(tangent, axisA).normalized;

                int vertexStart = index * OrbitTubeSides;
                for (int side = 0; side < OrbitTubeSides; side++)
                {
                    float angle = Mathf.PI * 2f * side / OrbitTubeSides;
                    Vector3 offset = axisA * Mathf.Cos(angle) + axisB * Mathf.Sin(angle);
                    vertices[vertexStart + side] = points[index] + offset * radius;
                }
            }
        }

        private void UpdateMapInput()
        {
            Mouse mouse = Mouse.current;
            if (mouse == null || mapCamera == null)
            {
                return;
            }

            Vector2 pointer = mouse.position.ReadValue();
            float scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) > 0.01f)
            {
                bool hasAnchor = TryProjectPointerToEcliptic(pointer, out Vector3 anchorBefore);
                orthographicSize = Mathf.Clamp(
                    orthographicSize * Mathf.Exp(-scroll * ScrollZoomSensitivity),
                    MinimumViewSize,
                    MaximumViewSize);
                ApplyViewScaleChanges();
                if (hasAnchor && TryProjectPointerToEcliptic(pointer, out Vector3 anchorAfter))
                {
                    focusPoint += anchorBefore - anchorAfter;
                    UpdateCameraTransform();
                }
            }

            if (mouse.rightButton.isPressed)
            {
                Vector2 delta = mouse.delta.ReadValue();
                cameraYaw += delta.x * 0.18f;
                cameraPitch = Mathf.Clamp(cameraPitch - delta.y * 0.18f, 12f, 88f);
                UpdateCameraTransform();
            }
            else if (mouse.middleButton.isPressed)
            {
                Vector2 delta = mouse.delta.ReadValue();
                float unitsPerPixel = orthographicSize * 2f / Mathf.Max(1f, Screen.height);
                focusPoint -= mapCamera.transform.right * delta.x * unitsPerPixel;
                focusPoint -= mapCamera.transform.up * delta.y * unitsPerPixel;
                UpdateCameraTransform();
            }

            if (mouse.leftButton.wasPressedThisFrame &&
                pointer.y > BottomBarHeight &&
                pointer.y < Screen.height - TopBarHeight)
            {
                SelectBodyAt(pointer);
            }
        }

        private void SelectBodyAt(Vector2 screenPoint)
        {
            BodyVisual nearest = null;
            float nearestDistance = 22f;
            foreach (BodyVisual body in bodies)
            {
                if (!body.Transform.gameObject.activeInHierarchy)
                {
                    continue;
                }
                Vector3 projected = mapCamera.WorldToScreenPoint(body.Transform.position);
                if (projected.z <= 0f)
                {
                    continue;
                }
                float distance = Vector2.Distance(screenPoint, new Vector2(projected.x, projected.y));
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = body;
                }
            }
            if (nearest != null)
            {
                selectedBody = nearest;
                ApplyViewScaleChanges();
            }
        }

        private bool TryProjectPointerToEcliptic(Vector2 pointer, out Vector3 point)
        {
            point = default;
            if (mapCamera == null)
            {
                return false;
            }

            Plane ecliptic = new Plane(Vector3.up, MapOrigin);
            Ray ray = mapCamera.ScreenPointToRay(pointer);
            if (!ecliptic.Raycast(ray, out float distance))
            {
                return false;
            }
            point = ray.GetPoint(distance);
            return true;
        }

        private void ApplyViewScaleChanges()
        {
            mapCamera.orthographicSize = orthographicSize;
            foreach (BodyVisual body in bodies)
            {
                ApplyBodyDisplayScale(body);
                UpdateOrbitLine(body, bodyByName.TryGetValue("Earth_0", out BodyVisual earth)
                    ? earth
                    : null);
            }
            UpdateCameraTransform();
        }

        private void SetView(float size)
        {
            orthographicSize = size;
            focusPoint = Vector3.zero;
            ApplyViewScaleChanges();
        }

        private void UpdateCameraTransform()
        {
            if (mapCamera == null)
            {
                return;
            }
            Quaternion rotation = Quaternion.Euler(cameraPitch, cameraYaw, 0f);
            mapCamera.transform.rotation = rotation;
            mapCamera.transform.position =
                MapOrigin + focusPoint - rotation * Vector3.forward * MapCameraDistance;
            mapCamera.orthographicSize = orthographicSize;
        }

        private void OnGUI()
        {
            if (!visible || mapCamera == null)
            {
                return;
            }

            EnsureStyles();
            int previousDepth = GUI.depth;
            Color previousColor = GUI.color;
            GUI.depth = -3000;
            DrawPanel(new Rect(0f, 0f, Screen.width, TopBarHeight), Panel);
            DrawPanel(
                new Rect(0f, Screen.height - BottomBarHeight, Screen.width, BottomBarHeight),
                Panel);

            GUI.Label(new Rect(22f, 10f, 240f, 34f), "太阳系战术星图", titleStyle);
            string date = SolarSystemOrbitMath.FromJulianDate(simulationJulianDate)
                .ToString("yyyy-MM-dd HH:mm 'UTC'");
            GUI.Label(new Rect(272f, 12f, 230f, 28f), date, detailStyle);

            float x = Screen.width - 650f;
            if (GUI.Button(new Rect(x, 10f, 76f, 32f), "内太阳系", buttonStyle))
            {
                SetView(InnerSystemViewSize);
            }
            x += 82f;
            if (GUI.Button(new Rect(x, 10f, 76f, 32f), "外行星", buttonStyle))
            {
                SetView(OuterPlanetsViewSize);
            }
            x += 82f;
            if (GUI.Button(new Rect(x, 10f, 76f, 32f), "全太阳系", buttonStyle))
            {
                SetView(FullSystemViewSize);
            }
            x += 94f;
            if (GUI.Button(new Rect(x, 10f, 40f, 32f), "<<", buttonStyle))
            {
                daysPerRealSecond = Mathf.Max(0.125f, daysPerRealSecond * 0.5f);
            }
            x += 44f;
            if (GUI.Button(new Rect(x, 10f, 40f, 32f), timeRunning ? "||" : ">", buttonStyle))
            {
                timeRunning = !timeRunning;
            }
            x += 44f;
            if (GUI.Button(new Rect(x, 10f, 40f, 32f), ">>", buttonStyle))
            {
                daysPerRealSecond = Mathf.Min(4096f, daysPerRealSecond * 2f);
            }
            x += 50f;
            GUI.Label(new Rect(x, 13f, 90f, 26f), $"{daysPerRealSecond:0.###} 日/秒", mutedStyle);

            DrawBodyLabels();
            DrawSelectedBodyDetails();
            GUI.color = previousColor;
            GUI.depth = previousDepth;
        }

        private void DrawBodyLabels()
        {
            List<Rect> occupied = new List<Rect>(bodies.Count);
            if (selectedBody != null)
            {
                DrawBodyLabel(selectedBody, occupied, true);
            }
            foreach (BodyVisual body in bodies)
            {
                if (body == selectedBody)
                {
                    continue;
                }
                DrawBodyLabel(body, occupied, false);
            }
        }

        private void DrawBodyLabel(BodyVisual body, List<Rect> occupied, bool selected)
        {
            if (!body.Transform.gameObject.activeInHierarchy)
            {
                return;
            }
            Vector3 projected = mapCamera.WorldToScreenPoint(body.Transform.position);
            if (projected.z <= 0f)
            {
                return;
            }
            float y = Screen.height - projected.y;
            Rect rect = new Rect(projected.x + 9f, y - 11f, 92f, 22f);
            if (rect.xMax < 0f || rect.xMin > Screen.width ||
                rect.yMin < TopBarHeight || rect.yMax > Screen.height - BottomBarHeight)
            {
                return;
            }
            if (!selected)
            {
                foreach (Rect existing in occupied)
                {
                    if (existing.Overlaps(rect))
                    {
                        return;
                    }
                }
            }
            occupied.Add(rect);
            GUI.Label(rect, body.Definition.ChineseName,
                selected ? selectedLabelStyle : labelStyle);
            if (selected)
            {
                DrawSelectionBrackets(projected.x, y);
            }
        }

        private static void DrawSelectionBrackets(float x, float y)
        {
            const float radius = 15f;
            const float length = 6f;
            Color previous = GUI.color;
            GUI.color = Selected;
            DrawBracketCorner(x - radius, y - radius, length, 1f, 1f);
            DrawBracketCorner(x + radius, y - radius, length, -1f, 1f);
            DrawBracketCorner(x - radius, y + radius, length, 1f, -1f);
            DrawBracketCorner(x + radius, y + radius, length, -1f, -1f);
            GUI.color = previous;
        }

        private static void DrawBracketCorner(
            float x,
            float y,
            float length,
            float horizontalDirection,
            float verticalDirection)
        {
            GUI.DrawTexture(new Rect(
                horizontalDirection > 0f ? x : x - length,
                y,
                length,
                1f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(
                x,
                verticalDirection > 0f ? y : y - length,
                1f,
                length), Texture2D.whiteTexture);
        }

        private void DrawSelectedBodyDetails()
        {
            float y = Screen.height - BottomBarHeight + 9f;
            if (selectedBody == null)
            {
                GUI.Label(new Rect(22f, y, 320f, 26f), "未选择天体", detailStyle);
                return;
            }

            BodyDefinition definition = selectedBody.Definition;
            double distance = selectedBody.PositionAu.magnitude;
            GUI.Label(
                new Rect(22f, y, 260f, 28f),
                $"{definition.ChineseName}  {definition.EnglishName}",
                titleStyle);
            GUI.Label(
                new Rect(300f, y + 2f, 660f, 24f),
                $"日心距 {distance:0.000} AU    半长轴 {GetSemiMajorAxis(definition):0.000} AU    " +
                $"偏心率 {GetEccentricity(definition):0.0000}    轨道倾角 {GetInclination(definition):0.00}°    " +
                $"周期 {definition.OrbitalPeriodDays:0.0} 日    半径 {definition.PhysicalRadiusKilometers:0} km",
                detailStyle);
            GUI.Label(
                new Rect(22f, y + 36f, Screen.width - 44f, 24f),
                "轨道距离按 AU 线性显示；天体直径为识别而放大。轨道根数：NASA/JPL，纪元起点：2101-01-01。",
                mutedStyle);
        }

        private void EnsureStyles()
        {
            if (titleStyle != null)
            {
                return;
            }

            titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 18,
                fontStyle = FontStyle.Bold,
                normal = { textColor = TextMain },
            };
            labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                normal = { textColor = TextMain },
            };
            selectedLabelStyle = new GUIStyle(labelStyle);
            selectedLabelStyle.normal.textColor = Selected;
            detailStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                normal = { textColor = TextMain },
            };
            mutedStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                normal = { textColor = TextMuted },
            };
            buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                normal = { textColor = TextMain, background = Texture2D.blackTexture },
                hover = { textColor = Color.white },
                active = { textColor = Selected },
            };
        }

        private static void DrawPanel(Rect rect, Color color)
        {
            Color previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = Line;
            GUI.DrawTexture(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), Texture2D.whiteTexture);
            GUI.color = previous;
        }

        private static double GetSemiMajorAxis(BodyDefinition definition)
        {
            if (definition.OrbitKind == OrbitKind.Planet)
            {
                return definition.PlanetOrbit.SemiMajorAxis;
            }
            if (definition.OrbitKind == OrbitKind.SmallBody || definition.OrbitKind == OrbitKind.Moon)
            {
                return definition.SmallBodyOrbit.SemiMajorAxis;
            }
            return 0d;
        }

        private static double GetEccentricity(BodyDefinition definition)
        {
            return definition.OrbitKind == OrbitKind.Planet
                ? definition.PlanetOrbit.Eccentricity
                : definition.SmallBodyOrbit.Eccentricity;
        }

        private static double GetInclination(BodyDefinition definition)
        {
            return definition.OrbitKind == OrbitKind.Planet
                ? definition.PlanetOrbit.InclinationDegrees
                : definition.SmallBodyOrbit.InclinationDegrees;
        }

        private static float CalculateSourceRadius(Transform body)
        {
            MeshFilter directFilter = body.GetComponent<MeshFilter>();
            if (directFilter != null && directFilter.sharedMesh != null)
            {
                Vector3 directExtents = directFilter.sharedMesh.bounds.extents;
                return Mathf.Max(
                    0.0001f,
                    directExtents.x,
                    directExtents.y,
                    directExtents.z);
            }

            MeshFilter[] filters = body.GetComponentsInChildren<MeshFilter>(true);
            float radius = 0f;
            foreach (MeshFilter filter in filters)
            {
                if (filter.sharedMesh == null)
                {
                    continue;
                }
                Vector3 extents = Vector3.Scale(
                    filter.sharedMesh.bounds.extents,
                    filter.transform.localScale);
                radius = Mathf.Max(radius, extents.x, extents.y, extents.z);
            }
            return Mathf.Max(0.0001f, radius);
        }

        private static Transform FindDescendant(Transform root, string name)
        {
            if (root.name == name)
            {
                return root;
            }
            for (int index = 0; index < root.childCount; index++)
            {
                Transform found = FindDescendant(root.GetChild(index), name);
                if (found != null)
                {
                    return found;
                }
            }
            return null;
        }

        private static void SetLayerRecursively(GameObject root, int layer)
        {
            root.layer = layer;
            for (int index = 0; index < root.transform.childCount; index++)
            {
                SetLayerRecursively(root.transform.GetChild(index).gameObject, layer);
            }
        }

        private static BodyDefinition[] CreateDefinitions()
        {
            const double smallBodyEpoch = 2488434.5d;
            return new[]
            {
                Body("Sun_8", "太阳", "Sun", OrbitKind.None, default, default,
                    0d, 696340d, 609.12f, 7.25f, new Color(1f, 0.7f, 0.2f, 0.8f)),
                Planet("Mercury_2", "水星", "Mercury", 87.969d, 2439.7d, 1407.6f, 0.034f,
                    new SolarOrbitalElements(.38709843, .20563661, 7.00559432, 252.25166724,
                        77.45771895, 48.33961819, 0, .00002123, -.00590158,
                        149472.67486623, .15940013, -.12214182), new Color(.48f, .58f, .62f, .52f)),
                Planet("Venus_1", "金星", "Venus", 224.701d, 6051.8d, -5832.5f, 177.36f,
                    new SolarOrbitalElements(.72332102, .00676399, 3.39777545, 181.97970850,
                        131.76755713, 76.67261496, -.00000026, -.00005107, .00043494,
                        58517.81560260, .05679648, -.27274174), new Color(.72f, .64f, .42f, .55f)),
                Planet("Earth_0", "地球", "Earth", 365.256d, 6378.137d, 23.9345f, 23.439f,
                    new SolarOrbitalElements(1.00000018, .01673163, -.00054346, 100.46691572,
                        102.93005885, -5.11260389, -.00000003, -.00003661, -.01337178,
                        35999.37306329, .31795260, -.24123856), new Color(.25f, .62f, .86f, .62f)),
                Planet("Mars_3", "火星", "Mars", 686.98d, 3389.5d, 24.6229f, 25.19f,
                    new SolarOrbitalElements(1.52371243, .09336511, 1.85181869, -4.56813164,
                        -23.91744784, 49.71320984, .00000097, .00009149, -.00724757,
                        19140.29934243, .45223625, -.26852431), new Color(.86f, .35f, .2f, .62f)),
                Planet("Jupiter_4", "木星", "Jupiter", 4332.59d, 69911d, 9.925f, 3.13f,
                    new SolarOrbitalElements(5.20248019, .04853590, 1.29861416, 34.33479152,
                        14.27495244, 100.29282654, -.00002864, .00018026, -.00322699,
                        3034.90371757, .18199196, .13024619,
                        -.00012452, .06064060, -.35635438, 38.35125), new Color(.72f, .58f, .43f, .58f)),
                Planet("Saturn_5", "土星", "Saturn", 10759.22d, 58232d, 10.656f, 26.73f,
                    new SolarOrbitalElements(9.54149883, .05550825, 2.49424102, 50.07571329,
                        92.86136063, 113.63998702, -.00003065, -.00032044, .00451969,
                        1222.11494724, .54179478, -.25015002,
                        .00025899, -.13434469, .87320147, 38.35125), new Color(.78f, .7f, .48f, .58f)),
                Planet("Uranus_6", "天王星", "Uranus", 30688.5d, 25362d, -17.24f, 97.77f,
                    new SolarOrbitalElements(19.18797948, .04685740, .77298127, 314.20276625,
                        172.43404441, 73.96250215, -.00020455, -.00001550, -.00180155,
                        428.49512595, .09266985, .05739699,
                        .00058331, -.97731848, .17689245, 7.67025), new Color(.35f, .76f, .82f, .58f)),
                Planet("Neptune_7", "海王星", "Neptune", 60182d, 24622d, 16.11f, 28.32f,
                    new SolarOrbitalElements(30.06952752, .00895439, 1.77005520, 304.22289287,
                        46.68158724, 131.78635853, .00006447, .00000818, .00022400,
                        218.46515314, .01009938, -.00606302,
                        -.00041348, .68346318, -.10162547, 7.67025), new Color(.24f, .44f, .86f, .58f)),
                SmallBody("ceres_10", "谷神星", "Ceres", 469.7d, 9.07417f, 4f,
                    new SolarSmallBodyOrbit(smallBodyEpoch, 2.767029043655, .0751549406509177,
                        10.5807981067195, 76.0676175972381, 79.0685509719359,
                        343.13691281556, 1681.19853685177), new Color(.58f, .64f, .66f, .48f)),
                SmallBody("eris_11", "阋神星", "Eris", 1200d, 25.9f, 78f,
                    new SolarSmallBodyOrbit(smallBodyEpoch, 67.6196775668769, .441652585567567,
                        44.2078468379019, 151.560441408204, 36.0271696999483,
                        258.72170337172, 203099.133356312), new Color(.66f, .72f, .76f, .48f)),
                SmallBody("haumea_12", "妊神星", "Haumea", 800d, 3.9154f, 28f,
                    new SolarSmallBodyOrbit(smallBodyEpoch, 43.1443659978959, .193681622664761,
                        28.2029226683418, 239.103117468046, 121.931256548188,
                        319.548009071628, 103510.537572203), new Color(.64f, .7f, .74f, .48f)),
                SmallBody("makemake_13", "鸟神星", "Makemake", 715d, 22.8266f, 29f,
                    new SolarSmallBodyOrbit(smallBodyEpoch, 45.7231818144105, .156403263078473,
                        28.9343368808691, 294.501329829284, 79.4388830871674,
                        259.962478233944, 112928.378937578), new Color(.68f, .54f, .42f, .48f)),
                Body("moon_9", "月球", "Moon", OrbitKind.Moon, default,
                    new SolarSmallBodyOrbit(SolarSystemOrbitMath.J2000JulianDate,
                        .00256955529, .0554, 5.16, 318.15, 125.08, 135.27, 27.321661),
                    27.321661d, 1737.4d, 655.728f, 6.68f, new Color(.56f, .62f, .66f, .52f)),
            };
        }

        private static BodyDefinition Planet(
            string node,
            string chinese,
            string english,
            double period,
            double radius,
            float rotation,
            float tilt,
            SolarOrbitalElements orbit,
            Color color)
        {
            return Body(node, chinese, english, OrbitKind.Planet, orbit, default,
                period, radius, rotation, tilt, color);
        }

        private static BodyDefinition SmallBody(
            string node,
            string chinese,
            string english,
            double radius,
            float rotation,
            float tilt,
            SolarSmallBodyOrbit orbit,
            Color color)
        {
            return Body(node, chinese, english, OrbitKind.SmallBody, default, orbit,
                orbit.OrbitalPeriodDays, radius, rotation, tilt, color);
        }

        private static BodyDefinition Body(
            string node,
            string chinese,
            string english,
            OrbitKind kind,
            SolarOrbitalElements planetOrbit,
            SolarSmallBodyOrbit smallBodyOrbit,
            double period,
            double radius,
            float rotation,
            float tilt,
            Color color)
        {
            return new BodyDefinition
            {
                NodeName = node,
                ChineseName = chinese,
                EnglishName = english,
                OrbitKind = kind,
                PlanetOrbit = planetOrbit,
                SmallBodyOrbit = smallBodyOrbit,
                OrbitalPeriodDays = period,
                PhysicalRadiusKilometers = radius,
                RotationHours = rotation,
                AxialTiltDegrees = tilt,
                OrbitColor = color,
            };
        }
    }
}
