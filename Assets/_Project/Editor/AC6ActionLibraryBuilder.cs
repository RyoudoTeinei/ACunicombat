using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ACunicombat.Mechs;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Step B of the action-library pipeline (step A is Tools/BuildAC6ActionClips.ps1, which turns
/// manifest HKX entries into full-sample pose JSONs). This builder turns each pose JSON into
/// one AnimationClip per frame part — retargeted from the canonical c0000 rest pose onto the
/// part's own bind pose — and upserts everything into a single MechActionRegistry asset.
/// Adding an action to the game is therefore: one manifest entry, run the ps1, run this menu.
/// Pipeline contract and retarget math: Docs/AC6动作库管线_设计.md.
/// </summary>
public static class AC6ActionLibraryBuilder
{
    private const string ManifestPath = "Tools/AssetManifests/c0000_action_manifest.json";
    private const string RawGltfRoot = "Assets/_Project/LocalSamples/AC6SkinnedModular/Raw/Gltf";
    private const string GeneratedActionsRoot = "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Actions";
    private const string RegistryAssetPath = GeneratedActionsRoot + "/AC6ActionRegistry.asset";
    private const string FrameSetCode = "1010";

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

    public static string Build()
    {
        var manifest = LoadManifest();
        if (manifest == null)
        {
            return $"Manifest missing or unreadable: {ManifestPath}";
        }

        var registry = LoadOrCreateRegistry();
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

                WriteBoneCurves(clip, target.path, pose, boneIndex, rotDelta, posDelta);
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
        Quaternion rotDelta, Vector3 posDelta)
    {
        var n = pose.Frames.Length;
        var rx = new Keyframe[n];
        var ry = new Keyframe[n];
        var rz = new Keyframe[n];
        var rw = new Keyframe[n];
        var px = new Keyframe[n];
        var py = new Keyframe[n];
        var pz = new Keyframe[n];
        for (var f = 0; f < n; f++)
        {
            var frame = pose.Frames[f];
            var local = frame.LocalTransforms[boneIndex];
            var q = ToQuaternion(local.Rotation) * rotDelta;
            var p = ToVector(local.Translation) + posDelta;
            var t = frame.Time;
            rx[f] = new Keyframe(t, q.x);
            ry[f] = new Keyframe(t, q.y);
            rz[f] = new Keyframe(t, q.z);
            rw[f] = new Keyframe(t, q.w);
            px[f] = new Keyframe(t, p.x);
            py[f] = new Keyframe(t, p.y);
            pz[f] = new Keyframe(t, p.z);
        }

        clip.SetCurve(path, typeof(Transform), "m_LocalRotation.x", new AnimationCurve(rx));
        clip.SetCurve(path, typeof(Transform), "m_LocalRotation.y", new AnimationCurve(ry));
        clip.SetCurve(path, typeof(Transform), "m_LocalRotation.z", new AnimationCurve(rz));
        clip.SetCurve(path, typeof(Transform), "m_LocalRotation.w", new AnimationCurve(rw));
        clip.SetCurve(path, typeof(Transform), "m_LocalPosition.x", new AnimationCurve(px));
        clip.SetCurve(path, typeof(Transform), "m_LocalPosition.y", new AnimationCurve(py));
        clip.SetCurve(path, typeof(Transform), "m_LocalPosition.z", new AnimationCurve(pz));
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
            registry = ScriptableObject.CreateInstance<MechActionRegistry>();
            AssetDatabase.CreateAsset(registry, RegistryAssetPath);
        }

        return registry;
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static Manifest LoadManifest()
    {
        if (!File.Exists(ManifestPath))
        {
            return null;
        }

        var manifest = JsonUtility.FromJson<Manifest>(File.ReadAllText(ManifestPath));
        return manifest?.entries == null ? null : manifest;
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

    private static Quaternion ToQuaternion(PoseQuaternion q) => new Quaternion(q.X, q.Y, q.Z, q.W);

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
