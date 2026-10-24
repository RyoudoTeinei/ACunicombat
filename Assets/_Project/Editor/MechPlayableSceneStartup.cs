using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Open the playable scene once per interactive editor session, not after every compile.</summary>
[InitializeOnLoad]
public static class MechPlayableSceneStartup
{
    public const string ScenePath = "Assets/_Project/Scenes/MainMenu.unity";
    private const string SessionKey = "ACunicombat.PlayableSceneStartup.Handled";
    private static double readyAfter = -1;

    static MechPlayableSceneStartup()
    {
        if (Application.isBatchMode || SessionState.GetBool(SessionKey, false)) return;
        EditorApplication.update += OpenWhenReady;
    }

    private static void Finish()
    {
        SessionState.SetBool(SessionKey, true);
        EditorApplication.update -= OpenWhenReady;
    }

    private static void OpenWhenReady()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            readyAfter = -1;
            return;
        }
        if (EditorApplication.isPlayingOrWillChangePlaymode) { Finish(); return; }
        // Allow Unity's scene restoration/import work to finish before choosing the startup scene.
        if (readyAfter < 0) { readyAfter = EditorApplication.timeSinceStartup + 1; return; }
        if (EditorApplication.timeSinceStartup < readyAfter) return;

        Finish();
        if (SceneManager.GetActiveScene().path == ScenePath) return;
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            if (!SceneManager.GetSceneAt(i).isDirty) continue;
            Debug.Log("[PlayableSceneStartup] Preserving an unsaved/recovered scene; automatic opening was skipped.");
            return;
        }
        if (!File.Exists(ScenePath) || AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) == null)
        {
            Debug.LogWarning("[PlayableSceneStartup] Playable scene is missing. Build it using Tools/ACunicombat/Build Main Menu and Scenario Editor.");
            return;
        }
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
    }
}
