using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class AC6RiggedMelanderPreviewBinder
{
    private const string RiggedModelPath =
        "Assets/_Project/ImportedFromBlend/MELANDER_1010_UnifiedSkinned/FBX/AC6_UNIFIED_MELANDER_1010_SKINNED.fbx";

    private const string HkxPreviewPrefabPath =
        "Assets/_Project/ImportedFromGame/Animations/C0000_A000_010900/AC6_C0000_A000_010900_SkeletonPreview.prefab";

    private const string ScenePreviewName = "AC6_C0000_A000_010900_SkeletonPreview_SCENE";
    private const string ModelRootName = "_RiggedMelanderModel";
    private const string ExtraBonesRootName = "_RiggedMelanderExtraBones";

    [MenuItem("Tools/ACunicombat/Animation/Attach Unified Skinned Melander To Original HKX Preview")]
    public static void AttachToOriginalHkxPreview()
    {
        AC6UnifiedSkinnedHkxPreviewImporter.CreateCleanPreview(true);
    }

    [MenuItem("Tools/ACunicombat/Animation/Attach Rigged Melander Model To Original HKX Preview")]
    public static void AttachLegacyMenuAlias()
    {
        AttachToOriginalHkxPreview();
    }

    [MenuItem("Tools/ACunicombat/Animation/Diagnose Original HKX Model Preview")]
    public static void DiagnoseOriginalHkxPreview()
    {
        var preview = GameObject.Find(ScenePreviewName);
        var riggedModel = preview != null ? preview.transform.Find(ModelRootName) : null;
        var extraBones = preview != null ? preview.transform.Find(ExtraBonesRootName) : null;
        var sampleRig = GameObject.Find("Sample_PlayerMech_Rig");
        var sampleAssembly = sampleRig != null ? sampleRig.transform.Find("_AssembledMech") : null;

        var previewMarkers = preview != null
            ? preview.GetComponentsInChildren<Transform>(true).Count(t => t.name == "_marker")
            : 0;
        var skinnedRenderers = riggedModel != null
            ? riggedModel.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length
            : 0;
        var meshRenderers = riggedModel != null
            ? riggedModel.GetComponentsInChildren<MeshRenderer>(true).Length
            : 0;

        var message =
            $"Preview object: {(preview != null ? preview.name : "missing")}\n" +
            $"Rigged model child: {(riggedModel != null ? riggedModel.name : "missing")}\n" +
            $"Skinned renderers under rigged model: {skinnedRenderers}\n" +
            $"Mesh renderers under rigged model: {meshRenderers}\n" +
            $"Extra proxy bones: {(extraBones != null ? extraBones.childCount.ToString() : "missing")}\n" +
            $"Skeleton markers: {previewMarkers}\n" +
            $"Static Sample_PlayerMech_Rig assembly visible: {(sampleAssembly != null && sampleAssembly.gameObject.activeInHierarchy ? "yes" : "no")}\n\n" +
            $"If the rigged model child is missing, run:\nTools > ACunicombat > Animation > Attach Unified Skinned Melander To Original HKX Preview";

        Debug.Log(message, preview != null ? preview : null);
        EditorUtility.DisplayDialog("AC6 HKX Preview Diagnose", message, "OK");
    }

    public static GameObject AttachToPreview(GameObject previewRoot, bool showDialog)
    {
        if (previewRoot == null)
        {
            return null;
        }

        var modelAsset = AssetDatabase.LoadAssetAtPath<GameObject>(RiggedModelPath);
        if (modelAsset == null)
        {
            if (showDialog)
            {
                EditorUtility.DisplayDialog(
                    "AC6 Rigged Melander",
                    $"Unified skinned Melander FBX was not found:\n{RiggedModelPath}\n\nRun Tools/Blender/ExportAC6UnifiedMelanderSkinnedPreview.py first.",
                    "OK");
            }

            Debug.Log($"AC6 rigged Melander model not found: {RiggedModelPath}");
            return null;
        }

        RemoveChild(previewRoot.transform, ModelRootName);
        RemoveChild(previewRoot.transform, ExtraBonesRootName);

        var modelInstance = PrefabUtility.InstantiatePrefab(modelAsset, previewRoot.transform) as GameObject;
        if (modelInstance == null)
        {
            modelInstance = UnityEngine.Object.Instantiate(modelAsset, previewRoot.transform);
        }

        modelInstance.name = ModelRootName;
        modelInstance.transform.localPosition = Vector3.zero;
        modelInstance.transform.localRotation = Quaternion.identity;
        modelInstance.transform.localScale = Vector3.one;

        var extraBonesRoot = new GameObject(ExtraBonesRootName);
        extraBonesRoot.transform.SetParent(previewRoot.transform, false);

        var targetBones = BuildTargetBoneMap(previewRoot.transform);
        var sourceToBoundBone = new Dictionary<Transform, Transform>();
        var stats = new BindStats();

        var renderers = modelInstance.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        for (var i = 0; i < renderers.Length; i++)
        {
            BindRenderer(renderers[i], targetBones, sourceToBoundBone, extraBonesRoot.transform, stats);
        }

        SetMarkerVisibility(previewRoot.transform, false);

        Selection.activeGameObject = previewRoot;
        EditorGUIUtility.PingObject(previewRoot);
        EditorSceneManager.MarkSceneDirty(previewRoot.scene);

        var message =
            $"Attached unified skinned Melander model to original HKX preview.\n" +
            $"Skinned renderers: {stats.rendererCount}\n" +
            $"Bones mapped by name: {stats.directBoneCount}\n" +
            $"Static proxy bones: {stats.proxyBoneCount}\n" +
            $"Missing/null bones: {stats.missingBoneCount}";

        if (stats.rendererCount == 0)
        {
            message += "\n\nWarning: no SkinnedMeshRenderer was found in the imported unified FBX.";
        }

        Debug.Log(message, previewRoot);
        if (showDialog)
        {
            EditorUtility.DisplayDialog("AC6 Rigged Melander", message, "OK");
        }

        return modelInstance;
    }

    private static void ConfigureRiggedModelImporter()
    {
        if (!File.Exists(ProjectAbsolutePath(RiggedModelPath)))
        {
            return;
        }

        var importer = AssetImporter.GetAtPath(RiggedModelPath) as ModelImporter;
        if (importer == null)
        {
            AssetDatabase.ImportAsset(RiggedModelPath, ImportAssetOptions.ForceUpdate);
            importer = AssetImporter.GetAtPath(RiggedModelPath) as ModelImporter;
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
        importer.preserveHierarchy = true;
        importer.SaveAndReimport();
    }

    private static GameObject FindOrCreatePreview()
    {
        var preview = GameObject.Find(ScenePreviewName);
        if (preview != null)
        {
            return preview;
        }

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(HkxPreviewPrefabPath);
        if (prefab == null)
        {
            EditorUtility.DisplayDialog(
                "AC6 Rigged Melander",
                $"HKX skeleton preview prefab was not found:\n{HkxPreviewPrefabPath}\n\nRun Tools/ACunicombat/Animation/Import AC6 Original HKX Sample first.",
                "OK");
            return null;
        }

        preview = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
        if (preview == null)
        {
            preview = UnityEngine.Object.Instantiate(prefab);
        }

        preview.name = ScenePreviewName;
        preview.transform.position = new Vector3(12f, 0f, -8f);
        preview.transform.rotation = Quaternion.identity;
        Undo.RegisterCreatedObjectUndo(preview, "Create AC6 Original HKX Preview");
        EditorSceneManager.MarkSceneDirty(preview.scene);
        return preview;
    }

    private static Dictionary<string, Transform> BuildTargetBoneMap(Transform root)
    {
        var map = new Dictionary<string, Transform>(StringComparer.Ordinal);
        var transforms = root.GetComponentsInChildren<Transform>(true);
        for (var i = 0; i < transforms.Length; i++)
        {
            var current = transforms[i];
            if (!map.ContainsKey(current.name))
            {
                map.Add(current.name, current);
            }
        }

        return map;
    }

    private static void BindRenderer(
        SkinnedMeshRenderer renderer,
        IReadOnlyDictionary<string, Transform> targetBones,
        Dictionary<Transform, Transform> sourceToBoundBone,
        Transform extraBonesRoot,
        BindStats stats)
    {
        if (renderer == null)
        {
            return;
        }

        stats.rendererCount++;
        renderer.updateWhenOffscreen = true;

        var sourceBones = renderer.bones;
        var boundBones = new Transform[sourceBones.Length];
        for (var i = 0; i < sourceBones.Length; i++)
        {
            boundBones[i] = ResolveBoundBone(sourceBones[i], targetBones, sourceToBoundBone, extraBonesRoot, stats);
        }

        renderer.bones = boundBones;
        renderer.rootBone = ResolveBoundBone(renderer.rootBone, targetBones, sourceToBoundBone, extraBonesRoot, stats);
    }

    private static Transform ResolveBoundBone(
        Transform source,
        IReadOnlyDictionary<string, Transform> targetBones,
        Dictionary<Transform, Transform> sourceToBoundBone,
        Transform extraBonesRoot,
        BindStats stats)
    {
        if (source == null)
        {
            stats.missingBoneCount++;
            return FindFallbackRoot(targetBones, extraBonesRoot);
        }

        if (targetBones.TryGetValue(source.name, out var target))
        {
            stats.directBoneCount++;
            return target;
        }

        if (sourceToBoundBone.TryGetValue(source, out var existingProxy))
        {
            return existingProxy;
        }

        var parent = ResolveProxyParent(source.parent, targetBones, sourceToBoundBone, extraBonesRoot, stats);
        var proxy = new GameObject(source.name).transform;
        proxy.SetParent(parent, false);
        proxy.localPosition = source.localPosition;
        proxy.localRotation = source.localRotation;
        proxy.localScale = source.localScale;

        sourceToBoundBone.Add(source, proxy);
        stats.proxyBoneCount++;
        return proxy;
    }

    private static Transform ResolveProxyParent(
        Transform sourceParent,
        IReadOnlyDictionary<string, Transform> targetBones,
        Dictionary<Transform, Transform> sourceToBoundBone,
        Transform extraBonesRoot,
        BindStats stats)
    {
        if (sourceParent == null)
        {
            return FindFallbackRoot(targetBones, extraBonesRoot);
        }

        if (targetBones.TryGetValue(sourceParent.name, out var targetParent))
        {
            return targetParent;
        }

        if (sourceParent.GetComponentInParent<SkinnedMeshRenderer>() != null)
        {
            return FindFallbackRoot(targetBones, extraBonesRoot);
        }

        return ResolveBoundBone(sourceParent, targetBones, sourceToBoundBone, extraBonesRoot, stats);
    }

    private static Transform FindFallbackRoot(IReadOnlyDictionary<string, Transform> targetBones, Transform extraBonesRoot)
    {
        if (targetBones.TryGetValue("Master", out var master))
        {
            return master;
        }

        if (targetBones.TryGetValue("Root", out var root))
        {
            return root;
        }

        return extraBonesRoot;
    }

    private static void SetMarkerVisibility(Transform root, bool visible)
    {
        var transforms = root.GetComponentsInChildren<Transform>(true);
        for (var i = 0; i < transforms.Length; i++)
        {
            if (transforms[i].name == "_marker")
            {
                transforms[i].gameObject.SetActive(visible);
            }
        }
    }

    private static void RemoveChild(Transform parent, string childName)
    {
        var child = parent.Find(childName);
        if (child != null)
        {
            Undo.DestroyObjectImmediate(child.gameObject);
        }
    }

    private static string ProjectAbsolutePath(string assetPath)
    {
        var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
        return Path.Combine(projectRoot, assetPath.Replace("/", Path.DirectorySeparatorChar.ToString()));
    }

    private sealed class BindStats
    {
        public int rendererCount;
        public int directBoneCount;
        public int proxyBoneCount;
        public int missingBoneCount;
    }
}
