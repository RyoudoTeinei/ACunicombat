using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

public static class AC6RiggedAnimationProbeImporter
{
    private const string AnimationRoot = "Assets/_Project/ImportedFromExtracted/Animations";
    private const string FbxPath = AnimationRoot + "/AC6_C4201_TestAction.fbx";
    private const string ControllerPath = AnimationRoot + "/AC6_C4201_TestAction.controller";
    private const string PreviewPrefabPath = AnimationRoot + "/AC6_C4201_TestAction_Preview.prefab";
    private const string ClipName = "AC6_C4201_Test_MechReady";

    [MenuItem("Tools/ACunicombat/Animation/Import AC6 Rigged Test Animation")]
    public static void ImportRiggedTestAnimation()
    {
        if (!File.Exists(ProjectAbsolutePath(FbxPath)))
        {
            EditorUtility.DisplayDialog(
                "AC6 Rigged Animation Probe",
                $"Generated FBX was not found:\n{FbxPath}\n\nRun Tools/Blender/ExportAC6RiggedTestAnimation.py first.",
                "OK");
            return;
        }

        EnsureFolder(AnimationRoot);
        ConfigureModelImporter();

        var clip = FindImportedClip();
        if (clip == null)
        {
            EditorUtility.DisplayDialog(
                "AC6 Rigged Animation Probe",
                $"No AnimationClip was imported from:\n{FbxPath}",
                "OK");
            return;
        }

        var controller = CreateOrUpdateController(clip);
        var previewPrefab = CreateOrUpdatePreviewPrefab(controller);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Selection.activeObject = previewPrefab != null ? previewPrefab : clip;
        EditorGUIUtility.PingObject(Selection.activeObject);

        EditorUtility.DisplayDialog(
            "AC6 Rigged Animation Probe",
            $"Imported rigged test animation.\nClip: {clip.name}\nPreview prefab: {PreviewPrefabPath}",
            "OK");
    }

    private static void ConfigureModelImporter()
    {
        AssetDatabase.ImportAsset(FbxPath, ImportAssetOptions.ForceUpdate);

        var importer = AssetImporter.GetAtPath(FbxPath) as ModelImporter;
        if (importer == null)
        {
            return;
        }

        importer.importAnimation = true;
        importer.animationType = ModelImporterAnimationType.Generic;
        importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        importer.importCameras = false;
        importer.importLights = false;
        importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
        importer.SaveAndReimport();

        importer = AssetImporter.GetAtPath(FbxPath) as ModelImporter;
        if (importer == null)
        {
            return;
        }

        var defaultClips = importer.defaultClipAnimations;
        if (defaultClips != null && defaultClips.Length > 0)
        {
            defaultClips[0].name = ClipName;
            defaultClips[0].loopTime = true;
            importer.clipAnimations = defaultClips;
            importer.SaveAndReimport();
        }
    }

    private static AnimationClip FindImportedClip()
    {
        return AssetDatabase.LoadAllAssetRepresentationsAtPath(FbxPath)
            .OfType<AnimationClip>()
            .Where(clip => clip != null && !clip.name.StartsWith("__preview__", System.StringComparison.Ordinal))
            .OrderByDescending(clip => clip.name == ClipName)
            .ThenBy(clip => clip.name)
            .FirstOrDefault();
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

        var state = stateMachine.AddState("Mech Ready Test");
        state.motion = clip;
        state.writeDefaultValues = true;
        stateMachine.defaultState = state;

        EditorUtility.SetDirty(controller);
        return controller;
    }

    private static GameObject CreateOrUpdatePreviewPrefab(RuntimeAnimatorController controller)
    {
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(FbxPath);
        if (model == null)
        {
            return null;
        }

        var instance = PrefabUtility.InstantiatePrefab(model) as GameObject;
        if (instance == null)
        {
            instance = Object.Instantiate(model);
        }

        instance.name = "AC6_C4201_TestAction_Preview";

        var animator = instance.GetComponent<Animator>();
        if (animator == null)
        {
            animator = instance.AddComponent<Animator>();
        }

        animator.runtimeAnimatorController = controller;
        animator.applyRootMotion = false;
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

        var prefab = PrefabUtility.SaveAsPrefabAsset(instance, PreviewPrefabPath);
        Object.DestroyImmediate(instance);

        return prefab;
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
        var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
        return projectRoot == null
            ? assetPath
            : Path.Combine(projectRoot, assetPath.Replace("/", Path.DirectorySeparatorChar.ToString()));
    }
}
