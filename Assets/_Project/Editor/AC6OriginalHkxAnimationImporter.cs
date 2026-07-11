using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;

public static class AC6OriginalHkxAnimationImporter
{
    private const string SampleName = "AC6_C0000_A000_010900";
    private const string AnimationFileName = "a000_010900.hkx";
    private const string ImportRoot = "Assets/_Project/ImportedFromGame/Animations/C0000_A000_010900";
    private const string PoseJsonPath = ImportRoot + "/" + SampleName + "_pose.json";
    private const string ClipPath = ImportRoot + "/" + SampleName + ".clip";
    private const string ControllerPath = ImportRoot + "/" + SampleName + ".controller";
    private const string PreviewPrefabPath = ImportRoot + "/" + SampleName + "_SkeletonPreview.prefab";
    private const string MarkerMaterialPath = ImportRoot + "/AC6_OriginalSkeletonMarker.mat";
    private const string ScenePreviewName = SampleName + "_SkeletonPreview_SCENE";

    private const string GameBaseAnibnd =
        @"E:\SteamLibrary\steamapps\common\ARMORED CORE VI FIRES OF RUBICON\Game\chr\c0000.anibnd.dcx";

    private const string GameAnimAnibnd =
        @"E:\SteamLibrary\steamapps\common\ARMORED CORE VI FIRES OF RUBICON\Game\chr\c0000_a000_00.anibnd.dcx";

    private const int SkeletonBindId = 4000000;
    private const int CompendiumBindId = 7000000;
    private const int AnimationBindId = 1000010900;

    [MenuItem("Tools/ACunicombat/Legacy/Animation/Import AC6 Original HKX Sample")]
    public static void ImportOriginalHkxSample()
    {
        EnsureFolder(ImportRoot);

        if (!File.Exists(ProjectAbsolutePath(PoseJsonPath)))
        {
            if (!TryGeneratePoseJson())
            {
                return;
            }
        }

        var pose = LoadPoseExport();
        if (pose == null || pose.Bones == null || pose.Frames == null || pose.Bones.Length == 0 || pose.Frames.Length == 0)
        {
            EditorUtility.DisplayDialog("AC6 Original HKX", $"Pose JSON is invalid:\n{PoseJsonPath}", "OK");
            return;
        }

        var clip = CreateOrUpdateClip(pose);
        var controller = CreateOrUpdateController(clip);
        var previewPrefab = CreateOrUpdatePreviewPrefab(pose, controller);
        var scenePreview = InstantiateScenePreview(previewPrefab);
        var attachedModel = scenePreview != null
            ? AC6UnifiedSkinnedHkxPreviewImporter.CreateCleanPreview(false)
            : null;

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        if (attachedModel != null)
        {
            Selection.activeObject = attachedModel;
        }
        else if (scenePreview != null)
        {
            Selection.activeObject = scenePreview;
        }
        else if (previewPrefab != null)
        {
            Selection.activeObject = previewPrefab;
        }
        else
        {
            Selection.activeObject = clip;
        }

        EditorGUIUtility.PingObject(Selection.activeObject);

        EditorUtility.DisplayDialog(
            "AC6 Original HKX",
            $"Imported original HKX animation sample.\nFrames: {pose.ExportedFrameCount}/{pose.SourceFrameCount}\nBones: {pose.Bones.Length}\nClip: {ClipPath}\n\nClean skinned preview: {(attachedModel != null ? attachedModel.name : "not created")}\nPress Play and watch the clean unified skinned preview, not Sample_PlayerMech_Rig or the blue-dot skeleton preview.",
            "OK");
    }

