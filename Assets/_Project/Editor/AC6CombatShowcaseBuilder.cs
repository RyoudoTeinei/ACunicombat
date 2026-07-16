using System;
using System.Collections.Generic;
using System.Linq;
using ACunicombat.Mechs;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

[InitializeOnLoad]
public static class AC6CombatShowcaseBuilder
{
    private const string ScenePath =
        "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Scenes/AC6CombatShowcase.unity";
    private const string ValidationScenePath =
        "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Scenes/AC6SkinnedModularValidation.unity";
    private const string LoadoutPath =
        "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Loadouts/AC6_Skinned_Uniform1010.asset";
    private const string AnimationPath =
        "Assets/_Project/LocalSamples/AC6MelanderCase/Raw/Gltf/Melander_1010_Rerun.glb";
    private const string BodyTimelinePath =
        "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Effects/AC6_a000_010900_Effects.asset";
    private const string MeleeTimelinePath =
        "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Effects/AC6_a000_040010_Effects.asset";
    private const string MaterialFolder =
        "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Materials";
    private const string TargetMaterialPath = MaterialFolder + "/AC6CombatShowcaseTarget.mat";
    private const string AutoBuildSessionKey = "ACunicombat.AC6CombatShowcase.AutoBuildAttempted";
    private static readonly HashSet<string> BatchPhases = new HashSet<string>();
    private static DateTime batchStartedUtc;
    private static bool batchSawMissileSalvo;
    private static bool batchSawRangedWeaponFire;
    private static bool batchSawMaterialBindings;
    private static bool batchSawRequiredEquipment;

    static AC6CombatShowcaseBuilder()
    {
        EditorApplication.delayCall += TryBuildOnFirstImport;
    }

    [MenuItem("Tools/ACunicombat/AC6/Build Combat Showcase")]
    public static void BuildMenu()
    {
        if (!Build(out var error))
        {
            EditorUtility.DisplayDialog("AC6 Combat Showcase", error, "OK");
            return;
        }

        EditorUtility.DisplayDialog(
            "AC6 Combat Showcase",
            "Built and opened AC6CombatShowcase.unity. Press Play to run the seven-phase loop.",
            "OK");
    }

    public static bool Build(out string error)
    {
        error = string.Empty;
        var loadout = AssetDatabase.LoadAssetAtPath<MechLoadoutDefinition>(LoadoutPath);
        if (loadout == null)
        {
            error = "Uniform1010 loadout is missing. Run Build And Validate Skinned Modular Samples first.";
            return false;
        }

        AssetDatabase.ImportAsset(AnimationPath, ImportAssetOptions.ForceUpdate);
        var bodyClip = AssetDatabase.LoadAllAssetsAtPath(AnimationPath)
            .OfType<AnimationClip>()
            .Where(candidate => candidate != null && candidate.length > 0.01f)
            .OrderByDescending(candidate => candidate.length)
            .FirstOrDefault();
        if (bodyClip == null)
        {
            error = "The Melander body AnimationClip is missing.";
            return false;
        }

        var scene = AssetDatabase.LoadAssetAtPath<SceneAsset>(ValidationScenePath) != null
            ? EditorSceneManager.OpenScene(ValidationScenePath, OpenSceneMode.Single)
            : EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        SceneManager.SetActiveScene(scene);
        var mech = GameObject.Find("Validation_Uniform1010");
        if (mech != null)
        {
            mech.transform.SetParent(null, true);
            var sceneRoots = scene.GetRootGameObjects();
            for (var i = 0; i < sceneRoots.Length; i++)
            {
                if (sceneRoots[i] != mech)
                {
                    UnityEngine.Object.DestroyImmediate(sceneRoots[i]);
                }
            }
        }

        var root = new GameObject("AC6_Combat_Showcase");
        if (mech == null)
        {
            mech = new GameObject("Player_Melander_1010");
            var assembler = mech.AddComponent<MechAssembler>();
            assembler.Loadout = loadout;
            assembler.EditorConfigureSynchronizedSkinnedParts(
                bodyClip,
                shouldAssembleOnAwake: false,
                shouldCreatePlaceholders: false,
                shouldLoop: true,
                playbackSpeed: 1f);
            assembler.Assemble();
        }

        mech.name = "Player_Melander_1010";
        mech.transform.SetParent(root.transform, false);
        mech.transform.localPosition = Vector3.zero;
        mech.transform.localRotation = Quaternion.identity;
        var existingAssembler = mech.GetComponent<MechAssembler>();
        if (existingAssembler != null)
        {
            existingAssembler.EditorConfigureSynchronizedSkinnedParts(
                bodyClip,
                shouldAssembleOnAwake: false,
                shouldCreatePlaceholders: false,
                shouldLoop: true,
                playbackSpeed: 1f);
        }

        var target = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        target.name = "Moving_Target";
        target.transform.SetParent(root.transform, false);
        target.transform.position = new Vector3(0f, 6f, -34f);
        target.transform.localScale = Vector3.one * 2.2f;
        var targetMaterial = GetOrCreateTargetMaterial();
        target.GetComponent<Renderer>().sharedMaterial = targetMaterial;

        var cameraObject = new GameObject("Combat Showcase Camera");
        cameraObject.transform.SetParent(root.transform, false);
        cameraObject.transform.position = new Vector3(13f, 8.5f, 20f);
        cameraObject.transform.rotation = Quaternion.LookRotation(
            new Vector3(0f, 5f, 0f) - cameraObject.transform.position,
            Vector3.up);
        var camera = cameraObject.AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.015f, 0.022f, 0.045f);
        camera.nearClipPlane = 0.1f;
        camera.farClipPlane = 500f;
        camera.fieldOfView = 56f;
        cameraObject.tag = "MainCamera";

