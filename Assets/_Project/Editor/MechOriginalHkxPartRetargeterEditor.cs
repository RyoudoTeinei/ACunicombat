using ACunicombat.Mechs;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

[CustomEditor(typeof(MechOriginalHkxPartRetargeter))]
public sealed class MechOriginalHkxPartRetargeterEditor : Editor
{
    private const string PreviewSceneName = "AC6_C0000_A000_010900_SkeletonPreview_SCENE";
    private const string PreviewPrefabPath =
        "Assets/_Project/ImportedFromGame/Animations/C0000_A000_010900/AC6_C0000_A000_010900_SkeletonPreview.prefab";

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var retargeter = (MechOriginalHkxPartRetargeter)target;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Retarget Debug", EditorStyles.boldLabel);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Auto Build Bindings"))
            {
                retargeter.AutoBuildBindings();
                EditorUtility.SetDirty(retargeter);
            }

            if (GUILayout.Button("Capture Base Pose"))
            {
                retargeter.CaptureBasePose();
                EditorUtility.SetDirty(retargeter);
            }
        }

        if (GUILayout.Button("Stable Rigid Modules"))
        {
            retargeter.ConfigureStableRigidModuleDefaults();
            retargeter.CaptureBasePose();
            EditorUtility.SetDirty(retargeter);
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Play"))
            {
                retargeter.Play();
                EditorUtility.SetDirty(retargeter);
            }

            if (GUILayout.Button("Pause"))
            {
                retargeter.Pause();
                EditorUtility.SetDirty(retargeter);
            }

            if (GUILayout.Button("Reset Pose"))
            {
                retargeter.ResetPose();
                EditorUtility.SetDirty(retargeter);
            }
        }
    }

    [MenuItem("Tools/ACunicombat/Animation/Bind _AssembledMech To Original HKX Preview")]
    public static void BindAssembledMechToOriginalHkxPreview()
    {
        var assembler = FindTargetAssembler();
        if (assembler == null)
        {
            EditorUtility.DisplayDialog(
                "Original HKX Part Retarget",
                "Select Sample_PlayerMech_Rig or another object with MechAssembler first.",
                "OK");
            return;
        }

        var preview = FindOrCreatePreview();
        if (preview == null)
        {
            return;
        }

        Undo.RegisterFullObjectHierarchyUndo(assembler.gameObject, "Bind assembled mech to original HKX preview");

        if (assembler.RuntimeState == null || assembler.RuntimeState.AssembledParts.Count == 0)
        {
            assembler.Assemble();
        }

        var proceduralPose = assembler.GetComponent<MechProceduralPoseAnimator>();
        if (proceduralPose != null)
        {
            proceduralPose.PausePose();
            proceduralPose.enabled = false;
            EditorUtility.SetDirty(proceduralPose);
        }

        HidePreviewVisuals(preview.transform);

        var retargeter = assembler.GetComponent<MechOriginalHkxPartRetargeter>();
        if (retargeter == null)
        {
            retargeter = assembler.gameObject.AddComponent<MechOriginalHkxPartRetargeter>();
        }

        retargeter.SourceSkeletonRoot = preview.transform;
        retargeter.TargetAssembler = assembler;
        retargeter.TargetAssemblyRoot = assembler.transform.Find("_AssembledMech");
        retargeter.AutoBuildBindings();
        retargeter.ConfigureStableRigidModuleDefaults();
        retargeter.CaptureBasePose();
        retargeter.Play();

        EditorUtility.SetDirty(retargeter);
        EditorUtility.SetDirty(assembler);
        EditorSceneManager.MarkSceneDirty(assembler.gameObject.scene);

        Selection.activeGameObject = assembler.gameObject;
        EditorGUIUtility.PingObject(assembler.gameObject);

        EditorUtility.DisplayDialog(
            "Original HKX Part Retarget",
            $"Bound _AssembledMech to original HKX preview.\nValid bindings: {retargeter.CountValidBindings()}\n\nPress Play and watch Sample_PlayerMech_Rig, not the separate skeleton preview.",
            "OK");
    }

    [MenuItem("Tools/ACunicombat/Animation/Stabilize _AssembledMech HKX Retarget")]
    public static void StabilizeAssembledMechHkxRetarget()
    {
        var assembler = FindTargetAssembler();
        if (assembler == null)
        {
            EditorUtility.DisplayDialog(
                "Original HKX Part Retarget",
                "Select Sample_PlayerMech_Rig or another object with MechAssembler first.",
                "OK");
            return;
        }

        var retargeter = assembler.GetComponent<MechOriginalHkxPartRetargeter>();
        if (retargeter == null)
        {
            EditorUtility.DisplayDialog(
                "Original HKX Part Retarget",
                "No MechOriginalHkxPartRetargeter is installed. Run Bind _AssembledMech To Original HKX Preview first.",
                "OK");
            return;
        }

        Undo.RegisterFullObjectHierarchyUndo(assembler.gameObject, "Stabilize assembled mech HKX retarget");
        retargeter.ResetPose();
        retargeter.ConfigureStableRigidModuleDefaults();
        retargeter.CaptureBasePose();
        retargeter.Play();

        EditorUtility.SetDirty(retargeter);
        EditorSceneManager.MarkSceneDirty(assembler.gameObject.scene);
        Selection.activeGameObject = assembler.gameObject;

        EditorUtility.DisplayDialog(
            "Original HKX Part Retarget",
            "Stabilized HKX retarget: source bone translations are disabled and module rotations are damped.\n\nPress Play and watch Sample_PlayerMech_Rig/_AssembledMech.",
            "OK");
    }

    private static MechAssembler FindTargetAssembler()
    {
        var selected = Selection.activeGameObject;
        if (selected != null)
        {
            var selectedAssembler = selected.GetComponent<MechAssembler>() ?? selected.GetComponentInParent<MechAssembler>();
            if (selectedAssembler != null)
            {
                return selectedAssembler;
            }
        }

        var named = GameObject.Find("Sample_PlayerMech_Rig");
        if (named != null)
        {
            var namedAssembler = named.GetComponent<MechAssembler>();
            if (namedAssembler != null)
            {
                return namedAssembler;
            }
        }

        return Object.FindObjectOfType<MechAssembler>();
    }

    private static GameObject FindOrCreatePreview()
    {
        var preview = GameObject.Find(PreviewSceneName);
        if (preview != null)
        {
            return preview;
        }

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PreviewPrefabPath);
        if (prefab == null)
        {
            EditorUtility.DisplayDialog(
                "Original HKX Part Retarget",
                $"Original HKX preview prefab was not found:\n{PreviewPrefabPath}\n\nRun Tools > ACunicombat > Animation > Import AC6 Original HKX Sample first.",
                "OK");
            return null;
        }

        preview = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
        if (preview == null)
        {
            preview = Object.Instantiate(prefab);
        }

        preview.name = PreviewSceneName;
        preview.transform.position = new Vector3(12f, 0f, -8f);
        preview.transform.rotation = Quaternion.identity;
        Undo.RegisterCreatedObjectUndo(preview, "Create original HKX preview");
        EditorSceneManager.MarkSceneDirty(preview.scene);
        return preview;
    }

    private static void HidePreviewVisuals(Transform previewRoot)
    {
        if (previewRoot == null)
        {
            return;
        }

        var riggedModel = previewRoot.Find("_RiggedMelanderModel");
        if (riggedModel != null)
        {
            riggedModel.gameObject.SetActive(false);
        }

        var renderers = previewRoot.GetComponentsInChildren<Renderer>(true);
        for (var i = 0; i < renderers.Length; i++)
        {
            if (renderers[i] != null)
            {
                renderers[i].enabled = false;
            }
        }
    }
}
