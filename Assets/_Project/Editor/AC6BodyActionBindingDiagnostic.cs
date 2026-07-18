using System;
using System.Collections.Generic;
using System.Linq;
using ACunicombat.Mechs;
using UnityEditor;
using UnityEngine;

public static class AC6BodyActionBindingDiagnostic
{
    private const string RegistryPath =
        "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Actions/AC6ActionRegistry.asset";
    private const string RawGltfRoot =
        "Assets/_Project/LocalSamples/AC6SkinnedModular/Raw/Gltf";

    private static readonly (MechPartSlot Slot, string Token)[] Parts =
    {
        (MechPartSlot.Head, "HD"),
        (MechPartSlot.Core, "BD"),
        (MechPartSlot.Arms, "AM"),
        (MechPartSlot.Legs, "LG")
    };

    [MenuItem("Tools/ACunicombat/AC6/Diagnose Body Action Bindings")]
    public static void RunMenu() => Run();

    public static void RunBatch()
    {
        var pass = Run();
        EditorApplication.Exit(pass ? 0 : 2);
    }

    public static bool Run()
    {
        var registry = AssetDatabase.LoadAssetAtPath<MechActionRegistry>(RegistryPath);
        if (registry == null || !registry.TryGetAction("a000_010102", out var action))
        {
            Debug.LogError("[AC6BodyActionDiagnostic] Registry or flight action is missing.");
            return false;
        }

        var allPass = true;
        foreach (var (slot, token) in Parts)
        {
            var partAsset = AssetDatabase.LoadMainAssetAtPath($"{RawGltfRoot}/{token}_M_1010.glb") as GameObject;
            var clip = action.ResolveClip(slot);
            if (partAsset == null || clip == null)
            {
                Debug.LogError($"[AC6BodyActionDiagnostic] {slot}: part or clip missing.");
                allPass = false;
                continue;
            }

            var instance = UnityEngine.Object.Instantiate(partAsset);
            instance.name = $"Diagnostic_{token}";
            try
            {
                var curvePaths = AnimationUtility.GetCurveBindings(clip)
                    .Select(binding => binding.path)
                    .ToHashSet(StringComparer.Ordinal);
                var rendererBonePaths = instance.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                    .SelectMany(renderer => renderer.bones ?? Array.Empty<Transform>())
                    .Where(bone => bone != null)
                    .Select(bone => AnimationUtility.CalculateTransformPath(bone, instance.transform))
                    .ToHashSet(StringComparer.Ordinal);
                var matchedPaths = curvePaths.Count(rendererBonePaths.Contains);
                var duplicateBoneNames = instance.GetComponentsInChildren<Transform>(true)
                    .GroupBy(transform => transform.name, StringComparer.Ordinal)
                    .Count(group => group.Count() > 1);

                clip.SampleAnimation(instance, 0f);
                var first = CaptureVisibleVertices(instance);
                var sampleTime = Mathf.Min(clip.length * 0.5f, 1.25f);
                clip.SampleAnimation(instance, sampleTime);
                var second = CaptureVisibleVertices(instance);
                var vertexDelta = CompareVisibleVertices(first, second);
                var pass = curvePaths.Count > 0 && matchedPaths > 0 && vertexDelta > 0.0005f;
                allPass &= pass;
                Debug.Log(
                    $"[AC6BodyActionDiagnostic] {slot}: {(pass ? "PASS" : "FAIL")} " +
                    $"curvePaths={curvePaths.Count} rendererBonePaths={rendererBonePaths.Count} " +
                    $"matched={matchedPaths} duplicateBoneNames={duplicateBoneNames} " +
                    $"visibleVertexMaxDelta={vertexDelta:0.######}m sampleTime={sampleTime:0.###}s.");

                if (!pass)
                {
                    var unmatched = curvePaths.Where(path => !rendererBonePaths.Contains(path)).Take(12);
                    Debug.LogWarning(
                        $"[AC6BodyActionDiagnostic] {slot} unmatched curve paths: " +
                        string.Join(", ", unmatched));
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        Debug.Log($"[AC6BodyActionDiagnostic] RESULT={(allPass ? "PASS" : "FAIL")}");
        return allPass;
    }

    private static List<Vector3[]> CaptureVisibleVertices(GameObject root)
    {
        var result = new List<Vector3[]>();
        var renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        for (var i = 0; i < renderers.Length; i++)
        {
            var mesh = new Mesh();
            try
            {
                renderers[i].updateWhenOffscreen = true;
                renderers[i].BakeMesh(mesh);
                result.Add(mesh.vertices);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        return result;
    }

    private static float CompareVisibleVertices(IReadOnlyList<Vector3[]> first, IReadOnlyList<Vector3[]> second)
    {
        var maxDelta = 0f;
        var rendererCount = Mathf.Min(first.Count, second.Count);
        for (var rendererIndex = 0; rendererIndex < rendererCount; rendererIndex++)
        {
            var firstVertices = first[rendererIndex];
            var secondVertices = second[rendererIndex];
            var vertexCount = Mathf.Min(firstVertices.Length, secondVertices.Length);
            for (var vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
            {
                maxDelta = Mathf.Max(
                    maxDelta,
                    Vector3.Distance(firstVertices[vertexIndex], secondVertices[vertexIndex]));
            }
        }

        return maxDelta;
    }
}
