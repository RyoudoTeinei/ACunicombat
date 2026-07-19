using ACunicombat.Mechs;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 一键装配玩家控制：选中带 MechBodyActionPlayer 的机体根节点后执行菜单，
/// 挂上输入适配/运动/行为图解释器/武器层/编排组件并建后置镜头。
/// 行为图资产取默认编译输出路径（先跑 Compile Behavior Graph）。
/// </summary>
public static class AC6PlayerControlRigBuilder
{
    private const string GraphAssetPath =
        "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/AC6BehaviorGraph.asset";

    [MenuItem("Tools/ACunicombat/AC6/Setup Player Control Rig")]
    public static void Setup()
    {
        var root = Selection.activeGameObject;
        if (root == null || root.GetComponentInChildren<MechBodyActionPlayer>() == null)
        {
            EditorUtility.DisplayDialog("Player Control Rig",
                "请先选中带 MechBodyActionPlayer 的机体根节点。", "OK");
            return;
        }

        var graph = AssetDatabase.LoadAssetAtPath<MechBehaviorGraphAsset>(GraphAssetPath);
        if (graph == null)
        {
            EditorUtility.DisplayDialog("Player Control Rig",
                $"行为图资产缺失：{GraphAssetPath}\n先运行 Tools > ACunicombat > AC6 > Compile Behavior Graph。", "OK");
            return;
        }

        Undo.RegisterFullObjectHierarchyUndo(root, "Setup Player Control Rig");
        var player = root.GetComponentInChildren<MechBodyActionPlayer>();

        var input = EnsureComponent<MechPlayerInputAdapter>(root);
        var motion = EnsureComponent<MechFlightMotionController>(root);
        var runner = EnsureComponent<MechBehaviorGraphRunner>(root);
        var weapon = EnsureComponent<MechWeaponArmLayer>(root);
        var rig = EnsureComponent<MechPlayerRig>(root);

        runner.Configure(graph, player);

        var camGo = GameObject.Find("AC6PlayerCamera");
        if (camGo == null)
        {
            camGo = new GameObject("AC6PlayerCamera", typeof(Camera), typeof(MechThirdPersonCamera));
        }

        var cam = camGo.GetComponent<MechThirdPersonCamera>();
        cam.Configure(root.transform, motion);

        var so = new SerializedObject(rig);
        so.FindProperty("input").objectReferenceValue = input;
        so.FindProperty("motion").objectReferenceValue = motion;
        so.FindProperty("cam").objectReferenceValue = cam;
        so.FindProperty("graphRunner").objectReferenceValue = runner;
        so.FindProperty("actionPlayer").objectReferenceValue = player;
        so.FindProperty("weaponLayer").objectReferenceValue = weapon;
        so.ApplyModifiedPropertiesWithoutUndo();

        EditorUtility.SetDirty(root);
        Debug.Log($"[AC6PlayerControlRig] rig assembled on '{root.name}' with graph " +
                  $"({graph.states.Length} states) and rear camera 'AC6PlayerCamera'.");
    }

    private static T EnsureComponent<T>(GameObject go) where T : Component
    {
        var existing = go.GetComponent<T>();
        return existing != null ? existing : Undo.AddComponent<T>(go);
    }
}