    private static bool TryGeneratePoseJson()
    {
        var unityDevRoot = UnityDevRoot();
        var dotnet = Path.Combine(unityDevRoot, "Tools", "dotnet9sdk", "dotnet.exe");
        var probeDir = Path.Combine(unityDevRoot, "Tools", "AC6AnimExportProbe", "bin", "Debug", "net9.0");
        var poseDir = Path.Combine(unityDevRoot, "Tools", "AC6HkxPoseExporter", "bin", "Debug", "net9.0");
        var probe = Path.Combine(probeDir, "AC6AnimExportProbe.dll");
        var poseExporter = Path.Combine(poseDir, "AC6HkxPoseExporter.dll");
        var tempDir = Path.Combine(unityDevRoot, "tmp", "ac6_anim_import", "c0000_a000_010900");
        var tempJson = Path.Combine(tempDir, SampleName + "_pose.json");

        var required = new[] { dotnet, probe, poseExporter, GameBaseAnibnd, GameAnimAnibnd };
        var missing = required.Where(path => !File.Exists(path)).ToArray();
        if (missing.Length > 0)
        {
            EditorUtility.DisplayDialog("AC6 Original HKX", "Missing required files:\n" + string.Join("\n", missing), "OK");
            return false;
        }

        Directory.CreateDirectory(tempDir);

        if (!RunTool(dotnet, probeDir, probe, "--extract-bnd", GameBaseAnibnd, tempDir, SkeletonBindId.ToString()))
            return false;

        if (!RunTool(dotnet, probeDir, probe, "--extract-bnd", GameAnimAnibnd, tempDir, CompendiumBindId.ToString(), AnimationBindId.ToString()))
            return false;

        var skeleton = Path.Combine(tempDir, $"{SkeletonBindId}_Skeleton.hkx");
        var compendium = Path.Combine(tempDir, $"{CompendiumBindId}_a000_00.compendium");
        var animation = Path.Combine(tempDir, $"{AnimationBindId}_{AnimationFileName}");

        if (!RunTool(
                dotnet,
                poseDir,
                poseExporter,
                "--skeleton", skeleton,
                "--anim", animation,
                "--compendium", compendium,
                "--out", tempJson,
                "--id", AnimationBindId.ToString()))
        {
            return false;
        }

        File.Copy(tempJson, ProjectAbsolutePath(PoseJsonPath), true);
        AssetDatabase.ImportAsset(PoseJsonPath, ImportAssetOptions.ForceUpdate);
        return true;
    }

    private static PoseExport LoadPoseExport()
    {
        var json = File.ReadAllText(ProjectAbsolutePath(PoseJsonPath));
        return JsonUtility.FromJson<PoseExport>(json);
    }

    private static AnimationClip CreateOrUpdateClip(PoseExport pose)
    {
        if (AssetDatabase.LoadAssetAtPath<AnimationClip>(ClipPath) != null)
        {
            AssetDatabase.DeleteAsset(ClipPath);
        }

        var clip = new AnimationClip
        {
            name = SampleName,
            frameRate = pose.FrameDuration > 0f ? 1f / pose.FrameDuration : 30f,
            wrapMode = WrapMode.Loop,
            legacy = false,
        };

        var bonePaths = BuildBonePaths(pose.Bones);
        for (var boneIndex = 0; boneIndex < pose.Bones.Length; boneIndex++)
        {
            var path = bonePaths[boneIndex];
            SetVector3Curves(clip, path, "m_LocalPosition", pose.Frames, boneIndex, tr => tr.Translation);
            SetQuaternionCurves(clip, path, pose.Frames, boneIndex);
            SetVector3Curves(clip, path, "m_LocalScale", pose.Frames, boneIndex, tr => tr.Scale);
        }

        clip.EnsureQuaternionContinuity();
        AssetDatabase.CreateAsset(clip, ClipPath);
        return clip;
    }

    private static void SetVector3Curves(
        AnimationClip clip,
        string path,
        string propertyPrefix,
        PoseFrame[] frames,
        int boneIndex,
        Func<PoseTransform, Vec3> read)
    {
        var x = new Keyframe[frames.Length];
        var y = new Keyframe[frames.Length];
        var z = new Keyframe[frames.Length];

        for (var i = 0; i < frames.Length; i++)
        {
            var value = read(frames[i].LocalTransforms[boneIndex]);
            var time = frames[i].Time;
            x[i] = new Keyframe(time, value.X);
            y[i] = new Keyframe(time, value.Y);
            z[i] = new Keyframe(time, value.Z);
        }

        AnimationUtility.SetEditorCurve(clip, Binding(path, propertyPrefix + ".x"), new AnimationCurve(x));
        AnimationUtility.SetEditorCurve(clip, Binding(path, propertyPrefix + ".y"), new AnimationCurve(y));
        AnimationUtility.SetEditorCurve(clip, Binding(path, propertyPrefix + ".z"), new AnimationCurve(z));
    }

