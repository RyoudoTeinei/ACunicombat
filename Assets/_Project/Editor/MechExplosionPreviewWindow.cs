using ACunicombat.Mechs;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.VFX;

public sealed class MechExplosionPreviewWindow : EditorWindow
{
    private const string PendingPreviewScaleKey = "ACunicombat.ExplosionPreview.PendingScale";
    private const string PendingPreviewKey = "ACunicombat.ExplosionPreview.Pending";
    private const float StandardMissileScale = 3.4f;
    private const float InterceptorMissileScale = 2.2f;

    private PreviewRenderUtility previewUtility;
    private GameObject activeExplosion;
    private readonly List<GameObject> previewVfxObjects = new();
    private readonly List<GameObject> previewParticleObjects = new();
    private float customScale = StandardMissileScale;
    private float cameraDistance = 18f;
    private float orbitYaw = 24f;
    private float orbitPitch = 9f;
    private Vector3 previewCenter = Vector3.zero;
    private float framedDistance = 8f;
    private bool loopPlayback = true;
    private double nextLoopTime;
    private double lastSimulationTime;
    private float previewAge;
    private float lastSpawnScale = StandardMissileScale;
    private Vector2 dragOrigin;
    private float dragYaw;
    private float dragPitch;

    [MenuItem("Tools/ACunicombat/Effects/Missile Explosion Preview")]
    public static void Open()
    {
        var window = GetWindow<MechExplosionPreviewWindow>("Explosion Preview");
        window.minSize = new Vector2(720f, 560f);
        window.Show();
    }

    private void OnEnable()
    {
        EditorApplication.update += UpdatePreview;
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        if (EditorApplication.isPlaying && SessionState.GetBool(PendingPreviewKey, false))
        {
            EditorApplication.delayCall += SpawnPendingPreview;
        }
    }

    private void OnDisable()
    {
        EditorApplication.update -= UpdatePreview;
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        CleanupPreview();
    }

