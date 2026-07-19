using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ACunicombat.Mechs;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 可玩场景一键构建（支持批处理）：编译行为图 → 为图内可用动作构建四部件 Clip 并
/// 入注册表 → 复用 showcase 构建器装配机体/灯光/特效 → 切外部控制模式 → 挂玩家
/// 控制 rig 与后置镜头 → 存为 AC6PlayerControl.unity → 无头冒烟验证图轨迹。
/// 批处理入口：-executeMethod AC6PlayableSceneBuilder.BuildAllBatch
/// </summary>
public static class AC6PlayableSceneBuilder
{
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
        BuildAll(out var report);
        EditorUtility.DisplayDialog("AC6 Playable Scene", report, "OK");
    }

    public static bool BuildAll(out string report)
    {
        var log = new List<string>();

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
                     "a000_028002", "a000_028003", "a000_004242", "a000_004243",
                     "a200_030020", "a200_030022", "a200_030030",
                     "a200_030040", "a200_030042", "a200_030050",
                 })
        {
            var upper = id.StartsWith("a2", StringComparison.Ordinal);
            wanted.TryAdd(id, (upper ? "UpperBody" : "FullBody", id.EndsWith("22") || id.EndsWith("42"), 0.1f));
        }

        var clipsBuilt = 0;
        var noPose = 0;
        foreach (var (actionId, meta) in wanted)
        {
            if (!File.Exists(Path.Combine(PoseRoot, actionId + "_pose.json")))
            {
                noPose++;
                continue;
            }

            if (FrameSlots.All(s =>
                    File.Exists($"{GeneratedActionsRoot}/{actionId}/AC6_{actionId}_{s.Token}.anim")))
            {
                continue;
            }

            var result = AC6ActionLibraryBuilder.BuildReviewAction(actionId, meta.layer, meta.loop);
            if (result.Contains("failed") || result.Contains("not found"))
            {
                log.Add($"clip build: {result}");
            }
            else
            {
                clipsBuilt++;
            }
        }

        log.Add($"clips: built={clipsBuilt}, no-pose={noPose}, wanted={wanted.Count}");

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

        // 5. 场景改装：外部控制模式 + 玩家 rig + 后置镜头。
        var scene = EditorSceneManager.OpenScene(ShowcaseScenePath, OpenSceneMode.Single);
        var controller = UnityEngine.Object.FindFirstObjectByType<AC6CombatShowcaseController>();
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
        var runner = Ensure<MechBehaviorGraphRunner>(mech);
        var weapon = Ensure<MechWeaponArmLayer>(mech);
        var rig = Ensure<MechPlayerRig>(mech);

        var runnerSo = new SerializedObject(runner);
        runnerSo.FindProperty("graph").objectReferenceValue = graph;
        runnerSo.ApplyModifiedPropertiesWithoutUndo();

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
        cam.Configure(mech.transform, motion);

        var rigSo = new SerializedObject(rig);
        rigSo.FindProperty("input").objectReferenceValue = input;
        rigSo.FindProperty("motion").objectReferenceValue = motion;
        rigSo.FindProperty("cam").objectReferenceValue = cam;
        rigSo.FindProperty("graphRunner").objectReferenceValue = runner;
        rigSo.FindProperty("weaponLayer").objectReferenceValue = weapon;
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

        var smoke = RunGraphSmokeTest(graph, out var trajectory);
        log.Add($"graph smoke: {(smoke ? "PASS" : "FAIL")}  trajectory: {trajectory}");

        report = string.Join("\n", log);
        return smoke;
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
}
