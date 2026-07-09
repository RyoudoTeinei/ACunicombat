using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class AC6LocalSampleCasePreparer
{
    private const string ManifestPath = "Tools/AssetManifests/AC6_Melander1010_LocalCase.json";
    private const string SourceRootEnvironmentVariable = "AC6_EXTRACTED_ASSETS";
    private const string ReportAssetPath = "Assets/_Project/LocalSamples/AC6MelanderCase/Reports/last_prepare_report.txt";

    [MenuItem("Tools/ACunicombat/AC6/Prepare Local Melander Case Assets")]
    public static void PrepareLocalMelanderCaseAssets()
    {
        var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            EditorUtility.DisplayDialog("AC6 Local Case", "Could not resolve Unity project root.", "OK");
            return;
        }

        var manifestFullPath = Path.Combine(projectRoot, ManifestPath.Replace("/", Path.DirectorySeparatorChar.ToString()));
        if (!File.Exists(manifestFullPath))
        {
            EditorUtility.DisplayDialog("AC6 Local Case", $"Manifest was not found:\n{manifestFullPath}", "OK");
            return;
        }

        var manifest = JsonUtility.FromJson<LocalCaseManifest>(File.ReadAllText(manifestFullPath));
        if (manifest == null || manifest.entries == null || manifest.entries.Length == 0)
        {
            EditorUtility.DisplayDialog("AC6 Local Case", "Manifest is empty or invalid.", "OK");
            return;
        }

        var sourceRoot = ResolveSourceRoot(manifest);
        if (!Directory.Exists(sourceRoot))
        {
            EditorUtility.DisplayDialog(
                "AC6 Local Case",
                $"Source root was not found:\n{sourceRoot}\n\nSet {SourceRootEnvironmentVariable} if your extracted assets live elsewhere.",
                "OK");
            return;
        }

        var outputRootAssetPath = NormalizeAssetPath(manifest.outputRoot);
        var outputRootFullPath = AssetPathToFullPath(projectRoot, outputRootAssetPath);
        Directory.CreateDirectory(outputRootFullPath);

        var copiedFiles = 0;
        var copiedEntries = 0;
        var missing = new List<string>();

        for (var i = 0; i < manifest.entries.Length; i++)
        {
            var entry = manifest.entries[i];
            var sourcePath = Path.Combine(sourceRoot, NormalizeRelativePath(entry.source));
            var targetPath = Path.Combine(outputRootFullPath, NormalizeRelativePath(entry.target));

            if (string.Equals(entry.mode, "directory", StringComparison.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(sourcePath))
                {
                    missing.Add($"{entry.id}: {sourcePath}");
                    continue;
                }

                copiedFiles += CopyDirectory(sourcePath, targetPath);
                copiedEntries++;
                continue;
            }

            if (!File.Exists(sourcePath))
            {
                missing.Add($"{entry.id}: {sourcePath}");
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? outputRootFullPath);
            File.Copy(sourcePath, targetPath, true);
            copiedFiles++;
            copiedEntries++;
        }

        var report = BuildReport(manifest, sourceRoot, outputRootAssetPath, copiedEntries, copiedFiles, missing);
        WriteReport(projectRoot, report);
        AssetDatabase.Refresh();

        Debug.Log(report);
        EditorUtility.DisplayDialog("AC6 Local Case", report, "OK");
    }

    private static string ResolveSourceRoot(LocalCaseManifest manifest)
    {
        var configured = Environment.GetEnvironmentVariable(SourceRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        return Path.GetFullPath(manifest.sourceRootHint);
    }

    private static int CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);

        var copied = 0;
        var files = Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories);
        for (var i = 0; i < files.Length; i++)
        {
            var sourceFile = files[i];
            if (sourceFile.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relativePath = GetRelativePath(sourceDirectory, sourceFile);
            var targetFile = Path.Combine(targetDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile) ?? targetDirectory);
            File.Copy(sourceFile, targetFile, true);
            copied++;
        }

        return copied;
    }

    private static string BuildReport(
        LocalCaseManifest manifest,
        string sourceRoot,
        string outputRootAssetPath,
        int copiedEntries,
        int copiedFiles,
        List<string> missing)
    {
        var lines = new List<string>
        {
            $"{manifest.displayName}",
            $"Case: {manifest.caseId}",
            $"Source: {sourceRoot}",
            $"Output: {outputRootAssetPath}",
            $"Copied entries: {copiedEntries}/{manifest.entries.Length}",
            $"Copied files: {copiedFiles}"
        };

        if (missing.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Missing:");
            lines.AddRange(missing);
        }
        else
        {
            lines.Add("Missing: 0");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static void WriteReport(string projectRoot, string report)
    {
        var reportFullPath = AssetPathToFullPath(projectRoot, ReportAssetPath);
        Directory.CreateDirectory(Path.GetDirectoryName(reportFullPath) ?? projectRoot);
        File.WriteAllText(reportFullPath, report);
    }

    private static string NormalizeAssetPath(string assetPath)
    {
        return assetPath.Replace('\\', '/').Trim('/');
    }

    private static string NormalizeRelativePath(string path)
    {
        return path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar).Trim(Path.DirectorySeparatorChar);
    }

    private static string GetRelativePath(string rootPath, string fullPath)
    {
        var normalizedRoot = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedFullPath = Path.GetFullPath(fullPath);
        if (!normalizedFullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFileName(fullPath);
        }

        return normalizedFullPath.Substring(normalizedRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string AssetPathToFullPath(string projectRoot, string assetPath)
    {
        return Path.Combine(projectRoot, NormalizeAssetPath(assetPath).Replace("/", Path.DirectorySeparatorChar.ToString()));
    }

    [Serializable]
    private sealed class LocalCaseManifest
    {
        public string caseId;
        public string displayName;
        public string sourceRootHint;
        public string outputRoot;
        public LocalCaseEntry[] entries;
    }

    [Serializable]
    private sealed class LocalCaseEntry
    {
        public string id;
        public string kind;
        public string mode;
        public string source;
        public string target;
    }
}
