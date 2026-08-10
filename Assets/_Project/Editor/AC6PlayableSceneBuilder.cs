using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ACunicombat.Mechs;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

/// <summary>
/// 可玩场景一键构建（支持批处理）：编译行为图 → 为图内可用动作构建四部件 Clip 并
/// 入注册表 → 复用 showcase 构建器装配机体/灯光/特效 → 切外部控制模式 → 挂玩家
/// 控制 rig 与后置镜头 → 存为 AC6PlayerControl.unity → 无头冒烟验证图轨迹。
/// 批处理入口：-executeMethod AC6PlayableSceneBuilder.BuildAllBatch
/// </summary>
public static class AC6PlayableSceneBuilder
{
    private const string BuildRequestFileName = "RunAC6PlayableSceneBuild.request";
    private const string SkyboxBuildRequestFileName =
        "RunAC6DeepSpaceSkyboxBuild.request";
    private const string GraphAssetPath =
        "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/AC6BehaviorGraph.asset";
    private const string ShowcaseScenePath =
        "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Scenes/AC6CombatShowcase.unity";
    private const string PlayableScenePath =
        "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Scenes/AC6PlayerControl.unity";
    private const string PlayerEnvironmentRootName =
        "AC6PlayerEnvironment";
    private const int PlayerEnvironmentSchemaVersion = 2;
    private static readonly string PlayerEnvironmentVersionObjectName =
        "__AC6PlayerEnvironmentVersion_" + PlayerEnvironmentSchemaVersion;
    private const string ShowcaseRegistryPath =
        "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Actions/AC6CombatShowcaseRegistry.asset";
    private const string GeneratedActionsRoot =
        "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Actions";
    // Bump this whenever source-to-Unity axis conversion changes so stale clips cannot survive.
    private const string ActionClipSchemaPath =
        GeneratedActionsRoot + "/AC6ActionClipSchema.version";
    private const string ActionClipSchemaVersion = "2-fromsoft-axis-normalized";
    private const string GeneratedMaterialsRoot =
        "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Materials";
    private const string DeepSpaceSkyboxMaterialPath =
        GeneratedMaterialsRoot + "/AC6SparseDeepSpaceSkybox.mat";
    private const string DeepSpaceSkyboxShaderName = "AC6/Deep Space Skybox";
    // Both panorama generations are dead: the sky is produced per pixel in the shader now.
    private const string LegacyDeepSpacePanoramaPngPath =
        GeneratedMaterialsRoot + "/AC6SparseDeepSpacePanorama.png";
    private const string LegacyDeepSpacePanoramaExrPath =
        GeneratedMaterialsRoot + "/AC6SparseDeepSpacePanorama.exr";
    private const string RenderingRoot = "Assets/_Project/Rendering";
    private const string CombatVolumeProfilePath =
        RenderingRoot + "/AC6SpaceCombatVolume.asset";
    private const string ThermalVolumeProfilePath =
        RenderingRoot + "/AC6ThermalVolume.asset";
    private const string CombatVolumeObjectName = "AC6 Combat Post";
    // Linear radiance, pushed into the skybox material so C# stays the single source of truth.
    // The disc is deliberately far past anything a display can show: it should read as a hole
    // punched in the image, and it is also what drives the bloom and the lens flare downstream.
    private const float SunDiskRadiance = 400f;
    // Higher than before despite the corona being much tighter - the same energy concentrated
    // into a fifth of the angular width has to be brighter per unit area to still sear.
    private const float SunCoronaRadiance = 14f;
    private const string SunLensFlareDataPath = RenderingRoot + "/AC6SunLensFlare.asset";
    // Shared by the key light's rotation and the skybox's analytic sun direction. These two must
    // not be allowed to drift apart, or the visible disc stops agreeing with the shading.
    private static readonly Vector3 KeyLightEuler = new(31f, -32f, 0f);
    private const string PoseRoot = "Tools/LocalOut/actions";
    private const string LoadoutRoot =
        "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Loadouts";

    private static readonly (MechPartSlot Slot, string Token)[] FrameSlots =
    {
        (MechPartSlot.Head, "HD"),
        (MechPartSlot.Core, "BD"),
        (MechPartSlot.Arms, "AM"),
        (MechPartSlot.Legs, "LG"),
    };

    private static bool playerEnvironmentValidationQueued;

    public static void BuildAllBatch()
    {
        var ok = BuildAll(out var report);
        Debug.Log($"[AC6PlayableScene] {(ok ? "PLAYABLE_READY" : "PLAYABLE_FAILED")}\n{report}");
        if (Application.isBatchMode)
        {
            EditorApplication.Exit(ok ? 0 : 1);
        }
    }

    [MenuItem("Tools/ACunicombat/AC6/Build Playable Player Scene")]
    public static void BuildMenu()
    {
        var previousScenePath = SceneManager.GetActiveScene().path;
        var ok = BuildAll(out var report);
        if (ok)
        {
            EditorSceneManager.OpenScene(PlayableScenePath, OpenSceneMode.Single);
        }
        else if (!string.IsNullOrEmpty(previousScenePath) && File.Exists(previousScenePath))
        {
            EditorSceneManager.OpenScene(previousScenePath, OpenSceneMode.Single);
        }

        EditorUtility.DisplayDialog("AC6 Playable Scene", report, "OK");
    }