    private static void SetQuaternionCurves(AnimationClip clip, string path, PoseFrame[] frames, int boneIndex)
    {
        var x = new Keyframe[frames.Length];
        var y = new Keyframe[frames.Length];
        var z = new Keyframe[frames.Length];
        var w = new Keyframe[frames.Length];
        var previous = new Quat { W = 1f };

        for (var i = 0; i < frames.Length; i++)
        {
            var q = frames[i].LocalTransforms[boneIndex].Rotation;
            if (i > 0 && Dot(previous, q) < 0f)
            {
                q.X = -q.X;
                q.Y = -q.Y;
                q.Z = -q.Z;
                q.W = -q.W;
            }

            previous = q;
            var time = frames[i].Time;
            x[i] = new Keyframe(time, q.X);
            y[i] = new Keyframe(time, q.Y);
            z[i] = new Keyframe(time, q.Z);
            w[i] = new Keyframe(time, q.W);
        }

        AnimationUtility.SetEditorCurve(clip, Binding(path, "m_LocalRotation.x"), new AnimationCurve(x));
        AnimationUtility.SetEditorCurve(clip, Binding(path, "m_LocalRotation.y"), new AnimationCurve(y));
        AnimationUtility.SetEditorCurve(clip, Binding(path, "m_LocalRotation.z"), new AnimationCurve(z));
        AnimationUtility.SetEditorCurve(clip, Binding(path, "m_LocalRotation.w"), new AnimationCurve(w));
    }

    private static EditorCurveBinding Binding(string path, string propertyName)
    {
        return EditorCurveBinding.FloatCurve(path, typeof(Transform), propertyName);
    }

    private static AnimatorController CreateOrUpdateController(AnimationClip clip)
    {
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
        if (controller == null)
        {
            controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
        }

        var stateMachine = controller.layers[0].stateMachine;
        foreach (var childState in stateMachine.states.ToArray())
        {
            stateMachine.RemoveState(childState.state);
        }

        var state = stateMachine.AddState("Original HKX Sample");
        state.motion = clip;
        state.writeDefaultValues = true;
        stateMachine.defaultState = state;

        EditorUtility.SetDirty(controller);
        return controller;
    }

    private static GameObject CreateOrUpdatePreviewPrefab(PoseExport pose, RuntimeAnimatorController controller)
    {
        var root = new GameObject(SampleName + "_SkeletonPreview");
        var material = CreateOrUpdateMarkerMaterial();
        var transforms = new Transform[pose.Bones.Length];

        for (var i = 0; i < pose.Bones.Length; i++)
        {
            var bone = pose.Bones[i];
            var boneObject = new GameObject(string.IsNullOrWhiteSpace(bone.Name) ? $"Bone_{i:000}" : bone.Name);
            transforms[i] = boneObject.transform;

            var parent = bone.ParentIndex >= 0 && bone.ParentIndex < transforms.Length && transforms[bone.ParentIndex] != null
                ? transforms[bone.ParentIndex]
                : root.transform;
            transforms[i].SetParent(parent, false);

            ApplyLocalTransform(transforms[i], bone.ReferenceLocalTransform);
            AddMarker(transforms[i], material);
        }

        var animator = root.AddComponent<Animator>();
        animator.runtimeAnimatorController = controller;
        animator.applyRootMotion = false;
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

        var prefab = PrefabUtility.SaveAsPrefabAsset(root, PreviewPrefabPath);
        UnityEngine.Object.DestroyImmediate(root);
        return prefab;
    }

    private static GameObject InstantiateScenePreview(GameObject previewPrefab)
    {
        if (previewPrefab == null)
        {
            return null;
        }

        var existing = GameObject.Find(ScenePreviewName);
        if (existing != null)
        {
            Undo.DestroyObjectImmediate(existing);
        }

        var instance = PrefabUtility.InstantiatePrefab(previewPrefab) as GameObject;
        if (instance == null)
        {
            instance = UnityEngine.Object.Instantiate(previewPrefab);
        }

        instance.name = ScenePreviewName;
        instance.transform.position = FindPreviewPosition();
        instance.transform.rotation = Quaternion.identity;

        Undo.RegisterCreatedObjectUndo(instance, "Create AC6 Original HKX Preview");
        EditorSceneManager.MarkSceneDirty(instance.scene);
        return instance;
    }

    private static Vector3 FindPreviewPosition()
    {
        var selected = Selection.activeGameObject;
        if (selected != null)
        {
            return selected.transform.position + new Vector3(12f, 0f, -8f);
        }

        var mech = GameObject.Find("Sample_PlayerMech_Rig");
        return mech != null
            ? mech.transform.position + new Vector3(12f, 0f, -8f)
            : new Vector3(12f, 0f, -8f);
    }

    private static void ApplyLocalTransform(Transform target, PoseTransform pose)
    {
        target.localPosition = new Vector3(pose.Translation.X, pose.Translation.Y, pose.Translation.Z);
        target.localRotation = new Quaternion(pose.Rotation.X, pose.Rotation.Y, pose.Rotation.Z, pose.Rotation.W);
        target.localScale = new Vector3(pose.Scale.X, pose.Scale.Y, pose.Scale.Z);
    }