        var keyLightObject = new GameObject("Key Light");
        keyLightObject.transform.SetParent(root.transform, false);
        keyLightObject.transform.rotation = Quaternion.Euler(42f, -35f, 0f);
        var keyLight = keyLightObject.AddComponent<Light>();
        keyLight.type = LightType.Directional;
        keyLight.color = new Color(0.82f, 0.9f, 1f);
        keyLight.intensity = 2.1f;

        var rimLightObject = new GameObject("Rim Light");
        rimLightObject.transform.SetParent(root.transform, false);
        rimLightObject.transform.position = new Vector3(-8f, 10f, -12f);
        var rimLight = rimLightObject.AddComponent<Light>();
        rimLight.type = LightType.Point;
        rimLight.color = new Color(0.2f, 0.5f, 1f);
        rimLight.intensity = 24f;
        rimLight.range = 45f;

        var controller = root.AddComponent<AC6CombatShowcaseController>();
        controller.Configure(
            mech,
            target.transform,
            camera,
            AssetDatabase.LoadAssetAtPath<MechActionEffectTimeline>(BodyTimelinePath),
            AssetDatabase.LoadAssetAtPath<MechActionEffectTimeline>(MeleeTimelinePath),
            AC6FxrEffectFactory.BuildLibrary());

        EditorSceneManager.SaveScene(scene, ScenePath);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Selection.activeGameObject = root;
        SceneView.lastActiveSceneView?.FrameSelected();
        Debug.Log($"[AC6CombatShowcase] Built {ScenePath}");
        return true;
    }

    public static void RunBatchValidation()
    {
        if (!Application.isBatchMode)
        {
            Debug.LogWarning("[AC6CombatShowcaseValidation] RunBatchValidation is intended for -batchmode.");
            return;
        }

        if (!Build(out var error))
        {
            throw new InvalidOperationException(error);
        }

        BatchPhases.Clear();
        batchSawMissileSalvo = false;
        batchSawRangedWeaponFire = false;
        batchSawMaterialBindings = false;
        batchSawRequiredEquipment = false;
        batchStartedUtc = DateTime.UtcNow;
        EditorSettings.enterPlayModeOptionsEnabled = true;
        EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
        Application.logMessageReceived += CaptureBatchLog;
        EditorApplication.update += UpdateBatchValidation;
        EditorApplication.isPlaying = true;
        Debug.Log("[AC6CombatShowcaseValidation] Started 31-second play-mode validation.");
    }

    private static void TryBuildOnFirstImport()
    {
        if (SessionState.GetBool(AutoBuildSessionKey, false) ||
            AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) != null)
        {
            return;
        }

        SessionState.SetBool(AutoBuildSessionKey, true);
        if (!Build(out var error))
        {
            Debug.LogWarning($"[AC6CombatShowcase] Automatic build skipped: {error}");
        }
    }

    private static void CaptureBatchLog(string condition, string stackTrace, LogType type)
    {
        if (condition.Contains("[AC6CombatShowcase] Phase "))
        {
            BatchPhases.Add(condition);
        }
        if (condition.Contains("Missile salvo spawned") && !condition.Contains("spawned 0"))
        {
            batchSawMissileSalvo = true;
        }
        if (condition.Contains("Ranged weapon fired action") &&
            !condition.EndsWith("action .", StringComparison.Ordinal))
        {
            batchSawRangedWeaponFire = true;
        }
        if (condition.Contains("Material bindings:"))
        {
            batchSawMaterialBindings = true;
        }
        if (condition.Contains("Bindings:") &&
            condition.Contains("missilePods=2") &&
            condition.Contains("rightHand=1") &&
            condition.Contains("pileBunker=1") &&
            !condition.Contains("boosters=0") &&
            !condition.Contains("exhausts=0"))
        {
            batchSawRequiredEquipment = true;
        }
    }

    private static void UpdateBatchValidation()
    {
        if ((DateTime.UtcNow - batchStartedUtc).TotalSeconds < 32d)
        {
            return;
        }

        EditorApplication.update -= UpdateBatchValidation;
        Application.logMessageReceived -= CaptureBatchLog;
        var success = BatchPhases.Count >= 7 &&
            batchSawMissileSalvo &&
            batchSawRangedWeaponFire &&
            batchSawMaterialBindings &&
            batchSawRequiredEquipment;
        Debug.Log(
            $"[AC6CombatShowcaseValidation] RESULT={(success ? "PASS" : "FAIL")} " +
            $"phases={BatchPhases.Count}/7 ranged={batchSawRangedWeaponFire} " +
            $"missile={batchSawMissileSalvo} " +
            $"materials={batchSawMaterialBindings} equipment={batchSawRequiredEquipment}");
        EditorApplication.isPlaying = false;
        EditorApplication.delayCall += () => EditorApplication.Exit(success ? 0 : 2);
    }

    private static Material GetOrCreateTargetMaterial()
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(TargetMaterialPath);
        if (material == null)
        {
            EnsureFolder(MaterialFolder);
            material = new Material(
                Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color"))
            {
                name = "AC6 Combat Showcase Target"
            };
            AssetDatabase.CreateAsset(material, TargetMaterialPath);
        }

        var targetColor = new Color(1f, 0.18f, 0.08f, 1f);
        if (material.HasProperty("_BaseColor"))
        {
            material.SetColor("_BaseColor", targetColor);
        }
        if (material.HasProperty("_Color"))
        {
            material.SetColor("_Color", targetColor);
        }
        EditorUtility.SetDirty(material);
        return material;
    }

    private static void EnsureFolder(string folderPath)
    {
        var segments = folderPath.Split('/');
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
