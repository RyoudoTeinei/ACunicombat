using System.Collections.Generic;
using System.IO;
using ACunicombat.Mechs;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class AC6Hover010900PreviewBuilder
{
    // Incrementing this source file also wakes an already-open Unity editor for build requests.
    private const string ActionId = "a000_010900";
    private const string RequestFileName = "BuildAC6Hover010900Preview.request";
    private const string GeneratedRoot =
        "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated";
    private const string ScenePath = GeneratedRoot + "/Scenes/AC6Hover010900Preview.unity";
    private const string LoadoutPath = GeneratedRoot +
        "/Loadouts/AC6_Skinned_Uniform1010.asset";
    private const string RegistryPath = GeneratedRoot +
        "/Actions/AC6Hover010900PreviewRegistry.asset";
    private const string ActionRoot = GeneratedRoot + "/Actions/" + ActionId;

    private static readonly (MechPartSlot Slot, string Token)[] FrameSlots =
    {
        (MechPartSlot.Head, "HD"),
        (MechPartSlot.Core, "BD"),
        (MechPartSlot.Arms, "AM"),
        (MechPartSlot.Legs, "LG"),
    };

    [MenuItem("Tools/ACunicombat/AC6/Preview a000_010900 Hover")]
    public static void BuildMenu()
    {
        if (!Build(out var report))
        {
            EditorUtility.DisplayDialog("a000_010900 Preview", report, "OK");
            return;
        }

        EditorUtility.DisplayDialog(
            "a000_010900 Preview",
            report + "\n\nPress Play to watch the 12-second loop.",
            "OK");
    }

    [InitializeOnLoadMethod]
    private static void BuildWhenRequested()
    {
        var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return;
        }

        var requestPath = Path.Combine(projectRoot, "Temp", RequestFileName);
        if (!File.Exists(requestPath))
        {
            return;
        }

        File.Delete(requestPath);
        EditorApplication.delayCall += () =>
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogWarning("[AC6Hover010900Preview] Build skipped while in Play Mode.");
                return;
            }

            if (!Build(out var report))
            {
                Debug.LogError("[AC6Hover010900Preview] " + report);
            }
            else
            {
                Debug.Log("[AC6Hover010900Preview] " + report);
            }
        };
    }

    public static bool Build(out string report)
    {
        if (!AC6SkinnedModularSampleBuilder.BuildSampleAssetsForPipeline(out var sampleReport))
        {
            report = "Modular sample build failed: " + sampleReport;
            return false;
        }

        var clipReport = AC6ActionLibraryBuilder.BuildReviewAction(ActionId, "FullBody", true);
        if (clipReport.Contains("failed") || clipReport.Contains("not found"))
        {
            report = clipReport;
            return false;
        }

        var slotClips = new List<MechBodyActionDefinition.SlotClip>();
        foreach (var (slot, token) in FrameSlots)
        {
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(
                $"{ActionRoot}/AC6_{ActionId}_{token}.anim");
            if (clip != null)
            {
                slotClips.Add(new MechBodyActionDefinition.SlotClip { slot = slot, clip = clip });
            }
        }

        if (slotClips.Count != FrameSlots.Length)
        {
            report = $"{ActionId} clips are incomplete: {slotClips.Count}/{FrameSlots.Length}.";
            return false;
        }

        EnsureFolder(GeneratedRoot + "/Actions");
        var registry = AssetDatabase.LoadAssetAtPath<MechActionRegistry>(RegistryPath);
        if (registry == null)
        {
            registry = ScriptableObject.CreateInstance<MechActionRegistry>();
            AssetDatabase.CreateAsset(registry, RegistryPath);
        }

        registry.EditorActions.Clear();
        registry.EditorActions.Add(new MechBodyActionDefinition
        {
            actionId = ActionId,
            displayNameZh = "a000_010900 isolated preview",
            categoryZh = "Review only",
            reviewStatus = "NeedsReview",
            layer = MechBodyActionLayer.FullBody,
            loop = true,
            blendInSeconds = 0f,
            blendOutSeconds = 0f,
            slotClips = slotClips,
        });
        EditorUtility.SetDirty(registry);

        var loadout = AssetDatabase.LoadAssetAtPath<MechLoadoutDefinition>(LoadoutPath);
        if (loadout == null)
        {
            report = "Melander 1010 loadout is missing: " + LoadoutPath;
            return false;
        }

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        SceneManager.SetActiveScene(scene);
        var root = new GameObject("AC6_a000_010900_Hover_Preview");
        var mech = new GameObject("Preview_Melander_1010");
        mech.transform.SetParent(root.transform, false);

        var assembler = mech.AddComponent<MechAssembler>();
        assembler.Loadout = loadout;
        assembler.EditorConfigureSynchronizedSkinnedParts(
            null,
            shouldAssembleOnAwake: false,
            shouldCreatePlaceholders: false,
            shouldLoop: true,
            playbackSpeed: 1f);
        assembler.Assemble();

        var legacyPlayer = mech.GetComponent<MechSynchronizedAnimationPlayer>();
        if (legacyPlayer != null)
        {
            legacyPlayer.StopPlayback();
            legacyPlayer.enabled = false;
            EditorUtility.SetDirty(legacyPlayer);
        }

        var preview = mech.AddComponent<MechSingleActionPreview>();
        preview.Configure(assembler, registry, ActionId);
        EditorUtility.SetDirty(preview);

        var cameraObject = new GameObject("Preview Camera", typeof(Camera), typeof(AudioListener));
        cameraObject.tag = "MainCamera";
        cameraObject.transform.SetParent(root.transform, false);
        cameraObject.transform.position = new Vector3(13f, 8.5f, 20f);
        cameraObject.transform.rotation = Quaternion.LookRotation(
            new Vector3(0f, 5f, 0f) - cameraObject.transform.position,
            Vector3.up);
        var camera = cameraObject.GetComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.015f, 0.02f, 0.035f);
        camera.fieldOfView = 52f;
        camera.nearClipPlane = 0.1f;
        camera.farClipPlane = 300f;

        CreateDirectionalLight(root.transform, "Key Light", new Vector3(38f, -35f, 0f), 2.1f);
        CreateDirectionalLight(root.transform, "Fill Light", new Vector3(330f, 145f, 0f), 0.8f);

        var labelObject = new GameObject("Action Label");
        labelObject.transform.SetParent(root.transform, false);
        labelObject.transform.position = new Vector3(0f, 9.5f, 0f);
        var label = labelObject.AddComponent<TextMesh>();
        label.text = "a000_010900  |  12 s isolated loop";
        label.anchor = TextAnchor.MiddleCenter;
        label.alignment = TextAlignment.Center;
        label.characterSize = 0.18f;
        label.fontSize = 42;

        EnsureFolder(GeneratedRoot + "/Scenes");
        EditorSceneManager.SaveScene(scene, ScenePath);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Selection.activeGameObject = root;
        SceneView.lastActiveSceneView?.FrameSelected();

        report = $"Built and opened {ScenePath}.";
        return true;
    }

    private static void CreateDirectionalLight(
        Transform parent,
        string name,
        Vector3 euler,
        float intensity)
    {
        var lightObject = new GameObject(name);
        lightObject.transform.SetParent(parent, false);
        lightObject.transform.rotation = Quaternion.Euler(euler);
        var light = lightObject.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = intensity;
        light.color = new Color(0.86f, 0.91f, 1f);
    }

    private static void EnsureFolder(string path)
    {
        var segments = path.Split('/');
        var current = segments[0];
        for (var i = 1; i < segments.Length; i++)
        {
            var next = current + "/" + segments[i];
            if (!AssetDatabase.IsValidFolder(next))
            {
                AssetDatabase.CreateFolder(current, segments[i]);
            }
            current = next;
        }
    }
}
