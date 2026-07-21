using ACunicombat.Mechs;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class AC6PlayerFlightPrototypeBuilder
{
    private const string ScenePath =
        "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Scenes/AC6PlayerFlightPrototype.unity";
    private const string ActionRegistryPath =
        "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Actions/AC6CombatShowcaseRegistry.asset";

    [MenuItem("Tools/ACunicombat/AC6/Build Player Flight Prototype")]
    public static void BuildMenu()
    {
        if (!Build(out var error))
        {
            EditorUtility.DisplayDialog("AC6 Player Flight Prototype", error, "OK");
            return;
        }

        EditorUtility.DisplayDialog(
            "AC6 Player Flight Prototype",
            "Built and opened AC6PlayerFlightPrototype.unity. Press Play and click the Game view to take control.",
            "OK");
    }

    public static bool Build(out string error)
    {
        error = string.Empty;
        if (!AC6CombatShowcaseBuilder.Build(out error))
        {
            return false;
        }

        var root = GameObject.Find("AC6_Combat_Showcase");
        var mech = GameObject.Find("Player_Melander_1010");
        var camera = Camera.main;
        var registry = AssetDatabase.LoadAssetAtPath<MechActionRegistry>(ActionRegistryPath);
        if (root == null || mech == null || camera == null || registry == null)
        {
            error = "The showcase base scene is incomplete (root, mech, camera, or action registry is missing).";
            return false;
        }

        var scriptedShowcase = root.GetComponent<AC6CombatShowcaseController>();
        if (scriptedShowcase != null)
        {
            Object.DestroyImmediate(scriptedShowcase);
        }

        var existingPlayer = root.GetComponent<AC6PlayerMechController>();
        if (existingPlayer != null)
        {
            Object.DestroyImmediate(existingPlayer);
        }

        root.name = "AC6_Player_Flight_Prototype";
        var target = root.transform.Find("Moving_Target");
        if (target != null)
        {
            target.name = "Aim_Reference_Target";
            target.position = new Vector3(0f, 5f, 85f);
        }

        mech.transform.position = Vector3.zero;
        // AC6 frame meshes face local -Z. Rotate the model so gameplay +Z is visually forward.
        mech.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
        var label = mech.transform.Find("Label");
        if (label != null)
        {
            label.gameObject.SetActive(false);
        }

        camera.gameObject.name = "Player Follow Camera";
        camera.transform.position = new Vector3(0f, 5.2f, -17f);
        camera.transform.rotation = Quaternion.identity;
        camera.fieldOfView = 62f;
        camera.nearClipPlane = 0.08f;
        camera.farClipPlane = 1200f;

        var player = root.AddComponent<AC6PlayerMechController>();
        player.Configure(mech, camera, registry);

        var scene = root.scene;
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, ScenePath);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Selection.activeGameObject = root;
        SceneView.lastActiveSceneView?.FrameSelected();
        Debug.Log($"[AC6Player] Built {ScenePath}");
        return true;
    }
}
