using System.IO;
using System.Linq;
using ACunicombat.Mechs;
using UnityEditor;
using UnityEngine;

public static partial class MechLiveBattlefieldReview
{
    private static int appearanceFrames;
    private static Camera appearanceCamera;

    public static void RunAppearanceReview()
    {
        SessionState.SetBool("LiveBattlefieldReview.appearance", true);
        RunRenderingFixReview();
    }

    private static void TickAppearance(MechPlayerRig rig, MechBattlefieldMap map, string dir)
    {
        if (Time.timeAsDouble < 12) return;
        var wing = Object.FindObjectsByType<CombatTestTarget>(FindObjectsInactive.Exclude)
            .FirstOrDefault(target => target.TeamId == 1 && target.GetComponent<MechPlayerRig>() != null && target.GetComponent<MechPlayerRig>().AiControlled);
        if (appearanceFrames == 0) Debug.Log("APPEARANCE_TICK t=" + Time.timeAsDouble + " wing=" + (wing != null));
        if (wing == null) return;
        if (MechAssemblyMenu.IsAnyMenuVisible)
            typeof(MechAssemblyMenu).GetMethod("CloseAllForSolarMap", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).Invoke(null, null);
        map.SetVisible(false);
        if (appearanceCamera == null)
        {
            var camera = Camera.main;
            if (camera == null) return;
            var controller = camera.GetComponent<MechThirdPersonCamera>();
            if (controller != null) controller.enabled = false;
            camera.enabled = false;
            appearanceCamera = new GameObject("Wing appearance camera").AddComponent<Camera>();
            appearanceCamera.depth = 100;
            appearanceCamera.nearClipPlane = .1f;
            appearanceCamera.farClipPlane = 10000000f;
        }
        appearanceCamera.transform.position = wing.transform.position + new Vector3(24, 12, -38);
        appearanceCamera.transform.LookAt(wing.transform.position + Vector3.up * 9);
        Time.timeScale = 0;
        if (++appearanceFrames < 15) return;
        Debug.Log("APPEARANCE_CAPTURE t=" + Time.timeAsDouble + " wing=" + wing.name + " dir=" + dir);
        WriteMaterialReport(wing, dir + "/materials.txt");
        var capture = new RenderTexture(Screen.width, Screen.height, 24);
        appearanceCamera.targetTexture = capture;
        appearanceCamera.Render();
        typeof(MechBattlefieldMap).GetMethod("SaveTexture", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            .Invoke(null, new object[] { capture, dir + "/wing.png" });
        if (!File.Exists(dir + "/wing.png")) return;
        SessionState.SetBool("LiveBattlefieldReview.appearance", false);
        SessionState.SetInt("LiveBattlefieldReview.exit", 0);
        SessionState.SetString(Key, "finish");
        Time.timeScale = 1;
        EditorApplication.isPlaying = false;
    }

    private static void WriteMaterialReport(CombatTestTarget wing, string path)
    {
        var report = new System.Text.StringBuilder();
        var block = new MaterialPropertyBlock();
        foreach (var renderer in wing.GetComponentsInChildren<Renderer>(true))
        {
            var materials = renderer.sharedMaterials;
            for (int i = 0; i < materials.Length; i++)
            {
                var material = materials[i];
                if (material == null) continue;
                renderer.GetPropertyBlock(block, i);
                report.Append(renderer.name).Append(" / ").Append(material.name).Append(" / ").Append(material.shader.name);
                if (material.HasProperty("_UseSourceAlbedo")) report.Append(" source=").Append(material.GetFloat("_UseSourceAlbedo"));
                if (material.HasProperty("_ZoneMode")) report.Append(" zone=").Append(material.GetFloat("_ZoneMode"));
                if (material.HasProperty("_BaseColor")) report.Append(" base=").Append(material.GetColor("_BaseColor"));
                if (material.HasProperty("_Zone1Color")) report.Append(" z1=").Append(material.GetColor("_Zone1Color"));
                if (material.HasProperty("_EmissionStrength")) report.Append(" emission=").Append(material.GetFloat("_EmissionStrength"));
                report.AppendLine();
            }
        }
        File.WriteAllText(path, report.ToString());
    }
}
