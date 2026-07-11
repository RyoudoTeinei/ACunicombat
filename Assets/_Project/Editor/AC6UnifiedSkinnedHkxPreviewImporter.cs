using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class AC6UnifiedSkinnedHkxPreviewImporter
{
    private const string SampleName = "AC6_C0000_A000_010900";
    private const string ImportRoot = "Assets/_Project/ImportedFromGame/Animations/C0000_A000_010900";
    private const string PoseJsonPath = ImportRoot + "/" + SampleName + "_pose.json";
    private const string ModelPath =
        "Assets/_Project/ImportedFromBlend/MELANDER_1010_HKXSkinned/FBX/AC6_HKX_MELANDER_1010_SKINNED.fbx";
    private const string BakedMaterialRoot = "Assets/_Project/ImportedFromBlend/MELANDER_1010/UnityMaterials";

    private const string ClipPath = ImportRoot + "/" + SampleName + "_HkxRestSkinned.clip";
    private const string ControllerPath = ImportRoot + "/" + SampleName + "_HkxRestSkinned.controller";
    private const string PrefabPath = ImportRoot + "/" + SampleName + "_HkxRestSkinnedPreview.prefab";
    private const string ScenePreviewName = SampleName + "_HkxRestSkinnedPreview_SCENE";
    private const string ModelInstanceName = "_HkxRestSkinnedMelander";
    private const string OldSkeletonPreviewName = SampleName + "_SkeletonPreview_SCENE";
    private const string OldUnifiedPreviewName = SampleName + "_UnifiedSkinnedPreview_SCENE";
    private const string RestPoseDiagnosticPath = "Temp/AC6RigDiagnostics/unity_hkx_rest_model_report.json";

    [MenuItem("Tools/ACunicombat/Legacy/Animation/Create Clean Unified Skinned HKX Preview")]
    public static void CreateCleanPreviewMenu()
    {
        CreateCleanPreview(true);
    }

    [MenuItem("Tools/ACunicombat/Legacy/Animation/Diagnose HKX Rest Skinned Model")]
    public static void DiagnoseModelRestPoseMenu()
    {
        DiagnoseModelRestPose(true);
    }

    public static void DiagnoseModelRestPoseBatch()
    {
        DiagnoseModelRestPose(false);
    }

    public static GameObject CreateCleanPreview(bool showDialog)
    {
        AssetDatabase.Refresh();
        EnsureFolder(ImportRoot);
        ConfigureModelImporter();

        var modelAsset = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
        if (modelAsset == null)
        {
            Display(showDialog, $"Unified skinned model was not found:\n{ModelPath}");
            return null;
        }

        if (!File.Exists(ProjectAbsolutePath(PoseJsonPath)))
        {
            Display(showDialog, $"Pose JSON was not found:\n{PoseJsonPath}\n\nRun Import AC6 Original HKX Sample first.");
            return null;
        }

        var pose = LoadPoseExport();
        if (pose == null || pose.Bones == null || pose.Frames == null || pose.Bones.Length == 0 || pose.Frames.Length == 0)
        {
            Display(showDialog, $"Pose JSON is invalid:\n{PoseJsonPath}");
            return null;
        }

        var prefab = CreateOrUpdatePrefab(
            modelAsset,
            pose,
            out var animatedBoneCount,
            out var materialStats,
            out var orientationStats);
        if (prefab == null)
        {
            Display(showDialog, "Failed to create unified skinned preview prefab.");
            return null;
        }

        var sceneInstance = InstantiateScenePreview(prefab);
        HideConfusingOldSceneObjects(sceneInstance);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Selection.activeGameObject = sceneInstance;
        EditorGUIUtility.PingObject(sceneInstance);

        var message =
            $"Created clean unified skinned HKX preview.\n" +
            $"Scene object: {ScenePreviewName}\n" +
            $"Model: {ModelPath}\n" +
            $"Animated model bones: {animatedBoneCount}/{pose.Bones.Length}\n" +
            $"Material overrides: {materialStats.appliedRenderers}/{materialStats.rendererCount} renderers, " +
            $"{materialStats.materialCount} baked materials scanned\n" +
            $"Preview orientation: {orientationStats.eulerAngles} / bounds {orientationStats.boundsSize}\n" +
            $"Clip: {ClipPath}\n\n" +
            "Press Play and watch this single selected object. The old blue-dot skeleton preview, old unified preview, and Sample_PlayerMech_Rig were hidden to avoid confusion.";

        Debug.Log(message, sceneInstance);
        Display(showDialog, message);
        return sceneInstance;
    }

    private static GameObject CreateOrUpdatePrefab(
        GameObject modelAsset,
        PoseExport pose,
        out int animatedBoneCount,
        out MaterialOverrideStats materialStats,
        out PreviewOrientationStats orientationStats)
    {
        var root = new GameObject(SampleName + "_HkxRestSkinnedPreview");
        var modelInstance = PrefabUtility.InstantiatePrefab(modelAsset, root.transform) as GameObject;
        if (modelInstance == null)
        {
            modelInstance = UnityEngine.Object.Instantiate(modelAsset, root.transform);
        }

        modelInstance.name = ModelInstanceName;
        modelInstance.transform.localPosition = Vector3.zero;
        modelInstance.transform.localRotation = Quaternion.identity;
        modelInstance.transform.localScale = Vector3.one;
        orientationStats = ApplyPreviewOrientation(modelInstance);
        materialStats = ApplyBakedMaterialOverrides(modelInstance);

        var modelBones = BuildModelBoneMap(root.transform, modelInstance.transform);
        var clip = CreateOrUpdateClip(pose, modelBones, out animatedBoneCount);
        var controller = CreateOrUpdateController(clip);

        var animator = root.AddComponent<Animator>();
        animator.runtimeAnimatorController = controller;
        animator.applyRootMotion = false;
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

        var prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        UnityEngine.Object.DestroyImmediate(root);
        return prefab;
    }

    private static Dictionary<string, ModelBoneInfo> BuildModelBoneMap(Transform root, Transform modelRoot)
    {
        var map = new Dictionary<string, ModelBoneInfo>(StringComparer.Ordinal);
        var preferredBones = new HashSet<Transform>();
        var renderers = modelRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        for (var i = 0; i < renderers.Length; i++)
        {
            AddBoneAndParents(preferredBones, renderers[i].rootBone, modelRoot);
            var bones = renderers[i].bones;
            for (var b = 0; b < bones.Length; b++)
            {
                AddBoneAndParents(preferredBones, bones[b], modelRoot);
            }
        }

        foreach (var bone in preferredBones)
        {
            if (bone != null && !map.ContainsKey(bone.name))
            {
                map.Add(bone.name, ModelBoneInfo.From(root, bone));
            }
        }

        var allTransforms = modelRoot.GetComponentsInChildren<Transform>(true);
        for (var i = 0; i < allTransforms.Length; i++)
        {
            var current = allTransforms[i];
            if (!map.ContainsKey(current.name))
            {
                map.Add(current.name, ModelBoneInfo.From(root, current));
            }
        }

        return map;
    }

    public static void DiagnoseModelRestPose(bool showDialog)
    {
        AssetDatabase.Refresh();
        ConfigureModelImporter();

        var modelAsset = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
        if (modelAsset == null)
        {
            Display(showDialog, $"HKX-rest skinned model was not found:\n{ModelPath}");
            return;
        }

        var pose = LoadPoseExport();
        if (pose == null || pose.Bones == null || pose.Bones.Length == 0)
        {
            Display(showDialog, $"Pose JSON is invalid:\n{PoseJsonPath}");
            return;
        }

        var root = new GameObject(SampleName + "_UnityRestPoseDiagnostic");
        var modelInstance = PrefabUtility.InstantiatePrefab(modelAsset, root.transform) as GameObject;
        if (modelInstance == null)
        {
            modelInstance = UnityEngine.Object.Instantiate(modelAsset, root.transform);
        }

        try
        {
            var modelBones = BuildModelBoneMap(root.transform, modelInstance.transform);
            var rows = new List<RestPoseBoneError>();
            var missing = new List<string>();
            for (var boneIndex = 0; boneIndex < pose.Bones.Length; boneIndex++)
            {
                var bone = pose.Bones[boneIndex];
                if (string.IsNullOrWhiteSpace(bone.Name) || !modelBones.TryGetValue(bone.Name, out var modelBone))
                {
                    missing.Add(bone.Name);
                    continue;
                }

                var expectedPosition = ToVector3(bone.ReferenceLocalTransform.Translation);
                var expectedRotation = ToQuaternion(bone.ReferenceLocalTransform.Rotation);
                rows.Add(new RestPoseBoneError
                {
                    bone = bone.Name,
                    positionError = Vector3.Distance(modelBone.baseLocalPosition, expectedPosition),
                    rotationErrorDegrees = Quaternion.Angle(modelBone.baseLocalRotation, expectedRotation),
                });
            }

            rows.Sort((a, b) =>
            {
                var rotation = b.rotationErrorDegrees.CompareTo(a.rotationErrorDegrees);
                return rotation != 0 ? rotation : b.positionError.CompareTo(a.positionError);
            });

            var report = new RestPoseDiagnosticReport
            {
                modelPath = ModelPath,
                poseJsonPath = PoseJsonPath,
                checkedBones = rows.Count,
                missingBones = missing.ToArray(),
                rotationErrorOverOneDegree = rows.Count(row => row.rotationErrorDegrees > 1f),
                positionErrorOverPoint001 = rows.Count(row => row.positionError > 0.001f),
                worstBones = rows.Take(30).ToArray(),
            };

            var outputPath = ProjectAbsolutePath(RestPoseDiagnosticPath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            File.WriteAllText(outputPath, JsonUtility.ToJson(report, true));

            var message =
                $"Unity HKX-rest model diagnostic written.\n" +
                $"Checked bones: {report.checkedBones}/{pose.Bones.Length}\n" +
                $"Missing bones: {report.missingBones.Length}\n" +
                $"Rotation error > 1 deg: {report.rotationErrorOverOneDegree}\n" +
                $"Position error > 0.001: {report.positionErrorOverPoint001}\n" +
                $"Report: {RestPoseDiagnosticPath}";

            Debug.Log(message);
            Display(showDialog, message);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    private static void AddBoneAndParents(HashSet<Transform> set, Transform bone, Transform stopAt)
    {
        var current = bone;
        while (current != null && current != stopAt.parent)
        {
            set.Add(current);
            if (current == stopAt)
            {
                break;
            }

            current = current.parent;
        }
    }

    private static AnimationClip CreateOrUpdateClip(PoseExport pose, IReadOnlyDictionary<string, ModelBoneInfo> modelBones, out int animatedBoneCount)
    {
        if (AssetDatabase.LoadAssetAtPath<AnimationClip>(ClipPath) != null)
        {
            AssetDatabase.DeleteAsset(ClipPath);
        }

        var clip = new AnimationClip
        {
            name = SampleName + "_HkxRestSkinned",
            frameRate = pose.FrameDuration > 0f ? 1f / pose.FrameDuration : 30f,
            wrapMode = WrapMode.Loop,
            legacy = false,
        };

        animatedBoneCount = 0;
        for (var boneIndex = 0; boneIndex < pose.Bones.Length; boneIndex++)
        {
            var bone = pose.Bones[boneIndex];
            if (string.IsNullOrWhiteSpace(bone.Name) || !modelBones.TryGetValue(bone.Name, out var modelBone))
            {
                continue;
            }

            animatedBoneCount++;
            SetPositionDeltaCurves(clip, modelBone, pose.Frames, boneIndex, bone.ReferenceLocalTransform.Translation);
            SetRotationDeltaCurves(clip, modelBone, pose.Frames, boneIndex, bone.ReferenceLocalTransform.Rotation);
        }

        clip.EnsureQuaternionContinuity();
        AssetDatabase.CreateAsset(clip, ClipPath);
        return clip;
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

        var state = stateMachine.AddState("Unified Skinned Original HKX");
        state.motion = clip;
        state.writeDefaultValues = true;
        stateMachine.defaultState = state;
        EditorUtility.SetDirty(controller);
        return controller;
    }

    private static GameObject InstantiateScenePreview(GameObject prefab)
    {
        var existing = GameObject.Find(ScenePreviewName);
        if (existing != null)
        {
            Undo.DestroyObjectImmediate(existing);
        }

        var instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
        if (instance == null)
        {
            instance = UnityEngine.Object.Instantiate(prefab);
        }

        instance.name = ScenePreviewName;
        instance.transform.position = Vector3.zero;
        instance.transform.rotation = Quaternion.identity;
        instance.transform.localScale = Vector3.one;
        Undo.RegisterCreatedObjectUndo(instance, "Create clean unified skinned HKX preview");
        EditorSceneManager.MarkSceneDirty(instance.scene);
        return instance;
    }

    private static void HideConfusingOldSceneObjects(GameObject keep)
    {
        HideIfExists("Sample_PlayerMech_Rig");
        HideIfExists(OldSkeletonPreviewName);
        HideIfExists(OldUnifiedPreviewName);

        if (keep != null)
        {
            keep.SetActive(true);
        }
    }

    private static void HideIfExists(string objectName)
    {
        var go = GameObject.Find(objectName);
        if (go == null)
        {
            return;
        }

        Undo.RecordObject(go, "Hide old AC6 preview object");
        go.SetActive(false);
        EditorUtility.SetDirty(go);
    }

    private static PreviewOrientationStats ApplyPreviewOrientation(GameObject modelInstance)
    {
        var candidates = new[]
        {
            Vector3.zero,
            new Vector3(90f, 0f, 0f),
            new Vector3(-90f, 0f, 0f),
            new Vector3(180f, 0f, 0f),
            new Vector3(0f, 90f, 0f),
            new Vector3(0f, -90f, 0f),
            new Vector3(0f, 180f, 0f),
            new Vector3(0f, 0f, 90f),
            new Vector3(0f, 0f, -90f),
            new Vector3(0f, 0f, 180f),
            new Vector3(90f, 90f, 0f),
            new Vector3(90f, -90f, 0f),
            new Vector3(-90f, 90f, 0f),
            new Vector3(-90f, -90f, 0f),
            new Vector3(90f, 0f, 90f),
            new Vector3(90f, 0f, -90f),
            new Vector3(-90f, 0f, 90f),
            new Vector3(-90f, 0f, -90f),
            new Vector3(0f, 90f, 90f),
            new Vector3(0f, -90f, 90f),
            new Vector3(0f, 90f, -90f),
            new Vector3(0f, -90f, -90f),
        };

        var head = FindDeepChild(modelInstance.transform, "Head");
        var pelvis = FindDeepChild(modelInstance.transform, "Pelvis");
        var bestEuler = Vector3.zero;
        var bestBounds = new Bounds(modelInstance.transform.position, Vector3.zero);
        var bestScore = float.NegativeInfinity;

        for (var i = 0; i < candidates.Length; i++)
        {
            modelInstance.transform.localRotation = Quaternion.Euler(candidates[i]);
            var bounds = CalculateMeshBounds(modelInstance);
            var size = bounds.size;
            var longestHorizontal = Mathf.Max(size.x, size.z);
            var score = size.y - longestHorizontal * 0.25f;

            if (head != null && pelvis != null)
            {
                score += Mathf.Sign(head.position.y - pelvis.position.y) * 100f;
            }

            if (score <= bestScore)
            {
                continue;
            }

            bestScore = score;
            bestEuler = candidates[i];
            bestBounds = bounds;
        }

        modelInstance.transform.localRotation = Quaternion.Euler(bestEuler);
        return new PreviewOrientationStats
        {
            eulerAngles = bestEuler,
            boundsSize = bestBounds.size,
        };
    }

    private static Transform FindDeepChild(Transform root, string childName)
    {
        var transforms = root.GetComponentsInChildren<Transform>(true);
        for (var i = 0; i < transforms.Length; i++)
        {
            if (transforms[i].name == childName)
            {
                return transforms[i];
            }
        }

        return null;
    }

    private static Bounds CalculateMeshBounds(GameObject root)
    {
        var renderers = root.GetComponentsInChildren<Renderer>(true);
        var bounds = new Bounds(root.transform.position, Vector3.zero);
        var initialized = false;
        for (var i = 0; i < renderers.Length; i++)
        {
            if (!TryGetMeshBounds(renderers[i], out var rendererBounds))
            {
                continue;
            }

            if (!initialized)
            {
                bounds = rendererBounds;
                initialized = true;
                continue;
            }

            bounds.Encapsulate(rendererBounds);
        }

        return bounds;
    }

    private static bool TryGetMeshBounds(Renderer renderer, out Bounds worldBounds)
    {
        Mesh mesh = null;
        if (renderer is SkinnedMeshRenderer skinnedMeshRenderer)
        {
            mesh = skinnedMeshRenderer.sharedMesh;
        }
        else if (renderer.TryGetComponent<MeshFilter>(out var meshFilter))
        {
            mesh = meshFilter.sharedMesh;
        }

        if (mesh == null)
        {
            worldBounds = renderer.bounds;
            return renderer.enabled;
        }

        var localBounds = mesh.bounds;
        var min = localBounds.min;
        var max = localBounds.max;
        var corners = new[]
        {
            new Vector3(min.x, min.y, min.z),
            new Vector3(min.x, min.y, max.z),
            new Vector3(min.x, max.y, min.z),
            new Vector3(min.x, max.y, max.z),
            new Vector3(max.x, min.y, min.z),
            new Vector3(max.x, min.y, max.z),
            new Vector3(max.x, max.y, min.z),
            new Vector3(max.x, max.y, max.z),
        };

        worldBounds = new Bounds(renderer.transform.TransformPoint(corners[0]), Vector3.zero);
        for (var i = 1; i < corners.Length; i++)
        {
            worldBounds.Encapsulate(renderer.transform.TransformPoint(corners[i]));
        }

        return true;
    }

    private static void ConfigureModelImporter()
    {
        if (!File.Exists(ProjectAbsolutePath(ModelPath)))
        {
            return;
        }

        var importer = AssetImporter.GetAtPath(ModelPath) as ModelImporter;
        if (importer == null)
        {
            AssetDatabase.ImportAsset(ModelPath, ImportAssetOptions.ForceUpdate);
            importer = AssetImporter.GetAtPath(ModelPath) as ModelImporter;
        }

        if (importer == null)
        {
            return;
        }

        importer.importAnimation = false;
        importer.animationType = ModelImporterAnimationType.Generic;
        importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        importer.importCameras = false;
        importer.importLights = false;
        importer.importBlendShapes = false;
        importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
        importer.bakeAxisConversion = false;
        importer.preserveHierarchy = true;
        importer.optimizeBones = false;
        importer.optimizeGameObjects = false;
        importer.SaveAndReimport();
    }

    private static MaterialOverrideStats ApplyBakedMaterialOverrides(GameObject modelInstance)
    {
        var materialPaths = AssetDatabase.FindAssets("t:Material", new[] { BakedMaterialRoot })
            .Select(AssetDatabase.GUIDToAssetPath)
            .ToArray();
        var materialsByKey = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < materialPaths.Length; i++)
        {
            var sourceMaterial = AssetDatabase.LoadAssetAtPath<Material>(materialPaths[i]);
            if (sourceMaterial == null)
            {
                continue;
            }

            var key = ExtractMeshMaterialKey(sourceMaterial.name);
            if (!string.IsNullOrWhiteSpace(key) && !materialsByKey.ContainsKey(key))
            {
                materialsByKey.Add(key, sourceMaterial);
            }
        }

        var renderers = modelInstance.GetComponentsInChildren<Renderer>(true);
        var stats = new MaterialOverrideStats
        {
            materialCount = materialsByKey.Count,
            rendererCount = renderers.Length,
        };

        for (var i = 0; i < renderers.Length; i++)
        {
            var renderer = renderers[i];
            var key = ExtractMeshMaterialKey(renderer.name);
            if (string.IsNullOrWhiteSpace(key) || !materialsByKey.TryGetValue(key, out var material))
            {
                continue;
            }

            var sharedMaterials = renderer.sharedMaterials;
            if (sharedMaterials == null || sharedMaterials.Length == 0)
            {
                renderer.sharedMaterial = material;
                PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
                EditorUtility.SetDirty(renderer);
                stats.appliedRenderers++;
                continue;
            }

            for (var m = 0; m < sharedMaterials.Length; m++)
            {
                sharedMaterials[m] = material;
            }

            renderer.sharedMaterials = sharedMaterials;
            PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
            EditorUtility.SetDirty(renderer);
            stats.appliedRenderers++;
        }

        return stats;
    }

    private static string ExtractMeshMaterialKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value
            .Replace("[", "_")
            .Replace("]", string.Empty)
            .Replace(".", "_")
            .Replace("-", "_")
            .Replace(" ", "_");
        var meshIndex = normalized.IndexOf("mesh_", StringComparison.OrdinalIgnoreCase);
        if (meshIndex < 0)
        {
            return string.Empty;
        }

        normalized = normalized.Substring(meshIndex);
        normalized = normalized.Replace("_UnifiedSkin", string.Empty);
        normalized = normalized.Replace("_Mat", string.Empty);
        var parts = normalized.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !string.Equals(parts[0], "mesh", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return $"mesh_{parts[1]}_{parts[2]}";
    }

    private static void SetPositionDeltaCurves(
        AnimationClip clip,
        ModelBoneInfo modelBone,
        PoseFrame[] frames,
        int boneIndex,
        Vec3 sourceReferencePosition)
    {
        var x = new Keyframe[frames.Length];
        var y = new Keyframe[frames.Length];
        var z = new Keyframe[frames.Length];

        var sourceReference = ToVector3(sourceReferencePosition);
        for (var i = 0; i < frames.Length; i++)
        {
            var sourcePosition = ToVector3(frames[i].LocalTransforms[boneIndex].Translation);
            var output = modelBone.baseLocalPosition + (sourcePosition - sourceReference);
            var time = frames[i].Time;
            x[i] = new Keyframe(time, output.x);
            y[i] = new Keyframe(time, output.y);
            z[i] = new Keyframe(time, output.z);
        }

        AnimationUtility.SetEditorCurve(clip, Binding(modelBone.path, "m_LocalPosition.x"), new AnimationCurve(x));
        AnimationUtility.SetEditorCurve(clip, Binding(modelBone.path, "m_LocalPosition.y"), new AnimationCurve(y));
        AnimationUtility.SetEditorCurve(clip, Binding(modelBone.path, "m_LocalPosition.z"), new AnimationCurve(z));
    }

    private static void SetRotationDeltaCurves(
        AnimationClip clip,
        ModelBoneInfo modelBone,
        PoseFrame[] frames,
        int boneIndex,
        Quat sourceReferenceRotation)
    {
        var x = new Keyframe[frames.Length];
        var y = new Keyframe[frames.Length];
        var z = new Keyframe[frames.Length];
        var w = new Keyframe[frames.Length];

        var sourceReference = ToQuaternion(sourceReferenceRotation);
        var previous = modelBone.baseLocalRotation;
        for (var i = 0; i < frames.Length; i++)
        {
            var sourceRotation = ToQuaternion(frames[i].LocalTransforms[boneIndex].Rotation);
            var sourceDelta = Quaternion.Inverse(sourceReference) * sourceRotation;
            var output = modelBone.baseLocalRotation * sourceDelta;
            if (i > 0 && Quaternion.Dot(previous, output) < 0f)
            {
                output = new Quaternion(-output.x, -output.y, -output.z, -output.w);
            }

            previous = output;
            var time = frames[i].Time;
            x[i] = new Keyframe(time, output.x);
            y[i] = new Keyframe(time, output.y);
            z[i] = new Keyframe(time, output.z);
            w[i] = new Keyframe(time, output.w);
        }

        AnimationUtility.SetEditorCurve(clip, Binding(modelBone.path, "m_LocalRotation.x"), new AnimationCurve(x));
        AnimationUtility.SetEditorCurve(clip, Binding(modelBone.path, "m_LocalRotation.y"), new AnimationCurve(y));
        AnimationUtility.SetEditorCurve(clip, Binding(modelBone.path, "m_LocalRotation.z"), new AnimationCurve(z));
        AnimationUtility.SetEditorCurve(clip, Binding(modelBone.path, "m_LocalRotation.w"), new AnimationCurve(w));
    }

    private static Vector3 ToVector3(Vec3 value)
    {
        return new Vector3(value.X, value.Y, value.Z);
    }

    private static Quaternion ToQuaternion(Quat value)
    {
        return new Quaternion(value.X, value.Y, value.Z, value.W);
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

    private static string PathFromRoot(Transform root, Transform target)
    {
        var names = new Stack<string>();
        var current = target;
        while (current != null && current != root)
        {
            names.Push(current.name);
            current = current.parent;
        }

        return string.Join("/", names);
    }

    private static float Dot(Quat a, Quat b)
    {
        return a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
    }

    private static PoseExport LoadPoseExport()
    {
        var json = File.ReadAllText(ProjectAbsolutePath(PoseJsonPath));
        return JsonUtility.FromJson<PoseExport>(json);
    }

    private static void Display(bool showDialog, string message)
    {
        if (showDialog)
        {
            EditorUtility.DisplayDialog("AC6 Unified Skinned HKX Preview", message, "OK");
        }
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
        var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
        return Path.Combine(projectRoot, assetPath.Replace("/", Path.DirectorySeparatorChar.ToString()));
    }

    private sealed class ModelBoneInfo
    {
        public string path;
        public Vector3 baseLocalPosition;
        public Quaternion baseLocalRotation;

        public static ModelBoneInfo From(Transform root, Transform bone)
        {
            return new ModelBoneInfo
            {
                path = PathFromRoot(root, bone),
                baseLocalPosition = bone.localPosition,
                baseLocalRotation = bone.localRotation
            };
        }
    }

    private struct MaterialOverrideStats
    {
        public int materialCount;
        public int rendererCount;
        public int appliedRenderers;
    }

    private struct PreviewOrientationStats
    {
        public Vector3 eulerAngles;
        public Vector3 boundsSize;
    }

    [Serializable]
    private sealed class RestPoseDiagnosticReport
    {
        public string modelPath;
        public string poseJsonPath;
        public int checkedBones;
        public string[] missingBones;
        public int rotationErrorOverOneDegree;
        public int positionErrorOverPoint001;
        public RestPoseBoneError[] worstBones;
    }

    [Serializable]
    private sealed class RestPoseBoneError
    {
        public string bone;
        public float positionError;
        public float rotationErrorDegrees;
    }

    [Serializable]
    private sealed class PoseExport
    {
        public float FrameDuration;
        public PoseBone[] Bones;
        public PoseFrame[] Frames;
    }

    [Serializable]
    private sealed class PoseBone
    {
        public string Name;
        public PoseTransform ReferenceLocalTransform;
    }

    [Serializable]
    private sealed class PoseFrame
    {
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
