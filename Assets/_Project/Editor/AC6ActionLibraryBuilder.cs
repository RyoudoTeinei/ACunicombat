using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ACunicombat.Mechs;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Step B of the action-library pipeline (step A is Tools/BuildAC6ActionClips.ps1, which turns
/// catalog HKX entries into full-sample pose JSONs). This builder turns each pose JSON into
/// one AnimationClip per frame part — retargeted from the canonical c0000 rest pose onto the
/// part's own bind pose — and upserts everything into a single MechActionRegistry asset.
/// Catalog entries backed by an aligned official BHV state or reviewed as Confirmed
/// can enter the production registry.
/// Pipeline contract and retarget math: Docs/AC6动作库管线_设计.md.
/// </summary>
public static class AC6ActionLibraryBuilder
{
    private const string CatalogPath = "Tools/AssetManifests/c0000_action_catalog.json";
    private const string RawGltfRoot = "Assets/_Project/LocalSamples/AC6SkinnedModular/Raw/Gltf";
    private const string GeneratedActionsRoot = "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Actions";
    private const string RegistryAssetPath = GeneratedActionsRoot + "/AC6ActionRegistry.asset";
    private const string FrameSetCode = "1010";
    private const float PositionCurveTolerance = 0.0005f;
    private const float RotationCurveToleranceDegrees = 0.05f;
    private const float RestPositionTolerance = 0.00001f;
    private const float RestRotationToleranceDegrees = 0.005f;

    private static readonly (MechPartSlot Slot, string Token)[] FrameSlots =
    {
        (MechPartSlot.Head, "HD"),
        (MechPartSlot.Core, "BD"),
        (MechPartSlot.Arms, "AM"),
        (MechPartSlot.Legs, "LG"),
    };

    [MenuItem("Tools/ACunicombat/AC6/Build Action Library")]
    public static void BuildMenu()
    {
        var report = Build();
        EditorUtility.DisplayDialog("AC6 Action Library", report, "OK");
    }

    [MenuItem("Tools/ACunicombat/AC6/Build All Physical Action Preview Clips")]
    public static void BuildAllPhysicalPreviewClipsMenu()
    {
        var report = BuildAllPhysicalPreviewClips();
        EditorUtility.DisplayDialog("AC6 Physical Action Preview Clips", report, "OK");
    }

    public static string Build()
    {
        var manifest = LoadConfirmedCatalog();
        if (manifest == null)
        {
            return $"Action catalog missing or unreadable: {CatalogPath}";
        }

        var registry = LoadOrCreateRegistry();
        registry.EditorActions.Clear();
        var built = 0;
        var skipped = 0;
        var failures = new List<string>();

        foreach (var entry in manifest.entries)
        {
            if (entry == null || !entry.enabled || string.IsNullOrWhiteSpace(entry.actionId))
            {
                skipped++;
                continue;
            }

            var poseJsonPath = Path.Combine(manifest.poseJsonRoot, entry.actionId + "_pose.json");
            if (!File.Exists(poseJsonPath))
            {
                failures.Add($"{entry.actionId}: pose JSON not found ({poseJsonPath}); run Tools/BuildAC6ActionClips.ps1 first.");
                continue;
            }

            try
            {
                var pose = JsonUtility.FromJson<PoseExport>(File.ReadAllText(poseJsonPath));
                var slotClips = BuildClipsForAllParts(entry, pose);
                UpsertRegistryEntry(registry, entry, slotClips);
                built++;
            }
            catch (Exception e)
            {
                failures.Add($"{entry.actionId}: {e.Message}");
            }
        }

        EditorUtility.SetDirty(registry);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        var report =
            $"Action library: built={built} skipped={skipped} failed={failures.Count}\n" +
            $"Registry: {RegistryAssetPath}\n" +
            string.Join("\n", failures);
        Debug.Log($"[AC6ActionLibrary] {report}");
        return report;
    }

    public static string BuildReviewAction(string actionId, string layer, bool loop)
    {
        if (string.IsNullOrWhiteSpace(actionId))
        {
            return "Action id is empty.";
        }

        var entry = new ManifestEntry
        {
            actionId = actionId,
            hkx = actionId,
            layer = layer,
            loop = loop,
            blendInSeconds = 0.15f,
            blendOutSeconds = 0.15f,
            enabled = true,
            reviewStatus = "NeedsReview",
            displayNameZh = $"待人工确认：{actionId}",
            categoryZh = "待人工确认"
        };
        var poseJsonPath = Path.Combine("Tools/LocalOut/actions", actionId + "_pose.json");
        if (!File.Exists(poseJsonPath))
        {
            return $"Pose JSON not found: {poseJsonPath}";
        }

        try
        {
            var pose = JsonUtility.FromJson<PoseExport>(File.ReadAllText(poseJsonPath));
            var clips = BuildClipsForAllParts(entry, pose);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return $"Review clips built for {actionId}: {clips.Count}/4.";
        }
        catch (Exception e)
        {
            return $"Review clip build failed for {actionId}: {e.Message}";
        }
    }

    public static string BuildAllPhysicalPreviewClips()
    {
        if (!File.Exists(CatalogPath))
        {
            return $"Action catalog missing: {CatalogPath}";
        }

        var catalog = JsonUtility.FromJson<CatalogDocument>(File.ReadAllText(CatalogPath));
        if (catalog?.entries == null)
        {
            return $"Action catalog is unreadable: {CatalogPath}";
        }

        var entries = catalog.entries
            .Where(entry => entry != null &&
                entry.sourceAvailable &&
                !string.IsNullOrWhiteSpace(entry.actionId) &&
                File.Exists(Path.Combine("Tools/LocalOut/actions", entry.actionId + "_pose.json")))
            .ToArray();
        var pending = entries
            .Where(entry => FrameSlots.Any(slot =>
                !File.Exists($"{GeneratedActionsRoot}/{entry.actionId}/AC6_{entry.actionId}_{slot.Token}.anim")))
            .ToArray();

        if (pending.Length == 0)
        {
            return $"All {entries.Length} physical actions already have four preview clips.";
        }

        foreach (var entry in pending)
        {
            EnsureFolder($"{GeneratedActionsRoot}/{entry.actionId}");
        }

        var built = 0;
        var canceled = false;
        var failures = new List<string>();
        AssetDatabase.StartAssetEditing();
        try
        {
            for (var i = 0; i < pending.Length; i++)
            {
                var catalogEntry = pending[i];
                canceled = EditorUtility.DisplayCancelableProgressBar(
                    "AC6 Physical Action Preview Clips",
                    $"{i + 1}/{pending.Length}  {catalogEntry.actionId}",
                    (float)i / pending.Length);
                if (canceled)
                {
                    break;
                }

                try
                {
                    var posePath = Path.Combine(
                        "Tools/LocalOut/actions", catalogEntry.actionId + "_pose.json");
                    var pose = JsonUtility.FromJson<PoseExport>(File.ReadAllText(posePath));
                    var entry = new ManifestEntry
                    {
                        actionId = catalogEntry.actionId,
                        hkx = catalogEntry.actionId,
                        layer = catalogEntry.layer,
                        loop = catalogEntry.loop,
                        blendInSeconds = 0.15f,
                        blendOutSeconds = 0.15f,
                        enabled = true,
                        reviewStatus = catalogEntry.reviewStatus,
                        displayNameZh = catalogEntry.displayNameZh,
                        categoryZh = catalogEntry.categoryZh
                    };
                    var clips = BuildClipsForAllParts(entry, pose);
                    if (clips.Count != FrameSlots.Length)
                    {
                        failures.Add($"{catalogEntry.actionId}: built {clips.Count}/{FrameSlots.Length} clips");
                        continue;
                    }

                    built++;
                }
                catch (Exception e)
                {
                    failures.Add($"{catalogEntry.actionId}: {e.Message}");
                }
            }
        }
        finally
        {
            AssetDatabase.StopAssetEditing();
            EditorUtility.ClearProgressBar();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        var failureDetails = failures.Count == 0
            ? string.Empty
            : "\n" + string.Join("\n", failures.Take(20));
        var report =
            $"Physical preview clips: built={built} pending={pending.Length} " +
            $"failed={failures.Count} canceled={canceled}." + failureDetails;
        Debug.Log($"[AC6ActionLibrary] {report}");
        return report;
    }

    // ---- step B core: pose JSON -> per-part clips ------------------------------------------

    private static List<MechBodyActionDefinition.SlotClip> BuildClipsForAllParts(
        ManifestEntry entry, PoseExport pose)
    {
        var slotClips = new List<MechBodyActionDefinition.SlotClip>();
        foreach (var (slot, token) in FrameSlots)
        {
            var glbPath = $"{RawGltfRoot}/{token}_M_{FrameSetCode}.glb";
            var partAsset = AssetDatabase.LoadMainAssetAtPath(glbPath) as GameObject;
            if (partAsset == null)
            {
                throw new InvalidOperationException($"frame part GLB missing: {glbPath}");
            }

            var clip = BuildPartClip(entry, pose, partAsset, token);
            if (clip != null)
            {
                slotClips.Add(new MechBodyActionDefinition.SlotClip { slot = slot, clip = clip });
            }
        }

        return slotClips;
    }

    /// <summary>
    /// Builds one part-space clip from the canonical-rig pose export. Curve values are
    /// retargeted with the same delta family AC6PartToGltf uses for equipment
    /// (q = q_anim ⊗ q_restCanonical⁻¹ ⊗ q_restPart, t = t_anim − t_restCanonical + t_restPart);
    /// when the part bind equals the canonical rest — the verified normal case for AC6 frame
    /// parts — the delta is identity and curves pass through unchanged. Curves are only
    /// emitted for bones the part actually owns; interface bones across parts stay consistent
    /// because every part receives the same source motion and the runtime shared-bone
    /// synchronizer reconciles residual drift.
    /// </summary>
    private static AnimationClip BuildPartClip(
        ManifestEntry entry, PoseExport pose, GameObject partAsset, string token)
    {
        // Part bind pose + relative curve paths come from a temporary instance.
        var instance = (GameObject)UnityEngine.Object.Instantiate(partAsset);
        try
        {
            var partBones = new Dictionary<string, (Transform transform, string path)>(StringComparer.Ordinal);
            CollectBonePaths(instance.transform, instance.transform, string.Empty, partBones);

            var clip = new AnimationClip
            {
                name = $"AC6_{entry.actionId}_{token}",
                frameRate = 1f / Mathf.Max(1e-5f, pose.FrameDuration),
            };

            var identityDeltas = 0;
            var mappedBones = 0;
            for (var boneIndex = 0; boneIndex < pose.Bones.Length; boneIndex++)
            {
                var bone = pose.Bones[boneIndex];
                if (!partBones.TryGetValue(bone.Name, out var target))
                {
                    continue;
                }

                mappedBones++;
                var restCanonical = bone.ReferenceLocalTransform;
                var restPart = target.transform;
                var rotDelta = Quaternion.Inverse(ToQuaternion(restCanonical.Rotation)) * restPart.localRotation;
                var posDelta = restPart.localPosition - ToVector(restCanonical.Translation);
                if (Quaternion.Angle(rotDelta, Quaternion.identity) < 0.05f && posDelta.sqrMagnitude < 1e-8f)
                {
                    identityDeltas++;
                }

                WriteBoneCurves(
                    clip,
                    target.path,
                    pose,
                    boneIndex,
                    rotDelta,
                    posDelta,
                    restPart.localRotation,
                    restPart.localPosition);
            }

            if (mappedBones == 0)
            {
                Debug.LogWarning($"[AC6ActionLibrary] {entry.actionId}/{token}: no canonical bones matched this part; clip skipped.");
                return null;
            }

            if (identityDeltas < mappedBones)
            {
                // Expected to be identity per the FLVER==HKX rest verification; a non-identity
                // delta means the retarget path is live and must be visually validated.
                Debug.LogWarning(
                    $"[AC6ActionLibrary] {entry.actionId}/{token}: {mappedBones - identityDeltas}/{mappedBones} " +
                    "bones use a non-identity rest delta — verify this part visually before shipping.");
            }

            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = entry.loop;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            clip.EnsureQuaternionContinuity();
            return SaveClipAsset(clip, entry.actionId, token);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(instance);
        }
    }

    private static void WriteBoneCurves(
        AnimationClip clip, string path, PoseExport pose, int boneIndex,
        Quaternion rotDelta, Vector3 posDelta,
        Quaternion targetRestRotation, Vector3 targetRestPosition)
    {
        var n = pose.Frames.Length;
        if (n == 0)
        {
            return;
        }

        var times = new float[n];
        var rotations = new Quaternion[n];
        var positions = new Vector3[n];
        var writesRotation = false;
        var writesPosition = false;
        for (var f = 0; f < n; f++)
        {
            var frame = pose.Frames[f];
            var local = frame.LocalTransforms[boneIndex];
            var q = Quaternion.Normalize(ToQuaternion(local.Rotation) * rotDelta);
            if (f > 0 && Quaternion.Dot(q, rotations[f - 1]) < 0f)
            {
                q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
            }

            var p = ToVector(local.Translation) + posDelta;
            times[f] = frame.Time;
            rotations[f] = q;
            positions[f] = p;
            writesRotation |= Quaternion.Angle(q, targetRestRotation) > RestRotationToleranceDegrees;
            writesPosition |= Vector3.Distance(p, targetRestPosition) > RestPositionTolerance;
        }

        if (writesRotation)
        {
            var indices = ReduceQuaternionKeys(times, rotations, RotationCurveToleranceDegrees);
            clip.SetCurve(path, typeof(Transform), "m_LocalRotation.x", BuildLinearCurve(indices, times, i => rotations[i].x));
            clip.SetCurve(path, typeof(Transform), "m_LocalRotation.y", BuildLinearCurve(indices, times, i => rotations[i].y));
            clip.SetCurve(path, typeof(Transform), "m_LocalRotation.z", BuildLinearCurve(indices, times, i => rotations[i].z));
            clip.SetCurve(path, typeof(Transform), "m_LocalRotation.w", BuildLinearCurve(indices, times, i => rotations[i].w));
        }

        if (writesPosition)
        {
            var indices = ReduceVectorKeys(times, positions, PositionCurveTolerance);
            clip.SetCurve(path, typeof(Transform), "m_LocalPosition.x", BuildLinearCurve(indices, times, i => positions[i].x));
            clip.SetCurve(path, typeof(Transform), "m_LocalPosition.y", BuildLinearCurve(indices, times, i => positions[i].y));
            clip.SetCurve(path, typeof(Transform), "m_LocalPosition.z", BuildLinearCurve(indices, times, i => positions[i].z));
        }
    }

    private static List<int> ReduceVectorKeys(float[] times, Vector3[] values, float tolerance)
    {
        return ReduceKeys(times, values.Length, (start, end, index) =>
        {
            var duration = times[end] - times[start];
            var t = duration > 1e-6f ? (times[index] - times[start]) / duration : 0f;
            return Vector3.Distance(values[index], Vector3.LerpUnclamped(values[start], values[end], t));
        }, tolerance);
    }

    private static List<int> ReduceQuaternionKeys(float[] times, Quaternion[] values, float toleranceDegrees)
    {
        return ReduceKeys(times, values.Length, (start, end, index) =>
        {
            var duration = times[end] - times[start];
            var t = duration > 1e-6f ? (times[index] - times[start]) / duration : 0f;
            return Quaternion.Angle(
                values[index],
                Quaternion.SlerpUnclamped(values[start], values[end], t));
        }, toleranceDegrees);
    }

    private static List<int> ReduceKeys(
        float[] times,
        int count,
        Func<int, int, int, float> calculateError,
        float tolerance)
    {
        var keep = new SortedSet<int>();
        if (count <= 0)
        {
            return keep.ToList();
        }

        keep.Add(0);
        if (count == 1)
        {
            return keep.ToList();
        }

        keep.Add(count - 1);
        var segments = new Stack<(int start, int end)>();
        segments.Push((0, count - 1));
        while (segments.Count > 0)
        {
            var segment = segments.Pop();
            if (segment.end - segment.start <= 1)
            {
                continue;
            }

            var maxError = tolerance;
            var split = -1;
            for (var i = segment.start + 1; i < segment.end; i++)
            {
                var error = calculateError(segment.start, segment.end, i);
                if (error > maxError)
                {
                    maxError = error;
                    split = i;
                }
            }

            if (split < 0)
            {
                continue;
            }

            keep.Add(split);
            segments.Push((segment.start, split));
            segments.Push((split, segment.end));
        }

        return keep.ToList();
    }

    private static AnimationCurve BuildLinearCurve(
        IReadOnlyList<int> indices,
        float[] times,
        Func<int, float> valueAt)
    {
        var keys = new Keyframe[indices.Count];
        for (var i = 0; i < indices.Count; i++)
        {
            var sourceIndex = indices[i];
            keys[i] = new Keyframe(times[sourceIndex], valueAt(sourceIndex));
        }

        var curve = new AnimationCurve(keys);
        for (var i = 0; i < keys.Length; i++)
        {
            AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
            AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
        }

        return curve;
    }

    private static AnimationClip SaveClipAsset(AnimationClip clip, string actionId, string token)
    {
        var folder = $"{GeneratedActionsRoot}/{actionId}";
        EnsureFolder(folder);
        var assetPath = $"{folder}/{clip.name}.anim";
        var existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(assetPath);
        if (existing == null)
        {
            AssetDatabase.CreateAsset(clip, assetPath);
            return clip;
        }

        // Keep the asset GUID stable so registry/scene references survive rebuilds.
        EditorUtility.CopySerialized(clip, existing);
        existing.name = clip.name;
        EditorUtility.SetDirty(existing);
        return existing;
    }

    // ---- registry upsert ---------------------------------------------------------------------

    private static void UpsertRegistryEntry(
        MechActionRegistry registry, ManifestEntry entry,
        List<MechBodyActionDefinition.SlotClip> slotClips)
    {
        var definition = registry.EditorActions.FirstOrDefault(a =>
            a != null && string.Equals(a.actionId, entry.actionId, StringComparison.OrdinalIgnoreCase));
        if (definition == null)
        {
            definition = new MechBodyActionDefinition { actionId = entry.actionId };
            registry.EditorActions.Add(definition);
        }

        definition.layer = string.Equals(entry.layer, "UpperBody", StringComparison.OrdinalIgnoreCase)
            ? MechBodyActionLayer.UpperBody
            : MechBodyActionLayer.FullBody;
        definition.displayNameZh = entry.displayNameZh;
        definition.categoryZh = entry.categoryZh;
        definition.reviewStatus = entry.reviewStatus;
        definition.loop = entry.loop;
        definition.blendInSeconds = entry.blendInSeconds;
        definition.blendOutSeconds = entry.blendOutSeconds;
        definition.slotClips = slotClips;
    }

    private static MechActionRegistry LoadOrCreateRegistry()
    {
        var registry = AssetDatabase.LoadAssetAtPath<MechActionRegistry>(RegistryAssetPath);
        if (registry == null)
        {
            EnsureFolder(GeneratedActionsRoot);
            var invalidAsset = AssetDatabase.LoadMainAssetAtPath(RegistryAssetPath);
            if (invalidAsset != null || File.Exists(RegistryAssetPath))
            {
                Debug.LogWarning(
                    $"[AC6ActionLibrary] Replacing invalid action registry at {RegistryAssetPath}. " +
                    "This usually means it was generated in a project copy with different script meta GUIDs.");
                AssetDatabase.DeleteAsset(RegistryAssetPath);
            }

            registry = ScriptableObject.CreateInstance<MechActionRegistry>();
            AssetDatabase.CreateAsset(registry, RegistryAssetPath);
        }

        return registry;
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static Manifest LoadConfirmedCatalog()
    {
        if (!File.Exists(CatalogPath))
        {
            return null;
        }

        var catalog = JsonUtility.FromJson<CatalogDocument>(File.ReadAllText(CatalogPath));
        if (catalog?.entries == null)
        {
            return null;
        }

        return new Manifest
        {
            poseJsonRoot = "Tools/LocalOut/actions",
            entries = catalog.entries
                .Where(entry => entry != null &&
                    (string.Equals(entry.reviewStatus, "OfficialMapped", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(entry.reviewStatus, "Confirmed", StringComparison.OrdinalIgnoreCase)))
                .Select(entry => new ManifestEntry
                {
                    actionId = entry.actionId,
                    hkx = entry.actionId,
                    layer = entry.layer,
                    loop = entry.loop,
                    blendInSeconds = 0.15f,
                    blendOutSeconds = 0.15f,
                    enabled = true,
                    reviewStatus = entry.reviewStatus,
                    displayNameZh = entry.displayNameZh,
                    categoryZh = entry.categoryZh
                })
                .ToArray()
        };
    }

    private static void CollectBonePaths(
        Transform root, Transform current, string path,
        IDictionary<string, (Transform, string)> result)
    {
        if (current != root && !result.ContainsKey(current.name))
        {
            result.Add(current.name, (current, path));
        }

        for (var i = 0; i < current.childCount; i++)
        {
            var child = current.GetChild(i);
            var childPath = string.IsNullOrEmpty(path) ? child.name : path + "/" + child.name;
            CollectBonePaths(root, child, childPath, result);
        }
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

    // AC6PartToGltf now performs the source X reflection before writing glTF. glTFast reflects it
    // back while importing, so imported transforms and raw HKX samples share the same Unity-space
    // values here. Applying the old second reflection mirrored anatomy and animation deltas.
    private static Quaternion ToQuaternion(PoseQuaternion q) =>
        new Quaternion(q.X, q.Y, q.Z, q.W);

    private static Vector3 ToVector(PoseVector v) => new Vector3(v.X, v.Y, v.Z);

    // ---- manifest / pose JSON DTOs (schemas owned by the ps1 driver and AC6HkxPoseExporter) ----

    [Serializable]
    private sealed class Manifest
    {
        public string poseJsonRoot;
        public ManifestEntry[] entries;
    }

    [Serializable]
    private sealed class ManifestEntry
    {
        public string actionId;
        public string hkx;
        public string layer;
        public bool loop;
        public float blendInSeconds;
        public float blendOutSeconds;
        public bool enabled;
        public string note;
        public string reviewStatus;
        public string displayNameZh;
        public string categoryZh;
    }

    [Serializable]
    private sealed class CatalogDocument
    {
        public CatalogEntry[] entries;
    }

    [Serializable]
    private sealed class CatalogEntry
    {
        public string actionId;
        public string displayNameZh;
        public string categoryZh;
        public string reviewStatus;
        public string layer;
        public bool loop;
        public bool sourceAvailable;
        public bool poseAvailable;
    }

    [Serializable]
    private sealed class PoseExport
    {
        public float Duration;
        public float FrameDuration;
        public PoseBone[] Bones;
        public PoseFrame[] Frames;
    }

    [Serializable]
    private sealed class PoseBone
    {
        public int Index;
        public string Name;
        public int ParentIndex;
        public PoseTransform ReferenceLocalTransform;
    }

    [Serializable]
    private sealed class PoseFrame
    {
        public int Frame;
        public float Time;
        public PoseTransform[] LocalTransforms;
    }

    [Serializable]
    private sealed class PoseTransform
    {
        public PoseVector Translation;
        public PoseQuaternion Rotation;
        public PoseVector Scale;
    }

    [Serializable]
    private sealed class PoseVector
    {
        public float X;
        public float Y;
        public float Z;
    }

    [Serializable]
    private sealed class PoseQuaternion
    {
        public float X;
        public float Y;
        public float Z;
        public float W;
    }
}
