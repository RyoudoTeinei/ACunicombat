using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ACunicombat.Mechs;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
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
    private const string PoseRoot = "Tools/LocalOut/actions";

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

    [InitializeOnLoadMethod]
    private static void RunRequestedBuildAfterReload()
    {
        var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return;
        }

        var requestPath = Path.Combine(projectRoot, "Temp", BuildRequestFileName);
        if (!File.Exists(requestPath))
        {
            return;
        }

        File.Delete(requestPath);
        EditorApplication.delayCall += () =>
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogError("AC6 playable scene build request was skipped because Unity is in Play Mode.");
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
        motion.ConfigureFlightEnvelope(250f, 2000f, 40f);
        motion.ConfigureQuickBoost(90f, 0.28f);
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
        cam.ConfigureFraming(20f, 6f, 8f);
        EditorUtility.SetDirty(cam);

        DisableShowcaseLabels(mech);
        BuildPlayerEnvironment();
        BuildNavigationReference();

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
        var envelopeGo = new GameObject("MotionSmoke_Envelope");
        var brakeGo = new GameObject("MotionSmoke_Brake");
        var airBrakeGo = new GameObject("MotionSmoke_AirBrake");
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

            var envelope = envelopeGo.AddComponent<MechFlightMotionController>();
            envelope.ConfigureModelForwardYawOffset(0f);
            envelope.ConfigureFlightEnvelope(20f, 200f, 5f);
            envelopeGo.transform.position = Vector3.zero;
            var fullThrottle = new MechPlayerInputFrame { MainThrottle = 1f };
            for (var i = 0; i < 600; i++)
            {
                envelope.Tick(in fullThrottle, dt);
            }

            var envelopeSpeed = envelope.HorizontalSpeed;
            var envelopeLimited = envelope.FlightEnvelopeLimiting;
            var overrideThrottle = new MechPlayerInputFrame
            {
                MainThrottle = 1f,
                FlightEnvelopeOverrideHeld = true,
            };
            for (var i = 0; i < 60; i++)
            {
                envelope.Tick(in overrideThrottle, dt);
            }

            var overrideSpeed = envelope.HorizontalSpeed;
            var envelopePass = envelopeSpeed >= 19f && envelopeSpeed <= 20.5f
                               && envelopeLimited
                               && overrideSpeed > envelopeSpeed + 20f;

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

            report = $"pitched-forward={(pitchedForwardPass ? "PASS" : "FAIL")} "
                     + $"(vy={climb.Velocity.y:F2}), "
                     + $"AB={(assaultBoostPass ? "PASS" : "FAIL")} "
                     + $"(speed={assaultBoost.Velocity.magnitude:F2}, vy={assaultBoost.Velocity.y:F2}), "
                     + $"QB-overspeed={(quickBoostPass ? "PASS" : "FAIL")} "
                     + $"({quickBoostFirstStepSpeed:F2}->{quickBoostSpeed:F2} m/s), "
                     + $"throttle-envelope={(envelopePass ? "PASS" : "FAIL")} "
                     + $"({envelopeSpeed:F2}->{overrideSpeed:F2} m/s), "
                     + $"ground-brake={(groundBrakePass ? "PASS" : "FAIL")} "
                     + $"({impulseSpeed:F2}->{stoppedSpeed:F2} m/s), "
                     + $"air-brake={(airBrakePass ? "PASS" : "FAIL")} "
                     + $"(horizontal={airBrakeSpeed:F3} m/s, altitude-error={airBrakeAltitudeError:F4} m)";
            return pitchedForwardPass && assaultBoostPass && quickBoostPass && envelopePass
                   && groundBrakePass && airBrakePass;
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(climbGo);
            UnityEngine.Object.DestroyImmediate(abGo);
            UnityEngine.Object.DestroyImmediate(qbGo);
            UnityEngine.Object.DestroyImmediate(envelopeGo);
            UnityEngine.Object.DestroyImmediate(brakeGo);
            UnityEngine.Object.DestroyImmediate(airBrakeGo);
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
