using ACunicombat.Mechs;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(MechProceduralPoseAnimator))]
public sealed class MechProceduralPoseAnimatorEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var animator = (MechProceduralPoseAnimator)target;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Pose Debug", EditorStyles.boldLabel);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Play Pose"))
            {
                animator.PlayPose();
                EditorUtility.SetDirty(animator);
            }

            if (GUILayout.Button("Pause + Reset"))
            {
                animator.PausePose();
                EditorUtility.SetDirty(animator);
            }
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Recache Parts"))
            {
                animator.RecachePoseParts();
                EditorUtility.SetDirty(animator);
            }

            if (GUILayout.Button("Reset Pose"))
            {
                animator.ResetPose();
                EditorUtility.SetDirty(animator);
            }
        }
    }

    [MenuItem("Tools/ACunicombat/Legacy/Animation/Add Procedural Pose Animator To Selected Mech")]
    public static void AddToSelectedMech()
    {
        var selected = Selection.activeGameObject;
        if (selected == null)
        {
            EditorUtility.DisplayDialog("Procedural Pose Animator", "Select a mech GameObject first.", "OK");
            return;
        }

        var assembler = selected.GetComponent<MechAssembler>();
        if (assembler == null)
        {
            assembler = selected.GetComponentInParent<MechAssembler>();
        }

        if (assembler == null)
        {
            EditorUtility.DisplayDialog("Procedural Pose Animator", "Selected object is not part of a MechAssembler.", "OK");
            return;
        }

        Undo.RegisterFullObjectHierarchyUndo(assembler.gameObject, "Add procedural pose animator");

        var poseAnimator = assembler.GetComponent<MechProceduralPoseAnimator>();
        if (poseAnimator == null)
        {
            poseAnimator = assembler.gameObject.AddComponent<MechProceduralPoseAnimator>();
        }

        if (assembler.RuntimeState == null || assembler.RuntimeState.AssembledParts.Count == 0)
        {
            assembler.Assemble();
        }

        poseAnimator.RecachePoseParts();
        EditorUtility.SetDirty(assembler);
        EditorUtility.SetDirty(poseAnimator);
        Selection.activeGameObject = assembler.gameObject;

        EditorUtility.DisplayDialog(
            "Procedural Pose Animator",
            "Added MechProceduralPoseAnimator to the selected mech. Press Play to see the assembled loadout move.",
            "OK");
    }
}
