using System.IO;
using System.Linq;
using ACunicombat.Mechs;
using UnityEditor;
using UnityEngine;

public static partial class MechLiveBattlefieldReview
{
    private static Camera tracerCamera;
    private static int tracerFrames;
    private static bool tracerSpawned;

    public static void RunTracerReview()
    {
        SessionState.SetBool("LiveBattlefieldReview.tracer", true);
        RunRenderingFixReview();
    }

    private static void TickTracer(MechPlayerRig rig, MechBattlefieldMap map, string dir)
    {
        if (Time.timeAsDouble < 12) return;
        if (MechAssemblyMenu.IsAnyMenuVisible)
            typeof(MechAssemblyMenu).GetMethod("CloseAllForSolarMap", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).Invoke(null, null);
        map.SetVisible(false);
        if (!tracerSpawned)
        {
            tracerSpawned = true;
            var main = Camera.main;
            if (main != null) main.enabled = false;
            tracerCamera = new GameObject("Tracer review camera").AddComponent<Camera>();
            tracerCamera.depth = 100;
            tracerCamera.nearClipPlane = .1f;
            tracerCamera.farClipPlane = 1000000f;
            tracerCamera.transform.position = new Vector3(40, 12, 20);
            tracerCamera.transform.LookAt(new Vector3(0, 0, 60));
            for (int i = 0; i < 3; i++)
            {
                var round = new GameObject("High speed tracer " + i);
                round.transform.position = new Vector3(-25, i * 3, 35 + i * 5);
                round.AddComponent<MechTracerRound>().Build(new Color(1f, .74f, .36f), 20f, 1500f);
                var tracer = round.GetComponent<MechTracerRound>();
                typeof(MechTracerRound).GetField("coreWidth", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(tracer, 5f);
                typeof(MechTracerRound).GetField("glowWidth", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(tracer, 5f);
                round.AddComponent<MechTestProjectile>().Configure(null, null, default, TestWeaponKind.Rifle, Vector3.right * 1500f, 10, .1f, 30);
            }
            Time.timeScale = .05f;
        }
        if (++tracerFrames < 3) return;
        Time.timeScale = 0;
        var capture = new RenderTexture(Screen.width, Screen.height, 24);
        File.WriteAllText(dir + "/tracer-state.txt", "camera=" + tracerCamera.transform.position + "\n" +
            string.Join("\n", Object.FindObjectsByType<MechTestProjectile>(FindObjectsInactive.Include)
                .Select(p => p.name + " active=" + p.gameObject.activeInHierarchy + " pos=" + p.transform.position + " velocity=" + p.Velocity)));
        File.AppendAllText(dir + "/tracer-state.txt", "\nTRAILS\n" + string.Join("\n", Object.FindObjectsByType<TrailRenderer>(FindObjectsInactive.Include)
            .Select(t => t.name + " pos=" + t.transform.position + " emit=" + t.emitting + " enabled=" + t.enabled + " mat=" + (t.sharedMaterial != null ? t.sharedMaterial.name : "null"))));
        tracerCamera.targetTexture = capture;
        tracerCamera.Render();
        typeof(MechBattlefieldMap).GetMethod("SaveTexture", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            .Invoke(null, new object[] { capture, dir + "/tracer.png" });
        if (!File.Exists(dir + "/tracer.png")) return;
        SessionState.SetBool("LiveBattlefieldReview.tracer", false);
        SessionState.SetInt("LiveBattlefieldReview.exit", 0);
        SessionState.SetString(Key, "finish");
        Time.timeScale = 1;
        EditorApplication.isPlaying = false;
    }
}
