using System.Collections.Generic;
using ACunicombat.Mechs;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class ACunicombatLegacySceneCleaner
{
    private static readonly string[] LegacyPreviewNamePrefixes =
    {
        "AC6_C0000_A000_010900_SkeletonPreview",
        "AC6_C0000_A000_010900_UnifiedSkinned",
        "AC6_C0000_A000_010900_HkxRestSkinned"
    };

    [MenuItem("Tools/ACunicombat/Project/Clean Current Scene Legacy Objects")]
    public static void CleanCurrentScene()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            EditorUtility.DisplayDialog(
                "ACunicombat Scene Cleanup",
                "Exit Play Mode before cleaning the scene.",
                "OK");
            return;
        }

        var scene = SceneManager.GetActiveScene();
        if (!scene.IsValid() || !scene.isLoaded)
        {
            EditorUtility.DisplayDialog(
                "ACunicombat Scene Cleanup",
                "No loaded active scene was found.",
                "OK");
            return;
        }

        var legacyObjects = FindLegacyPreviewObjects(scene);
        var proceduralAnimators = FindSceneComponents<MechProceduralPoseAnimator>(scene);
        var retargeters = FindSceneComponents<MechOriginalHkxPartRetargeter>(scene);

        if (legacyObjects.Count == 0 && proceduralAnimators.Count == 0 && retargeters.Count == 0)
        {
            EditorUtility.DisplayDialog(
                "ACunicombat Scene Cleanup",
                "No legacy AC6 preview objects or retargeting components were found in the current scene.",
                "OK");
            return;
        }

        Undo.SetCurrentGroupName("Clean ACunicombat legacy scene objects");
        var undoGroup = Undo.GetCurrentGroup();

        for (var i = 0; i < proceduralAnimators.Count; i++)
        {
            Undo.DestroyObjectImmediate(proceduralAnimators[i]);
        }

        for (var i = 0; i < retargeters.Count; i++)
        {
            Undo.DestroyObjectImmediate(retargeters[i]);
        }

        for (var i = 0; i < legacyObjects.Count; i++)
        {
            Undo.DestroyObjectImmediate(legacyObjects[i]);
        }

        Undo.CollapseUndoOperations(undoGroup);
        EditorSceneManager.MarkSceneDirty(scene);

        var message =
            $"Cleaned scene: {scene.name}\n" +
            $"Legacy preview objects removed: {legacyObjects.Count}\n" +
            $"Procedural pose components removed: {proceduralAnimators.Count}\n" +
            $"HKX retarget components removed: {retargeters.Count}\n\n" +
            "The current GLB model, Animator, cameras, lights, and mech loadout components were not changed.";

        Debug.Log(message);
        EditorUtility.DisplayDialog("ACunicombat Scene Cleanup", message, "OK");
    }

    private static List<GameObject> FindLegacyPreviewObjects(Scene scene)
    {
        var result = new List<GameObject>();
        var roots = scene.GetRootGameObjects();
        for (var i = 0; i < roots.Length; i++)
        {
            var transforms = roots[i].GetComponentsInChildren<Transform>(true);
            for (var j = 0; j < transforms.Length; j++)
            {
                var candidate = transforms[j].gameObject;
                if (IsLegacyPreviewName(candidate.name))
                {
                    result.Add(candidate);
                }
            }
        }

        RemoveNestedMatches(result);
        return result;
    }

    private static List<T> FindSceneComponents<T>(Scene scene) where T : Component
    {
        var result = new List<T>();
        var roots = scene.GetRootGameObjects();
        for (var i = 0; i < roots.Length; i++)
        {
            result.AddRange(roots[i].GetComponentsInChildren<T>(true));
        }

        return result;
    }

    private static bool IsLegacyPreviewName(string objectName)
    {
        for (var i = 0; i < LegacyPreviewNamePrefixes.Length; i++)
        {
            if (objectName.StartsWith(LegacyPreviewNamePrefixes[i], System.StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void RemoveNestedMatches(List<GameObject> matches)
    {
        for (var i = matches.Count - 1; i >= 0; i--)
        {
            var parent = matches[i].transform.parent;
            while (parent != null)
            {
                if (matches.Contains(parent.gameObject))
                {
                    matches.RemoveAt(i);
                    break;
                }

                parent = parent.parent;
            }
        }
    }
}