    private static void AddMarker(Transform parent, Material material)
    {
        var marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
        marker.name = "_marker";
        marker.transform.SetParent(parent, false);
        marker.transform.localPosition = Vector3.zero;
        marker.transform.localRotation = Quaternion.identity;
        marker.transform.localScale = Vector3.one * 0.08f;

        var collider = marker.GetComponent<Collider>();
        if (collider != null)
        {
            UnityEngine.Object.DestroyImmediate(collider);
        }

        var renderer = marker.GetComponent<MeshRenderer>();
        if (renderer != null)
        {
            renderer.sharedMaterial = material;
        }
    }

    private static Material CreateOrUpdateMarkerMaterial()
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(MarkerMaterialPath);
        if (material == null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            material = new Material(shader)
            {
                name = "AC6_OriginalSkeletonMarker",
                color = new Color(0.1f, 0.8f, 1f, 1f),
            };
            AssetDatabase.CreateAsset(material, MarkerMaterialPath);
        }

        material.color = new Color(0.1f, 0.8f, 1f, 1f);
        EditorUtility.SetDirty(material);
        return material;
    }

    private static string[] BuildBonePaths(PoseBone[] bones)
    {
        var paths = new string[bones.Length];
        for (var i = 0; i < bones.Length; i++)
        {
            paths[i] = BuildBonePath(bones, paths, i);
        }

        return paths;
    }

    private static string BuildBonePath(PoseBone[] bones, string[] paths, int index)
    {
        if (!string.IsNullOrEmpty(paths[index]))
        {
            return paths[index];
        }

        var name = string.IsNullOrWhiteSpace(bones[index].Name) ? $"Bone_{index:000}" : bones[index].Name;
        var parent = bones[index].ParentIndex;
        paths[index] = parent >= 0 && parent < bones.Length
            ? BuildBonePath(bones, paths, parent) + "/" + name
            : name;
        return paths[index];
    }

    private static float Dot(Quat a, Quat b)
    {
        return a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
    }

    private static bool RunTool(string dotnet, string workingDirectory, params string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = dotnet,
            Arguments = string.Join(" ", args.Select(QuoteArg)),
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(startInfo);
        if (process == null)
        {
            EditorUtility.DisplayDialog("AC6 Original HKX", $"Failed to start:\n{dotnet}", "OK");
            return false;
        }

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(120000))
        {
            process.Kill();
            EditorUtility.DisplayDialog("AC6 Original HKX", $"Timed out:\n{dotnet} {startInfo.Arguments}", "OK");
            return false;
        }

        if (process.ExitCode != 0)
        {
            EditorUtility.DisplayDialog(
                "AC6 Original HKX",
                $"Command failed ({process.ExitCode}):\n{dotnet} {startInfo.Arguments}\n\n{stdout}\n{stderr}",
                "OK");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(stdout))
        {
            Debug.Log(stdout);
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            Debug.LogWarning(stderr);
        }

        return true;
    }

    private static string QuoteArg(string arg)
    {
        return "\"" + arg.Replace("\"", "\\\"") + "\"";
    }

    private static void EnsureFolder(string folderPath)
    {
        if (AssetDatabase.IsValidFolder(folderPath))
        {
            return;
        }

        var parent = Path.GetDirectoryName(folderPath)?.Replace("\\", "/");
        if (string.IsNullOrEmpty(parent))
        {
            return;
        }

        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(folderPath));
    }

    private static string ProjectAbsolutePath(string assetPath)
    {
        var projectRoot = ProjectRoot();
        return Path.Combine(projectRoot, assetPath.Replace("/", Path.DirectorySeparatorChar.ToString()));
    }

    private static string ProjectRoot()
    {
        return Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
    }

    private static string UnityDevRoot()
    {
        return Directory.GetParent(ProjectRoot())?.FullName ?? ProjectRoot();
    }

    [Serializable]
    private sealed class PoseExport
    {
        public string SourceSkeleton;
        public string SourceAnimation;
        public string SourceCompendium;
        public string AnimationName;
        public float Duration;
        public float FrameDuration;
        public int SourceFrameCount;
        public int ExportedFrameCount;
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
    private struct PoseTransform
    {
        public Vec3 Translation;
        public Quat Rotation;
        public Vec3 Scale;
    }

    [Serializable]
    private struct Vec3
    {
        public float X;
        public float Y;
        public float Z;
    }

    [Serializable]
    private struct Quat
    {
        public float X;
        public float Y;
        public float Z;
        public float W;
    }
}