    [MenuItem("Tools/ACunicombat/AC6/Rebuild Player Environment")]
    public static void RebuildPlayerEnvironmentMenu()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogWarning(
                "[AC6PlayableScene] Stop Play Mode before rebuilding the player environment.");
            return;
        }

        BuildPlayerEnvironment();
        EnsureSpaceDustFieldForCurrentPlayer();
        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        EditorSceneManager.SaveOpenScenes();
        // The scene save above only flushes the scene file. Everything this rebuild also touches
        // that lives outside the scene - the lens flare data asset, volume profiles, materials -
        // was only EditorUtility.SetDirty'd, which keeps it correct in the current Editor session
        // but writes nothing to disk. Without this, those assets silently revert to their last
        // truly-saved content the next time something forces them to reload from disk.
        AssetDatabase.SaveAssets();
        Selection.activeGameObject = GameObject.Find("AC6PlayerEnvironment");
        Debug.Log("[AC6PlayableScene] Player environment rebuilt with finite structural platform.");
    }

    // Also services one-shot rebuild requests created while the editor is open.
    [InitializeOnLoadMethod]
    private static void RunRequestedBuildAfterReload()
    {
        RegisterPlayerEnvironmentValidation();

        var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return;
        }

        var requestPath = Path.Combine(projectRoot, "Temp", BuildRequestFileName);
        var skyboxRequestPath = Path.Combine(
            projectRoot,
            "Temp",
            SkyboxBuildRequestFileName);
        var buildRequested = File.Exists(requestPath);
        var skyboxBuildRequested = File.Exists(skyboxRequestPath);
        if (!buildRequested && !skyboxBuildRequested)
        {
            return;
        }

        if (buildRequested)
        {
            File.Delete(requestPath);
        }

        if (skyboxBuildRequested)
        {
            File.Delete(skyboxRequestPath);
        }

        EditorApplication.delayCall += () =>
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogError("AC6 playable scene build request was skipped because Unity is in Play Mode.");
                return;
            }

            if (skyboxBuildRequested && !buildRequested)
            {
                ConfigureDeepSpaceEnvironment();
                EditorSceneManager.MarkSceneDirty(
                    SceneManager.GetActiveScene());
                EditorSceneManager.SaveOpenScenes();
                AssetDatabase.SaveAssets();
                Debug.Log(
                    "[AC6PlayableScene] Deep-space skybox rebuilt with distant sun.");
                return;
            }

            var ok = BuildAll(out var report);
            Debug.Log($"[AC6PlayableScene] {(ok ? "PLAYABLE_READY" : "PLAYABLE_FAILED")}\n{report}");
            if (ok)
            {
                EditorSceneManager.OpenScene(PlayableScenePath, OpenSceneMode.Single);
            }
        };
    }

    private static void RegisterPlayerEnvironmentValidation()
    {
        EditorSceneManager.sceneOpened -= OnSceneOpened;
        EditorSceneManager.sceneOpened += OnSceneOpened;
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        QueuePlayerEnvironmentValidation();
    }

    private static void OnSceneOpened(Scene scene, OpenSceneMode mode)
    {
        if (string.Equals(
                scene.path,
                PlayableScenePath,
                StringComparison.OrdinalIgnoreCase))
        {
            QueuePlayerEnvironmentValidation();
        }
    }

    private static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.EnteredEditMode)
        {
            QueuePlayerEnvironmentValidation();
        }
    }

    private static void QueuePlayerEnvironmentValidation()
    {
        if (playerEnvironmentValidationQueued)
        {
            return;
        }

        playerEnvironmentValidationQueued = true;
        EditorApplication.update -= ValidatePlayerEnvironmentWhenEditorIsReady;
        EditorApplication.update += ValidatePlayerEnvironmentWhenEditorIsReady;
    }

    private static void ValidatePlayerEnvironmentWhenEditorIsReady()
    {
        // InitializeOnLoad and sceneOpened can both fire while Unity is still compiling or
        // importing. Keep the request queued until the editor is actually ready instead of
        // silently dropping the only validation attempt for this session.
        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            return;
        }

        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            EditorApplication.update -= ValidatePlayerEnvironmentWhenEditorIsReady;
            playerEnvironmentValidationQueued = false;
            return;
        }

        EditorApplication.update -= ValidatePlayerEnvironmentWhenEditorIsReady;
        playerEnvironmentValidationQueued = false;
        ValidatePlayerEnvironmentVersion();
    }

    private static void ValidatePlayerEnvironmentVersion()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            return;
        }

        var scene = SceneManager.GetActiveScene();
        if (!scene.IsValid() ||
            !scene.isLoaded ||
            !string.Equals(
                scene.path,
                PlayableScenePath,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var environmentRoot = scene.GetRootGameObjects()
            .FirstOrDefault(root =>
                string.Equals(
                    root.name,
                    PlayerEnvironmentRootName,
                    StringComparison.Ordinal));
        if (environmentRoot != null &&
            environmentRoot.transform.Find(
                PlayerEnvironmentVersionObjectName) != null)
        {
            return;
        }

        BuildPlayerEnvironment();
        EnsureSpaceDustFieldForCurrentPlayer();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        Debug.Log(
            "[AC6PlayableScene] Player environment was stale and has been " +
            $"upgraded automatically to schema {PlayerEnvironmentSchemaVersion}.");
    }

    public static bool BuildAll(out string report)
    {
        var log = new List<string>();

        // Equipment GLBs and their generated definitions/loadouts must be rebuilt before
        // the showcase scene is assembled. Otherwise newly imported native equipment can
        // exist in Assets while the playable scene silently keeps stale loadout references.
        if (!AC6SkinnedModularSampleBuilder.BuildSampleAssetsForPipeline(out var modularReport))
        {
            report = $"modular sample build failed: {modularReport}";
            return false;
        }

        log.Add("modular samples: " + modularReport);

        // 1. 编译行为图。
        log.Add(AC6BehaviorGraphCompiler.Compile());
        var graph = AssetDatabase.LoadAssetAtPath<MechBehaviorGraphAsset>(GraphAssetPath);
        if (graph == null)
        {
            report = string.Join("\n", log.Append("graph asset missing after compile"));
            return false;
        }

        // 2. 收集需要的动作并构建 Clip。
        var wanted = new Dictionary<string, (string layer, bool loop, float blend)>(StringComparer.Ordinal);
        foreach (var state in graph.states)
        {
            if (state.animAvailable && !string.IsNullOrEmpty(state.actionId))
            {
                wanted[state.actionId] = ("FullBody", state.loop, state.blendInSeconds);
            }
        }

        foreach (var id in new[]
                 {
                     "a000_028002", "a000_028003", "a000_004240", "a000_004241",
                     "a000_004242", "a000_004243", "a000_122410", "a000_122411",
                     "a000_122412", "a000_122413",
                     "a200_030020", "a200_030022", "a200_030030",
                     "a200_030040", "a200_030042", "a200_030050",
                 })
        {
            var upper = id.StartsWith("a2", StringComparison.Ordinal);
            wanted.TryAdd(id, (upper ? "UpperBody" : "FullBody", id.EndsWith("22") || id.EndsWith("42"), 0.1f));
        }

        var clipsBuilt = 0;
        var noPose = 0;
        var clipBuildFailed = false;
        var forceClipRebuild = !File.Exists(ActionClipSchemaPath) ||
            !string.Equals(
                File.ReadAllText(ActionClipSchemaPath).Trim(),
                ActionClipSchemaVersion,
                StringComparison.Ordinal);
        foreach (var (actionId, meta) in wanted)
        {
            if (!File.Exists(Path.Combine(PoseRoot, actionId + "_pose.json")))
            {
                noPose++;
                continue;
            }

            if (!forceClipRebuild && FrameSlots.All(s =>
                    File.Exists($"{GeneratedActionsRoot}/{actionId}/AC6_{actionId}_{s.Token}.anim")))
            {
                continue;
            }

            var result = AC6ActionLibraryBuilder.BuildReviewAction(actionId, meta.layer, meta.loop);
            if (result.Contains("failed") || result.Contains("not found"))
            {
                clipBuildFailed = true;
                log.Add($"clip build: {result}");
            }
            else
            {
                clipsBuilt++;
            }
        }

        if (forceClipRebuild && !clipBuildFailed)
        {
            File.WriteAllText(ActionClipSchemaPath, ActionClipSchemaVersion + Environment.NewLine);
            AssetDatabase.ImportAsset(ActionClipSchemaPath, ImportAssetOptions.ForceUpdate);
        }

        log.Add(
            $"clips: built={clipsBuilt}, no-pose={noPose}, wanted={wanted.Count}, " +
            $"schema-rebuild={forceClipRebuild}");

        // The first compile happens before missing clips are built. Compile again so
        // newly decoded actions become available to the runtime graph in this build.
        if (clipsBuilt > 0)
        {
            log.Add(AC6BehaviorGraphCompiler.Compile());
            graph = AssetDatabase.LoadAssetAtPath<MechBehaviorGraphAsset>(GraphAssetPath);
            if (graph == null)
            {
                report = string.Join("\n", log.Append("graph asset missing after clip rebuild"));
                return false;
            }
        }

        // 3. showcase 装配（机体/灯光/特效/它自己的注册表）。
        if (!AC6CombatShowcaseBuilder.Build(out var showcaseError))
        {
            report = string.Join("\n", log.Append($"showcase build failed: {showcaseError}"));
            return false;
        }

        // 4. 把图内其余动作补进 showcase 注册表（播放器运行时用它）。
        var registry = AssetDatabase.LoadAssetAtPath<MechActionRegistry>(ShowcaseRegistryPath);
        if (registry == null)
        {
            report = string.Join("\n", log.Append($"showcase registry missing: {ShowcaseRegistryPath}"));
            return false;
        }

        var registered = 0;
        foreach (var (actionId, meta) in wanted)
        {
            if (registry.HasAction(actionId))
            {
                continue;
            }

            var slotClips = new List<MechBodyActionDefinition.SlotClip>();
            foreach (var (slot, token) in FrameSlots)
            {
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(
                    $"{GeneratedActionsRoot}/{actionId}/AC6_{actionId}_{token}.anim");
                if (clip != null)
                {
                    slotClips.Add(new MechBodyActionDefinition.SlotClip { slot = slot, clip = clip });
                }
            }

            if (slotClips.Count == 0)
            {
                continue;
            }

            registry.EditorActions.Add(new MechBodyActionDefinition
            {
                actionId = actionId,
                displayNameZh = $"行为图动作 {actionId}",
                categoryZh = "行为图",
                reviewStatus = "OfficialMapped",
                layer = meta.layer == "UpperBody" ? MechBodyActionLayer.UpperBody : MechBodyActionLayer.FullBody,
                loop = meta.loop,
                blendInSeconds = Mathf.Max(meta.blend, 0.02f),
                blendOutSeconds = 0f,
                slotClips = slotClips,
            });
            registered++;
        }

        EditorUtility.SetDirty(registry);
        AssetDatabase.SaveAssets();
        log.Add($"registry: +{registered} entries (total {registry.Actions.Count})");

        var requiredTransientActions = new[]
        {
            "a000_004240", // forward QB
            "a000_004241", // backward QB
            "a000_004242", // left QB
            "a000_004243", // right QB
            "a000_122410", // forward air QB
            "a000_122411", // backward air QB
            "a000_122412", // left air QB
            "a000_122413", // right air QB
            "a000_020210", // assault boost entry
            "a000_120200", // boost flight loop
        };
        var missingTransientActions = requiredTransientActions
            .Where(actionId => !registry.HasAction(actionId))
            .ToArray();
        if (missingTransientActions.Length > 0)
        {
            report = string.Join("\n", log.Append(
                $"transient action coverage failed: {string.Join(", ", missingTransientActions)}"));
            return false;
        }

        log.Add("transient actions: PASS (4-way ground QB, 4-way air QB, AB entry, boost flight)");

        // 5. 场景改装：外部控制模式 + 玩家 rig + 后置镜头。
        var scene = EditorSceneManager.OpenScene(ShowcaseScenePath, OpenSceneMode.Single);
        var controller = UnityEngine.Object.FindAnyObjectByType<AC6CombatShowcaseController>();
        if (controller == null)
        {
            report = string.Join("\n", log.Append("showcase controller not found in scene"));
            return false;
        }

        var so = new SerializedObject(controller);
        so.FindProperty("externalControlMode").boolValue = true;
        so.ApplyModifiedPropertiesWithoutUndo();

        var mech = GameObject.Find("Player_Melander_1010");
        if (mech == null)
        {
            report = string.Join("\n", log.Append("mech root not found"));
            return false;
        }

        var input = Ensure<MechPlayerInputAdapter>(mech);
        var motion = Ensure<MechFlightMotionController>(mech);
        var thrustVisualizer = Ensure<MechThrustVisualizer>(mech);
        var trajectoryPredictor = Ensure<MechTrajectoryPredictor>(mech);
        var runner = Ensure<MechBehaviorGraphRunner>(mech);
        var weapon = Ensure<MechWeaponArmLayer>(mech);
        var rig = Ensure<MechPlayerRig>(mech);

        rig.ConfigureActionPlayback(
            controller.ActionRegistry,
            controller.UpperBodyBoneNames);

        weapon.ApplyVerifiedSideMapping();
        if (!weapon.HasVerifiedSideMapping)
        {
            report = string.Join("\n", log.Append(
                $"weapon side mapping failed: left={weapon.LeftFireActionId}, right={weapon.RightFireActionId}"));
            return false;
        }

        log.Add("weapon side mapping: PASS (LMB -> anatomical left / 03004x-03005x; RMB -> anatomical right / 03002x-03003x)");
        EditorUtility.SetDirty(weapon);

        // Persist these references through the runtime API. The previous SerializedObject-only
        // assignment could be lost when the generated scene was saved, leaving graph == null.
        runner.Configure(graph, null);
        // Native AC6 GLBs use -Z as their visible forward after import. Rotate only
        // the rendered mech by 180 degrees; logical yaw remains camera/movement yaw.
        motion.ConfigureModelForwardYawOffset(180f);
        motion.ConfigurePropulsionForceModel(true, true, 35f);
        motion.ConfigureTranslationThrusterAssist(0.35f, 48f, 36f, 52f, 48f);
        motion.ConfigureTechnicalSafetySpeed(2000f);
        motion.ConfigureQuickBoost(90f, 0.28f);
        motion.ConfigureManeuverEnergy(100f, 4f, 15f, 18f, 24f, 0.75f, 0.2f);
        motion.ConfigureGravity(0.2f);
        thrustVisualizer.Configure(motion);
        trajectoryPredictor.Configure(motion);
        EditorUtility.SetDirty(runner);
        EditorUtility.SetDirty(motion);
        EditorUtility.SetDirty(thrustVisualizer);
        EditorUtility.SetDirty(trajectoryPredictor);
        EditorUtility.SetDirty(mech.transform);

        var showcaseCam = GameObject.Find("Combat Showcase Camera");
        if (showcaseCam != null)
        {
            showcaseCam.SetActive(false);
        }

        var camGo = GameObject.Find("AC6PlayerCamera");
        if (camGo == null)
        {
            camGo = new GameObject("AC6PlayerCamera", typeof(Camera), typeof(AudioListener),
                typeof(MechThirdPersonCamera));
        }

        var cam = camGo.GetComponent<MechThirdPersonCamera>();
        camGo.tag = "MainCamera";
        cam.Configure(mech.transform, motion);
        cam.ConfigureViewDistance(0.5f, 100000f);
        cam.ConfigureFraming(20f, 7.5f, 8f);
        cam.ConfigureHighSpeedFraming(8f, 0.26f, 0.18f, 0.45f);
        EditorUtility.SetDirty(cam);

        var dustField = GetOrCreateSpaceDustField(cam);

        DisableShowcaseLabels(mech);
        BuildPlayerEnvironment();
        var combatTarget = BuildCombatTestTarget(mech);
        BuildNavigationReference();

        var testWeaponSystem = Ensure<MechTestWeaponSystem>(mech);
        testWeaponSystem.Configure(
            input,
            mech.GetComponent<MechRuntimeState>(),
            camGo.GetComponent<Camera>(),
            combatTarget,
            AC6FxrEffectFactory.BuildLibrary());
        testWeaponSystem.ConfigureAimViewport(new Vector2(0.5f, 0.56f));
        EditorUtility.SetDirty(testWeaponSystem);
        var radarHud = Ensure<MechRadarHudPresenter>(mech);
        radarHud.Configure(
            camGo.GetComponent<Camera>(),
            testWeaponSystem.RadarController,
            new Vector2(0.5f, 0.56f));
        EditorUtility.SetDirty(radarHud);
        log.Add("combat target: PASS (rifle, cannon, left/right guided missile test path)");

        var rigSo = new SerializedObject(rig);
        rigSo.FindProperty("input").objectReferenceValue = input;
        rigSo.FindProperty("motion").objectReferenceValue = motion;
        rigSo.FindProperty("thrustVisualizer").objectReferenceValue = thrustVisualizer;
        rigSo.FindProperty("cam").objectReferenceValue = cam;
        rigSo.FindProperty("dustField").objectReferenceValue = dustField;
        rigSo.FindProperty("graphRunner").objectReferenceValue = runner;
        rigSo.FindProperty("weaponLayer").objectReferenceValue = weapon;
        rigSo.FindProperty("showFlightInstruments").boolValue = true;
        rigSo.ApplyModifiedPropertiesWithoutUndo();

        EditorSceneManager.SaveScene(scene, PlayableScenePath);
        // Same reasoning as the menu-driven rebuild: the scene save above does not flush the
        // loose ScriptableObject assets (lens flare data, volume profiles, materials) that this
        // pipeline also SetDirty's - those need their own explicit save or they revert on the
        // next forced reload from disk.
        AssetDatabase.SaveAssets();
        log.Add($"scene: {PlayableScenePath}");

        // 6. 无头冒烟：脚本化输入驱动图，断言轨迹离开待机并到达飞行系状态。
        // 场景切换会触发未引用资产卸载，先前的实例可能已是"假 null"——重新加载。
        graph = AssetDatabase.LoadAssetAtPath<MechBehaviorGraphAsset>(GraphAssetPath);
        if (graph == null)
        {
            report = string.Join("\n", log.Append("graph asset unloaded and not reloadable"));
            return false;
        }

        var graphSmoke = RunGraphSmokeTest(graph, out var trajectory);
        log.Add($"graph smoke: {(graphSmoke ? "PASS" : "FAIL")}  trajectory: {trajectory}");

        var motionSmoke = RunMotionSmokeTest(out var motionReport);
        log.Add($"motion smoke: {(motionSmoke ? "PASS" : "FAIL")}  {motionReport}");

        report = string.Join("\n", log);
        return graphSmoke && motionSmoke;
    }

    private static void EnsureSpaceDustFieldForCurrentPlayer()
    {
        var cameraObject = GameObject.Find("AC6PlayerCamera");
        var playerObject = GameObject.Find("Player_Melander_1010");
        var camera = cameraObject != null
            ? cameraObject.GetComponent<MechThirdPersonCamera>()
            : null;
        var rig = playerObject != null
            ? playerObject.GetComponent<MechPlayerRig>()
            : null;

        if (camera == null || rig == null)
        {
            Debug.LogWarning(
                "[AC6PlayableScene] Space dust was not rebuilt because the player camera or rig is missing.");
            return;
        }

        var dustField = GetOrCreateSpaceDustField(camera);
        var rigSo = new SerializedObject(rig);
        rigSo.FindProperty("dustField").objectReferenceValue = dustField;
        rigSo.ApplyModifiedPropertiesWithoutUndo();
    }

    private static MechSpaceDustField GetOrCreateSpaceDustField(MechThirdPersonCamera camera)
    {
        const string objectName = "AC6SpaceDustField";
        var dustFieldObject = GameObject.Find(objectName);
        if (dustFieldObject == null)
        {
            dustFieldObject = new GameObject(objectName);
        }

        var dustField = dustFieldObject.GetComponent<MechSpaceDustField>();
        if (dustField == null)
        {
            dustField = dustFieldObject.AddComponent<MechSpaceDustField>();
        }

        dustField.Configure(camera.transform);
        EditorUtility.SetDirty(dustField);
        return dustField;
    }

    private static bool RunMotionSmokeTest(out string report)
    {
        const float dt = 1f / 60f;
        var climbGo = new GameObject("MotionSmoke_Climb");
        var abGo = new GameObject("MotionSmoke_AB");
        var qbGo = new GameObject("MotionSmoke_QB");
        var sustainedThrustGo = new GameObject("MotionSmoke_SustainedThrust");
        var brakeGo = new GameObject("MotionSmoke_Brake");
        var airBrakeGo = new GameObject("MotionSmoke_AirBrake");
        var energyGo = new GameObject("MotionSmoke_Energy");
        var quickBoostEnergyGo = new GameObject("MotionSmoke_QBEnergy");
        try
        {
            var climb = climbGo.AddComponent<MechFlightMotionController>();
            climb.ConfigureModelForwardYawOffset(0f);
            climbGo.transform.position = new Vector3(0f, 10f, 0f);
            climb.SetAimPitch(-30f);
            var forward = new MechPlayerInputFrame { MoveLocal = Vector2.up };
            climb.Tick(in forward, dt);
            var pitchedForwardPass = climb.Velocity.y > 0.01f;

            var assaultBoost = abGo.AddComponent<MechFlightMotionController>();
            assaultBoost.ConfigureModelForwardYawOffset(0f);
            abGo.transform.position = new Vector3(0f, 10f, 0f);
            assaultBoost.SetAimPitch(-30f);
            assaultBoost.TriggerAssaultBoost();
            var noInput = default(MechPlayerInputFrame);
            assaultBoost.Tick(in noInput, dt);
            var assaultBoostPass = assaultBoost.AssaultBoostActive
                                   && assaultBoost.Velocity.magnitude > 0.01f
                                   && assaultBoost.Velocity.y > 0.01f;

            var quickBoost = qbGo.AddComponent<MechFlightMotionController>();
            quickBoost.ConfigureModelForwardYawOffset(0f);
            quickBoost.ConfigureQuickBoost(90f, 0.28f);
            quickBoost.BeginQuickBoost(Vector2.up);
            quickBoost.Tick(in noInput, dt);
            var quickBoostFirstStepSpeed = quickBoost.HorizontalSpeed;
            for (var i = 1; i < 16; i++)
            {
                quickBoost.Tick(in noInput, dt);
            }
            var quickBoostSpeed = quickBoost.HorizontalSpeed;
            var quickBoostPass = quickBoost.QuickBoostActive
                                 && quickBoostFirstStepSpeed > 0f
                                 && quickBoostSpeed > quickBoostFirstStepSpeed
                                 && quickBoostSpeed > 80f;

            var sustainedThrust =
                sustainedThrustGo.AddComponent<MechFlightMotionController>();
            sustainedThrust.ConfigureModelForwardYawOffset(0f);
            sustainedThrust.ConfigureTechnicalSafetySpeed(200f);
            sustainedThrustGo.transform.position = Vector3.zero;
            var fullThrust = new MechPlayerInputFrame { MoveLocal = Vector2.up };
            for (var i = 0; i < 600; i++)
            {
                sustainedThrust.Tick(in fullThrust, dt);
            }

            var sustainedThrustSpeed = sustainedThrust.HorizontalSpeed;
            var fullMainOutput = sustainedThrust.MainEngineOutput;
            var sustainedThrustPass =
                sustainedThrustSpeed > 100f &&
                sustainedThrustSpeed <= 200.01f &&
                fullMainOutput >= 0.99f;
            sustainedThrust.Tick(in noInput, dt);
            var releaseStopsMainEngine =
                sustainedThrust.MainEngineOutput <= 0.001f;

            var brake = brakeGo.AddComponent<MechFlightMotionController>();
            brake.ConfigureModelForwardYawOffset(0f);
            brakeGo.transform.position = Vector3.zero;
            brake.BeginQuickBoost(Vector2.up);
            for (var i = 0; i < 16; i++)
            {
                brake.Tick(in noInput, dt);
            }
            var impulseSpeed = new Vector2(brake.Velocity.x, brake.Velocity.z).magnitude;
            for (var i = 0; i < 180; i++)
            {
                brake.Tick(in noInput, dt);
            }

            var stoppedSpeed = new Vector2(brake.Velocity.x, brake.Velocity.z).magnitude;
            var groundBrakePass = impulseSpeed > 1f && stoppedSpeed <= 0.01f;

            var airBrake = airBrakeGo.AddComponent<MechFlightMotionController>();
            airBrake.ConfigureModelForwardYawOffset(0f);
            airBrakeGo.transform.position = new Vector3(0f, 25f, 0f);
            airBrake.BeginQuickBoost(Vector2.up);
            for (var i = 0; i < 10; i++)
            {
                airBrake.Tick(in noInput, dt);
            }
            var airBrakeStartY = airBrakeGo.transform.position.y;
            var activeBrake = new MechPlayerInputFrame { BrakeHeld = true };
            for (var i = 0; i < 180; i++)
            {
                airBrake.Tick(in activeBrake, dt);
            }

            var airBrakeSpeed = airBrake.HorizontalSpeed;
            var airBrakeAltitudeError = Mathf.Abs(airBrakeGo.transform.position.y - airBrakeStartY);
            var airBrakePass = airBrakeSpeed <= 0.01f && airBrakeAltitudeError <= 0.001f;

            var energy = energyGo.AddComponent<MechFlightMotionController>();
            energy.ConfigureModelForwardYawOffset(0f);
            energy.ConfigureManeuverEnergy(
                100f,
                100f,
                150f,
                30f,
                50f,
                0.25f,
                0.2f);
            for (var i = 0; i < 61; i++)
            {
                energy.Tick(in forward, dt);
            }
            var movementEnergyDepleted = energy.ManeuverEnergyDepleted &&
                                         energy.CurrentManeuverEnergy <= 0.001f;
            for (var i = 0; i < 60; i++)
            {
                energy.Tick(in noInput, dt);
            }
            var movementEnergyRecovered = !energy.ManeuverEnergyDepleted &&
                                          energy.CurrentManeuverEnergy >= 20f;

            var quickBoostEnergy =
                quickBoostEnergyGo.AddComponent<MechFlightMotionController>();
            quickBoostEnergy.ConfigureModelForwardYawOffset(0f);
            quickBoostEnergy.ConfigureManeuverEnergy(
                100f,
                0f,
                0f,
                30f,
                0f,
                0.25f,
                0.2f);
            var firstQuickBoost = quickBoostEnergy.BeginQuickBoost(Vector2.up);
            var secondQuickBoost = quickBoostEnergy.BeginQuickBoost(Vector2.up);
            var thirdQuickBoost = quickBoostEnergy.BeginQuickBoost(Vector2.up);
            var rejectedQuickBoost = !quickBoostEnergy.BeginQuickBoost(Vector2.up);
            var quickBoostEnergyPass =
                firstQuickBoost &&
                secondQuickBoost &&
                thirdQuickBoost &&
                rejectedQuickBoost &&
                Mathf.Abs(quickBoostEnergy.CurrentManeuverEnergy - 10f) <= 0.001f;
            var energyPass = movementEnergyDepleted &&
                             movementEnergyRecovered &&
                             quickBoostEnergyPass;

            report = $"pitched-forward={(pitchedForwardPass ? "PASS" : "FAIL")} "
                     + $"(vy={climb.Velocity.y:F2}), "
                     + $"AB={(assaultBoostPass ? "PASS" : "FAIL")} "
                     + $"(speed={assaultBoost.Velocity.magnitude:F2}, vy={assaultBoost.Velocity.y:F2}), "
                     + $"QB-overspeed={(quickBoostPass ? "PASS" : "FAIL")} "
                     + $"({quickBoostFirstStepSpeed:F2}->{quickBoostSpeed:F2} m/s), "
                      + $"sustained-thrust={(sustainedThrustPass ? "PASS" : "FAIL")} "
                      + $"({sustainedThrustSpeed:F2} m/s, output={fullMainOutput:P0}), "
                      + $"release-main={(releaseStopsMainEngine ? "PASS" : "FAIL")}, "
                     + $"ground-brake={(groundBrakePass ? "PASS" : "FAIL")} "
                     + $"({impulseSpeed:F2}->{stoppedSpeed:F2} m/s), "
                     + $"air-brake={(airBrakePass ? "PASS" : "FAIL")} "
                     + $"(horizontal={airBrakeSpeed:F3} m/s, altitude-error={airBrakeAltitudeError:F4} m), "
                     + $"energy={(energyPass ? "PASS" : "FAIL")} "
                     + $"(recovered={energy.CurrentManeuverEnergy:F1}, "
                     + $"qb-left={quickBoostEnergy.CurrentManeuverEnergy:F1})";
            return pitchedForwardPass && assaultBoostPass && quickBoostPass && sustainedThrustPass
                   && releaseStopsMainEngine
                   && groundBrakePass && airBrakePass && energyPass;
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(climbGo);
            UnityEngine.Object.DestroyImmediate(abGo);
            UnityEngine.Object.DestroyImmediate(qbGo);
            UnityEngine.Object.DestroyImmediate(sustainedThrustGo);
            UnityEngine.Object.DestroyImmediate(brakeGo);
            UnityEngine.Object.DestroyImmediate(airBrakeGo);
            UnityEngine.Object.DestroyImmediate(energyGo);
            UnityEngine.Object.DestroyImmediate(quickBoostEnergyGo);
        }
    }

    private static bool RunGraphSmokeTest(MechBehaviorGraphAsset graph, out string trajectory)
    {
        var go = new GameObject("GraphSmokeTest");
        try
        {
            var runner = go.AddComponent<MechBehaviorGraphRunner>();
            runner.Configure(graph, null);
            var visited = new List<int> { runner.CurrentStateIndex };

            void Drive(uint padBits, bool grounded, float seconds)
            {
                var ctx = new MechGraphContext
                {
                    PadBits = padBits,
                    CharControlFlags = 0x1F,
                    Grounded = grounded,
                    Airborne = !grounded,
                };
                for (var t = 0f; t < seconds; t += 1f / 60f)
                {
                    runner.Tick(in ctx, 1f / 60f);
                    if (visited[visited.Count - 1] != runner.CurrentStateIndex)
                    {
                        visited.Add(runner.CurrentStateIndex);
                    }
                }
            }

            uint Bit(int b) => 1u << b;
            Drive(Bit(MechPadBits.NoInput), true, 0.5f);                             // 待机
            Drive(Bit(MechPadBits.Move) | Bit(MechPadBits.Run) | Bit(MechPadBits.GroundBoost), true, 1.5f);
            Drive(Bit(MechPadBits.Jump) | Bit(MechPadBits.JumpHeld) | Bit(MechPadBits.MoveUp), true, 0.2f);
            Drive(Bit(MechPadBits.JumpHeld) | Bit(MechPadBits.MoveUp) | Bit(MechPadBits.RiseStartBoost), false, 1.5f);
            Drive(Bit(MechPadBits.Move) | Bit(MechPadBits.FlightBoost), false, 1.5f); // 飞行
            Drive(Bit(MechPadBits.Move) | Bit(MechPadBits.FlightBoost)
                  | Bit(MechPadBits.QuickBoost) | Bit(MechPadBits.DashSidestep), false, 0.3f);
            Drive(Bit(MechPadBits.Move) | Bit(MechPadBits.FlightBoost), false, 1.5f);

            trajectory = string.Join(" → ", visited.Select(i =>
                i >= 0 && i < graph.states.Length
                    ? $"[{i}]{graph.states[i].officialName}"
                    : $"[!{i}]"));
            return visited.Count >= 3 && visited.All(i => i >= 0 && i < graph.states.Length);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(go);
        }
    }

    private static T Ensure<T>(GameObject go) where T : Component
    {
        var existing = go.GetComponent<T>();
        return existing != null ? existing : go.AddComponent<T>();
    }

    private static void DisableShowcaseLabels(GameObject mech)
    {
        foreach (var child in mech.GetComponentsInChildren<Transform>(true))
        {
            if (child.name == "Label")
            {
                child.gameObject.SetActive(false);
            }
        }
    }

    private static void BuildNavigationReference()
    {
        const string rootName = "AC6PlayerNavigationReference";
        var existing = GameObject.Find(rootName);
        if (existing != null)
        {
            UnityEngine.Object.DestroyImmediate(existing);
        }

        var root = new GameObject(rootName);
        for (var z = -144; z <= 480; z += 24)
        {
            CreateReferenceMarker(root.transform, new Vector3(-36f, 0f, z), new Vector3(0.8f, 6f, 0.8f));
            CreateReferenceMarker(root.transform, new Vector3(36f, 0f, z), new Vector3(0.8f, 6f, 0.8f));
        }

        for (var x = -144; x <= 144; x += 24)
        {
            CreateReferenceMarker(root.transform, new Vector3(x, 10f, 180f), Vector3.one * 0.6f);
        }
    }

    private static void BuildPlayerEnvironment()
    {
        var existing = GameObject.Find(PlayerEnvironmentRootName);
        if (existing != null)
        {
            UnityEngine.Object.DestroyImmediate(existing);
        }

        Directory.CreateDirectory(GeneratedMaterialsRoot);
        var floorMaterial = GetOrCreateEnvironmentMaterial(
            $"{GeneratedMaterialsRoot}/AC6PlayerFloor.mat",
            new Color(0.12f, 0.14f, 0.16f, 1f), 0.72f, 0.28f);
        var padMaterial = GetOrCreateEnvironmentMaterial(
            $"{GeneratedMaterialsRoot}/AC6PlayerLandingPad.mat",
            new Color(0.075f, 0.085f, 0.095f, 1f), 0.82f, 0.22f);
        var seamMaterial = GetOrCreateEnvironmentMaterial(
            $"{GeneratedMaterialsRoot}/AC6PlayerFloorSeams.mat",
            new Color(0.025f, 0.03f, 0.035f, 1f), 0.45f, 0.12f);
        var markingMaterial = GetOrCreateEnvironmentMaterial(
            $"{GeneratedMaterialsRoot}/AC6PlayerFloorMarkings.mat",
            new Color(0.72f, 0.48f, 0.075f, 1f), 0.35f, 0.25f);

        var root = new GameObject(PlayerEnvironmentRootName);
        CreateEnvironmentBlock(
            root.transform, "Structural Floor", new Vector3(0f, -0.4f, 180f),
            new Vector3(640f, 0.8f, 720f), floorMaterial, true);
        CreateEnvironmentBlock(
            root.transform, "Landing Pad", new Vector3(0f, 0.0125f, 0f),
            new Vector3(34f, 0.025f, 34f), padMaterial, false);

        // Thin recessed-looking strips establish panel scale without introducing collider steps.
        for (var x = -320; x <= 320; x += 20)
        {
            CreateEnvironmentBlock(
                root.transform, "Panel Seam X", new Vector3(x, 0.031f, 180f),
                new Vector3(0.045f, 0.012f, 720f), seamMaterial, false);
        }

        for (var z = -180; z <= 540; z += 20)
        {
            CreateEnvironmentBlock(
                root.transform, "Panel Seam Z", new Vector3(0f, 0.031f, z),
                new Vector3(640f, 0.012f, 0.045f), seamMaterial, false);
        }

        // Two restrained service-lane lines make heading and translation readable in play mode.
        foreach (var x in new[] { -18f, 18f })
        {
            CreateEnvironmentBlock(
                root.transform, "Service Lane Marking", new Vector3(x, 0.043f, 180f),
                new Vector3(0.22f, 0.014f, 700f), markingMaterial, false);
        }

        CreateEnvironmentBlock(
            root.transform, "Landing Pad Centerline", new Vector3(0f, 0.05f, 0f),
            new Vector3(0.18f, 0.014f, 30f), markingMaterial, false);

        // Two complete up/plateau/down courses give the transform-driven controller real
        // traversable gradients. Their colliders are sampled by MechFlightMotionController.
        CreateRampCourse(root.transform, "Gentle", -105f, 95f, 100f, 8f,
            60f, 52f, floorMaterial, markingMaterial);
        CreateRampCourse(root.transform, "Medium", 105f, 115f, 82f, 12f,
            60f, 44f, floorMaterial, markingMaterial);

        // A broad cross-slope is useful for testing lateral translation without adding art.
        CreateEnvironmentRamp(
            root.transform, "Cross Slope", new Vector3(0f, 5.45f, 390f),
            new Vector3(120f, 1f, 70f), new Vector3(-9f, 0f, 0f), floorMaterial);

        ConfigureDeepSpaceEnvironment();

        // The playable scene is generated and intentionally ignored by Git. Keep a serialized
        // schema marker in it so editor startup can distinguish a current scene from one produced
        // by an older environment builder without destructively rebuilding on every launch.
        var versionObject = new GameObject(
            PlayerEnvironmentVersionObjectName)
        {
            hideFlags = HideFlags.HideInHierarchy,
        };
        versionObject.transform.SetParent(root.transform, false);
    }

    private static void ConfigureDeepSpaceEnvironment()
    {
        var skyboxShader = Shader.Find(DeepSpaceSkyboxShaderName);
        if (skyboxShader == null)
        {
            Debug.LogWarning(
                $"[AC6PlayableScene] Skybox shader '{DeepSpaceSkyboxShaderName}' is unavailable.");
            return;
        }

        var skybox = AssetDatabase.LoadAssetAtPath<Material>(DeepSpaceSkyboxMaterialPath);
        if (skybox == null)
        {
            skybox = new Material(skyboxShader);
            AssetDatabase.CreateAsset(skybox, DeepSpaceSkyboxMaterialPath);
        }

        skybox.shader = skyboxShader;

        // Every property is written explicitly, not just the few that differ from the shader's
        // defaults. Assigning a new shader to an existing material does not reliably seed the
        // incoming properties with that shader's defaults - this material was authored against
        // Skybox/Panoramic, so the entire procedural parameter set arrives unset. A zero
        // _SunCoronaFalloff is the worst of those: pow(x, 0) is 1 even at x = 0, which turns the
        // corona into a sky-wide constant and renders the whole background solid white.
        skybox.SetColor("_BackgroundRadiance", new Color(0.0008f, 0.0008f, 0.0011f, 1f));

        skybox.SetFloat("_StarDensity", 0.35f);
        skybox.SetFloat("_StarBrightness", 1f);
        skybox.SetFloat("_StarMagnitudeBias", 3.6f);
        skybox.SetFloat("_StarColorSpread", 0.55f);
        skybox.SetFloat("_StarCoarseCells", 22f);
        skybox.SetFloat("_StarFineCells", 92f);
        skybox.SetFloat("_StarRadius", 0.035f);

        // The sun is drawn analytically from this direction, so it has to be the exact inverse of
        // the key light's forward or the visible disc and the shading disagree. Both read
        // KeyLightEuler rather than repeating the numbers.
        skybox.SetVector("_SunDirection", -(Quaternion.Euler(KeyLightEuler) * Vector3.forward));
        skybox.SetColor("_SunColor", new Color(1f, 0.972f, 0.863f, 1f));
        skybox.SetFloat("_SunDiskRadiance", SunDiskRadiance);
        skybox.SetFloat("_SunAngularRadius", 0.72f);
        // The corona was 7 degrees wide with a gentle 2.6 exponent, which is why the sun read as
        // one big soft ball: at a 60 degree FOV a 7 degree corona is a tenth of the screen, so the
        // 0.72 degree disc was buried inside its own glow and never registered as a core at all.
        // Pulled in to roughly three disc radii and steepened, so it hugs the disc as glare rather
        // than replacing it. The wide, structured part of the effect is the lens flare's job, not
        // the corona's - a real corona is not what makes a photographed sun look photographed.
        skybox.SetFloat("_SunCoronaRadiance", SunCoronaRadiance);
        skybox.SetFloat("_SunCoronaRadius", 2.2f);
        skybox.SetFloat("_SunCoronaFalloff", 5.5f);

        skybox.SetFloat("_NebulaStrength", 0.4f);
        skybox.SetFloat("_NebulaScale", 1.6f);
        skybox.SetColor("_NebulaColorA", new Color(0.055f, 0.075f, 0.135f, 1f));
        skybox.SetColor("_NebulaColorB", new Color(0.115f, 0.050f, 0.070f, 1f));

        EditorUtility.SetDirty(skybox);

        // The old panorama asset is no longer referenced by anything. Left on disk it would keep
        // showing up in searches as though it were still the sky.
        foreach (var stalePath in new[]
                 {
                     LegacyDeepSpacePanoramaPngPath,
                     LegacyDeepSpacePanoramaExrPath,
                 })
        {
            if (AssetDatabase.LoadAssetAtPath<Texture2D>(stalePath) != null)
            {
                AssetDatabase.DeleteAsset(stalePath);
            }
        }

        RenderSettings.skybox = skybox;
        RenderSettings.fog = false;
        RenderSettings.ambientMode = AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.16f, 0.19f, 0.24f, 1f);
        RenderSettings.ambientEquatorColor = new Color(0.085f, 0.10f, 0.13f, 1f);
        RenderSettings.ambientGroundColor = new Color(0.035f, 0.04f, 0.055f, 1f);
        // 0.85 was propping the dark side up while the all-black-mech bug was being chased. In
        // vacuum there is no atmospheric scattering, so anything not facing the sun or the planet
        // should genuinely fall away. This is the main knob for "deep space" - push it lower for
        // more depth, at the cost of readability on the unlit side.
        RenderSettings.ambientIntensity = 0.42f;
        // Only meaningful now that the panorama is HDR. Against the old LDR sky the probe had no
        // value brighter than white, so raising this just lifted a flat grey.
        RenderSettings.reflectionIntensity = 0.7f;
        RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
        // Smoothness selects the probe mip via PerceptualRoughnessToMipmapLevel, so probe
        // resolution is what decides whether a high-smoothness zone gets a crisp sun glint or a
        // smear. 128 (the default) is not enough for the per-zone metallic pass to read.
        RenderSettings.defaultReflectionResolution = 256;

        // 上面刚改写了天空盒材质的全部程序化参数，这里显式重算一次环境反射，让 Scene 视图
        // 当场用上新值而不是等编辑器自己察觉。场景没有烘焙的环境光照数据
        // （m_LightingDataAsset 为空引用），默认反射走 Skybox 模式由 Unity 自动生成，
        // 因此这只是把时序钉死，不是必需项——真正导致"必须手动重建才有正常光影"的是
        // Volume Profile 的子资产从未落盘，见 GetOrAddVolumeComponent。
        DynamicGI.UpdateEnvironment();

        var playerCamera = GameObject.Find("AC6PlayerCamera");
        var camera = playerCamera != null ? playerCamera.GetComponent<Camera>() : null;
        if (camera != null)
        {
            camera.clearFlags = CameraClearFlags.Skybox;
            camera.backgroundColor = Color.black;
            // Without this the Volume stack is evaluated and then thrown away - no tonemapping,
            // no bloom, and the HDR sun stays a flat clipped white disc.
            var cameraData = camera.GetUniversalAdditionalCameraData();
            if (cameraData != null)
            {
                cameraData.renderPostProcessing = true;
                EditorUtility.SetDirty(cameraData);
            }

            EditorUtility.SetDirty(camera);
        }

        var keyLightObject = GameObject.Find("Key Light");
        var keyLight = keyLightObject != null ? keyLightObject.GetComponent<Light>() : null;
        if (keyLight != null)
        {
            keyLight.type = LightType.Directional;
            keyLight.color = new Color(1f, 0.965f, 0.9f, 1f);
            // 2.35 was a "can see the mech" value. Sunlight in vacuum is not subtle, and with ACES
            // now in the chain the mid tones need to sit high enough to reach the shoulder: a 0.42
            // albedo zone lands near 4.2 in the HDR buffer here, which is where the curve starts
            // rolling off. Deliberately NOT pushed to 30-50 - that shoves the whole mech into the
            // shoulder and flattens it. The glare comes from the sun disc in the panorama (220
            // linear) plus Bloom, not from cranking this.
            keyLight.intensity = 10f;
            // Hard, not Soft. Vacuum has no atmosphere to scatter light around the terminator, and
            // the sun subtends only about 0.53 degrees from here, so the penumbra it casts is a
            // few centimetres at mech scale - visually a knife edge. Soft shadows were inventing a
            // gradient that physically is not there, and single-tap is cheaper besides.
            keyLight.shadows = LightShadows.Hard;
            keyLight.transform.rotation = Quaternion.Euler(KeyLightEuler);
            RenderSettings.sun = keyLight;
            ConfigureSunLensFlare(keyLight);
            EditorUtility.SetDirty(keyLight);
        }

        var fillLightObject = GameObject.Find("Gameplay Fill Light");
        if (fillLightObject == null)
        {
            fillLightObject = new GameObject("Gameplay Fill Light");
            if (keyLightObject != null)
            {
                fillLightObject.transform.SetParent(
                    keyLightObject.transform.parent,
                    false);
            }
        }

        var fillLight = fillLightObject.GetComponent<Light>();
        if (fillLight == null)
        {
            fillLight = fillLightObject.AddComponent<Light>();
        }
        fillLight.type = LightType.Directional;
        fillLight.color = new Color(0.46f, 0.58f, 0.72f, 1f);
        // Stands in for planetshine off the surface below. Scaled down with the ambient so the
        // contrast ratio against the new key light stays in the same territory instead of the
        // unlit side quietly becoming a second key.
        fillLight.intensity = 0.5f;
        fillLight.shadows = LightShadows.None;
        fillLight.transform.rotation = Quaternion.Euler(24f, 148f, 0f);
        EditorUtility.SetDirty(fillLight);
        EditorUtility.SetDirty(fillLightObject);

        var rimLightObject = GameObject.Find("Rim Light");
        if (rimLightObject == null)
        {
            rimLightObject = new GameObject("Rim Light");
            if (keyLightObject != null)
            {
                rimLightObject.transform.SetParent(
                    keyLightObject.transform.parent,
                    false);
            }
        }

        var rimLight = rimLightObject.GetComponent<Light>();
        if (rimLight == null)
        {
            rimLight = rimLightObject.AddComponent<Light>();
        }

        // Was a Point light with a 45 m range, which only ever reached the mech near spawn - and
        // it could not reach it at all until the _CLUSTER_LIGHT_LOOP keyword fix, since the mech
        // shader was dropping every non-directional light. Directional works at any distance and
        // costs nothing extra.
        //
        // Safe to add as a second directional: URP's GetBrightestDirectionalLightIndex returns
        // RenderSettings.sun outright when it is set, and that is the key light, so the main
        // light slot does not change hands on intensity.
        rimLight.type = LightType.Directional;
        rimLight.color = new Color(0.48f, 0.62f, 0.85f, 1f);
        // Roughly a fifth of the key. A rim separates the silhouette from black; push it much
        // past this and it stops reading as an edge and becomes a second key.
        rimLight.intensity = 2.2f;
        rimLight.shadows = LightShadows.None;
        // Pitch sign follows the same convention KeyLightEuler does: positive pitch puts the
        // source above. Negative here means it comes from slightly below, on an azimuth
        // roughly perpendicular to the key/fill axis, so it contributes an edge neither of the
        // other two can.
        rimLight.transform.rotation = Quaternion.Euler(-16f, 62f, 0f);
        EditorUtility.SetDirty(rimLight);
        EditorUtility.SetDirty(rimLightObject);

        ConfigurePostProcessing();
    }

    /// <summary>
    /// Two global Volumes, not one. The thermal screen pass injects at
    /// BeforeRenderingPostProcessing, so ACES and any exposure or saturation grading would land on
    /// top of the thermal image and re-map the very grey levels the AGC window was calibrated
    /// against. The thermal Volume sits at a higher priority and counter-overrides everything the
    /// combat Volume sets, so toggling modes swaps the whole display chain instead of layering a
    /// film look over an instrument readout.
    ///
    /// Priority beats a volumeLayerMask here purely because it costs no layer: layers are a
    /// 32-slot global project resource and a scene builder has no business spending one.
    /// </summary>
    /// <summary>
    /// Authored lens flare on the sun. The screen-space flare in the volume reacts to whatever is
    /// over-bright, which is useful but generic - it cannot produce the structure that reads as a
    /// photographed sun: one long anamorphic streak, a halo ring, and a line of ghosts marching
    /// across the optical axis. Those are discrete elements and have to be placed.
    ///
    /// Everything here is a starting point meant to be tuned in the Inspector; the asset is a
    /// normal LensFlareDataSRP and every element is editable.
    /// </summary>
    private static void ConfigureSunLensFlare(Light keyLight)
    {
        var flareData = AssetDatabase.LoadAssetAtPath<LensFlareDataSRP>(SunLensFlareDataPath);
        if (flareData == null)
        {
            flareData = ScriptableObject.CreateInstance<LensFlareDataSRP>();
            AssetDatabase.CreateAsset(flareData, SunLensFlareDataPath);
        }

        var warmWhite = new Color(1f, 0.94f, 0.84f, 1f);
        var coolWhite = new Color(0.82f, 0.90f, 1f, 1f);

        // NOTE on units: sizeXY is in half-screen NDC (the screen edge sits at 1.0), and the
        // falloff gradient spans the element's own normalized 0-1 radius - it is NOT a fixed
        // screen-space width. A streak with sizeXY.x = 18 has its zero-intensity point 18 screen-
        // widths past the light; the visible frame only ever samples the flat top of that curve,
        // which is why the previous version rendered as a hard-edged bar clear across the screen
        // instead of a tapered streak. Keeping sizeXY.x in the 1-2 range lets the taper actually
        // land inside the frame.
        //
        // NOTE on falloff: LensFlareDataElementSRP.fallOff is [Range(0,1)] and its setter clamps
        // with Mathf.Clamp01 - the earlier 3.2/4.5/5.5 values here were silently clamped to 1.0,
        // which is the *softest* curve the field supports (pow(x, 1) - linear), not a steep one.
        // Low values (~0.1-0.3) read as a hard, sharp-edged core; values near 1 read as a long
        // soft taper. Pick per-element accordingly rather than reasoning about it as an exponent
        // that can exceed 1.

        var primaryStreak = BuildFlareElement(
            SRPLensFlareType.Circle, 0f, new Vector2(1.3f, 0.018f), 1f, 0.8f, coolWhite,
            fallOff: 1f);

        var secondaryStreak = BuildFlareElement(
            SRPLensFlareType.Circle, 0f, new Vector2(2.1f, 0.008f), 1f, 0.28f, coolWhite,
            fallOff: 1f);

        // A few faint off-axis streaks so the flare doesn't read as a single horizontal bar
        // welded to the sun - real anamorphic/spherical combos always leak a little in other
        // directions too (coating glints, aperture diffraction). rotation is in degrees and
        // turns the whole element about the light's screen position; kept far below the
        // horizontal streaks' intensity (0.8 / 0.28) so horizontal still dominates the read.
        var verticalStreak = BuildFlareElement(
            SRPLensFlareType.Circle, 0f, new Vector2(0.9f, 0.012f), 1f, 0.12f, coolWhite,
            fallOff: 1f, rotation: 90f);

        var diagonalStreakA = BuildFlareElement(
            SRPLensFlareType.Circle, 0f, new Vector2(0.8f, 0.01f), 1f, 0.08f, coolWhite,
            fallOff: 1f, rotation: 45f);

        var diagonalStreakB = BuildFlareElement(
            SRPLensFlareType.Circle, 0f, new Vector2(0.8f, 0.01f), 1f, 0.08f, coolWhite,
            fallOff: 1f, rotation: 135f);

        // Halo ring: angular rainbow gradient instead of a flat tint. A ring with hue cycling
        // around it is the recognizable "chromatic dispersion" halo from anamorphic/coated glass -
        // a flat-tinted ring reads as a plain circle no matter how it's placed.
        var halo = BuildFlareElement(
            SRPLensFlareType.Ring, 0.55f, Vector2.one, 1.7f, 0.14f, Color.white,
            fallOff: 0.6f, ringThickness: 0.1f);
        halo.tintColorType = SRPLensFlareColorType.AngularGradient;
        halo.tintGradient = BuildRainbowTextureGradient(0.6f);

        // Ghosts: a single multi-instance element with random distribution instead of three
        // hand-placed ones. allowMultipleElement + Random distribution is what URP's own system
        // is built to do this with - it gives each ghost its own position/scale/rotation/intensity
        // jitter along the light-to-center axis, which reads as glass rather than a placed decal
        // in a way that a few manually-tuned instances don't. colorGradient tints each instance by
        // its position along that axis, which both sells the "different ghost, different glass"
        // read and gives the rainbow-across-the-chain look asked for.
        //
        // NOTE on the polygon shape softness: fallOff of 0.35 does NOT map to the shader's
        // pow() exponent directly - LensFlareCommonSRP remaps it as
        // exp(lerp(0, 4, 1-fallOff)), so 0.35 was actually exponent ~13.5. At that exponent the
        // SDF sits near 1.0 (fully opaque) across almost the whole interior and only crashes to 0
        // in the last sliver before the edge - a flat-bright disc with a razor cutoff, which is
        // exactly the "hard edge, uniform interior" complaint. Raising it near 1.0 gives exponent
        // ~1 (linear ramp start-to-edge). edgeOffset also widens/narrows that ramp for polygons
        // specifically (it's exponentiated by 5 in C# before reaching the shader), and
        // sdfRoundness blunts the hexagon's corners so it reads as an aberrated glass reflection
        // rather than a stamped decal.
        var ghosts = BuildFlareElement(
            SRPLensFlareType.Polygon, 0.3f, Vector2.one, 0.38f, 0.075f, Color.white,
            fallOff: 0.88f, sideCount: 6);
        ghosts.edgeOffset = 0.4f;
        ghosts.sdfRoundness = 0.45f;
        ghosts.allowMultipleElement = true;
        ghosts.count = 5;
        ghosts.distribution = SRPLensFlareDistribution.Random;
        ghosts.seed = 17;
        ghosts.lengthSpread = 1.1f;
        ghosts.positionVariation = new Vector2(0.9f, 0f);
        ghosts.scaleVariation = 0.6f;
        ghosts.intensityVariation = 0.7f;
        ghosts.rotationVariation = 55f;
        // The flare uses Blend One One, so alpha-only fades do not attenuate its RGB output.
        // Fade neutral RGB toward black instead: this softens the ghost without changing the hue
        // supplied by the independent per-instance rainbow colorGradient below.
        ghosts.tintColorType = SRPLensFlareColorType.RadialGradient;
        ghosts.tintGradient = BuildGhostRadialFadeGradient();
        ghosts.colorGradient = BuildRainbowColorGradient(0.85f);

        flareData.elements = new[]
        {
            // Tight glow welded to the disc. Low falloff so it reads as the core searing rather
            // than as a second, larger sun.
            BuildFlareElement(
                SRPLensFlareType.Circle, 0f, Vector2.one, 0.5f, 1.5f, warmWhite, fallOff: 0.12f),
            primaryStreak,
            secondaryStreak,
            verticalStreak,
            diagonalStreakA,
            diagonalStreakB,
            halo,
            ghosts,
        };
        EditorUtility.SetDirty(flareData);

        var flare = keyLight.GetComponent<LensFlareComponentSRP>();
        if (flare == null)
        {
            flare = keyLight.gameObject.AddComponent<LensFlareComponentSRP>();
        }

        flare.lensFlareData = flareData;
        flare.intensity = 1f;
        flare.scale = 1f;
        // Off for a directional light: there is no light shape to attenuate by, and leaving it on
        // makes the flare's strength depend on something meaningless here.
        flare.attenuationByLightShape = false;
        // The flare should die when the mech or the platform passes in front of the sun. Without
        // this it punches through solid geometry, which is the single most fake-looking failure
        // this effect has.
        flare.useOcclusion = true;
        flare.occlusionRadius = 0.15f;
        flare.allowOffScreen = false;
        EditorUtility.SetDirty(flare);
    }

    private static LensFlareDataElementSRP BuildFlareElement(
        SRPLensFlareType flareType,
        float position,
        Vector2 sizeXY,
        float uniformScale,
        float localIntensity,
        Color tint,
        float fallOff = 1f,
        float ringThickness = 0.25f,
        int sideCount = 6,
        float rotation = 0f)
    {
        return new LensFlareDataElementSRP
        {
            visible = true,
            flareType = flareType,
            position = position,
            sizeXY = sizeXY,
            uniformScale = uniformScale,
            localIntensity = localIntensity,
            tint = tint,
            fallOff = fallOff,
            ringThickness = ringThickness,
            sideCount = sideCount,
            rotation = rotation,
            // Required for the streak elements: with aspect preservation on, a 18 x 0.05 size is
            // squashed back to square and the bar never appears.
            preserveAspectRatio = false,
            blendMode = SRPLensFlareBlendMode.Additive,
            modulateByLightColor = true,
            autoRotate = false,
        };
    }

    /// <summary>
    /// Full-saturation hue wheel for an AngularGradient tint (e.g. the halo ring). Angle 0 and
    /// angle 1 are the same point on the circle, so the first and last color keys must match or
    /// the wheel shows a visible seam - HSVToRGB(0, ...) and HSVToRGB(1, ...) both give red, so a
    /// plain 0..1 sweep is already seamless.
    /// </summary>
    private static TextureGradient BuildRainbowTextureGradient(float alpha)
    {
        const int keyCount = 7;
        var colorKeys = new GradientColorKey[keyCount];
        for (var i = 0; i < keyCount; i++)
        {
            var t = i / (float)(keyCount - 1);
            colorKeys[i] = new GradientColorKey(Color.HSVToRGB(t, 0.55f, 1f), t);
        }

        var alphaKeys = new[]
        {
            new GradientAlphaKey(alpha, 0f),
            new GradientAlphaKey(alpha, 1f),
        };
        return new TextureGradient(colorKeys, alphaKeys);
    }

    /// <summary>
    /// Center-bright neutral luminance ramp for an additive ghost. Blend One One ignores alpha,
    /// so the RGB channels themselves must approach black at the edge.
    /// </summary>
    private static TextureGradient BuildGhostRadialFadeGradient()
    {
        var colorKeys = new[]
        {
            new GradientColorKey(Color.white, 0f),
            new GradientColorKey(new Color(0.55f, 0.55f, 0.55f, 1f), 0.5f),
            new GradientColorKey(new Color(0.05f, 0.05f, 0.05f, 1f), 1f),
        };
        var alphaKeys = new[]
        {
            new GradientAlphaKey(1f, 0f),
            new GradientAlphaKey(1f, 1f),
        };
        return new TextureGradient(colorKeys, alphaKeys);
    }

    /// <summary>
    /// Same hue wheel as <see cref="BuildRainbowTextureGradient"/> but as a plain Gradient, for
    /// LensFlareDataElementSRP.colorGradient (the per-instance tint used when allowMultipleElement
    /// + Random distribution spreads one element into several).
    /// </summary>
    private static Gradient BuildRainbowColorGradient(float saturation)
    {
        const int keyCount = 7;
        var colorKeys = new GradientColorKey[keyCount];
        for (var i = 0; i < keyCount; i++)
        {
            var t = i / (float)(keyCount - 1);
            colorKeys[i] = new GradientColorKey(Color.HSVToRGB(t, saturation, 1f), t);
        }

        var gradient = new Gradient();
        gradient.SetKeys(
            colorKeys,
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
        return gradient;
    }

    private static void ConfigurePostProcessing()
    {
        var combatProfile = GetOrCreateVolumeProfile(CombatVolumeProfilePath);
        ConfigureCombatVolumeProfile(combatProfile);

        var thermalProfile = GetOrCreateVolumeProfile(ThermalVolumeProfilePath);
        ConfigureThermalVolumeProfile(thermalProfile);

        GetOrCreateGlobalVolume(CombatVolumeObjectName, 0f, combatProfile).enabled = true;

        var thermalVolume = GetOrCreateGlobalVolume(
            MechThermalVisionController.ThermalVolumeObjectName,
            100f,
            thermalProfile);
        // Starts off; MechThermalVisionController.SetMode owns it from here.
        thermalVolume.enabled = false;

        var controller = UnityEngine.Object.FindFirstObjectByType<MechThermalVisionController>();
        if (controller != null)
        {
            controller.BindThermalVolume(thermalVolume);
            EditorUtility.SetDirty(controller);
        }
    }

    private static void ConfigureCombatVolumeProfile(VolumeProfile profile)
    {
        var tonemapping = GetOrAddVolumeComponent<Tonemapping>(profile);
        tonemapping.active = true;
        tonemapping.mode.overrideState = true;
        tonemapping.mode.value = TonemappingMode.ACES;

        var bloom = GetOrAddVolumeComponent<Bloom>(profile);
        bloom.active = true;
        bloom.threshold.overrideState = true;
        // Above the brightest star (the panorama authors every star below 1.0 linear) and far
        // below the sun disc, so the sun and hot speculars bloom while the star field stays
        // as points rather than becoming a field of competing glare sources.
        bloom.threshold.value = 1.1f;
        bloom.intensity.overrideState = true;
        bloom.intensity.value = 0.85f;
        bloom.scatter.overrideState = true;
        // Tightened from 0.62. Scatter weights the upsample toward the coarser mips, so a high
        // value spreads the glow into a wide soft halo - which, stacked on the old wide corona,
        // was most of why the sun looked like a diffuse ball instead of a searing point.
        bloom.scatter.value = 0.45f;
        bloom.highQualityFiltering.overrideState = true;
        bloom.highQualityFiltering.value = true;

        var colorAdjustments = GetOrAddVolumeComponent<ColorAdjustments>(profile);
        colorAdjustments.active = true;
        // Present but neutral on purpose. ACES should be judged on its own before grading gets
        // layered over it; these are the two knobs to reach for once it has been.
        colorAdjustments.postExposure.overrideState = true;
        colorAdjustments.postExposure.value = 0f;
        colorAdjustments.saturation.overrideState = true;
        colorAdjustments.saturation.value = 0f;

        // Lens artefacts off the sun. This reads a bloom mip rather than tracing light sources,
        // so it costs almost nothing and keys off whatever is genuinely over-bright - which, with
        // the analytic 220-radiance disc, is the sun and hot speculars and nothing else. It needs
        // Bloom active to have a mip to read; the combat profile enables it above.
        var lensFlare = GetOrAddVolumeComponent<ScreenSpaceLensFlare>(profile);
        lensFlare.active = true;
        lensFlare.intensity.overrideState = true;
        // Pulled back now that the data-driven flare on the key light carries the sun. This one
        // stays on at a low level because it reacts to anything over-bright, not just the sun -
        // which is what will make thruster plumes and missile motors streak later.
        lensFlare.intensity.value = 0.18f;
        lensFlare.bloomMip.overrideState = true;
        lensFlare.bloomMip.value = 1;
        // The anamorphic streak is most of what reads as "looking at a sun through a lens".
        lensFlare.streaksIntensity.overrideState = true;
        lensFlare.streaksIntensity.value = 1.2f;
        lensFlare.streaksLength.overrideState = true;
        lensFlare.streaksLength.value = 0.5f;
        lensFlare.streaksThreshold.overrideState = true;
        lensFlare.streaksThreshold.value = 0.5f;
        lensFlare.chromaticAbberationIntensity.overrideState = true;
        lensFlare.chromaticAbberationIntensity.value = 0.3f;

        EditorUtility.SetDirty(profile);
    }

    private static void ConfigureThermalVolumeProfile(VolumeProfile profile)
    {
        var tonemapping = GetOrAddVolumeComponent<Tonemapping>(profile);
        tonemapping.active = true;
        tonemapping.mode.overrideState = true;
        // No S-curve over an instrument readout. The AGC maps radiance to grey levels linearly
        // and ACES would redistribute exactly those levels - which is how the mech went invisible
        // against the sky the first time, just via a different mechanism.
        tonemapping.mode.value = TonemappingMode.None;

        var bloom = GetOrAddVolumeComponent<Bloom>(profile);
        bloom.active = true;
        bloom.threshold.overrideState = true;
        // Just under white rather than at it. AC6ThermalRadiance's exponential rolloff is tuned so
        // ordinary armour stays below pure white while thrusters and missile motors roll into
        // saturation, so a threshold here selects exactly those and nothing else - the cold
        // background sits near 0.025 and never comes close. This is what makes hot sources burn
        // without needing the screen pass to emit above-white HDR values.
        bloom.threshold.value = 0.9f;
        bloom.intensity.overrideState = true;
        bloom.intensity.value = 0.35f;
        bloom.scatter.overrideState = true;
        bloom.scatter.value = 0.5f;
        bloom.highQualityFiltering.overrideState = true;
        bloom.highQualityFiltering.value = true;

        var colorAdjustments = GetOrAddVolumeComponent<ColorAdjustments>(profile);
        colorAdjustments.active = true;
        colorAdjustments.postExposure.overrideState = true;
        colorAdjustments.postExposure.value = 0f;
        colorAdjustments.saturation.overrideState = true;
        colorAdjustments.saturation.value = 0f;

        // Counter-overridden to zero for the same reason everything else here is: a thermal
        // imager has no camera lens in the visible path to throw flares, and the AGC grey levels
        // are a readout, not a photograph.
        var lensFlare = GetOrAddVolumeComponent<ScreenSpaceLensFlare>(profile);
        lensFlare.active = true;
        lensFlare.intensity.overrideState = true;
        lensFlare.intensity.value = 0f;

        EditorUtility.SetDirty(profile);
    }

    private static VolumeProfile GetOrCreateVolumeProfile(string path)
    {
        var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(path);
        if (profile != null)
        {
            return profile;
        }

        profile = ScriptableObject.CreateInstance<VolumeProfile>();
        AssetDatabase.CreateAsset(profile, path);
        return profile;
    }

    private static T GetOrAddVolumeComponent<T>(VolumeProfile profile)
        where T : VolumeComponent
    {
        if (profile.TryGet<T>(out var existing))
        {
            return existing;
        }

        var component = profile.Add<T>(true);

        // 这一行是"为什么不跑 Rebuild Player Environment 就没有正常光影"的答案。
        //
        // VolumeProfile.Add 是运行期 API：它 CreateInstance 出来的 VolumeComponent 只被
        // profile.components 列表引用，并不属于任何资产文件。因此保存时那些引用会被写成
        // `fileID: 0`，而 VolumeProfile.OnEnable 又会 `components.RemoveAll(x => x == null)`
        // 把空引用清掉——重新加载后 profile 就是彻底空的：没有 ACES 色调映射、没有 Bloom、
        // 没有 ScreenSpaceLensFlare。构建当次因为内存里的组件还活着所以画面正确，
        // 一旦重启/重载就全部消失，于是表现为"每次都得手动重建一遍"。
        //
        // 症状可以据此对上：Key Light 强度 10 是按 ACES 的肩部标定的，少了色调映射后
        // 0.12 albedo 的地板线性值直接裁到 1.0（画面里那片发白的地面），HDR 太阳盘塌成
        // 一个硬白盘；而挂在场景灯光上的 LensFlareComponentSRP 不走 Volume，所以鬼影和
        // 拉丝仍然照常渲染——这也正是截图里"太阳有光晕但整体曝光不对"的原因。
        //
        // Unity 自己的 VolumeProfileFactory.CreateVolumeComponent 就是靠这一行落盘的；
        // hideFlags 与 name 已由 VolumeProfile.Add 在 UNITY_EDITOR 下设好，无需重复。
        AssetDatabase.AddObjectToAsset(component, profile);
        EditorUtility.SetDirty(profile);
        return component;
    }

    private static Volume GetOrCreateGlobalVolume(
        string objectName,
        float priority,
        VolumeProfile profile)
    {
        var volumeObject = GameObject.Find(objectName) ?? new GameObject(objectName);
        var volume = volumeObject.GetComponent<Volume>();
        if (volume == null)
        {
            volume = volumeObject.AddComponent<Volume>();
        }

        volume.isGlobal = true;
        volume.priority = priority;
        volume.weight = 1f;
        volume.sharedProfile = profile;
        EditorUtility.SetDirty(volume);
        return volume;
    }

    private static CombatTestTarget BuildCombatTestTarget(GameObject playerMech)
    {
        const string firstTargetName = "AC6AiCombatMech_A";
        const string secondTargetName = "AC6AiCombatMech_B";
        foreach (var staleName in new[]
                 {
                     firstTargetName,
                     secondTargetName,
                     "AC6CombatTestTarget",
                     "AC6AirborneCombatTestTarget",
                     "Moving_Target",
                 })
        {
            var stale = GameObject.Find(staleName);
            if (stale != null)
            {
                UnityEngine.Object.DestroyImmediate(stale);
            }
        }

        var playerAimPoint = playerMech.transform.Find("TargetAimPoint");
        if (playerAimPoint == null)
        {
            playerAimPoint = new GameObject("TargetAimPoint").transform;
            playerAimPoint.SetParent(playerMech.transform, false);
            playerAimPoint.localPosition = new Vector3(0f, 5.5f, 0f);
        }

        var playerTarget = Ensure<CombatTestTarget>(playerMech);
        playerTarget.Configure(playerAimPoint, 5000f, false, 0f, 0f);
        playerTarget.ConfigureIdentity("PLAYER-MECH", 1);
        EditorUtility.SetDirty(playerTarget);

        var playerLoadout =
            playerMech.GetComponent<MechRuntimeState>()?.CurrentLoadout;
        var variantA = BuildAiVariantLoadout(
            playerLoadout,
            $"{LoadoutRoot}/AC6_Skinned_MixedA.asset",
            $"{LoadoutRoot}/AC6_AI_Target_A.asset",
            "ac6_ai_target_a",
            "AI Target A");
        var first = BuildAiCombatMech(
            playerMech,
            playerTarget,
            variantA,
            firstTargetName,
            "AI-MECH-A",
            new Vector3(0f, 11f, 150f),
            new Color(0.96f, 0.97f, 0.98f, 1f));
        return first;
    }

    private static CombatTestTarget BuildAiCombatMech(
        GameObject playerMech,
        CombatTestTarget playerTarget,
        MechLoadoutDefinition variantLoadout,
        string objectName,
        string targetId,
        Vector3 position,
        Color identificationColor)
    {
        var root = UnityEngine.Object.Instantiate(
            playerMech,
            playerMech.transform.parent);
        root.name = objectName;
        root.transform.position = position;
        root.transform.rotation = Quaternion.LookRotation(
            playerMech.transform.position - position,
            Vector3.up);

        var assembler = root.GetComponent<MechAssembler>();
        if (assembler != null && variantLoadout != null)
        {
            assembler.Loadout = variantLoadout;
            assembler.Assemble();
            EditorUtility.SetDirty(assembler);
        }

        var radarHud = root.GetComponent<MechRadarHudPresenter>();
        if (radarHud != null)
        {
            UnityEngine.Object.DestroyImmediate(radarHud);
        }
        var warningHud = root.GetComponent<MechThreatWarningPresenter>();
        if (warningHud != null)
        {
            UnityEngine.Object.DestroyImmediate(warningHud);
        }
        var trajectoryPredictor = root.GetComponent<MechTrajectoryPredictor>();
        if (trajectoryPredictor != null)
        {
            trajectoryPredictor.enabled = false;
            EditorUtility.SetDirty(trajectoryPredictor);
        }

        var input = Ensure<MechPlayerInputAdapter>(root);
        input.SetExternalControl(true);
        var rig = Ensure<MechPlayerRig>(root);
        rig.ConfigureAiControl();
        rig.enabled = true;

        var aimPoint = root.transform.Find("TargetAimPoint");
        var target = Ensure<CombatTestTarget>(root);
        target.Configure(aimPoint, 5000f, false, 0f, 0f);
        target.ConfigureIdentity(targetId, 2);

        var sensorObject = new GameObject("AI Fire-Control Sensor");
        sensorObject.transform.SetParent(root.transform, false);
        sensorObject.transform.localPosition = new Vector3(0f, 6f, 0f);
        var sensorCamera = sensorObject.AddComponent<Camera>();
        sensorCamera.enabled = false;
        sensorCamera.fieldOfView = 60f;
        sensorCamera.nearClipPlane = 0.1f;
        sensorCamera.farClipPlane = 100000f;

        var weaponSystem = Ensure<MechTestWeaponSystem>(root);
        weaponSystem.Configure(
            input,
            root.GetComponent<MechRuntimeState>(),
            sensorCamera,
            playerTarget,
            AC6FxrEffectFactory.BuildLibrary());
        weaponSystem.ConfigureAimViewport(new Vector2(0.5f, 0.5f));
        weaponSystem.enabled = true;
        if (weaponSystem.RadarController != null)
        {
            weaponSystem.RadarController.enabled = true;
            EditorUtility.SetDirty(weaponSystem.RadarController);
        }

        Ensure<MechTestRadarEmitter>(root);
        var legacyAiSensors = root.GetComponent<MechAiSensorSuite>();
        if (legacyAiSensors != null)
        {
            UnityEngine.Object.DestroyImmediate(legacyAiSensors);
        }
        var ai = Ensure<MechAiTargetController>(root);
        ai.Configure(playerTarget, sensorCamera);
        ai.ConfigureCombatBehavior(3000f, 5000f, 8f);
        ai.enabled = true;

        var aiSo = new SerializedObject(ai);
        var tintProperty = aiSo.FindProperty("identificationTint");
        if (tintProperty != null)
        {
            tintProperty.colorValue = identificationColor;
            aiSo.ApplyModifiedPropertiesWithoutUndo();
        }

        EditorUtility.SetDirty(input);
        EditorUtility.SetDirty(rig);
        EditorUtility.SetDirty(target);
        EditorUtility.SetDirty(sensorCamera);
        EditorUtility.SetDirty(weaponSystem);
        EditorUtility.SetDirty(ai);
        EditorUtility.SetDirty(root);
        return target;
    }

    private static MechLoadoutDefinition BuildAiVariantLoadout(
        MechLoadoutDefinition playerLoadout,
        string frameSourcePath,
        string destinationPath,
        string loadoutId,
        string displayName)
    {
        var frameSource =
            AssetDatabase.LoadAssetAtPath<MechLoadoutDefinition>(
                frameSourcePath);
        if (playerLoadout == null || frameSource == null)
        {
            return playerLoadout;
        }

        var assignments = new List<MechPartAssignment>();
        foreach (var playerAssignment in
                 playerLoadout.EnumerateEnabledAssignments())
        {
            var part = playerAssignment.part;
            if (FrameSlots.Any(entry => entry.Slot == playerAssignment.slot) &&
                frameSource.TryGetPart(playerAssignment.slot, out var framePart))
            {
                part = framePart;
            }

            assignments.Add(new MechPartAssignment
            {
                slot = playerAssignment.slot,
                part = part,
                enabled = playerAssignment.enabled,
            });
        }

        var loadout =
            AssetDatabase.LoadAssetAtPath<MechLoadoutDefinition>(
                destinationPath);
        if (loadout == null)
        {
            loadout = ScriptableObject.CreateInstance<MechLoadoutDefinition>();
            AssetDatabase.CreateAsset(loadout, destinationPath);
        }

        loadout.EditorConfigure(loadoutId, displayName, assignments);
        EditorUtility.SetDirty(loadout);
        AssetDatabase.SaveAssets();
        return loadout;
    }

    private static void CreateRampCourse(
        Transform parent,
        string name,
        float x,
        float startZ,
        float rampLength,
        float angleDegrees,
        float width,
        float plateauLength,
        Material surfaceMaterial,
        Material markingMaterial)
    {
        const float thickness = 1f;
        var radians = angleDegrees * Mathf.Deg2Rad;
        var rise = Mathf.Sin(radians) * rampLength;
        var centerY = rise * 0.5f - Mathf.Cos(radians) * thickness * 0.5f;
        var plateauY = rise - 0.4f;
        var upCenterZ = startZ + rampLength * 0.5f;
        var plateauCenterZ = startZ + rampLength + plateauLength * 0.5f;
        var downCenterZ = startZ + rampLength + plateauLength + rampLength * 0.5f;

        CreateEnvironmentRamp(
            parent, $"{name} Ramp Up", new Vector3(x, centerY, upCenterZ),
            new Vector3(width, thickness, rampLength), new Vector3(-angleDegrees, 0f, 0f),
            surfaceMaterial);
        CreateEnvironmentBlock(
            parent, $"{name} Plateau", new Vector3(x, plateauY, plateauCenterZ),
            new Vector3(width, 0.8f, plateauLength), surfaceMaterial, true);
        CreateEnvironmentRamp(
            parent, $"{name} Ramp Down", new Vector3(x, centerY, downCenterZ),
            new Vector3(width, thickness, rampLength), new Vector3(angleDegrees, 0f, 0f),
            surfaceMaterial);

        CreateEnvironmentBlock(
            parent, $"{name} Course Marking",
            new Vector3(x, rise + 0.025f, plateauCenterZ),
            new Vector3(0.35f, 0.02f, plateauLength - 4f), markingMaterial, false);
    }

    private static Material GetOrCreateEnvironmentMaterial(
        string path, Color color, float metallic, float smoothness)
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            material = new Material(shader);
            AssetDatabase.CreateAsset(material, path);
        }

        material.color = color;
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", metallic);
        if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
        EditorUtility.SetDirty(material);
        return material;
    }

    private static void CreateEnvironmentBlock(
        Transform parent, string name, Vector3 position, Vector3 scale,
        Material material, bool keepCollider)
    {
        var block = GameObject.CreatePrimitive(PrimitiveType.Cube);
        block.name = name;
        block.transform.SetParent(parent, false);
        block.transform.position = position;
        block.transform.localScale = scale;
        block.GetComponent<MeshRenderer>().sharedMaterial = material;

        if (!keepCollider)
        {
            UnityEngine.Object.DestroyImmediate(block.GetComponent<Collider>());
        }
    }

    private static void CreateEnvironmentRamp(
        Transform parent,
        string name,
        Vector3 position,
        Vector3 scale,
        Vector3 eulerAngles,
        Material material)
    {
        var ramp = GameObject.CreatePrimitive(PrimitiveType.Cube);
        ramp.name = name;
        ramp.transform.SetParent(parent, false);
        ramp.transform.SetPositionAndRotation(position, Quaternion.Euler(eulerAngles));
        ramp.transform.localScale = scale;
        ramp.GetComponent<MeshRenderer>().sharedMaterial = material;
    }

    private static void CreateReferenceMarker(Transform parent, Vector3 position, Vector3 scale)
    {
        var marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
        marker.name = "Navigation Marker";
        marker.transform.SetParent(parent, false);
        marker.transform.position = position;
        marker.transform.localScale = scale;
        var collider = marker.GetComponent<Collider>();
        if (collider != null)
        {
            UnityEngine.Object.DestroyImmediate(collider);
        }
    }
}
