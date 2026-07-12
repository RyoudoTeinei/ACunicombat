using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ACunicombat.Mechs;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class AC6SkinnedModularSampleBuilder
{
    // Lets the open editor rebuild the generated validation assets after a domain reload.
    private const string ValidationRequestFileName = "RunAC6SkinnedModularValidation.request";
    private const string RawGltfRoot = "Assets/_Project/LocalSamples/AC6SkinnedModular/Raw/Gltf";
    private const string GeneratedRoot = "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated";
    private const string PartRoot = GeneratedRoot + "/Parts";
    private const string LoadoutRoot = GeneratedRoot + "/Loadouts";
    private const string SceneRoot = GeneratedRoot + "/Scenes";
    private const string ReportRoot = "Assets/_Project/LocalSamples/AC6SkinnedModular/Reports";
    private const string ValidationScenePath = SceneRoot + "/AC6SkinnedModularValidation.unity";
    private const string AnimationSourcePath =
        "Assets/_Project/LocalSamples/AC6MelanderCase/Raw/Gltf/Melander_1010_Rerun.glb";
    private const string NativeEquipmentRoot =
        "Assets/_Project/LocalSamples/AC6SkinnedModular/Raw/NativeEquipment";

    private static readonly Regex EquipmentActionPattern =
        new Regex(@"a\d{3}_\d+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly string[] FrameCodes = { "1010", "2010", "3010" };
    private static readonly string[] HeadCoreDrivenBones =
    {
        "Master", "Root", "Root_Rot_XYZ", "BD_Root", "Spine", "Offset_SpineLink",
        "SpineLink", "Offset_Spine1", "Spine1", "Neck"
    };
    private static readonly string[] ArmsCoreDrivenBones =
    {
        "Master", "Root", "Root_Rot_XYZ", "BD_Root", "Spine", "Offset_SpineLink",
        "SpineLink", "Offset_Spine1", "Spine1", "Offset_ClavicleLink", "L_ClavicleLink",
        "L_ClavicleLink_EX", "Offset_L_Clavicle", "L_Clavicle", "L_ArmJoint",
        "R_ClavicleLink", "Offset_R_Clavicle", "R_Clavicle", "R_ArmJoint", "R_ClavicleLink_EX"
    };
    private static readonly string[] CoreLegsDrivenBones =
    {
        "Master", "Root", "Root_Rot_XYZ", "BD_Root"
    };
    private static readonly FrameSlotSpec[] FrameSlots =
    {
        new FrameSlotSpec("HD", "Head", MechPartSlot.Head),
        new FrameSlotSpec("BD", "Core", MechPartSlot.Core),
        new FrameSlotSpec("AM", "Arms", MechPartSlot.Arms),
        new FrameSlotSpec("LG", "Legs", MechPartSlot.Legs)
    };

    [MenuItem("Tools/ACunicombat/AC6/Build Skinned Modular Sample Assets")]
    public static void BuildSampleAssetsMenu()
    {
        if (!TryBuildSampleAssets(out var result, out var error))
        {
            EditorUtility.DisplayDialog("AC6 Skinned Modular Sample", error, "OK");
            return;
        }

        EditorUtility.DisplayDialog(
            "AC6 Skinned Modular Sample",
            $"Built {result.FrameParts.Count} skinned frame parts and {result.Loadouts.Count} loadouts.\n\n" +
            $"Parts: {PartRoot}\nLoadouts: {LoadoutRoot}",
            "OK");
    }

    [MenuItem("Tools/ACunicombat/AC6/Build And Validate Skinned Modular Samples")]
    public static void BuildAndValidateMenu()
    {
        var report = BuildAndValidate(false);
        EditorUtility.DisplayDialog("AC6 Skinned Modular Validation", report.Summary, "OK");
    }

    public static void RunBatch()
    {
        var report = BuildAndValidate(true);
        if (Application.isBatchMode)
        {
            EditorApplication.Exit(report.Failures == 0 ? 0 : 1);
        }
    }

    [InitializeOnLoadMethod]
    private static void RunRequestedValidationAfterReload()
    {
        var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return;
        }

        var requestPath = Path.Combine(projectRoot, "Temp", ValidationRequestFileName);
        if (!File.Exists(requestPath))
        {
            return;
        }

        File.Delete(requestPath);
        EditorApplication.delayCall += () =>
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogError("AC6 skinned modular validation request was skipped because Unity is in Play Mode.");
                return;
            }

            var report = BuildAndValidate(false);
            Debug.Log(report.Summary);
        };
    }

    private static ValidationReport BuildAndValidate(bool batchMode)
    {
        var report = new ValidationReport();
        try
        {
            if (!TryBuildSampleAssets(out var result, out var error))
            {
                report.Failures++;
                report.Log.AppendLine("BUILD FAIL: " + error);
                report.Finish();
                WriteReport(report);
                return report;
            }

            BuildValidationSceneAndRunChecks(result, report, batchMode);
        }
        catch (Exception ex)
        {
            report.Failures++;
            report.Log.AppendLine("EXCEPTION: " + ex);
        }

        report.Finish();
        WriteReport(report);
        Debug.Log("[AC6SkinnedModularValidation]\n" + report.Log);
        return report;
    }

    private static bool TryBuildSampleAssets(out BuildResult result, out string error)
    {
        result = null;
        error = string.Empty;

        EnsureAssetFolder(GeneratedRoot);
        EnsureAssetFolder(PartRoot);
        EnsureAssetFolder(LoadoutRoot);
        EnsureAssetFolder(SceneRoot);
        Directory.CreateDirectory(ReportRoot);
        AssetDatabase.Refresh();

        AssetDatabase.ImportAsset(AnimationSourcePath, ImportAssetOptions.ForceUpdate);
        var animationClip = AssetDatabase.LoadAllAssetsAtPath(AnimationSourcePath)
            .OfType<AnimationClip>()
            .Where(candidate => candidate != null && candidate.length > 0.01f)
            .OrderByDescending(candidate => candidate.length)
            .FirstOrDefault();
        if (animationClip == null)
        {
            error = $"AnimationClip was not found in:\n{AnimationSourcePath}\n\nRun the Melander GLB pipeline first.";
            return false;
        }

        var frameParts = new Dictionary<string, MechPartDefinition>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        for (var codeIndex = 0; codeIndex < FrameCodes.Length; codeIndex++)
        {
            var code = FrameCodes[codeIndex];
            for (var slotIndex = 0; slotIndex < FrameSlots.Length; slotIndex++)
            {
                var slot = FrameSlots[slotIndex];
                var glbPath = $"{RawGltfRoot}/{slot.Token}_M_{code}.glb";
                AssetDatabase.ImportAsset(glbPath, ImportAssetOptions.ForceUpdate);
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(glbPath);
                var partClip = AssetDatabase.LoadAllAssetsAtPath(glbPath)
                    .OfType<AnimationClip>()
                    .Where(candidate => candidate != null && candidate.length > 0.01f)
                    .OrderByDescending(candidate => candidate.length)
                    .FirstOrDefault();
                if (model == null || partClip == null)
                {
                    missing.Add(glbPath + (model == null ? " (model missing)" : " (animation clip missing)"));
                    continue;
                }

                var id = $"AC6_SKIN_{slot.Token}_M_{code}";
                var assetPath = $"{PartRoot}/{id}.asset";
                var part = LoadOrCreateAsset<MechPartDefinition>(assetPath);
                part.EditorConfigure(
                    id,
                    $"AC6 {SetDisplayName(code)} {slot.DisplayName}",
                    slot.Slot,
                    MechPartCategory.Frame,
                    model,
                    BuildFrameStats(slot.Slot, MassScale(code)),
                    BuildFrameMountPoints(slot.Slot),
                    new[] { "frame", "ac6-skinned", $"set-{code}", slot.Token.ToLowerInvariant() },
                    null,
                    MechVisualRigMode.SynchronizedSkinned,
                    partClip,
                    AncestorDriverForSlot(slot.Slot),
                    AncestorDrivenBonesForSlot(slot.Slot));
                frameParts[FrameKey(slot.Slot, code)] = part;
            }
        }

        if (missing.Count > 0)
        {
            error = "The following modular GLBs are missing or failed to import:\n" + string.Join("\n", missing);
            return false;
        }

        var internals = BuildInternalParts();
        var boosters = BuildBoosterParts(missing);
        var weapons = LoadWeaponParts(missing);
        if (missing.Count > 0)
        {
            error = "The following equipment assets are missing:\n" + string.Join("\n", missing);
            return false;
        }

        var loadouts = new Dictionary<string, MechLoadoutDefinition>(StringComparer.OrdinalIgnoreCase);
        CreateLoadout(loadouts, "Uniform1010", "Melander 1010", frameParts, internals, boosters, weapons, "1010", "1010", "1010", "1010");
        CreateLoadout(loadouts, "Uniform2010", "Nacht 2010", frameParts, internals, boosters, weapons, "2010", "2010", "2010", "2010");
        CreateLoadout(loadouts, "Uniform3010", "Tian-Qiang 3010", frameParts, internals, boosters, weapons, "3010", "3010", "3010", "3010");
        // Mixed loadouts take the left-hand bazooka so the left palm dummy (gun, dmy_23) is
        // verified too; the uniform loadouts keep melee on the left forearm (dmy_25).
        CreateLoadout(loadouts, "MixedA", "Mixed A 2010/1010/3010/2010", frameParts, internals, boosters, weapons, "2010", "1010", "3010", "2010", leftHandWeaponKey: "WP_L_1410");
        CreateLoadout(loadouts, "MixedB", "Mixed B 3010/2010/1010/3010", frameParts, internals, boosters, weapons, "3010", "2010", "1010", "3010", leftHandWeaponKey: "WP_L_1410");

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        result = new BuildResult(animationClip, frameParts, loadouts);
        return true;
    }

    private static void BuildValidationSceneAndRunChecks(BuildResult result, ValidationReport report, bool batchMode)
    {
        var animationAssetPath = AssetDatabase.GetAssetPath(result.AnimationClip);
        var animationClipName = result.AnimationClip.name;
        var loadoutPaths = result.Loadouts
            .Select(pair => new KeyValuePair<string, string>(pair.Key, AssetDatabase.GetAssetPath(pair.Value)))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToList();

        var previousActiveScene = SceneManager.GetActiveScene();
        Scene validationScene;
        var isOpenValidationScene = !batchMode &&
            string.Equals(previousActiveScene.path, ValidationScenePath, StringComparison.OrdinalIgnoreCase);
        var isPreviewScene = !batchMode && !isOpenValidationScene;
        if (batchMode)
        {
            if (string.IsNullOrWhiteSpace(previousActiveScene.path))
            {
                EditorSceneManager.SaveScene(previousActiveScene, SceneRoot + "/BatchBootstrap.unity");
            }

            validationScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            SceneManager.SetActiveScene(validationScene);
        }
        else if (isOpenValidationScene)
        {
            validationScene = previousActiveScene;
            var oldRoots = validationScene.GetRootGameObjects();
            for (var i = 0; i < oldRoots.Length; i++)
            {
                UnityEngine.Object.DestroyImmediate(oldRoots[i]);
            }
        }
        else
        {
            validationScene = EditorSceneManager.NewPreviewScene();
        }

        var validationRoot = new GameObject("AC6_Skinned_Modular_Validation");
        if (isPreviewScene)
        {
            SceneManager.MoveGameObjectToScene(validationRoot, validationScene);
        }
        var mechs = new List<GameObject>();
        var animationClip = AssetDatabase.LoadAllAssetsAtPath(animationAssetPath)
            .OfType<AnimationClip>()
            .FirstOrDefault(candidate => candidate.name == animationClipName);
        if (animationClip == null)
        {
            throw new InvalidOperationException($"Animation clip '{animationClipName}' could not be reloaded from {animationAssetPath}.");
        }

        for (var i = 0; i < loadoutPaths.Count; i++)
        {
            var pair = loadoutPaths[i];
            var loadout = AssetDatabase.LoadAssetAtPath<MechLoadoutDefinition>(pair.Value);
            if (loadout == null)
            {
                throw new InvalidOperationException($"Loadout '{pair.Key}' could not be reloaded from {pair.Value}.");
            }

            var mech = new GameObject($"Validation_{pair.Key}");
            mech.transform.SetParent(validationRoot.transform, false);
            mech.transform.position = new Vector3((i - (loadoutPaths.Count - 1) * 0.5f) * 14f, 0f, 0f);

            var assembler = mech.AddComponent<MechAssembler>();
            assembler.Loadout = loadout;
            assembler.EditorConfigureSynchronizedSkinnedParts(animationClip, true, false, true, 1f);
            assembler.Assemble();

            var label = new GameObject("Label");
            label.transform.SetParent(mech.transform, false);
            label.transform.localPosition = new Vector3(0f, 7.2f, 0f);
            var text = label.AddComponent<TextMesh>();
            text.text = loadout.DisplayName;
            text.anchor = TextAnchor.MiddleCenter;
            text.alignment = TextAlignment.Center;
            text.characterSize = 0.25f;
            text.fontSize = 36;

            mechs.Add(mech);
            ValidateMech(pair.Key, mech, animationClip, report);
        }

        CreateSceneLighting(validationRoot.transform, mechs);
        if (batchMode || isOpenValidationScene)
        {
            // Persist an inspection scene in a coherent mounted rest state. Validation samples
            // equipment actions independently, so saving immediately after the checks can leave
            // a booster gimbal or melee attack at an unrelated mid-action pose.
            for (var i = 0; i < mechs.Count; i++)
            {
                SampleValidationPose(mechs[i], animationClip, 0f);
            }

            EnsureAssetFolder(SceneRoot);
            EditorSceneManager.SaveScene(validationScene, ValidationScenePath);
        }

        for (var i = 0; i < mechs.Count; i++)
        {
            for (var j = 0; j < mechs.Count; j++)
            {
                mechs[j].SetActive(i == j);
            }

            var label = mechs[i].transform.Find("Label");
            if (label != null)
            {
                label.gameObject.SetActive(false);
            }

            var bounds = CombinedBounds(mechs[i]);
            SampleValidationPose(mechs[i], animationClip, 0f);
            bounds = CombinedBounds(mechs[i]);
            Screenshot(mechs[i], bounds, Path.Combine(ReportRoot, mechs[i].name + "_rest.png"));

            SampleValidationPose(mechs[i], animationClip, 0.5f);
            bounds = CombinedBounds(mechs[i]);
            Screenshot(mechs[i], bounds, Path.Combine(ReportRoot, mechs[i].name + "_action.png"));

            if (label != null)
            {
                label.gameObject.SetActive(true);
            }
        }

        for (var i = 0; i < mechs.Count; i++)
        {
            mechs[i].SetActive(true);
        }

        if (isPreviewScene && previousActiveScene.IsValid() && previousActiveScene.isLoaded)
        {
            SceneManager.SetActiveScene(previousActiveScene);
        }

        if (isPreviewScene)
        {
            EditorSceneManager.ClosePreviewScene(validationScene);
        }
    }

    private static void ValidateMech(string key, GameObject mech, AnimationClip clip, ValidationReport report)
    {
        var runtime = mech.GetComponent<MechRuntimeState>();
        var player = mech.GetComponent<MechSynchronizedAnimationPlayer>();
        var frameRecords = runtime == null
            ? new List<MechAssemblyRecord>()
            : runtime.AssembledParts.Where(record => record?.part != null && record.part.UsesSynchronizedSkinnedRig).ToList();
        var equipmentRecords = runtime == null
            ? new List<MechAssemblyRecord>()
            : runtime.AssembledParts.Where(record => record?.part != null &&
                (record.slot == MechPartSlot.Booster || MechSlotRules.IsWeaponSlot(record.slot))).ToList();
        var weaponRecords = equipmentRecords.Where(record => MechSlotRules.IsWeaponSlot(record.slot)).ToList();
        var boosterRecord = equipmentRecords.FirstOrDefault(record => record.slot == MechPartSlot.Booster);
        var coreRecord = frameRecords.FirstOrDefault(record => record.slot == MechPartSlot.Core);

        var ok = true;
        ok &= Check(frameRecords.Count == 4, key, $"expected 4 skinned frame records, got {frameRecords.Count}", report);
        ok &= Check(runtime != null && runtime.LoadoutValid, key, "assembled equipment loadout is invalid", report);
        ok &= Check(boosterRecord?.part?.ModelPrefab != null, key, "visible booster model is missing", report);
        ok &= Check(weaponRecords.Count == 4, key, $"expected 4 mounted weapons, got {weaponRecords.Count}", report);

        var boosterMounts = runtime == null
            ? new List<MechMountPoint>()
            : runtime.MountPoints.Where(mount => mount != null && mount.TargetSlot == MechPartSlot.Booster).ToList();
        ok &= Check(boosterMounts.Count == 2, key, $"expected 2 rear booster mounts, got {boosterMounts.Count}", report);
        for (var i = 0; i < boosterMounts.Count; i++)
        {
            var source = boosterMounts[i].transform.parent;
            ok &= Check(source != null && source.name.IndexOf("_dmy_800", StringComparison.OrdinalIgnoreCase) >= 0,
                key, $"booster mount {i} is not attached to a _dmy_800 node", report);
            var sourceLocalZ = source != null && coreRecord?.instance != null
                ? coreRecord.instance.transform.InverseTransformPoint(source.position).z
                : float.MinValue;
            ok &= Check(sourceLocalZ > 0.001f, key,
                $"booster mount {i} uses a front/effect-only ref 800 at local z={sourceLocalZ:0.###}", report);
        }

        var boosterVisuals = mech.GetComponentsInChildren<Transform>(true)
            .Where(candidate => candidate != null && candidate.name.StartsWith("Booster_", StringComparison.Ordinal) &&
                candidate.GetComponentInChildren<Renderer>(true) != null)
            .ToList();
        ok &= Check(boosterVisuals.Count == 2, key, $"expected 2 visible rear booster instances, got {boosterVisuals.Count}", report);
        // The wrapper install rotation is IDENTITY by design: the resting gimbal pose comes
        // from sampling the native idle action on the internal animation targets at assembly.
        // What gets asserted is the FINAL nozzle geometry axis (exhaustDir/pitch below), which
        // reflects every layer — never wrapper Euler values, which a playing action would
        // double-apply and which said nothing about the mesh in the first place.
        ok &= Check(boosterRecord?.part != null && boosterRecord.part.LocalEulerAngles == Vector3.zero, key,
            "booster wrapper install rotation must stay identity (pose comes from the sampled idle action)", report);
        for (var i = 0; i < boosterVisuals.Count; i++)
        {
            // Semantic seat check: core ref 800 is the flame emission point, and the pod is
            // installed so its own central flame dummy (_dmy_31) sits exactly on it. The root
            // therefore carries the idle-posed -p31 install offset; what must be zero is the
            // distance between the pod's flame anchor and the socket. The wrapper rotation is
            // identity by design — the resting pose lives in the sampled idle action.
            var visual = boosterVisuals[i];
            var socket = visual.parent != null ? visual.parent.parent : null;
            var flame = FindChildBySuffix(visual, "_dmy_31");
            var rotationError = Quaternion.Angle(visual.localRotation, Quaternion.identity);
            var scaleError = (visual.localScale - Vector3.one).magnitude;
            var flameError = flame != null && socket != null
                ? Vector3.Distance(flame.position, socket.position)
                : float.MaxValue;
            report.Log.AppendLine(
                $"[{key}] booster {i}: flameToSocket={flameError:0.####}m rootOffset={Format(visual.localPosition)} drot={rotationError:0.###}deg dscale={scaleError:0.####}");
            ok &= Check(flame != null, key, $"booster visual {i} has no _dmy_31 flame anchor", report);
            ok &= Check(flameError < 0.005f, key, $"booster visual {i} flame anchor is {flameError:0.####}m away from its ref 800 socket", report);
            ok &= Check(scaleError < 0.0001f, key, $"booster visual {i} root scale error is {scaleError:0.####}", report);

            // FINAL geometry axis, not wrapper numerics: the flame dummy rides the same bone
            // that skins the bell, so its world frame IS the nozzle axis after every layer
            // (install transform, sampled idle pose, playing actions) has been applied.
            // Exhaust must point backward and never upward; exact pitch is logged so it can be
            // compared against in-game reference footage.
            if (flame != null)
            {
                var exhaustDir = (-flame.forward).normalized;
                var backDot = Vector3.Dot(exhaustDir, mech.transform.forward); // mech faces -Z; +Z is backward
                var pitchDeg = Mathf.Asin(Mathf.Clamp(exhaustDir.y, -1f, 1f)) * Mathf.Rad2Deg;
                report.Log.AppendLine(
                    $"[{key}] booster {i}: exhaustDir={Format(exhaustDir)} backDot={backDot:0.###} pitchDeg={pitchDeg:0.##}");
                ok &= Check(backDot > 0.6f, key,
                    $"booster visual {i} exhaust axis is not pointing backward (backDot {backDot:0.###})", report);
                ok &= Check(exhaustDir.y <= 0.1f, key,
                    $"booster visual {i} exhaust axis points upward (y {exhaustDir.y:0.###})", report);
            }
        }
        for (var i = 0; i < weaponRecords.Count; i++)
        {
            var record = weaponRecords[i];
            var mountPoint = record.parentMount != null ? record.parentMount.GetComponent<MechMountPoint>() : null;
            var mountSource = record.parentMount != null ? record.parentMount.parent : null;
            var expectedMountSuffix = ExpectedEquipmentMountSuffix(record.slot, record.part);
            var modelPath = record.part == null ? string.Empty : AssetDatabase.GetAssetPath(record.part.ModelPrefab);
            ok &= Check(record.instance != null && record.instance.GetComponentInChildren<Renderer>(true) != null,
                key, $"{record.slot} has no visible renderer", report);
            ok &= Check(mountPoint != null && mountPoint.TargetSlot == record.slot,
                key, $"{record.slot} is not attached to its animated hardpoint", report);
            ok &= Check(mountSource != null && mountSource.name.EndsWith(expectedMountSuffix, StringComparison.OrdinalIgnoreCase),
                key, $"{record.slot} expected mount {expectedMountSuffix}, got {mountSource?.name ?? "<none>"}", report);
            ok &= Check(modelPath.StartsWith(NativeEquipmentRoot, StringComparison.OrdinalIgnoreCase),
                key, $"{record.slot} still references a legacy or Blender-baked asset: {modelPath}", report);

            // Grip contact: hand-mounted weapons must actually sit in the hand — the palm
            // (or forearm) dummy has to touch the weapon's surface. Guards against a weapon
            // that follows its dummy but hovers away from the grip.
            if (record.slot == MechPartSlot.LeftHandWeapon || record.slot == MechPartSlot.RightHandWeapon)
            {
                var gripDistance = mountSource != null && record.instance != null
                    ? MinDistanceToWeaponSurface(record.instance, mountSource.position)
                    : float.MaxValue;
                report.Log.AppendLine($"[{key}] {record.slot} gripDistance={gripDistance:0.####}m");
                ok &= Check(gripDistance < 0.15f, key,
                    $"{record.slot} grip point is {gripDistance:0.###}m away from the weapon surface", report);
            }

            var expectedActionCount = ExpectedNativeActionCount(record.part.PartId);
            ok &= Check(expectedActionCount < 0 || record.part.EquipmentAnimations.Count == expectedActionCount,
                key, $"{record.slot} expected {expectedActionCount} native actions, got {record.part.EquipmentAnimations.Count}", report);
            var equipmentPlayer = record.instance.GetComponent<MechEquipmentAnimationPlayer>();
            ok &= Check(record.part.EquipmentAnimations.Count == 0 ? equipmentPlayer == null : equipmentPlayer != null,
                key, $"{record.slot} independent equipment animation player state is incorrect", report);
            if (record.part.HasTag("melee"))
            {
                // Machines without the game's partsbnd archives build equipment with zero
                // actions; the idle-id assertion only applies once actions exist.
                ok &= Check(record.part.EquipmentAnimations.Count == 0
                        || string.Equals(record.part.DefaultEquipmentActionId, "a000_000000", StringComparison.OrdinalIgnoreCase),
                    key, $"{record.slot} melee default must be native idle a000_000000, got {record.part.DefaultEquipmentActionId}", report);
                ok &= Check(!record.part.PlayDefaultEquipmentActionOnAssemble,
                    key, $"{record.slot} melee attack must not autoplay on assembly", report);
            }
        }

        var boosterModelPath = boosterRecord?.part == null
            ? string.Empty
            : AssetDatabase.GetAssetPath(boosterRecord.part.ModelPrefab);
        ok &= Check(boosterModelPath.StartsWith(NativeEquipmentRoot, StringComparison.OrdinalIgnoreCase),
            key, $"booster still references a legacy or Blender-baked asset: {boosterModelPath}", report);
        var expectedBoosterActions = ExpectedNativeActionCount(boosterRecord?.part?.PartId);
        ok &= Check(expectedBoosterActions < 0 || boosterRecord?.part?.EquipmentAnimations.Count == expectedBoosterActions,
            key, $"booster expected {expectedBoosterActions} native actions, got {boosterRecord?.part?.EquipmentAnimations.Count ?? -1}", report);
        ok &= Check(boosterRecord?.part != null && (boosterRecord.part.EquipmentAnimations.Count == 0
                || string.Equals(boosterRecord.part.DefaultEquipmentActionId, "a000_000000", StringComparison.OrdinalIgnoreCase)),
            key, $"booster default must be native idle a000_000000, got {boosterRecord?.part?.DefaultEquipmentActionId ?? "<none>"}", report);
        ok &= Check(boosterRecord?.part != null && !boosterRecord.part.PlayDefaultEquipmentActionOnAssemble,
            key, "booster thrust/gimbal action must not autoplay on assembly", report);

        var expectedPlayers = weaponRecords.Count(record => record.part.EquipmentAnimations.Count > 0)
            + (boosterRecord != null && boosterRecord.part.EquipmentAnimations.Count > 0 ? boosterVisuals.Count : 0);
        var equipmentPlayers = mech.GetComponentsInChildren<MechEquipmentAnimationPlayer>(true);
        ok &= Check(equipmentPlayers.Length == expectedPlayers,
            key, $"expected {expectedPlayers} independent native equipment players, got {equipmentPlayers.Length}", report);
        for (var i = 0; i < equipmentPlayers.Length; i++)
        {
            var equipmentPlayer = equipmentPlayers[i];
            ok &= Check(equipmentPlayer.Actions.Count > 0,
                key, $"{equipmentPlayer.name} has an empty native action list", report);
            if (equipmentPlayer.Actions.Count > 0)
            {
                var action = equipmentPlayer.Actions.FirstOrDefault(candidate =>
                    string.Equals(candidate.ActionId, equipmentPlayer.DefaultActionId, StringComparison.OrdinalIgnoreCase))
                    ?? equipmentPlayer.Actions[0];
                ok &= Check(equipmentPlayer.SampleActionAtTime(action.ActionId, action.Clip.length * 0.5f),
                    key, $"{equipmentPlayer.name} failed to sample native action {action.ActionId}", report);
            }
        }

        ok &= Check(player != null, key, "synchronized animation player is missing", report);
        if (player != null)
        {
            ok &= Check(player.PartAnimators.Count == 4, key, $"expected 4 synchronized Animators, got {player.PartAnimators.Count}", report);
            ok &= Check(player.PartClips.Count == 4, key, $"expected 4 part-specific clips, got {player.PartClips.Count}", report);
            ok &= Check(player.SharedBoneBindingCount == 34, key, $"expected exactly 34 interface-driven shared bone links, got {player.SharedBoneBindingCount}", report);
        }

        var skeletonRoots = new HashSet<Transform>();
        for (var i = 0; i < frameRecords.Count; i++)
        {
            var record = frameRecords[i];
            var renderers = record.instance.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(renderer =>
                {
                    var owner = renderer.GetComponentInParent<MechAssembledPart>();
                    var equipmentMount = renderer.GetComponentInParent<MechMountPoint>();
                    return equipmentMount == null && owner != null && owner.gameObject == record.instance;
                })
                .ToArray();
            ok &= Check(renderers.Length > 0, key, $"{record.slot} has no SkinnedMeshRenderer", report);
            for (var r = 0; r < renderers.Length; r++)
            {
                var renderer = renderers[r];
                ok &= Check(renderer.bones.Length > 0, key, $"{record.slot}/{renderer.name} has no bones", report);
                ok &= Check(renderer.bones.All(bone => bone != null), key, $"{record.slot}/{renderer.name} has null bone references", report);
                if (renderer.rootBone != null)
                {
                    skeletonRoots.Add(renderer.rootBone);
                }
            }
        }
        ok &= Check(skeletonRoots.Count == 4, key, $"expected 4 independent part skeleton roots, got {skeletonRoots.Count}", report);

        // --- Bind-pose golden socket positions (Uniform1010 only; values measured from the
        // FLVER dummies with AC6PartToGltf --dump-dummies, X negated for glTFast's RH->LH
        // conversion). The assembled mech is already animated by the time we get here, so the
        // goldens are checked on fresh, un-animated instances of the raw part GLBs — that is
        // the state the FLVER dump values describe. Catches both "socket in wrong space"
        // failure modes seen earlier.
        if (string.Equals(key, "Uniform1010", StringComparison.OrdinalIgnoreCase))
        {
            ok &= WithRawAssetInstance($"{RawGltfRoot}/AM_M_1010.glb", key, report, instance =>
            {
                var pass = CheckGoldenSocket(instance, "_dmy_20", new Vector3(2.271f, 7.906f, -1.971f), key, report);
                pass &= CheckGoldenSocket(instance, "_dmy_23", new Vector3(-2.270f, 7.904f, -1.974f), key, report);
                pass &= CheckGoldenSocket(instance, "_dmy_25", new Vector3(-2.895f, 7.655f, -0.746f), key, report);
                return pass;
            });
            ok &= WithRawAssetInstance($"{RawGltfRoot}/BD_M_1010.glb", key, report, instance =>
            {
                var pass = CheckGoldenSocket(instance, "_dmy_13", new Vector3(-1.458f, 10.013f, 1.856f), key, report);
                pass &= CheckGoldenSocket(instance, "_dmy_14", new Vector3(1.458f, 10.013f, 1.856f), key, report);
                pass &= CheckGoldenSocketSet(instance, "_dmy_800", new[]
                {
                    new Vector3(0f, 8.622f, -2.225f),
                    new Vector3(0.413f, 8.944f, 2.402f),
                    new Vector3(-0.413f, 8.944f, 2.402f),
                }, key, report);
                return pass;
            });
        }

        // Per-core golden sockets: each core carries its own back-weapon and nozzle layout
        // (verified from the FLVER dummies; 2010/3010 back mounts even ride core-local AP
        // mechanism bones), so every core gets its own bind-pose numbers.
        if (string.Equals(key, "Uniform2010", StringComparison.OrdinalIgnoreCase))
        {
            ok &= WithRawAssetInstance($"{RawGltfRoot}/BD_M_2010.glb", key, report, instance =>
            {
                var pass = CheckGoldenSocket(instance, "_dmy_13", new Vector3(-1.525f, 10.309f, 1.938f), key, report);
                pass &= CheckGoldenSocket(instance, "_dmy_14", new Vector3(1.525f, 10.309f, 1.938f), key, report);
                pass &= CheckGoldenSocketSet(instance, "_dmy_800", new[]
                {
                    new Vector3(0f, 8.763f, -2.225f),
                    new Vector3(0.459f, 9.155f, 2.469f),
                    new Vector3(-0.459f, 9.155f, 2.469f),
                }, key, report);
                return pass;
            });
        }

        if (string.Equals(key, "Uniform3010", StringComparison.OrdinalIgnoreCase))
        {
            ok &= WithRawAssetInstance($"{RawGltfRoot}/BD_M_3010.glb", key, report, instance =>
            {
                var pass = CheckGoldenSocket(instance, "_dmy_13", new Vector3(-1.180f, 10.747f, 1.798f), key, report);
                pass &= CheckGoldenSocket(instance, "_dmy_14", new Vector3(1.180f, 10.747f, 1.798f), key, report);
                pass &= CheckGoldenSocketSet(instance, "_dmy_800", new[]
                {
                    new Vector3(0f, 9.081f, -2.225f),
                    new Vector3(0.459f, 9.472f, 2.469f),
                    new Vector3(-0.459f, 9.472f, 2.469f),
                }, key, report);
                return pass;
            });
        }

        // --- Installation transform must be invariant: equipment root relative to its animated
        // dummy source may never change, at rest or mid-action.
        var installBaselines = new Dictionary<MechAssemblyRecord, Matrix4x4>();
        foreach (var record in equipmentRecords)
        {
            if (record.instance == null || record.parentMount == null || record.parentMount.parent == null)
                continue;
            installBaselines[record] = record.parentMount.parent.worldToLocalMatrix * record.instance.transform.localToWorldMatrix;
        }

        var sampleTimes = new[] { 0f, clip.length * 0.5f };
        for (var i = 0; i < sampleTimes.Length; i++)
        {
            player?.EvaluatePlayableGraphAtTime(sampleTimes[i]);
            if (player != null)
            {
                player.GetSharedBonePoseErrors(out var positionError, out var rotationError, out var scaleError);
                report.Log.AppendLine(
                    $"[{key}] t={sampleTimes[i]:0.###} shared ancestors={player.SharedBoneBindingCount} " +
                    $"posError={positionError:0.######}m rotError={rotationError:0.######}deg scaleError={scaleError:0.######}");
                ok &= Check(positionError < 0.0001f, key, $"shared ancestor position mismatch at t={sampleTimes[i]:0.###}: {positionError:0.######}m", report);
                ok &= Check(rotationError < 0.001f, key, $"shared ancestor rotation mismatch at t={sampleTimes[i]:0.###}: {rotationError:0.######}deg", report);
                ok &= Check(scaleError < 0.0001f, key, $"shared ancestor scale mismatch at t={sampleTimes[i]:0.###}: {scaleError:0.######}", report);
            }

            var bounds = CombinedBounds(mech);
            var magnitude = bounds.size.magnitude;
            report.Log.AppendLine($"[{key}] t={sampleTimes[i]:0.###} bounds center={Format(bounds.center)} size={Format(bounds.size)}");
            ok &= Check(!float.IsNaN(magnitude) && magnitude > 2f && magnitude < 30f, key, $"invalid combined bounds at t={sampleTimes[i]:0.###}: {magnitude}", report);

            for (var r = 0; r < frameRecords.Count; r++)
            {
                var partBounds = CombinedBounds(frameRecords[r].instance);
                var distance = Vector3.Distance(partBounds.center, bounds.center);
                ok &= Check(distance < 12f, key, $"{frameRecords[r].slot} drifted {distance:0.###}m from assembly center", report);
            }

            // Equipment must hug the frame: measure against the frame-only bounds (closest-point
            // distance), not the combined bounds that the misplaced equipment itself inflates.
            var frameBounds = CombinedBounds(frameRecords[0].instance);
            for (var r = 1; r < frameRecords.Count; r++)
                frameBounds.Encapsulate(CombinedBounds(frameRecords[r].instance));
            for (var r = 0; r < equipmentRecords.Count; r++)
            {
                var equipmentBounds = CombinedBounds(equipmentRecords[r].instance);
                var distance = Mathf.Sqrt(frameBounds.SqrDistance(equipmentBounds.center));
                report.Log.AppendLine($"[{key}] t={sampleTimes[i]:0.###} {equipmentRecords[r].slot} center={Format(equipmentBounds.center)} distToFrame={distance:0.###}m");
                ok &= Check(distance < 4f, key, $"{equipmentRecords[r].slot} floats {distance:0.###}m away from the frame bounds", report);
            }

            foreach (var pair in installBaselines)
            {
                var record = pair.Key;
                if (record.instance == null || record.parentMount == null || record.parentMount.parent == null)
                    continue;
                var current = record.parentMount.parent.worldToLocalMatrix * record.instance.transform.localToWorldMatrix;
                var positionDrift = ((Vector3)current.GetColumn(3) - (Vector3)pair.Value.GetColumn(3)).magnitude;
                var rotationDrift = Quaternion.Angle(current.rotation, pair.Value.rotation);
                ok &= Check(positionDrift < 0.001f && rotationDrift < 0.05f, key,
                    $"{record.slot} installation transform drifted at t={sampleTimes[i]:0.###} (pos {positionDrift:0.####}m, rot {rotationDrift:0.###}deg)", report);
            }

            // Muzzle-forward semantic check plus per-equipment diagnostics and close-up
            // screenshots (the far overview shots hid a reversed weapon once already).
            for (var r = 0; r < weaponRecords.Count; r++)
            {
                var record = weaponRecords[r];
                if (record.instance == null)
                    continue;

                var mountSource = record.parentMount != null ? record.parentMount.parent : null;
                var rootLocal = mech.transform.InverseTransformPoint(record.instance.transform.position);
                var isMelee = record.part.HasTag("melee");
                var muzzle = FindMuzzleTransform(record.instance.transform);
                report.Log.AppendLine(
                    $"[{key}] t={sampleTimes[i]:0.###} DIAG {record.slot} part={record.part.PartId} " +
                    $"mount={(mountSource != null ? GetTransformPath(mountSource, mech.transform) : "<none>")} " +
                    $"rootLocal={Format(rootLocal)} melee={isMelee} muzzle={(muzzle != null ? muzzle.name : "<none>")}");

                if (muzzle != null)
                {
                    // The muzzle dummy's own frame encodes the firing direction (FLVER forward
                    // = the frame's -Z, preserved through glTF). Positions are useless here:
                    // e.g. WP_L_0010's dmy_10 sits at the receiver, not the barrel tip.
                    var muzzleLocal = mech.transform.InverseTransformPoint(muzzle.position);
                    var muzzleDir = -muzzle.forward;
                    var mechForward = -mech.transform.forward;
                    var forwardness = Vector3.Dot(muzzleDir.normalized, mechForward.normalized);
                    report.Log.AppendLine($"[{key}] t={sampleTimes[i]:0.###} DIAG {record.slot} muzzleLocal={Format(muzzleLocal)} muzzleDir={Format(muzzleDir)} forwardness={forwardness:0.###}");
                    // Only asserted at rest: mid-action the arm may legitimately re-aim the gun.
                    if (!isMelee && i == 0)
                    {
                        // A reversed weapon scores around -1, sideways around 0; the bind pose
                        // may legitimately droop the barrel, so only require "clearly forward".
                        ok &= Check(forwardness > 0.1f, key,
                            $"{record.slot} muzzle points away from mech forward at rest (dot {forwardness:0.###}, muzzle {muzzle.name})", report);
                    }
                }
                else if (!isMelee && i == 0)
                {
                    ok &= Check(false, key, $"{record.slot} has no muzzle dummy to verify firing direction", report);
                }

                Screenshot(mech, GrowBounds(CombinedBounds(record.instance), 1.35f),
                    Path.Combine(ReportRoot, $"Closeup_{key}_{record.slot}_t{sampleTimes[i]:000}.png"));
            }

            if (boosterVisuals.Count > 0)
            {
                var boosterBounds = CombinedBounds(boosterVisuals[0].gameObject);
                for (var b = 1; b < boosterVisuals.Count; b++)
                    boosterBounds.Encapsulate(CombinedBounds(boosterVisuals[b].gameObject));
                // Boosters sit on the core's rear: shoot from behind-right so the torso does
                // not stand between the camera and the nozzles.
                Screenshot(mech, GrowBounds(boosterBounds, 1.6f),
                    Path.Combine(ReportRoot, $"Closeup_{key}_Boosters_t{sampleTimes[i]:000}.png"),
                    new Vector3(0.55f, 0.3f, 1f));
                Screenshot(mech, GrowBounds(boosterBounds, 1.6f),
                    Path.Combine(ReportRoot, $"Closeup_{key}_Boosters_side_t{sampleTimes[i]:000}.png"),
                    new Vector3(1f, 0.05f, 0.15f));
            }
        }

        if (ok)
        {
            report.Passes++;
            report.Log.AppendLine($"[{key}] PASS: frame animation and native reference-space equipment actions stay assembled.");
        }

        player?.StopPlayback();
        for (var i = 0; i < equipmentPlayers.Length; i++)
        {
            equipmentPlayers[i].StopPlayback();
        }
    }

    /// <summary>
    /// Expected native action count per equipment key, read from the manifest that
    /// Tools/BuildAC6NativeEquipmentSamples.ps1 writes next to the GLBs. Machines without the
    /// game's partsbnd archives legitimately build equipment with zero actions; the manifest is
    /// the single source of truth for what was actually decoded. Returns -1 when the part has
    /// no manifest entry (check skipped).
    /// </summary>
    private static int ExpectedNativeActionCount(string partId)
    {
        if (string.IsNullOrWhiteSpace(partId))
        {
            return -1;
        }

        _nativeEquipmentManifest ??= LoadNativeEquipmentManifest();
        foreach (var entry in _nativeEquipmentManifest)
        {
            if (partId.EndsWith(entry.key, StringComparison.OrdinalIgnoreCase))
            {
                return entry.animationCount;
            }
        }

        return -1;
    }

    private static List<NativeEquipmentManifestEntry> _nativeEquipmentManifest;

    private static List<NativeEquipmentManifestEntry> LoadNativeEquipmentManifest()
    {
        var manifestPath = NativeEquipmentRoot + "/AC6NativeEquipmentManifest.json";
        if (!File.Exists(manifestPath))
        {
            return new List<NativeEquipmentManifestEntry>();
        }

        var wrapped = "{\"items\":" + File.ReadAllText(manifestPath) + "}";
        var manifest = JsonUtility.FromJson<NativeEquipmentManifestWrapper>(wrapped);
        return manifest?.items ?? new List<NativeEquipmentManifestEntry>();
    }

    [Serializable]
    private sealed class NativeEquipmentManifestWrapper
    {
        public List<NativeEquipmentManifestEntry> items;
    }

    [Serializable]
    private sealed class NativeEquipmentManifestEntry
    {
        public string key;
        public int animationCount;
    }

    /// <summary>
    /// Finds the muzzle / firing-direction dummy inside a weapon instance. AC6 weapons use
    /// ref 10 for the primary muzzle; multi-tube back weapons use ref 300/200 series.
    /// </summary>
    /// <summary>
    /// Minimum distance from a world-space point to any rendered vertex of the weapon,
    /// sampled from baked skinned meshes and static mesh filters.
    /// </summary>
    private static float MinDistanceToWeaponSurface(GameObject weapon, Vector3 point)
    {
        var best = float.MaxValue;
        foreach (var skinned in weapon.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            var baked = new Mesh();
            try
            {
                skinned.BakeMesh(baked);
                best = Mathf.Min(best, MinDistance(baked.vertices, skinned.transform.localToWorldMatrix, point));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(baked);
            }
        }

        foreach (var filter in weapon.GetComponentsInChildren<MeshFilter>(true))
        {
            if (filter.sharedMesh != null)
            {
                best = Mathf.Min(best, MinDistance(filter.sharedMesh.vertices, filter.transform.localToWorldMatrix, point));
            }
        }

        return best;
    }

    private static float MinDistance(Vector3[] vertices, Matrix4x4 localToWorld, Vector3 point)
    {
        var best = float.MaxValue;
        for (var i = 0; i < vertices.Length; i++)
        {
            var d = (localToWorld.MultiplyPoint3x4(vertices[i]) - point).sqrMagnitude;
            if (d < best)
            {
                best = d;
            }
        }

        return Mathf.Sqrt(best);
    }

    /// <summary>
    /// Position of the pod's central flame dummy (_dmy_31) in model-root space, measured after
    /// sampling the first frame of the native idle action on a temporary instance. Falls back
    /// to the bind pose when the build has no actions (machines without the game's partsbnd).
    /// </summary>
    private static Vector3 SampleRestingFlameAnchorLocal(GameObject model, IReadOnlyList<MechEquipmentAnimationDefinition> actions)
    {
        var instance = UnityEngine.Object.Instantiate(model);
        try
        {
            var idle = actions?.FirstOrDefault(action =>
                string.Equals(action.ActionId, "a000_000000", StringComparison.OrdinalIgnoreCase));
            if (idle?.Clip != null)
            {
                idle.Clip.SampleAnimation(instance, 0f);
            }

            var anchor = FindChildBySuffix(instance.transform, "_dmy_31");
            return anchor != null
                ? instance.transform.InverseTransformPoint(anchor.position)
                : Vector3.zero;
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(instance);
        }
    }

    private static Transform FindChildBySuffix(Transform root, string suffix)
    {
        var transforms = root.GetComponentsInChildren<Transform>(true);
        for (var i = 0; i < transforms.Length; i++)
        {
            if (transforms[i].name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return transforms[i];
            }
        }

        return null;
    }

    private static Transform FindMuzzleTransform(Transform weaponRoot)
    {
        var transforms = weaponRoot.GetComponentsInChildren<Transform>(true);
        foreach (var suffix in new[] { "_dmy_10", "_dmy_300", "_dmy_200" })
        {
            foreach (var candidate in transforms)
            {
                if (candidate.name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string GetTransformPath(Transform target, Transform root)
    {
        if (target == null)
        {
            return "<none>";
        }

        var path = target.name;
        var current = target.parent;
        while (current != null && current != root)
        {
            path = current.name + "/" + path;
            current = current.parent;
        }

        return path;
    }

    private static Bounds GrowBounds(Bounds bounds, float factor)
    {
        bounds.size *= factor;
        return bounds;
    }

    private static bool WithRawAssetInstance(string assetPath, string key, ValidationReport report, Func<GameObject, bool> check)
    {
        var asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
        if (asset == null)
        {
            return Check(false, key, $"golden source asset missing: {assetPath}", report);
        }

        var instance = UnityEngine.Object.Instantiate(asset);
        try
        {
            return check(instance);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(instance);
        }
    }

    private static bool CheckGoldenSocket(GameObject mech, string suffix, Vector3 expectedLocal, string key, ValidationReport report)
    {
        var socket = mech.GetComponentsInChildren<Transform>(true)
            .FirstOrDefault(candidate => candidate.name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                || candidate.name.Contains(suffix + "_", StringComparison.OrdinalIgnoreCase)
                || candidate.name.Contains(suffix + " ", StringComparison.OrdinalIgnoreCase));
        if (socket == null)
        {
            return Check(false, key, $"golden socket {suffix} was not found", report);
        }

        var actual = mech.transform.InverseTransformPoint(socket.position);
        var distance = Vector3.Distance(actual, expectedLocal);
        report.Log.AppendLine($"[{key}] golden {suffix}: expected={Format(expectedLocal)} actual={Format(actual)} d={distance:0.####}m");
        return Check(distance < 0.02f, key, $"socket {suffix} rest position is off by {distance:0.###}m (expected {Format(expectedLocal)}, got {Format(actual)})", report);
    }

    private static bool CheckGoldenSocketSet(GameObject mech, string suffix, Vector3[] expectedLocals, string key, ValidationReport report)
    {
        var sockets = mech.GetComponentsInChildren<Transform>(true)
            .Where(candidate => candidate.GetComponent<MechMountPoint>() == null
                && candidate.name.Contains(suffix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (sockets.Count < expectedLocals.Length)
        {
            return Check(false, key, $"expected {expectedLocals.Length} sockets matching {suffix}, found {sockets.Count}", report);
        }

        var ok = true;
        var unmatched = sockets.Select(socket => mech.transform.InverseTransformPoint(socket.position)).ToList();
        foreach (var expected in expectedLocals)
        {
            var bestIndex = -1;
            var bestDistance = float.MaxValue;
            for (var i = 0; i < unmatched.Count; i++)
            {
                var distance = Vector3.Distance(unmatched[i], expected);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestIndex = i;
                }
            }

            report.Log.AppendLine($"[{key}] golden {suffix}: expected={Format(expected)} best={((bestIndex >= 0) ? Format(unmatched[bestIndex]) : "<none>")} d={bestDistance:0.####}m");
            ok &= Check(bestDistance < 0.02f, key, $"no socket matching {suffix} within 0.02m of {Format(expected)} (best {bestDistance:0.###}m)", report);
            if (bestIndex >= 0)
            {
                unmatched.RemoveAt(bestIndex);
            }
        }

        return ok;
    }

    private static string ExpectedEquipmentMountSuffix(MechPartSlot slot, MechPartDefinition part)
    {
        switch (slot)
        {
            case MechPartSlot.LeftHandWeapon:
                return part != null && part.HasTag("melee") ? "_dmy_25" : "_dmy_23";
            case MechPartSlot.RightHandWeapon: return "_dmy_20";
            case MechPartSlot.LeftBackWeapon: return "_dmy_13";
            case MechPartSlot.RightBackWeapon: return "_dmy_14";
            default: return string.Empty;
        }
    }

    private static void SampleValidationPose(GameObject mech, AnimationClip bodyClip, float normalizedTime)
    {
        var bodyPlayer = mech.GetComponent<MechSynchronizedAnimationPlayer>();
        if (bodyPlayer != null && bodyClip != null)
        {
            bodyPlayer.SampleAtTime(bodyClip.length * Mathf.Clamp01(normalizedTime));
        }

        var equipmentPlayers = mech.GetComponentsInChildren<MechEquipmentAnimationPlayer>(true);
        for (var i = 0; i < equipmentPlayers.Length; i++)
        {
            var player = equipmentPlayers[i];
            if (player.Actions.Count == 0)
            {
                continue;
            }

            var action = player.Actions.FirstOrDefault(candidate =>
                string.Equals(candidate.ActionId, player.DefaultActionId, StringComparison.OrdinalIgnoreCase))
                ?? player.Actions[0];
            player.SampleActionAtTime(action.ActionId, action.Clip.length * Mathf.Clamp01(normalizedTime));
        }
    }

    private static bool Check(bool condition, string key, string failure, ValidationReport report)
    {
        if (condition)
        {
            return true;
        }

        report.Failures++;
        report.Log.AppendLine($"[{key}] FAIL: {failure}");
        return false;
    }

    private static Dictionary<MechPartSlot, MechPartDefinition> BuildInternalParts()
    {
        var result = new Dictionary<MechPartSlot, MechPartDefinition>();
        result[MechPartSlot.Generator] = CreateInternalPart(
            "AC6_SKIN_SAMPLE_GENERATOR", "Sample Compact Reactor", MechPartSlot.Generator,
            new MechPartStats { massKg = 3800f, powerOutputMw = 70f, heatGenerationMw = 12f });
        result[MechPartSlot.FireControlSystem] = CreateInternalPart(
            "AC6_SKIN_SAMPLE_FCS", "Sample Fire Control System", MechPartSlot.FireControlSystem,
            new MechPartStats { massKg = 700f, fireControlQuality = 65f, trackCapacity = 8, guidanceChannels = 3 });
        result[MechPartSlot.Radar] = CreateInternalPart(
            "AC6_SKIN_SAMPLE_RADAR", "Sample Active Radar", MechPartSlot.Radar,
            new MechPartStats { massKg = 900f, radarRangeKm = 42f, radarRefreshHz = 2.5f, trackCapacity = 8 });
        return result;
    }

    private static Dictionary<string, MechPartDefinition> BuildBoosterParts(ICollection<string> missing)
    {
        var result = new Dictionary<string, MechPartDefinition>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < FrameCodes.Length; i++)
        {
            var code = FrameCodes[i];
            var key = $"BS_A_{code}";
            var sourcePath = $"{NativeEquipmentRoot}/{key}_NATIVE.glb";
            if (!TryLoadNativeEquipment(sourcePath, true, out var model, out var actions))
            {
                missing.Add(sourcePath + " (native booster model failed to import)");
                continue;
            }

            // Each core has one front braking/effect-only ref 800 and two rear main-thrust
            // ref 800 dummies. Only the rear pair instantiate pods. After applying the native
            // gimbal angle, each pod's central flame dummy (_dmy_31) must remain on ref 800.
            var flameAnchor = FindChildBySuffix(model.transform, "_dmy_31");
            if (flameAnchor == null)
            {
                missing.Add(sourcePath + " (booster flame anchor _dmy_31 not found)");
                continue;
            }
            // Install rotation stays IDENTITY. The resting gimbal pose comes from sampling the
            // default idle action's first frame at assembly (MechAssembler), which drives the
            // real animation target nodes inside the GLB. Baking an action-derived angle onto
            // the wrapper double-applies as soon as any action plays, because the exported
            // clips carry absolute rest-composed root values. The -p31 offset is measured on an
            // idle-posed instance so a tilted resting gimbal still puts _dmy_31 on ref 800.
            var installOffset = -SampleRestingFlameAnchorLocal(model, actions);

            var id = $"AC6_NATIVE_{key}";
            var part = LoadOrCreateAsset<MechPartDefinition>($"{PartRoot}/{id}.asset");
            part.EditorConfigure(
                id,
                $"AC6 Native Booster {code}",
                MechPartSlot.Booster,
                MechPartCategory.Internal,
                model,
                new MechPartStats
                {
                    massKg = code == "3010" ? 4800f : code == "2010" ? 3400f : 3900f,
                    peakPowerDrawMw = code == "3010" ? 28f : code == "2010" ? 19f : 22f,
                    mainThrustKn = code == "3010" ? 720f : code == "2010" ? 610f : 660f,
                    lateralThrustKn = code == "3010" ? 310f : code == "2010" ? 370f : 340f,
                    reverseThrustKn = code == "3010" ? 280f : code == "2010" ? 330f : 305f
                },
                Array.Empty<MechMountPointDefinition>(),
                new[] { "internal", "booster", "ac6-native", $"set-{code}" },
                newVisualRigMode: MechVisualRigMode.MountedPrefab,
                shouldInstantiateVisualAtAllMounts: true,
                newEquipmentAnimations: actions,
                // 000001 is an active gimbal/thrust action whose root starts around 20 degrees
                // from the mounted frame. Keep it available, but assemble the pod in native idle.
                newDefaultEquipmentActionId: SelectDefaultAction(actions, "a000_000000"),
                shouldPlayDefaultEquipmentActionOnAssemble: false,
                newLocalPosition: installOffset,
                newLocalEulerAngles: Vector3.zero,
                newLocalScale: Vector3.one);
            result[code] = part;
        }

        return result;
    }

    private static Dictionary<string, MechPartDefinition> LoadWeaponParts(ICollection<string> missing)
    {
        var specs = new[]
        {
            // EquipParamWeapon model 10 is PB-033M ASHMEAD and uses WepAbsorpPos 200.
            new NativeWeaponSpec("WP_L_0010", "Native Pile Bunker ASHMEAD 0010", MechPartSlot.LeftHandWeapon, "a000_000000", 4180f, isMelee: true),
            // 0230/0250 are also melee. AC6 mounts every WepAbsorpPos 200 weapon on the
            // forearm dummy 25, not the gun/palm dummy 23.
            new NativeWeaponSpec("WP_L_0230", "Native Left Laser Blade 0230", MechPartSlot.LeftHandWeapon, "a000_000000", 4700f, isMelee: true),
            new NativeWeaponSpec("WP_L_0250", "Native Left Blade 0250", MechPartSlot.LeftHandWeapon, "a000_000000", 6100f, isMelee: true),
            // 1310/1410 are hand-held firearms (regulation: WepAbsorpPos 100 -> right 3020 /
            // left 3023, i.e. the palm dummies). They exist so the gun grip path is exercised:
            // with 0010 reclassified as the ASHMEAD pile bunker the samples had no hand gun.
            new NativeWeaponSpec("WP_R_1310", "Native Right Grenade Launcher 1310", MechPartSlot.RightHandWeapon, "a000_000000", 4900f),
            new NativeWeaponSpec("WP_L_1410", "Native Left Bazooka 1410", MechPartSlot.LeftHandWeapon, "a000_000000", 4600f),
            new NativeWeaponSpec("WP_L_4210", "Native Left Plasma Missile 4210", MechPartSlot.LeftBackWeapon, "a000_800210", 4200f),
            new NativeWeaponSpec("WP_R_4210", "Native Right Plasma Missile 4210", MechPartSlot.RightBackWeapon, "a000_800210", 4200f),
            new NativeWeaponSpec("WP_R_4410", "Native Right Songbirds 4410", MechPartSlot.RightBackWeapon, "a000_800210", 5500f)
        };
        var result = new Dictionary<string, MechPartDefinition>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < specs.Length; i++)
        {
            var spec = specs[i];
            var path = $"{NativeEquipmentRoot}/{spec.Key}_NATIVE.glb";
            if (!TryLoadNativeEquipment(path, false, out var model, out var actions))
            {
                missing.Add(path + " (native weapon model failed to import)");
                continue;
            }

            var id = "AC6_NATIVE_" + spec.Key;
            var part = LoadOrCreateAsset<MechPartDefinition>($"{PartRoot}/{id}.asset");
            part.EditorConfigure(
                id,
                spec.DisplayName,
                spec.Slot,
                MechPartCategory.Weapon,
                model,
                new MechPartStats
                {
                    massKg = spec.MassKg,
                    peakPowerDrawMw = 4f,
                    heatGenerationMw = 2f
                },
                Array.Empty<MechMountPointDefinition>(),
                spec.IsMelee
                    ? new[] { "weapon", "ac6-native", "melee", spec.Key.ToLowerInvariant() }
                    : new[] { "weapon", "ac6-native", spec.Key.ToLowerInvariant() },
                newVisualRigMode: MechVisualRigMode.MountedPrefab,
                newEquipmentAnimations: actions,
                newDefaultEquipmentActionId: SelectDefaultAction(actions, spec.PreferredActionId),
                // Melee 040xxx clips are attack/deploy actions. They must be triggered together
                // with the body action, not autoplayed while the mech is in its mounted rest pose.
                shouldPlayDefaultEquipmentActionOnAssemble: actions.Count > 0 && !spec.IsMelee,
                newLocalPosition: Vector3.zero,
                newLocalEulerAngles: Vector3.zero,
                newLocalScale: Vector3.one);
            result[spec.Key] = part;
        }

        return result;
    }

    private static bool TryLoadNativeEquipment(
        string glbPath,
        bool loopBoosterAction,
        out GameObject model,
        out List<MechEquipmentAnimationDefinition> actions)
    {
        AssetDatabase.ImportAsset(
            glbPath,
            ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
        model = AssetDatabase.LoadAssetAtPath<GameObject>(glbPath);
        actions = new List<MechEquipmentAnimationDefinition>();
        if (model == null)
        {
            return false;
        }

        var seenActionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var clips = AssetDatabase.LoadAllAssetsAtPath(glbPath)
            .OfType<AnimationClip>()
            .Where(candidate => candidate != null && candidate.length > 0.001f)
            .OrderBy(candidate => candidate.name, StringComparer.OrdinalIgnoreCase);
        foreach (var clip in clips)
        {
            var match = EquipmentActionPattern.Match(clip.name);
            if (!match.Success)
            {
                continue;
            }

            var actionId = match.Value.ToLowerInvariant();
            if (!seenActionIds.Add(actionId))
            {
                continue;
            }

            var shouldLoop = loopBoosterAction && actionId == "a000_000001";
            actions.Add(new MechEquipmentAnimationDefinition(actionId, clip, shouldLoop));
        }

        return true;
    }

    private static string SelectDefaultAction(
        IReadOnlyList<MechEquipmentAnimationDefinition> actions,
        string preferredActionId)
    {
        if (actions == null || actions.Count == 0)
        {
            return string.Empty;
        }

        for (var i = 0; i < actions.Count; i++)
        {
            if (string.Equals(actions[i].ActionId, preferredActionId, StringComparison.OrdinalIgnoreCase))
            {
                return actions[i].ActionId;
            }
        }

        return actions[0].ActionId;
    }

    private static bool TryGetEquipmentRootRotation(
        IReadOnlyList<MechEquipmentAnimationDefinition> actions,
        string actionId,
        float timeSeconds,
        out Quaternion rotation)
    {
        rotation = Quaternion.identity;
        var action = actions?.FirstOrDefault(candidate => candidate?.Clip != null &&
            string.Equals(candidate.ActionId, actionId, StringComparison.OrdinalIgnoreCase));
        if (action?.Clip == null)
        {
            return false;
        }

        var quaternionBindings = AnimationUtility.GetCurveBindings(action.Clip)
            .Where(binding => binding.propertyName.StartsWith("m_LocalRotation.", StringComparison.Ordinal))
            .GroupBy(binding => binding.path)
            .OrderBy(group => string.IsNullOrEmpty(group.Key) ? 0 : 1)
            .ThenBy(group => group.Key?.Length ?? 0);
        foreach (var group in quaternionBindings)
        {
            var components = new float[4];
            var found = new bool[4];
            foreach (var binding in group)
            {
                var component = RotationComponentIndex(binding.propertyName);
                var curve = component >= 0 ? AnimationUtility.GetEditorCurve(action.Clip, binding) : null;
                if (curve == null)
                {
                    continue;
                }

                components[component] = curve.Evaluate(timeSeconds);
                found[component] = true;
            }

            if (!found.All(value => value))
            {
                continue;
            }

            var magnitude = Mathf.Sqrt(
                components[0] * components[0] +
                components[1] * components[1] +
                components[2] * components[2] +
                components[3] * components[3]);
            if (magnitude < 0.0001f)
            {
                continue;
            }

            rotation = new Quaternion(
                components[0] / magnitude,
                components[1] / magnitude,
                components[2] / magnitude,
                components[3] / magnitude);
            return true;
        }

        return false;
    }

    private static int RotationComponentIndex(string propertyName)
    {
        if (propertyName.EndsWith(".x", StringComparison.Ordinal)) return 0;
        if (propertyName.EndsWith(".y", StringComparison.Ordinal)) return 1;
        if (propertyName.EndsWith(".z", StringComparison.Ordinal)) return 2;
        if (propertyName.EndsWith(".w", StringComparison.Ordinal)) return 3;
        return -1;
    }

    private static MechPartDefinition CreateInternalPart(string id, string displayName, MechPartSlot slot, MechPartStats stats)
    {
        var part = LoadOrCreateAsset<MechPartDefinition>($"{PartRoot}/{id}.asset");
        part.EditorConfigure(
            id,
            displayName,
            slot,
            MechPartCategory.Internal,
            null,
            stats,
            Array.Empty<MechMountPointDefinition>(),
            new[] { "internal", "sample" });
        return part;
    }

    private static void CreateLoadout(
        IDictionary<string, MechLoadoutDefinition> destination,
        string key,
        string displayName,
        IReadOnlyDictionary<string, MechPartDefinition> frameParts,
        IReadOnlyDictionary<MechPartSlot, MechPartDefinition> internals,
        IReadOnlyDictionary<string, MechPartDefinition> boosters,
        IReadOnlyDictionary<string, MechPartDefinition> weapons,
        string headCode,
        string coreCode,
        string armsCode,
        string legsCode,
        string leftHandWeaponKey = null)
    {
        var assignments = new List<MechPartAssignment>
        {
            Assignment(MechPartSlot.Head, frameParts[FrameKey(MechPartSlot.Head, headCode)]),
            Assignment(MechPartSlot.Core, frameParts[FrameKey(MechPartSlot.Core, coreCode)]),
            Assignment(MechPartSlot.Arms, frameParts[FrameKey(MechPartSlot.Arms, armsCode)]),
            Assignment(MechPartSlot.Legs, frameParts[FrameKey(MechPartSlot.Legs, legsCode)]),
            Assignment(MechPartSlot.Booster, boosters[coreCode]),
            Assignment(MechPartSlot.Generator, internals[MechPartSlot.Generator]),
            Assignment(MechPartSlot.FireControlSystem, internals[MechPartSlot.FireControlSystem]),
            Assignment(MechPartSlot.Radar, internals[MechPartSlot.Radar]),
            Assignment(MechPartSlot.LeftHandWeapon, weapons[leftHandWeaponKey ?? LeftHandWeaponForCode(armsCode)]),
            // Every loadout carries a right-hand firearm so the palm-grip path (dmy_20,
            // WepAbsorpPos 100) is always exercised alongside the melee forearm path.
            Assignment(MechPartSlot.RightHandWeapon, weapons["WP_R_1310"]),
            Assignment(MechPartSlot.LeftBackWeapon, weapons["WP_L_4210"]),
            Assignment(MechPartSlot.RightBackWeapon, weapons[RightBackWeaponForCode(coreCode)])
        };

        var loadout = LoadOrCreateAsset<MechLoadoutDefinition>($"{LoadoutRoot}/AC6_Skinned_{key}.asset");
        loadout.EditorConfigure("ac6_skinned_" + key.ToLowerInvariant(), displayName, assignments);
        destination[key] = loadout;
    }

    private static MechPartAssignment Assignment(MechPartSlot slot, MechPartDefinition part)
        => new MechPartAssignment { slot = slot, part = part, enabled = true };

    private static string LeftHandWeaponForCode(string code)
    {
        switch (code)
        {
            case "2010": return "WP_L_0230";
            case "3010": return "WP_L_0250";
            default: return "WP_L_0010";
        }
    }

    private static string RightBackWeaponForCode(string code)
        => code == "2010" ? "WP_R_4410" : "WP_R_4210";

    private static IReadOnlyList<MechMountPointDefinition> BuildFrameMountPoints(MechPartSlot slot)
    {
        // AC6 WepAbsorpPos prefixes select the source part: 2xxx = body/core,
        // 3xxx = arms. The remaining three digits are the FLVER dummy reference ID.
        // Installation rotation is IDENTITY for every AC6 equipment slot. Evidence chain
        // (2026-07-11, close-range regression after the RotationX(pi) attempt was refuted):
        //  - The mech faces -Z in FromSoft model space (HD ref=14 camera dummy sits 6.66 in
        //    front of the face at -Z looking back; eye SFX dummies fire -Z).
        //  - Every measured muzzle fires -Z in its weapon's model space (WP_L_0010 dmy_10,
        //    WP_R_4210 dmy_300/301/302, WP_R_4410 dmy_200/300; blade thrust of WP_L_0250 too).
        //  - The mount dummies' CreateWorld frames are ~identity (forward -Z, up +Y).
        //  So identity install aligns muzzle with mech forward; DSAS's RotationX(pi) belongs to
        //  DSAS's own render convention and must NOT be replayed here.
        // AC6 left-hand melee weapons use the forearm dummy 25 (WepAbsorpPos 200 -> 3025), not
        // the palm dummy 23 (WepAbsorpPos 100 -> 3023); the mounts express that via tags.
        if (slot == MechPartSlot.Core)
        {
            return new[]
            {
                Mount("Booster Nozzles", MechPartSlot.Booster, MechHardpointType.Internal, "_dmy_800", true, 10000f,
                    sourceRegion: MechMountSourceRegion.Rear),
                Mount("Left Back", MechPartSlot.LeftBackWeapon, MechHardpointType.BackWeapon, "_dmy_13", false, 10000f),
                Mount("Right Back", MechPartSlot.RightBackWeapon, MechHardpointType.BackWeapon, "_dmy_14", false, 10000f)
            };
        }

        if (slot == MechPartSlot.Arms)
        {
            return new[]
            {
                Mount("Left Hand", MechPartSlot.LeftHandWeapon, MechHardpointType.HandWeapon, "_dmy_23", false, 8000f, blockedTags: new[] { "melee" }),
                Mount("Left Melee", MechPartSlot.LeftHandWeapon, MechHardpointType.HandWeapon, "_dmy_25", false, 8000f, requiredTags: new[] { "melee" }),
                Mount("Right Hand", MechPartSlot.RightHandWeapon, MechHardpointType.HandWeapon, "_dmy_20", false, 8000f)
            };
        }

        return Array.Empty<MechMountPointDefinition>();
    }

    private static MechMountPointDefinition Mount(
        string id,
        MechPartSlot targetSlot,
        MechHardpointType hardpointType,
        string sourceTransformNameSuffix,
        bool matchAll,
        float maxMassKg,
        string[] requiredTags = null,
        string[] blockedTags = null,
        MechMountSourceRegion sourceRegion = MechMountSourceRegion.Any)
    {
        return new MechMountPointDefinition
        {
            mountId = id,
            targetSlot = targetSlot,
            hardpointType = hardpointType,
            maxMassKg = maxMassKg,
            sourceTransformNameSuffix = sourceTransformNameSuffix,
            matchAllSourceTransforms = matchAll,
            sourceTransformRegion = sourceRegion,
            localScale = Vector3.one,
            requiredTags = requiredTags ?? Array.Empty<string>(),
            blockedTags = blockedTags ?? Array.Empty<string>()
        };
    }

    private static MechPartStats BuildFrameStats(MechPartSlot slot, float scale)
    {
        switch (slot)
        {
            case MechPartSlot.Head:
                return new MechPartStats { massKg = 1700f * scale, armorPoints = 260f * scale, structurePoints = 180f * scale, peakPowerDrawMw = 1.1f };
            case MechPartSlot.Core:
                return new MechPartStats { massKg = 12000f * scale, armorPoints = 1750f * scale, structurePoints = 1450f * scale, heatDissipationMw = 18f * scale, hardpointCapacityKg = 16000f * scale };
            case MechPartSlot.Arms:
                return new MechPartStats { massKg = 6900f * scale, armorPoints = 850f * scale, structurePoints = 680f * scale, weaponRecoilControl = 58f * scale };
            case MechPartSlot.Legs:
                return new MechPartStats { massKg = 9800f * scale, armorPoints = 1250f * scale, structurePoints = 1050f * scale, supportedMassKg = 65000f * scale, stability = 44f * scale };
            default:
                return new MechPartStats();
        }
    }

    private static float MassScale(string code)
    {
        switch (code)
        {
            case "2010": return 0.82f;
            case "3010": return 1.22f;
            default: return 1f;
        }
    }

    private static string SetDisplayName(string code)
    {
        switch (code)
        {
            case "2010": return "Nacht 2010";
            case "3010": return "Tian-Qiang 3010";
            default: return "Melander 1010";
        }
    }

    private static string FrameKey(MechPartSlot slot, string code) => slot + "_" + code;

    private static MechPartSlot AncestorDriverForSlot(MechPartSlot slot)
    {
        switch (slot)
        {
            case MechPartSlot.Core: return MechPartSlot.Legs;
            case MechPartSlot.Head:
            case MechPartSlot.Arms:
                return MechPartSlot.Core;
            default:
                return MechPartSlot.None;
        }
    }

    private static IReadOnlyList<string> AncestorDrivenBonesForSlot(MechPartSlot slot)
    {
        switch (slot)
        {
            case MechPartSlot.Core: return CoreLegsDrivenBones;
            case MechPartSlot.Head: return HeadCoreDrivenBones;
            case MechPartSlot.Arms: return ArmsCoreDrivenBones;
            default: return Array.Empty<string>();
        }
    }

    private static T LoadOrCreateAsset<T>(string path) where T : ScriptableObject
    {
        var asset = AssetDatabase.LoadAssetAtPath<T>(path);
        if (asset != null)
        {
            return asset;
        }

        asset = ScriptableObject.CreateInstance<T>();
        AssetDatabase.CreateAsset(asset, path);
        return asset;
    }

    private static void EnsureAssetFolder(string assetPath)
    {
        var normalized = assetPath.Replace('\\', '/').TrimEnd('/');
        if (AssetDatabase.IsValidFolder(normalized))
        {
            return;
        }

        var parent = Path.GetDirectoryName(normalized)?.Replace('\\', '/');
        var folderName = Path.GetFileName(normalized);
        if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(folderName))
        {
            throw new InvalidOperationException("Invalid asset folder path: " + assetPath);
        }

        EnsureAssetFolder(parent);
        AssetDatabase.CreateFolder(parent, folderName);
    }

    private static Bounds CombinedBounds(GameObject root)
    {
        var result = new Bounds();
        var initialized = false;
        var renderers = root.GetComponentsInChildren<Renderer>(true);
        for (var i = 0; i < renderers.Length; i++)
        {
            if (renderers[i].GetComponent<TextMesh>() != null)
            {
                continue;
            }

            var rendererBounds = renderers[i].bounds;
            if (renderers[i] is SkinnedMeshRenderer skinned)
            {
                skinned.updateWhenOffscreen = true;
                var baked = new Mesh();
                try
                {
                    skinned.BakeMesh(baked);
                    rendererBounds = TransformBounds(skinned.transform.localToWorldMatrix, baked.bounds);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(baked);
                }
            }

            if (!initialized)
            {
                result = rendererBounds;
                initialized = true;
            }
            else
            {
                result.Encapsulate(rendererBounds);
            }
        }

        return result;
    }

    private static Bounds TransformBounds(Matrix4x4 matrix, Bounds localBounds)
    {
        var center = matrix.MultiplyPoint3x4(localBounds.center);
        var extents = localBounds.extents;
        var axisX = matrix.MultiplyVector(new Vector3(extents.x, 0f, 0f));
        var axisY = matrix.MultiplyVector(new Vector3(0f, extents.y, 0f));
        var axisZ = matrix.MultiplyVector(new Vector3(0f, 0f, extents.z));
        extents = new Vector3(
            Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x),
            Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y),
            Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z));
        return new Bounds(center, extents * 2f);
    }

    private static void CreateSceneLighting(Transform parent, IReadOnlyList<GameObject> mechs)
    {
        var lightObject = new GameObject("Directional Light");
        lightObject.transform.SetParent(parent, false);
        lightObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        var light = lightObject.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.6f;

        var cameraObject = new GameObject("Validation Camera");
        cameraObject.transform.SetParent(parent, false);
        cameraObject.transform.position = new Vector3(0f, 7f, -42f);
        cameraObject.transform.rotation = Quaternion.Euler(8f, 0f, 0f);
        var camera = cameraObject.AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.14f, 0.16f, 0.2f);
        camera.nearClipPlane = 0.1f;
        camera.farClipPlane = 250f;
    }

    private static void Screenshot(GameObject subject, Bounds bounds, string filePath, Vector3? viewDirection = null)
    {
        var cameraObject = new GameObject("ValidationScreenshotCamera");
        var lightObject = new GameObject("ValidationScreenshotLight");
        var subjectTransforms = subject.GetComponentsInChildren<Transform>(true);
        var originalLayers = new int[subjectTransforms.Length];
        const int screenshotLayer = 31;
        try
        {
            for (var i = 0; i < subjectTransforms.Length; i++)
            {
                originalLayers[i] = subjectTransforms[i].gameObject.layer;
                subjectTransforms[i].gameObject.layer = screenshotLayer;
            }

            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.8f;
            light.cullingMask = 1 << screenshotLayer;
            lightObject.transform.rotation = Quaternion.Euler(45f, -35f, 0f);

            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.14f, 0.16f, 0.2f);
            camera.cullingMask = 1 << screenshotLayer;
            // Distance floor keeps close-ups of small bounds (nozzles) from placing the camera
            // inside surrounding geometry; the near plane stays proportional but never huge.
            var distance = Mathf.Max(Mathf.Max(bounds.size.magnitude, 1f) * 1.1f, 2.5f);
            var direction = (viewDirection ?? new Vector3(0.7f, 0.2f, -1f)).normalized;
            camera.transform.position = bounds.center + direction * distance;
            camera.transform.LookAt(bounds.center);
            camera.nearClipPlane = Mathf.Clamp(distance * 0.01f, 0.01f, 0.3f);
            camera.farClipPlane = distance * 10f;

            var renderTexture = new RenderTexture(900, 1100, 24);
            camera.targetTexture = renderTexture;
            camera.Render();

            var previous = RenderTexture.active;
            RenderTexture.active = renderTexture;
            var texture = new Texture2D(renderTexture.width, renderTexture.height, TextureFormat.RGB24, false);
            texture.ReadPixels(new Rect(0, 0, renderTexture.width, renderTexture.height), 0, 0);
            texture.Apply();
            RenderTexture.active = previous;
            camera.targetTexture = null;

            Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? ReportRoot);
            File.WriteAllBytes(filePath, texture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(texture);
            renderTexture.Release();
            UnityEngine.Object.DestroyImmediate(renderTexture);
        }
        finally
        {
            for (var i = 0; i < subjectTransforms.Length; i++)
            {
                if (subjectTransforms[i] != null)
                {
                    subjectTransforms[i].gameObject.layer = originalLayers[i];
                }
            }

            UnityEngine.Object.DestroyImmediate(cameraObject);
            UnityEngine.Object.DestroyImmediate(lightObject);
        }
    }

    private static void WriteReport(ValidationReport report)
    {
        Directory.CreateDirectory(ReportRoot);
        File.WriteAllText(Path.Combine(ReportRoot, "skinned_modular_validation.txt"), report.Log.ToString());
        AssetDatabase.Refresh();
    }

    private static string Format(Vector3 value) => $"({value.x:0.###},{value.y:0.###},{value.z:0.###})";

    private sealed class NativeWeaponSpec
    {
        public NativeWeaponSpec(
            string key,
            string displayName,
            MechPartSlot slot,
            string preferredActionId,
            float massKg,
            bool isMelee = false)
        {
            Key = key;
            DisplayName = displayName;
            Slot = slot;
            IsMelee = isMelee;
            PreferredActionId = preferredActionId;
            MassKg = massKg;
        }

        public string Key { get; }
        public string DisplayName { get; }
        public MechPartSlot Slot { get; }
        public bool IsMelee { get; }
        public string PreferredActionId { get; }
        public float MassKg { get; }
    }

    private sealed class FrameSlotSpec
    {
        public FrameSlotSpec(string token, string displayName, MechPartSlot slot)
        {
            Token = token;
            DisplayName = displayName;
            Slot = slot;
        }

        public string Token { get; }
        public string DisplayName { get; }
        public MechPartSlot Slot { get; }
    }

    private sealed class BuildResult
    {
        public BuildResult(
            AnimationClip animationClip,
            Dictionary<string, MechPartDefinition> frameParts,
            Dictionary<string, MechLoadoutDefinition> loadouts)
        {
            AnimationClip = animationClip;
            FrameParts = frameParts;
            Loadouts = loadouts;
        }

        public AnimationClip AnimationClip { get; }
        public Dictionary<string, MechPartDefinition> FrameParts { get; }
        public Dictionary<string, MechLoadoutDefinition> Loadouts { get; }
    }

    private sealed class ValidationReport
    {
        public StringBuilder Log { get; } = new StringBuilder();
        public int Passes { get; set; }
        public int Failures { get; set; }
        public string Summary { get; private set; } = string.Empty;

        public void Finish()
        {
            Summary = Failures == 0
                ? $"Validation passed for {Passes} loadouts.\n\nScene: {ValidationScenePath}\nReport: {ReportRoot}/skinned_modular_validation.txt"
                : $"Validation found {Failures} failure(s) across {Passes} passing loadouts.\n\nSee: {ReportRoot}/skinned_modular_validation.txt";
            Log.AppendLine();
            Log.AppendLine(Summary);
        }
    }
}
