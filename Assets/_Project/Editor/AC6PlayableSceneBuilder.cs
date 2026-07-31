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
    private const string DeepSpaceSkyTexturePath =
        GeneratedMaterialsRoot + "/AC6SparseDeepSpacePanorama.exr";
    // The 8-bit PNG this replaces capped every pixel at 1.0, which blocked two separate things:
    // the sun could never cross a bloom threshold of 1.0, and the skybox reflection probe had no
    // value brighter than white for metal to reflect. Metals have almost no diffuse term, so
    // raising metallic against an LDR probe darkens the mech instead of making it shine.
    private const string LegacyDeepSpaceSkyTexturePath =
        GeneratedMaterialsRoot + "/AC6SparseDeepSpacePanorama.png";
    private const string DeepSpaceSkyTextureVersion = "3-hdr-sun";
    private const string RenderingRoot = "Assets/_Project/Rendering";
    private const string CombatVolumeProfilePath =
        RenderingRoot + "/AC6SpaceCombatVolume.asset";
    private const string ThermalVolumeProfilePath =
        RenderingRoot + "/AC6ThermalVolume.asset";
    private const string CombatVolumeObjectName = "AC6 Combat Post";
    // Linear radiance, authored directly (the EXR is not sRGB encoded).
    private const float SunDiskRadiance = 220f;
    private const float SunCoronaRadiance = 6.5f;
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
        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        EditorSceneManager.SaveOpenScenes();
        Selection.activeGameObject = GameObject.Find("AC6PlayerEnvironment");
        Debug.Log("[AC6PlayableScene] Player environment rebuilt with finite structural platform.");
    }

    // Also services one-shot rebuild requests created while the editor is open.
    [InitializeOnLoadMethod]
    private static void RunRequestedBuildAfterReload()
    {
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
        rigSo.FindProperty("graphRunner").objectReferenceValue = runner;
        rigSo.FindProperty("weaponLayer").objectReferenceValue = weapon;
        rigSo.FindProperty("showFlightInstruments").boolValue = true;
        rigSo.ApplyModifiedPropertiesWithoutUndo();

        EditorSceneManager.SaveScene(scene, PlayableScenePath);
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
            var fullThrottle = new MechPlayerInputFrame { MainThrottle = 1f };
            for (var i = 0; i < 600; i++)
            {
                sustainedThrust.Tick(in fullThrottle, dt);
            }

            var sustainedThrustSpeed = sustainedThrust.HorizontalSpeed;
            var sustainedThrustPass =
                sustainedThrustSpeed > 100f &&
                sustainedThrustSpeed <= 200.01f;

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
                     + $"({sustainedThrustSpeed:F2} m/s), "
                     + $"ground-brake={(groundBrakePass ? "PASS" : "FAIL")} "
                     + $"({impulseSpeed:F2}->{stoppedSpeed:F2} m/s), "
                     + $"air-brake={(airBrakePass ? "PASS" : "FAIL")} "
                     + $"(horizontal={airBrakeSpeed:F3} m/s, altitude-error={airBrakeAltitudeError:F4} m), "
                     + $"energy={(energyPass ? "PASS" : "FAIL")} "
                     + $"(recovered={energy.CurrentManeuverEnergy:F1}, "
                     + $"qb-left={quickBoostEnergy.CurrentManeuverEnergy:F1})";
            return pitchedForwardPass && assaultBoostPass && quickBoostPass && sustainedThrustPass
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
        const string rootName = "AC6PlayerEnvironment";
        var existing = GameObject.Find(rootName);
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

        var root = new GameObject(rootName);
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
    }

    private static void ConfigureDeepSpaceEnvironment()
    {
        var skyboxShader = Shader.Find("Skybox/Panoramic");
        if (skyboxShader == null)
        {
            Debug.LogWarning("[AC6PlayableScene] Unity panoramic skybox shader is unavailable.");
            return;
        }

        var panorama = GetOrCreateDeepSpacePanorama();
        if (panorama == null)
        {
            Debug.LogWarning("[AC6PlayableScene] Failed to generate the deep-space panorama.");
            return;
        }

        var skybox = AssetDatabase.LoadAssetAtPath<Material>(DeepSpaceSkyboxMaterialPath);
        if (skybox == null)
        {
            skybox = new Material(skyboxShader);
            AssetDatabase.CreateAsset(skybox, DeepSpaceSkyboxMaterialPath);
        }

        skybox.shader = skyboxShader;
        skybox.SetTexture("_MainTex", panorama);
        if (skybox.HasProperty("_Tint"))
        {
            skybox.SetColor("_Tint", Color.white);
        }

        if (skybox.HasProperty("_Exposure"))
        {
            // 1.12 was compensating for the old LDR panorama. The EXR carries absolute linear
            // radiance, so leave it at unity and tune brightness where it is authored instead.
            skybox.SetFloat("_Exposure", 1f);
        }

        if (skybox.HasProperty("_Rotation"))
        {
            skybox.SetFloat("_Rotation", 0f);
        }

        if (skybox.HasProperty("_Mapping"))
        {
            skybox.SetFloat("_Mapping", 1f);
        }

        if (skybox.HasProperty("_ImageType"))
        {
            skybox.SetFloat("_ImageType", 0f);
        }

        if (skybox.HasProperty("_MirrorOnBack"))
        {
            skybox.SetFloat("_MirrorOnBack", 0f);
        }

        if (skybox.GetTexture("_MainTex") != panorama)
        {
            Debug.LogError("[AC6PlayableScene] Deep-space panorama was not bound to the skybox material.");
            return;
        }
        EditorUtility.SetDirty(skybox);

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
            keyLight.transform.rotation = Quaternion.Euler(31f, -32f, 0f);
            RenderSettings.sun = keyLight;
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
        // Pitch sign follows the same convention PaintDistantSun relies on: positive pitch puts
        // the source above. Negative here means it comes from slightly below, on an azimuth
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
        bloom.scatter.value = 0.62f;
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
        return profile.TryGet<T>(out var component) ? component : profile.Add<T>(true);
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

    private static Texture2D GetOrCreateDeepSpacePanorama()
    {
        var panorama = AssetDatabase.LoadAssetAtPath<Texture2D>(DeepSpaceSkyTexturePath);
        var existingImporter =
            AssetImporter.GetAtPath(DeepSpaceSkyTexturePath) as TextureImporter;
        if (panorama != null &&
            existingImporter != null &&
            existingImporter.userData == DeepSpaceSkyTextureVersion)
        {
            return panorama;
        }

        const int width = 4096;
        const int height = 2048;
        var pixels = new Color[width * height];

        // Flat, and near neutral. The old version ramped the blue channel by latitude, but
        // latitude IS the pole axis of an equirectangular map, so a latitude ramp can only ever
        // produce a cap at the zenith and another at the nadir. Equirect also converges every
        // pixel of the top row onto a single point, which wipes out star detail there - so the cap
        // read as a smooth starless blue patch overhead. Any large-scale variation has to come
        // from an actual sky feature, never from the map's v coordinate.
        //
        // Neutral rather than blue: a blue night sky is an atmospheric convention. Vacuum has no
        // Rayleigh scattering, and the old (1, 2, 6) ratio made blue five times red.
        var background = SrgbByteToLinear(3f, 3f, 4f);
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = background;
        }

        var random = new System.Random(0xAC6006);
        const int starCount = 2100;
        for (var starIndex = 0; starIndex < starCount; starIndex++)
        {
            var longitude = random.NextDouble();
            var latitude = Math.Asin(random.NextDouble() * 2.0 - 1.0) / Math.PI + 0.5;
            var centerX = Mathf.Clamp((int)(longitude * width), 0, width - 1);
            var centerY = Mathf.Clamp((int)(latitude * height), 0, height - 1);

            var tier = random.NextDouble();
            var radius = tier > 0.985 ? 2 : tier > 0.88 ? 1 : 0;
            var brightness = tier > 0.985
                ? random.Next(205, 256)
                : tier > 0.88
                    ? random.Next(125, 205)
                    : random.Next(58, 126);
            var temperature = random.NextDouble();
            var red = temperature < 0.18 ? brightness : Mathf.RoundToInt(brightness * 0.82f);
            var green = Mathf.RoundToInt(brightness * (temperature > 0.82 ? 0.82f : 0.9f));
            var blue = temperature > 0.82 ? brightness : Mathf.RoundToInt(brightness * 0.92f);

            for (var offsetY = -radius; offsetY <= radius; offsetY++)
            {
                var y = centerY + offsetY;
                if (y < 0 || y >= height)
                {
                    continue;
                }

                for (var offsetX = -radius; offsetX <= radius; offsetX++)
                {
                    var x = (centerX + offsetX + width) % width;
                    var distance = Mathf.Sqrt(offsetX * offsetX + offsetY * offsetY);
                    var falloff = radius == 0 ? 1f : Mathf.Clamp01(1.2f - distance / (radius + 0.35f));
                    if (falloff <= 0f)
                    {
                        continue;
                    }

                    var index = y * width + x;
                    var existing = pixels[index];
                    // Star tiers stay below 1.0 linear on purpose: they should read as points,
                    // not as a field of bloom sources competing with the sun.
                    var star = SrgbByteToLinear(
                        red * falloff,
                        green * falloff,
                        blue * falloff);
                    pixels[index] = new Color(
                        Mathf.Max(existing.r, star.r),
                        Mathf.Max(existing.g, star.g),
                        Mathf.Max(existing.b, star.b),
                        1f);
                }
            }
        }

        PaintDistantSun(pixels, width, height);

        // Half-float linear, written straight out as EXR. SetPixels (not SetPixels32) so the
        // above-white sun survives; RGBA32 would have clipped it back to the old LDR behaviour.
        var texture = new Texture2D(width, height, TextureFormat.RGBAHalf, false, true);
        texture.SetPixels(pixels);
        texture.Apply(false, false);

        var absolutePath = Path.Combine(
            Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty,
            DeepSpaceSkyTexturePath);
        File.WriteAllBytes(absolutePath, texture.EncodeToEXR(Texture2D.EXRFlags.None));
        UnityEngine.Object.DestroyImmediate(texture);

        // The 8-bit panorama and its .meta would otherwise linger next to the EXR and keep
        // showing up in asset searches as the "real" sky.
        if (AssetDatabase.LoadAssetAtPath<Texture2D>(LegacyDeepSpaceSkyTexturePath) != null)
        {
            AssetDatabase.DeleteAsset(LegacyDeepSpaceSkyTexturePath);
        }

        AssetDatabase.ImportAsset(DeepSpaceSkyTexturePath, ImportAssetOptions.ForceSynchronousImport);
        if (AssetImporter.GetAtPath(DeepSpaceSkyTexturePath) is TextureImporter importer)
        {
            importer.textureType = TextureImporterType.Default;
            // The EXR already holds linear radiance. Leaving sRGB on would apply a second
            // decode and crush the star field toward black.
            importer.sRGBTexture = false;
            importer.mipmapEnabled = true;
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.filterMode = FilterMode.Bilinear;
            // Uncompressed while the radiance values are still being tuned, so nothing gets
            // misread as a BC6H block artifact. Switch to CompressedHQ (BC6H) once locked.
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.maxTextureSize = width;
            importer.userData = DeepSpaceSkyTextureVersion;
            importer.SaveAndReimport();
        }

        return AssetDatabase.LoadAssetAtPath<Texture2D>(DeepSpaceSkyTexturePath);
    }

    private static void PaintDistantSun(
        Color[] pixels,
        int width,
        int height)
    {
        // Matches the scene's Key Light: the visible sun lies opposite the
        // direction in which the directional light rays travel.
        var lightRotation = Quaternion.Euler(31f, -32f, 0f);
        var sunDirection = -(lightRotation * Vector3.forward);
        var longitude = Mathf.Atan2(sunDirection.z, sunDirection.x);
        var latitude = Mathf.Asin(Mathf.Clamp(sunDirection.y, -1f, 1f));
        var centerX = (0.5f - longitude / (2f * Mathf.PI)) * width;
        var centerY = (0.5f + latitude / Mathf.PI) * height;
        var latitudeScale = Mathf.Max(0.1f, Mathf.Cos(latitude));
        const float diskRadiusDegrees = 0.72f;
        const float coronaRadiusDegrees = 3.2f;
        var coronaPixels = Mathf.CeilToInt(
            coronaRadiusDegrees / 180f * height);

        for (var offsetY = -coronaPixels; offsetY <= coronaPixels; offsetY++)
        {
            var y = Mathf.RoundToInt(centerY) + offsetY;
            if (y < 0 || y >= height)
            {
                continue;
            }

            for (var offsetX = -coronaPixels * 2;
                 offsetX <= coronaPixels * 2;
                 offsetX++)
            {
                var x = (Mathf.RoundToInt(centerX) + offsetX + width) % width;
                var longitudeDegrees =
                    offsetX / (float)width * 360f * latitudeScale;
                var latitudeDegrees =
                    offsetY / (float)height * 180f;
                var angularDistance = Mathf.Sqrt(
                    longitudeDegrees * longitudeDegrees +
                    latitudeDegrees * latitudeDegrees);
                if (angularDistance > coronaRadiusDegrees)
                {
                    continue;
                }

                var index = y * width + x;
                var existing = pixels[index];
                if (angularDistance <= diskRadiusDegrees)
                {
                    var edge = Mathf.SmoothStep(
                        1f,
                        0.82f,
                        angularDistance / diskRadiusDegrees);
                    // (255, 248, 220) is kept only as a chromaticity ratio now; the magnitude
                    // comes from SunDiskRadiance so the disk lands far above any bloom threshold.
                    var disk = new Color(1f, 0.9725f, 0.8627f) * (SunDiskRadiance * edge);
                    disk.a = 1f;
                    pixels[index] = disk;
                    continue;
                }

                var corona = 1f -
                    Mathf.InverseLerp(
                        diskRadiusDegrees,
                        coronaRadiusDegrees,
                        angularDistance);
                corona = corona * corona;
                // Deliberately far dimmer than the disk. In vacuum most of the glare a viewer
                // perceives is a camera artefact, so it belongs to Bloom rather than the texture.
                var halo = new Color(1f, 0.8039f, 0.4902f) * (SunCoronaRadiance * corona);
                pixels[index] = new Color(
                    existing.r + halo.r,
                    existing.g + halo.g,
                    existing.b + halo.b,
                    1f);
            }
        }
    }

    /// <summary>
    /// The star field and background were authored as 0-255 sRGB values against the old 8-bit
    /// panorama. The EXR stores linear radiance, so those values have to be decoded rather than
    /// copied, or the sky comes out far brighter than it was tuned to be.
    /// </summary>
    private static Color SrgbByteToLinear(float red, float green, float blue)
    {
        return new Color(
            Mathf.GammaToLinearSpace(Mathf.Clamp01(red / 255f)),
            Mathf.GammaToLinearSpace(Mathf.Clamp01(green / 255f)),
            Mathf.GammaToLinearSpace(Mathf.Clamp01(blue / 255f)),
            1f);
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
            weaponSystem.RadarController.enabled = false;
            EditorUtility.SetDirty(weaponSystem.RadarController);
        }

        Ensure<MechTestRadarEmitter>(root);
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
