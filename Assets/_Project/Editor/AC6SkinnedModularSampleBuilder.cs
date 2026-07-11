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
        CreateLoadout(loadouts, "MixedA", "Mixed A 2010/1010/3010/2010", frameParts, internals, boosters, weapons, "2010", "1010", "3010", "2010");
        CreateLoadout(loadouts, "MixedB", "Mixed B 3010/2010/1010/3010", frameParts, internals, boosters, weapons, "3010", "2010", "1010", "3010");

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
        var isPreviewScene = !batchMode;
        if (batchMode)
        {
            if (string.IsNullOrWhiteSpace(previousActiveScene.path))
            {
                EditorSceneManager.SaveScene(previousActiveScene, SceneRoot + "/BatchBootstrap.unity");
            }

            validationScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            SceneManager.SetActiveScene(validationScene);
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
        if (batchMode)
        {
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

        var ok = true;
        ok &= Check(frameRecords.Count == 4, key, $"expected 4 skinned frame records, got {frameRecords.Count}", report);
        ok &= Check(runtime != null && runtime.LoadoutValid, key, "assembled equipment loadout is invalid", report);
        ok &= Check(boosterRecord?.part?.ModelPrefab != null, key, "visible booster model is missing", report);
        ok &= Check(weaponRecords.Count == 3, key, $"expected 3 mounted weapons, got {weaponRecords.Count}", report);

        var boosterMounts = runtime == null
            ? new List<MechMountPoint>()
            : runtime.MountPoints.Where(mount => mount != null && mount.TargetSlot == MechPartSlot.Booster).ToList();
        ok &= Check(boosterMounts.Count == 3, key, $"expected 3 animated booster mounts, got {boosterMounts.Count}", report);
        for (var i = 0; i < boosterMounts.Count; i++)
        {
            var source = boosterMounts[i].transform.parent;
            ok &= Check(source != null && source.name.IndexOf("_dmy_800", StringComparison.OrdinalIgnoreCase) >= 0,
                key, $"booster mount {i} is not attached to a _dmy_800 node", report);
        }

        var boosterVisuals = mech.GetComponentsInChildren<Transform>(true)
            .Where(candidate => candidate != null && candidate.name.StartsWith("Booster_", StringComparison.Ordinal) &&
                candidate.GetComponentInChildren<Renderer>(true) != null)
            .ToList();
        ok &= Check(boosterVisuals.Count == 3, key, $"expected 3 visible booster instances, got {boosterVisuals.Count}", report);
        for (var i = 0; i < weaponRecords.Count; i++)
        {
            var record = weaponRecords[i];
            var mountPoint = record.parentMount != null ? record.parentMount.GetComponent<MechMountPoint>() : null;
            var mountSource = record.parentMount != null ? record.parentMount.parent : null;
            var expectedMountSuffix = ExpectedEquipmentMountSuffix(record.slot);
            var modelPath = record.part == null ? string.Empty : AssetDatabase.GetAssetPath(record.part.ModelPrefab);
            ok &= Check(record.instance != null && record.instance.GetComponentInChildren<Renderer>(true) != null,
                key, $"{record.slot} has no visible renderer", report);
            ok &= Check(mountPoint != null && mountPoint.TargetSlot == record.slot,
                key, $"{record.slot} is not attached to its animated hardpoint", report);
            ok &= Check(mountSource != null && mountSource.name.EndsWith(expectedMountSuffix, StringComparison.OrdinalIgnoreCase),
                key, $"{record.slot} expected mount {expectedMountSuffix}, got {mountSource?.name ?? "<none>"}", report);
            ok &= Check(modelPath.StartsWith(NativeEquipmentRoot, StringComparison.OrdinalIgnoreCase),
                key, $"{record.slot} still references a legacy or Blender-baked asset: {modelPath}", report);

            var expectedActionCount = ExpectedNativeActionCount(record.part.PartId);
            ok &= Check(expectedActionCount < 0 || record.part.EquipmentAnimations.Count == expectedActionCount,
                key, $"{record.slot} expected {expectedActionCount} native actions, got {record.part.EquipmentAnimations.Count}", report);
            var equipmentPlayer = record.instance.GetComponent<MechEquipmentAnimationPlayer>();
            ok &= Check(record.part.EquipmentAnimations.Count == 0 ? equipmentPlayer == null : equipmentPlayer != null,
                key, $"{record.slot} independent equipment animation player state is incorrect", report);
        }

        var boosterModelPath = boosterRecord?.part == null
            ? string.Empty
            : AssetDatabase.GetAssetPath(boosterRecord.part.ModelPrefab);
        ok &= Check(boosterModelPath.StartsWith(NativeEquipmentRoot, StringComparison.OrdinalIgnoreCase),
            key, $"booster still references a legacy or Blender-baked asset: {boosterModelPath}", report);
        var expectedBoosterActions = ExpectedNativeActionCount(boosterRecord?.part?.PartId);
        ok &= Check(expectedBoosterActions < 0 || boosterRecord?.part?.EquipmentAnimations.Count == expectedBoosterActions,
            key, $"booster expected {expectedBoosterActions} native actions, got {boosterRecord?.part?.EquipmentAnimations.Count ?? -1}", report);

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

    private static string ExpectedEquipmentMountSuffix(MechPartSlot slot)
    {
        switch (slot)
        {
            case MechPartSlot.LeftHandWeapon: return "_dmy_23";
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
                newDefaultEquipmentActionId: SelectDefaultAction(actions, "a000_000001"),
                shouldPlayDefaultEquipmentActionOnAssemble: true,
                newLocalPosition: Vector3.zero,
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
            new NativeWeaponSpec("WP_L_0010", "Native Left Rifle 0010", MechPartSlot.LeftHandWeapon, "a000_040010", 4200f),
            new NativeWeaponSpec("WP_L_0230", "Native Left Rifle 0230", MechPartSlot.LeftHandWeapon, "a000_040010", 4700f),
            new NativeWeaponSpec("WP_L_0250", "Native Left Heavy Rifle 0250", MechPartSlot.LeftHandWeapon, "a000_040010", 6100f),
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
                new[] { "weapon", "ac6-native", spec.Key.ToLowerInvariant() },
                newVisualRigMode: MechVisualRigMode.MountedPrefab,
                newEquipmentAnimations: actions,
                newDefaultEquipmentActionId: SelectDefaultAction(actions, spec.PreferredActionId),
                shouldPlayDefaultEquipmentActionOnAssemble: actions.Count > 0,
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
        string legsCode)
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
            Assignment(MechPartSlot.LeftHandWeapon, weapons[LeftHandWeaponForCode(armsCode)]),
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
        // DSAS NewChrAsm.DoWPN: for AC6 every weapon slot places the model at
        // RotationX(pi) * dummyCurrentMatrix — one uniform 180-degree X flip, no per-slot or
        // left/right variants. Encoded here as the mount's local rotation under the dummy node.
        // Boosters have no DSAS reference implementation (AC6Booster_NotImlementedYet), so the
        // nozzle mounts start at identity until the screenshots say otherwise.
        var weaponInstallEuler = new Vector3(180f, 0f, 0f);

        if (slot == MechPartSlot.Core)
        {
            return new[]
            {
                Mount("Booster Nozzles", MechPartSlot.Booster, MechHardpointType.Internal, "_dmy_800", true, 10000f, Vector3.zero),
                Mount("Left Back", MechPartSlot.LeftBackWeapon, MechHardpointType.BackWeapon, "_dmy_13", false, 10000f, weaponInstallEuler),
                Mount("Right Back", MechPartSlot.RightBackWeapon, MechHardpointType.BackWeapon, "_dmy_14", false, 10000f, weaponInstallEuler)
            };
        }

        if (slot == MechPartSlot.Arms)
        {
            return new[]
            {
                Mount("Left Hand", MechPartSlot.LeftHandWeapon, MechHardpointType.HandWeapon, "_dmy_23", false, 8000f, weaponInstallEuler),
                Mount("Right Hand", MechPartSlot.RightHandWeapon, MechHardpointType.HandWeapon, "_dmy_20", false, 8000f, weaponInstallEuler)
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
        Vector3 installEulerAngles)
    {
        return new MechMountPointDefinition
        {
            mountId = id,
            targetSlot = targetSlot,
            hardpointType = hardpointType,
            maxMassKg = maxMassKg,
            sourceTransformNameSuffix = sourceTransformNameSuffix,
            matchAllSourceTransforms = matchAll,
            localEulerAngles = installEulerAngles,
            localScale = Vector3.one
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

    private static void Screenshot(GameObject subject, Bounds bounds, string filePath)
    {
        var cameraObject = new GameObject("ValidationScreenshotCamera");
        var lightObject = new GameObject("ValidationScreenshotLight");
        try
        {
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.8f;
            lightObject.transform.rotation = Quaternion.Euler(45f, -35f, 0f);

            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.14f, 0.16f, 0.2f);
            var distance = Mathf.Max(bounds.size.magnitude, 1f) * 1.1f;
            camera.transform.position = bounds.center + new Vector3(0.7f, 0.2f, -1f).normalized * distance;
            camera.transform.LookAt(bounds.center);
            camera.nearClipPlane = Mathf.Max(0.01f, distance * 0.01f);
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
            float massKg)
        {
            Key = key;
            DisplayName = displayName;
            Slot = slot;
            PreferredActionId = preferredActionId;
            MassKg = massKg;
        }

        public string Key { get; }
        public string DisplayName { get; }
        public MechPartSlot Slot { get; }
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
