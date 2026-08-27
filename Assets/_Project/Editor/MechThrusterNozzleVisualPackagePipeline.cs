using System;
using System.IO;
using System.Text;
using ACunicombat.Mechs;
using UnityEditor;
using UnityEngine;

internal static class MechThrusterNozzleVisualPackagePipeline
{
    internal const string PackageAssetPath =
        "Assets/_Project/Resources/Mech/ThrusterNozzleLayout.json";

    internal static bool IsCurrentPackageSaved(MechThrusterNozzleLayoutData package)
    {
        var absolutePath = Path.GetFullPath(PackageAssetPath);
        if (!File.Exists(absolutePath))
        {
            return false;
        }

        try
        {
            var saved = JsonUtility.FromJson<MechThrusterNozzleLayoutData>(
                File.ReadAllText(absolutePath));
            saved ??= new MechThrusterNozzleLayoutData();
            saved.NormalizePackageMetadata();

            var current = package?.Clone() ?? new MechThrusterNozzleLayoutData();
            current.NormalizePackageMetadata();
            return string.Equals(
                JsonUtility.ToJson(current),
                JsonUtility.ToJson(saved),
                StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static string PackageVisualOverrides(
        MechThrusterNozzleLayoutData package,
        MechMainThrusterPlume plume)
    {
        var output = package?.Clone() ?? new MechThrusterNozzleLayoutData();
        output.NormalizePackageMetadata();

        var absolutePath = Path.GetFullPath(PackageAssetPath);
        var directory = Path.GetDirectoryName(absolutePath) ?? string.Empty;
        Directory.CreateDirectory(directory);

        var temporaryPath = absolutePath + ".tmp";
        File.WriteAllText(
            temporaryPath,
            JsonUtility.ToJson(output, true),
            new UTF8Encoding(false));
        File.Copy(temporaryPath, absolutePath, true);
        File.Delete(temporaryPath);

        AssetDatabase.ImportAsset(PackageAssetPath, ImportAssetOptions.ForceUpdate);
        return
            $"喷口配置包已生成：\n{PackageAssetPath}\n\n" +
            $"模型自动发现喷口：{plume?.DiscoveredAuxiliaryNozzleCount ?? 0}\n" +
            $"已应用视觉覆盖：{plume?.AppliedVisualOverrideCount ?? 0}\n" +
            $"当前失配覆盖：{plume?.UnmatchedVisualOverrideCount ?? 0}\n\n" +
            "该资源包保存位置、方向、羽流尺寸、额定推力与矢量能力；运行时物理分配器直接读取这些配置。";
    }
}