    private void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.EnteredPlayMode &&
            SessionState.GetBool(PendingPreviewKey, false))
        {
            EditorApplication.delayCall += SpawnPendingPreview;
            return;
        }

        if (state == PlayModeStateChange.ExitingPlayMode ||
            state == PlayModeStateChange.EnteredEditMode)
        {
            CleanupPreview();
        }
    }

    private void OnGUI()
    {
        DrawHeader();
        var previewRect = GUILayoutUtility.GetRect(
            320f,
            4096f,
            260f,
            4096f,
            GUILayout.ExpandWidth(true),
            GUILayout.ExpandHeight(true));

        if (!EditorApplication.isPlaying)
        {
            EditorGUI.DrawRect(previewRect, Color.black);
            DrawCenteredMessage(previewRect, "点击下方导弹按钮，观察台会自动进入 Play 并播放爆炸");
        }
        else
        {
            EnsurePreviewUtility();
            RenderPreview(previewRect);
            DrawPreviewStatus(previewRect);
            HandlePreviewInput(previewRect);
        }

        DrawControls();
    }

    private static void DrawHeader()
    {
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            GUILayout.Label("导弹爆炸独立观察台", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            GUILayout.Label("左键拖拽环绕  |  滚轮缩放", EditorStyles.miniLabel);
        }
    }

    private void DrawControls()
    {
        EditorGUILayout.Space(5f);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("普通导弹", GUILayout.Height(32f)))
            {
                Spawn(StandardMissileScale);
            }
            if (GUILayout.Button("拦截弹", GUILayout.Height(32f)))
            {
                Spawn(InterceptorMissileScale);
            }
            if (GUILayout.Button("自定义尺寸", GUILayout.Height(32f)))
            {
                Spawn(customScale);
            }

            using (new EditorGUI.DisabledScope(!EditorApplication.isPlaying))
            {
                loopPlayback = GUILayout.Toggle(
                    loopPlayback,
                    "循环重放",
                    "Button",
                    GUILayout.Width(92f),
                    GUILayout.Height(32f));
            }
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            customScale = EditorGUILayout.Slider("爆炸尺寸", customScale, 1f, 8f);
            cameraDistance = EditorGUILayout.Slider("观察距离", cameraDistance, 0.25f, 80f);
            if (GUILayout.Button("重置视角", GUILayout.Width(84f)))
            {
                cameraDistance = framedDistance;
                orbitYaw = 24f;
                orbitPitch = 9f;
            }
        }
    }

    private static void DrawCenteredMessage(Rect rect, string message)
    {
        var style = new GUIStyle(EditorStyles.centeredGreyMiniLabel)
        {
            fontSize = 14,
            alignment = TextAnchor.MiddleCenter,
        };
        style.normal.textColor = new Color(0.72f, 0.76f, 0.8f, 1f);
        GUI.Label(rect, message, style);
    }

    private void RenderPreview(Rect rect)
    {
        if (previewUtility == null || Event.current.type != EventType.Repaint)
        {
            return;
        }

        previewUtility.BeginPreview(rect, GUIStyle.none);
        var orbit = Quaternion.Euler(orbitPitch, orbitYaw, 0f);
        previewUtility.camera.transform.position = previewCenter + orbit * (Vector3.back * cameraDistance);
        previewUtility.camera.transform.rotation = orbit;
        previewUtility.Render(true);
        previewUtility.EndAndDrawPreview(rect);
    }

    private void DrawPreviewStatus(Rect rect)
    {
        var explosionReady = activeExplosion != null;
        var effects = GetPreviewEffects();
        var aliveCount = 0;
        var culledCount = 0;
        for (var i = 0; i < effects.Length; i++)
        {
            aliveCount += effects[i].aliveParticleCount;
            if (effects[i].culled)
            {
                culledCount++;
            }
        }

        var particleCount = 0;
        for (var objectIndex = 0; objectIndex < previewParticleObjects.Count; objectIndex++)
        {
            if (previewParticleObjects[objectIndex] == null)
            {
                continue;
            }

            var systems = previewParticleObjects[objectIndex].GetComponentsInChildren<ParticleSystem>(true);
            for (var systemIndex = 0; systemIndex < systems.Length; systemIndex++)
            {
                particleCount += systems[systemIndex].particleCount;
            }
        }
        var status = explosionReady
            ? $"预览场景：活动  VFX：{effects.Length}  存活：{aliveCount}  剔除：{culledCount}  粒子：{particleCount}"
            : "预览场景：就绪  爆炸：等待播放";
        var statusRect = new Rect(rect.x + 10f, rect.y + 9f, 540f, 22f);
        EditorGUI.DrawRect(statusRect, new Color(0f, 0f, 0f, 0.72f));
        var style = new GUIStyle(EditorStyles.miniLabel);
        style.normal.textColor = new Color(0.45f, 1f, 0.62f, 1f);
        GUI.Label(new Rect(statusRect.x + 6f, statusRect.y + 2f, statusRect.width - 12f, 18f), status, style);
    }

    private void HandlePreviewInput(Rect rect)
    {
        var current = Event.current;
        if (!rect.Contains(current.mousePosition))
        {
            return;
        }

        if (current.type == EventType.MouseDown && current.button == 0)
        {
            dragOrigin = current.mousePosition;
            dragYaw = orbitYaw;
            dragPitch = orbitPitch;
            current.Use();
        }
        else if (current.type == EventType.MouseDrag && current.button == 0)
        {
            var delta = current.mousePosition - dragOrigin;
            orbitYaw = dragYaw + delta.x * 0.35f;
            orbitPitch = Mathf.Clamp(dragPitch - delta.y * 0.3f, -75f, 75f);
            current.Use();
            Repaint();
        }
        else if (current.type == EventType.ScrollWheel)
        {
            cameraDistance = Mathf.Clamp(cameraDistance * (1f + current.delta.y * 0.08f), 0.25f, 80f);
            current.Use();
            Repaint();
        }
    }

    private void Spawn(float scale)
    {
        if (!EditorApplication.isPlaying)
        {
            SessionState.SetFloat(PendingPreviewScaleKey, scale);
            SessionState.SetBool(PendingPreviewKey, true);
            ShowNotification(new GUIContent("正在进入 Play 模式并启动预览..."));
            EditorApplication.EnterPlaymode();
            return;
        }

        RecreatePreviewUtility();
        lastSpawnScale = scale;
        activeExplosion = MechImpactEffect.SpawnExplosion(Vector3.zero, Vector3.forward, scale);
        if (activeExplosion != null)
        {
            activeExplosion.hideFlags = HideFlags.DontSave;
            RegisterEffectsForPreview(activeExplosion);
            FrameExplosion(scale);
        }
        previewAge = 0f;
        lastSimulationTime = EditorApplication.timeSinceStartup;
        nextLoopTime = EditorApplication.timeSinceStartup + 3.5d;
        Repaint();
    }

    private void SpawnPendingPreview()
    {
        if (!EditorApplication.isPlaying || !SessionState.GetBool(PendingPreviewKey, false))
        {
            return;
        }

        var scale = SessionState.GetFloat(PendingPreviewScaleKey, StandardMissileScale);
        SessionState.EraseBool(PendingPreviewKey);
        SessionState.EraseFloat(PendingPreviewScaleKey);
        Spawn(scale);
    }

    private void UpdatePreview()
    {
        if (!EditorApplication.isPlaying)
        {
            return;
        }

        var now = EditorApplication.timeSinceStartup;
        AdvancePreviewSimulation(now);

        if (loopPlayback && now >= nextLoopTime)
        {
            Spawn(lastSpawnScale);
        }
        Repaint();
    }

    private void EnsurePreviewUtility()
    {
        if (previewUtility == null)
        {
            CreatePreviewUtility();
        }
    }

    private void RecreatePreviewUtility()
    {
        CleanupPreview();
        CreatePreviewUtility();
    }

    private void CreatePreviewUtility()
    {
        previewUtility = new PreviewRenderUtility();
        previewUtility.camera.fieldOfView = 42f;
        previewUtility.camera.allowHDR = true;
        previewUtility.camera.allowMSAA = false;
        previewUtility.camera.nearClipPlane = 0.05f;
        previewUtility.camera.farClipPlane = 250f;
        previewUtility.camera.clearFlags = CameraClearFlags.SolidColor;
        previewUtility.camera.backgroundColor = Color.black;
        previewUtility.camera.cullingMask = ~0;
        previewUtility.ambientColor = new Color(0.03f, 0.03f, 0.035f, 1f);
        previewUtility.lights[0].intensity = 0.35f;
        previewUtility.lights[1].intensity = 0.15f;
    }

    private static void PrepareVisualEffects(GameObject root)
    {
        var effects = root.GetComponentsInChildren<VisualEffect>(true);
        for (var i = 0; i < effects.Length; i++)
        {
            var effect = effects[i];
            effect.enabled = true;
            effect.Reinit();
            effect.Play();
            // Preview scenes do not receive the normal player-loop VFX update.
            // Pause automatic time and advance the graph explicitly below.
            effect.pause = true;
            effect.Simulate(1f / 60f, 1);
        }
    }

    private void AdvancePreviewSimulation(double now)
    {
        if (previewVfxObjects.Count == 0 && previewParticleObjects.Count == 0)
        {
            lastSimulationTime = now;
            return;
        }

        var deltaTime = lastSimulationTime > 0d
            ? Mathf.Clamp((float)(now - lastSimulationTime), 1f / 240f, 1f / 20f)
            : 1f / 60f;
        lastSimulationTime = now;
        previewAge += deltaTime;

        var effects = GetPreviewEffects();
        for (var i = 0; i < effects.Length; i++)
        {
            var effect = effects[i];
            if (ShouldStopEmission(effect.name, previewAge))
            {
                effect.Stop();
            }
            effect.Simulate(deltaTime, 1);
        }

        for (var objectIndex = 0; objectIndex < previewParticleObjects.Count; objectIndex++)
        {
            var particleObject = previewParticleObjects[objectIndex];
            if (particleObject == null)
            {
                continue;
            }

            var systems = particleObject.GetComponentsInChildren<ParticleSystem>(true);
            for (var systemIndex = 0; systemIndex < systems.Length; systemIndex++)
            {
                systems[systemIndex].Simulate(deltaTime, true, false, false);
            }
        }
    }

    private static bool ShouldStopEmission(string effectName, float age)
    {
        if (effectName.StartsWith("HotFragments"))
        {
            return age >= 0.095f;
        }
        if (effectName.StartsWith("Fireball_") || effectName.StartsWith("Flame_"))
        {
            return age >= 0.035f;
        }
        if (effectName.StartsWith("Smoke_"))
        {
            return age >= 0.46f;
        }
        return false;
    }

    private void RegisterEffectsForPreview(GameObject explosionRoot)
    {
        // PreviewRenderUtility owns a separate scene and does not automatically render
        // runtime particle children. Register both GPU VFX and classic Particle Systems.
        var particleSystems = explosionRoot.GetComponentsInChildren<ParticleSystem>(true);
        for (var i = 0; i < particleSystems.Length; i++)
        {
            var particleObject = particleSystems[i].gameObject;
            particleObject.transform.SetParent(null, true);
            particleObject.hideFlags = HideFlags.DontSave;
            AddManagedPreviewObject(previewUtility, particleObject);
            previewParticleObjects.Add(particleObject);
            particleSystems[i].Clear(true);
            particleSystems[i].Play(true);
            particleSystems[i].Simulate(1f / 60f, true, false, false);
        }

        var effects = explosionRoot.GetComponentsInChildren<VisualEffect>(true);
        for (var i = 0; i < effects.Length; i++)
        {
            var effectObject = effects[i].gameObject;
            effectObject.transform.SetParent(null, true);
            effectObject.hideFlags = HideFlags.DontSave;
            AddManagedPreviewObject(previewUtility, effectObject);
            previewVfxObjects.Add(effectObject);
            PrepareVisualEffects(effectObject);
        }
    }

    private static void AddManagedPreviewObject(PreviewRenderUtility utility, GameObject root)
    {
        var method = typeof(PreviewRenderUtility).GetMethod(
            "AddManagedGO",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new[] { typeof(GameObject) },
            null);
        if (method != null)
        {
            method.Invoke(utility, new object[] { root });
            return;
        }

        utility.AddSingleGO(root);
    }

    private void FrameExplosion(float scale)
    {
        var hasBounds = false;
        var bounds = new Bounds(Vector3.zero, Vector3.zero);
        for (var objectIndex = 0; objectIndex < previewVfxObjects.Count; objectIndex++)
        {
            var renderers = previewVfxObjects[objectIndex].GetComponentsInChildren<Renderer>(true);
            for (var rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
            {
                var candidate = renderers[rendererIndex].bounds;
                if (!IsFinite(candidate.center) || !IsFinite(candidate.size) || candidate.size.sqrMagnitude <= 0.000001f)
                {
                    continue;
                }

                if (!hasBounds)
                {
                    bounds = candidate;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(candidate);
                }
            }
        }

        for (var objectIndex = 0; objectIndex < previewParticleObjects.Count; objectIndex++)
        {
            var renderers = previewParticleObjects[objectIndex].GetComponentsInChildren<Renderer>(true);
            for (var rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
            {
                var candidate = renderers[rendererIndex].bounds;
                if (!IsFinite(candidate.center) || !IsFinite(candidate.size) || candidate.size.sqrMagnitude <= 0.000001f)
                {
                    continue;
                }

                if (!hasBounds)
                {
                    bounds = candidate;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(candidate);
                }
            }
        }

        previewCenter = hasBounds ? bounds.center : Vector3.zero;
        var radius = hasBounds ? Mathf.Max(0.25f, bounds.extents.magnitude) : Mathf.Max(1f, scale);
        var halfFov = previewUtility.camera.fieldOfView * 0.5f * Mathf.Deg2Rad;
        framedDistance = Mathf.Clamp(radius / Mathf.Tan(halfFov) * 1.18f, 0.35f, 80f);
        cameraDistance = framedDistance;
        previewUtility.camera.nearClipPlane = Mathf.Max(0.001f, framedDistance - radius * 1.35f);
        previewUtility.camera.farClipPlane = Mathf.Max(250f, framedDistance + radius * 4f);
    }

    private static bool IsFinite(Vector3 value)
    {
        return float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
    }

    private VisualEffect[] GetPreviewEffects()
    {
        var effects = new List<VisualEffect>(previewVfxObjects.Count);
        for (var i = 0; i < previewVfxObjects.Count; i++)
        {
            if (previewVfxObjects[i] != null && previewVfxObjects[i].TryGetComponent<VisualEffect>(out var effect))
            {
                effects.Add(effect);
            }
        }
        return effects.ToArray();
    }

    private void CleanupPreview()
    {
        if (activeExplosion != null)
        {
            DestroyImmediate(activeExplosion);
        }
        if (previewUtility != null)
        {
            previewUtility.Cleanup();
        }
        previewUtility = null;
        activeExplosion = null;
        previewVfxObjects.Clear();
        previewParticleObjects.Clear();
    }
}
