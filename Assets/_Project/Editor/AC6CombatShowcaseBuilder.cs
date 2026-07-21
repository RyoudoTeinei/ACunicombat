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
    private const string ShowcaseActionRegistryPath =
        "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Actions/AC6CombatShowcaseRegistry.asset";
    private const string BehaviorGraphPath =
        "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/AC6BehaviorGraph.asset";
    private const string GeneratedActionsRoot =
        "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Actions";
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
    private static bool batchSawRangedWeaponFire;
    private static bool batchSawMaterialBindings;
    private static bool batchSawRequiredEquipment;
    private static bool batchSawExhaustAxisPass;
    private static bool batchSawExhaustAxisFail;
    private static bool batchSawBodyActionGraph;
    private static bool batchSawBehaviorGraph;
    private static int batchBehaviorTransitions;
    private static readonly HashSet<string> BatchBodyActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> BatchUpperBodyActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> BatchPoseChecks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] RequiredBodyActions =
    {
        "a000_000000", "a000_020200", "a000_004240"
    };

    private static readonly string[] RequiredUpperBodyActions =
    {
        "a200_030020", "a200_030022", "a200_030030"
    };

    private static readonly string[] RequiredPosePhases =
    {
        "ForwardAssaultBoost", "LeftQuickBoost", "RightQuickBoost", "RaiseAndFire", "Recovery"
    };

    private static readonly ShowcaseActionSpec[] ShowcaseActions =
    {
        new ShowcaseActionSpec("a000_000000", "基础待机", MechBodyActionLayer.FullBody, true, 0.12f, 0.12f),
        new ShowcaseActionSpec("a000_120200", "前向 AB 演示（官方 Boost Flight 姿态）", MechBodyActionLayer.FullBody, true, 0.08f, 0.12f),
        new ShowcaseActionSpec("a000_028002", "左 QB 方向段候选", MechBodyActionLayer.FullBody, false, 0.04f, 0.08f),
        new ShowcaseActionSpec("a000_004242", "左 QB 收束（人工目检：左移制动）", MechBodyActionLayer.FullBody, false, 0.04f, 0.1f),
        new ShowcaseActionSpec("a000_028003", "右 QB 方向段候选", MechBodyActionLayer.FullBody, false, 0.04f, 0.08f),
        new ShowcaseActionSpec("a000_004243", "右 QB 收束（人工目检：右移制动）", MechBodyActionLayer.FullBody, false, 0.04f, 0.1f),
        new ShowcaseActionSpec("a200_030020", "抬臂", MechBodyActionLayer.UpperBody, false, 0.04f, 0.06f),
        new ShowcaseActionSpec("a200_030022", "举枪保持", MechBodyActionLayer.UpperBody, true, 0.04f, 0.06f),
        new ShowcaseActionSpec("a200_030030", "开火与后坐", MechBodyActionLayer.UpperBody, false, 0.03f, 0.1f),
    };

    private static readonly (MechPartSlot Slot, string Token)[] ShowcaseFrameSlots =
    {
        (MechPartSlot.Head, "HD"),
        (MechPartSlot.Core, "BD"),
        (MechPartSlot.Arms, "AM"),
        (MechPartSlot.Legs, "LG"),
    };

    private static readonly string[] UpperBodyBoneNames =
    {
        "Spine1", "Neck", "Offset_ClavicleLink", "Spine1_EX", "Head",
        "L_ClavicleLink", "R_ClavicleLink", "L_ClavicleLink_EX", "Offset_L_Clavicle",
        "Offset_R_Clavicle", "R_ClavicleLink_EX", "L_Clavicle", "R_Clavicle",
        "L_ArmJoint", "R_ArmJoint", "L_ClavicleEX_A", "L_ClavicleEX_B",
        "Root_L_UpperArm", "R_ClavicleEX_A", "R_ClavicleEX_B", "Root_R_UpperArm",
        "L_UpperArm", "R_UpperArm", "L_ForeArm", "L_UpArmEX_A", "L_UpArmEX_B",
        "L_UpArmTwist", "R_ForeArm", "R_UpArmEX_A", "R_UpArmEX_B", "R_UpArmTwist",
        "L_Elbow", "L_ForeArmTwist", "L_HandTwist", "L_Wrist", "R_Elbow",
        "R_ForeArmTwist", "R_HandTwist", "R_Wrist", "Root_L_Hand", "Root_R_Hand",
        "L_Hand", "R_Hand", "L_Finger0", "L_Finger1", "L_Finger2", "L_Finger3",
        "L_Finger4", "L_Weapon_00", "R_Finger0", "R_Finger1", "R_Finger2",
        "R_Finger3", "R_Finger4", "R_Weapon_00", "L_Finger01", "L_Finger11",
        "L_Finger21", "L_Finger31", "L_Finger41", "R_Finger01", "R_Finger11",
        "R_Finger21", "R_Finger31", "R_Finger41", "L_Finger02", "L_Finger12",
        "L_Finger22", "L_Finger32", "L_Finger42", "R_Finger02", "R_Finger12",
        "R_Finger22", "R_Finger32", "R_Finger42"
    };

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
            "Built and opened AC6CombatShowcase.unity. Press Play to run the six-phase AB/QB/fire loop.",
            "OK");
    }

    public static bool Build(out string error)
    {
        error = string.Empty;
        var actionBuildReport = AC6ActionLibraryBuilder.Build();
        var behaviorBuildReport = AC6BehaviorGraphCompiler.Compile();
        var behaviorGraph = AssetDatabase.LoadAssetAtPath<MechBehaviorGraphAsset>(BehaviorGraphPath);
        if (behaviorGraph == null)
        {
            error = $"Behavior graph was not generated. {behaviorBuildReport}";
            return false;
        }

        if (!BuildShowcaseActionRegistry(
                behaviorGraph,
                out var actionRegistry,
                out var showcaseRegistryError))
        {
            error = $"{showcaseRegistryError} {actionBuildReport} {behaviorBuildReport}";
            return false;
        }
        var missingActions = RequiredBodyActions.Concat(RequiredUpperBodyActions)
            .Where(actionId => !actionRegistry.HasAction(actionId))
            .ToArray();
        if (missingActions.Length > 0)
        {
            error = $"Action registry is incomplete: {string.Join(", ", missingActions)}. {actionBuildReport}";
            return false;
        }

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
        MechAssembler assembler;
        if (mech == null)
        {
            mech = new GameObject("Player_Melander_1010");
            assembler = mech.AddComponent<MechAssembler>();
        }
        else
        {
            assembler = mech.GetComponent<MechAssembler>();
            if (assembler == null)
            {
                assembler = mech.AddComponent<MechAssembler>();
            }
        }

        mech.name = "Player_Melander_1010";
        mech.transform.SetParent(root.transform, false);
        mech.transform.localPosition = Vector3.zero;
        mech.transform.localRotation = Quaternion.identity;
        // The validation scene contains a serialized assembly. Always rebuild it from the
        // current loadout so generated player/showcase scenes never retain stale equipment.
        assembler.Loadout = loadout;
        assembler.EditorConfigureSynchronizedSkinnedParts(
            bodyClip,
            shouldAssembleOnAwake: false,
            shouldCreatePlaceholders: false,
            shouldLoop: true,
            playbackSpeed: 1f);
        assembler.Assemble();

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
        controller.ConfigureBodyActions(
            actionRegistry,
            UpperBodyBoneNames,
            "a000_000000",
            "a000_120200",
            "a000_028002",
            "a000_004242",
            "a000_028003",
            "a000_004243",
            "a000_000000",
            "a200_030020",
            "a200_030022",
            "a200_030030");
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
        batchSawRangedWeaponFire = false;
        batchSawMaterialBindings = false;
        batchSawRequiredEquipment = false;
        batchSawExhaustAxisPass = false;
        batchSawExhaustAxisFail = false;
        batchSawBodyActionGraph = false;
        batchSawBehaviorGraph = false;
        batchBehaviorTransitions = 0;
        BatchBodyActions.Clear();
        BatchUpperBodyActions.Clear();
        BatchPoseChecks.Clear();
        batchStartedUtc = DateTime.UtcNow;
        EditorSettings.enterPlayModeOptionsEnabled = true;
        EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
        Application.logMessageReceived += CaptureBatchLog;
        EditorApplication.update += UpdateBatchValidation;
        EditorApplication.isPlaying = true;
        Debug.Log("[AC6CombatShowcaseValidation] Started 24-second play-mode validation.");
    }

    private static void TryBuildOnFirstImport()
    {
        if (SessionState.GetBool(AutoBuildSessionKey, false) ||
            (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) != null &&
             AssetDatabase.LoadAssetAtPath<MechActionRegistry>(ShowcaseActionRegistryPath) != null))
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
        if (condition.Contains("Exhaust axis check: PASS"))
        {
            batchSawExhaustAxisPass = true;
        }
        if (condition.Contains("Exhaust axis check: FAIL"))
        {
            batchSawExhaustAxisFail = true;
        }
        if (condition.Contains("Body action graph: PASS"))
        {
            batchSawBodyActionGraph = true;
        }
        if (condition.Contains("[AC6CombatShowcase] Behavior graph: PASS"))
        {
            batchSawBehaviorGraph = true;
        }
        if (condition.Contains("[AC6BehaviorGraph] State "))
        {
            batchBehaviorTransitions++;
        }
        CaptureId(condition, "Body action PASS: ", BatchBodyActions);
        CaptureId(condition, "Upper-body action PASS: ", BatchUpperBodyActions);
        if (condition.Contains("Body pose check: PASS phase="))
        {
            var marker = "phase=";
            var start = condition.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
            var end = condition.IndexOf(' ', start);
            BatchPoseChecks.Add(end > start ? condition.Substring(start, end - start) : condition.Substring(start));
        }
    }

    private static void UpdateBatchValidation()
    {
        if ((DateTime.UtcNow - batchStartedUtc).TotalSeconds < 24d)
        {
            return;
        }

        EditorApplication.update -= UpdateBatchValidation;
        Application.logMessageReceived -= CaptureBatchLog;
        var success = BatchPhases.Count >= 6 &&
            batchSawRangedWeaponFire &&
            batchSawMaterialBindings &&
            batchSawRequiredEquipment &&
            batchSawExhaustAxisPass &&
            !batchSawExhaustAxisFail &&
            batchSawBodyActionGraph &&
            batchSawBehaviorGraph &&
            batchBehaviorTransitions >= 3 &&
            RequiredBodyActions.All(BatchBodyActions.Contains) &&
            RequiredUpperBodyActions.All(BatchUpperBodyActions.Contains) &&
            RequiredPosePhases.All(BatchPoseChecks.Contains);
        Debug.Log(
            $"[AC6CombatShowcaseValidation] RESULT={(success ? "PASS" : "FAIL")} " +
            $"phases={BatchPhases.Count}/6 ranged={batchSawRangedWeaponFire} " +
            $"materials={batchSawMaterialBindings} equipment={batchSawRequiredEquipment} " +
            $"exhaustAxis={(batchSawExhaustAxisPass && !batchSawExhaustAxisFail)} " +
            $"bodyGraph={batchSawBodyActionGraph} bodyActions={BatchBodyActions.Count}/{RequiredBodyActions.Length} " +
            $"behaviorGraph={batchSawBehaviorGraph} transitions={batchBehaviorTransitions} " +
            $"upperActions={BatchUpperBodyActions.Count}/{RequiredUpperBodyActions.Length} " +
            $"poseChecks={BatchPoseChecks.Count}/{RequiredPosePhases.Length}");
        EditorApplication.isPlaying = false;
        EditorApplication.delayCall += () => EditorApplication.Exit(success ? 0 : 2);
    }

    private static void CaptureId(string condition, string marker, HashSet<string> destination)
    {
        var markerIndex = condition.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return;
        }

        var start = markerIndex + marker.Length;
        var end = condition.IndexOfAny(new[] { ' ', '.', '\r', '\n' }, start);
        destination.Add(end > start ? condition.Substring(start, end - start) : condition.Substring(start));
    }

    private static bool BuildShowcaseActionRegistry(
        MechBehaviorGraphAsset behaviorGraph,
        out MechActionRegistry registry,
        out string error)
    {
        error = string.Empty;
        registry = AssetDatabase.LoadAssetAtPath<MechActionRegistry>(ShowcaseActionRegistryPath);
        if (registry == null)
        {
            EnsureFolder(GeneratedActionsRoot);
            registry = ScriptableObject.CreateInstance<MechActionRegistry>();
            AssetDatabase.CreateAsset(registry, ShowcaseActionRegistryPath);
        }

        registry.EditorActions.Clear();
        var registeredActionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var stateIndex = 0; stateIndex < behaviorGraph.states.Length; stateIndex++)
        {
            ref readonly var state = ref behaviorGraph.states[stateIndex];
            if (!state.animAvailable || string.IsNullOrWhiteSpace(state.actionId) ||
                !registeredActionIds.Add(state.actionId))
            {
                continue;
            }

            var spec = new ShowcaseActionSpec(
                state.actionId,
                state.officialName,
                MechBodyActionLayer.FullBody,
                state.loop,
                state.blendInSeconds,
                0.1f);
            var slotClips = LoadShowcaseClips(spec);
            if (slotClips.Count != ShowcaseFrameSlots.Length)
            {
                error = $"Behavior graph clips are incomplete for {state.actionId}: " +
                        $"{slotClips.Count}/{ShowcaseFrameSlots.Length}.";
                return false;
            }

            registry.EditorActions.Add(new MechBodyActionDefinition
            {
                actionId = state.actionId,
                displayNameZh = state.officialName,
                categoryZh = "AC6 behavior graph",
                reviewStatus = "BehaviorGraph",
                layer = MechBodyActionLayer.FullBody,
                loop = state.loop,
                blendInSeconds = state.blendInSeconds,
                blendOutSeconds = 0.1f,
                slotClips = slotClips,
            });
        }

        for (var actionIndex = 0; actionIndex < ShowcaseActions.Length; actionIndex++)
        {
            var spec = ShowcaseActions[actionIndex];
            if (!registeredActionIds.Add(spec.ActionId))
            {
                continue;
            }

            var slotClips = LoadShowcaseClips(spec);
            if (slotClips.Count != ShowcaseFrameSlots.Length)
            {
                AC6ActionLibraryBuilder.BuildReviewAction(
                    spec.ActionId,
                    spec.Layer.ToString(),
                    spec.Loop);
                slotClips = LoadShowcaseClips(spec);
            }

            if (slotClips.Count != ShowcaseFrameSlots.Length)
            {
                error = $"Showcase clips are incomplete for {spec.ActionId}: " +
                        $"{slotClips.Count}/{ShowcaseFrameSlots.Length}.";
                return false;
            }

            registry.EditorActions.Add(new MechBodyActionDefinition
            {
                actionId = spec.ActionId,
                displayNameZh = spec.DisplayNameZh,
                categoryZh = "固定演示动作",
                reviewStatus = "ShowcaseOnly",
                layer = spec.Layer,
                loop = spec.Loop,
                blendInSeconds = spec.BlendInSeconds,
                blendOutSeconds = spec.BlendOutSeconds,
                slotClips = slotClips,
            });
        }

        EditorUtility.SetDirty(registry);
        AssetDatabase.SaveAssets();
        return true;
    }

    private static List<MechBodyActionDefinition.SlotClip> LoadShowcaseClips(
        ShowcaseActionSpec spec)
    {
        var clips = new List<MechBodyActionDefinition.SlotClip>();
        for (var slotIndex = 0; slotIndex < ShowcaseFrameSlots.Length; slotIndex++)
        {
            var (slot, token) = ShowcaseFrameSlots[slotIndex];
            var clipPath = $"{GeneratedActionsRoot}/{spec.ActionId}/" +
                           $"AC6_{spec.ActionId}_{token}.anim";
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
            if (clip != null)
            {
                clips.Add(new MechBodyActionDefinition.SlotClip { slot = slot, clip = clip });
            }
        }

        return clips;
    }

    private sealed class ShowcaseActionSpec
    {
        public ShowcaseActionSpec(
            string actionId,
            string displayNameZh,
            MechBodyActionLayer layer,
            bool loop,
            float blendInSeconds,
            float blendOutSeconds)
        {
            ActionId = actionId;
            DisplayNameZh = displayNameZh;
            Layer = layer;
            Loop = loop;
            BlendInSeconds = blendInSeconds;
            BlendOutSeconds = blendOutSeconds;
        }

        public string ActionId { get; }
        public string DisplayNameZh { get; }
        public MechBodyActionLayer Layer { get; }
        public bool Loop { get; }
        public float BlendInSeconds { get; }
        public float BlendOutSeconds { get; }
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
