using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ACunicombat.Mechs;
using UnityEditor;
using UnityEngine;

/// <summary>
/// BHV 行为图编译器：把三份数据编译成运行时 MechBehaviorGraphAsset——
///   1. Tools/BhvDebug/c0000/basenormal_states.json（222 状态 + 8673 转换 + 条件文本，双重验证过）
///   2. Tools/AssetManifests/c0000_condition_catalog.json（140 条件原子的求值绑定）
///   3. Tools/AssetManifests/c0000_blend_map.json（TAE 官方 blend-in）
///      + c0000_state_anim_overrides.json（QB 等别名/占位覆盖）
/// 输出扁平数组资产。图内容零手写：改目录/覆盖后重跑本编译器即可，转换拓扑永不手编。
/// </summary>
public static class AC6BehaviorGraphCompiler
{
    private const string StatesJsonPath = "Tools/BhvDebug/c0000/basenormal_states.json";
    private const string CatalogPath = "Tools/AssetManifests/c0000_condition_catalog.json";
    private const string BlendMapPath = "Tools/AssetManifests/c0000_blend_map.json";
    private const string OverridesPath = "Tools/AssetManifests/c0000_state_anim_overrides.json";
    private const string OutputAssetPath =
        "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/AC6BehaviorGraph.asset";
    private const string GeneratedActionsRoot =
        "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Actions";
    private static readonly string[] FrameTokens = { "HD", "BD", "AM", "LG" };

    [MenuItem("Tools/ACunicombat/AC6/Compile Behavior Graph")]
    public static void CompileMenu()
    {
        var report = Compile();
        EditorUtility.DisplayDialog("AC6 Behavior Graph", report, "OK");
    }

    public static string Compile()
    {
        foreach (var required in new[] { StatesJsonPath, CatalogPath, BlendMapPath })
        {
            if (!File.Exists(required))
            {
                return $"missing input: {required}";
            }
        }

        var statesDoc = JsonUtility.FromJson<StatesDoc>(File.ReadAllText(StatesJsonPath));
        var catalog = JsonUtility.FromJson<CatalogDoc>(File.ReadAllText(CatalogPath));
        var blendMap = JsonUtility.FromJson<BlendDoc>(File.ReadAllText(BlendMapPath)).entries
            .ToDictionary(e => e.actionId, e => e.blendInSeconds);
        var overrides = File.Exists(OverridesPath)
            ? JsonUtility.FromJson<OverridesDoc>(File.ReadAllText(OverridesPath)).entries
                .ToDictionary(e => e.stateIndex, e => e.actionId)
            : new Dictionary<int, string>();

        // 原子表：目录序即索引；未收录的条件文本动态追加为 ConstFalse。
        var atoms = new List<MechBehaviorGraphAsset.Atom>();
        var atomIndexByText = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var atom in catalog.atoms)
        {
            atomIndexByText[atom.text] = atoms.Count;
            atoms.Add(new MechBehaviorGraphAsset.Atom
            {
                debugText = atom.text,
                kind = ParseKind(atom.bindingKind),
                param = atom.bindingParam,
            });
        }

        var states = new MechBehaviorGraphAsset.State[statesDoc.states.Length];
        var transitions = new List<MechBehaviorGraphAsset.Transition>(9000);
        var conditions = new List<MechBehaviorGraphAsset.Condition>(36000);
        var unavailable = 0;
        var appendedAtoms = 0;

        for (var i = 0; i < statesDoc.states.Length; i++)
        {
            var src = statesDoc.states[i];
            var actionId = overrides.TryGetValue(i, out var ov)
                ? ov
                : string.IsNullOrEmpty(src.hkxName) ? null : src.hkxName.Replace(".hkt", "");
            var available = TryGetActionClipDuration(actionId, out var durationSeconds);
            if (!available)
            {
                unavailable++;
            }

            // 循环启发：会检查"アニメーション終了"的状态天然是非循环段。
            var checksAnimEnd = src.transitions.Any(t =>
                t.conditions != null && t.conditions.Any(c => c.Contains("アニメーション終了")));

            states[i] = new MechBehaviorGraphAsset.State
            {
                officialName = src.name,
                actionId = actionId ?? string.Empty,
                animAvailable = available,
                transitionStart = transitions.Count,
                transitionCount = src.transitions.Length,
                blendInSeconds = actionId != null && blendMap.TryGetValue(actionId, out var b) ? b : 0f,
                loop = !checksAnimEnd,
            };

            foreach (var t in src.transitions)
            {
                var condStart = conditions.Count;
                if (t.conditions != null)
                {
                    foreach (var text in t.conditions)
                    {
                        var negate = text.TrimEnd().EndsWith("反転", StringComparison.Ordinal);
                        if (!atomIndexByText.TryGetValue(text, out var atomIndex))
                        {
                            atomIndex = atoms.Count;
                            atomIndexByText[text] = atomIndex;
                            atoms.Add(new MechBehaviorGraphAsset.Atom
                            {
                                debugText = text,
                                kind = MechConditionKind.ConstFalse,
                                param = 0,
                            });
                            appendedAtoms++;
                        }

                        conditions.Add(new MechBehaviorGraphAsset.Condition
                        {
                            atomIndex = (ushort)atomIndex,
                            negate = negate,
                        });
                    }
                }

                transitions.Add(new MechBehaviorGraphAsset.Transition
                {
                    targetState = (ushort)t.target,
                    conditionStart = condStart,
                    conditionCount = conditions.Count - condStart,
                });
            }
        }

        if (atoms.Count > 256)
        {
            return $"atom count {atoms.Count} exceeds the 4-word mask budget (256).";
        }

        var asset = AssetDatabase.LoadAssetAtPath<MechBehaviorGraphAsset>(OutputAssetPath);
        if (asset == null)
        {
            asset = ScriptableObject.CreateInstance<MechBehaviorGraphAsset>();
            AssetDatabase.CreateAsset(asset, OutputAssetPath);
        }

        asset.states = states;
        asset.transitions = transitions.ToArray();
        asset.conditions = conditions.ToArray();
        asset.atoms = atoms.ToArray();
        asset.initialState = 0; // 待機
        EditorUtility.SetDirty(asset);
        AssetDatabase.SaveAssets();

        var mappedAtoms = asset.atoms.Count(a => a.kind != MechConditionKind.ConstFalse);
        var report =
            $"Behavior graph compiled: states={states.Length} (anim-unavailable={unavailable}), " +
            $"transitions={asset.transitions.Length}, conditions={asset.conditions.Length}, " +
            $"atoms={asset.atoms.Length} (mapped={mappedAtoms}, appended-unmapped={appendedAtoms})\n" +
            $"-> {OutputAssetPath}";
        Debug.Log($"[AC6BehaviorGraph] {report}");
        return report;
    }

    private static bool TryGetActionClipDuration(string actionId, out float durationSeconds)
    {
        durationSeconds = 0f;
        if (string.IsNullOrWhiteSpace(actionId))
        {
            return false;
        }

        for (var i = 0; i < FrameTokens.Length; i++)
        {
            var path = $"{GeneratedActionsRoot}/{actionId}/AC6_{actionId}_{FrameTokens[i]}.anim";
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (clip == null || clip.length <= 0f)
            {
                durationSeconds = 0f;
                return false;
            }

            durationSeconds = Mathf.Max(durationSeconds, clip.length);
        }

        return true;
    }

    private static MechConditionKind ParseKind(string kind) =>
        Enum.TryParse<MechConditionKind>(kind, out var parsed) ? parsed : MechConditionKind.ConstFalse;

    // ---- 输入 JSON DTO（schema 由 ParseBhvStateTable.py 与目录生成脚本持有）----

#pragma warning disable 0649
    [Serializable]
    private sealed class StatesDoc
    {
        public StateEntry[] states;
    }

    [Serializable]
    private sealed class StateEntry
    {
        public int index;
        public string name;
        public string hkxName;
        public TransitionEntry[] transitions;
    }

    [Serializable]
    private sealed class TransitionEntry
    {
        public int target;
        public string[] conditions;
    }

    [Serializable]
    private sealed class CatalogDoc
    {
        public AtomEntry[] atoms;
    }

    [Serializable]
    private sealed class AtomEntry
    {
        public string text;
        public string bindingKind;
        public int bindingParam;
    }

    [Serializable]
    private sealed class BlendDoc
    {
        public BlendEntry[] entries;
    }

    [Serializable]
    private sealed class BlendEntry
    {
        public string actionId;
        public float blendInSeconds;
    }

    [Serializable]
    private sealed class OverridesDoc
    {
        public OverrideEntry[] entries;
    }

    [Serializable]
    private sealed class OverrideEntry
    {
        public int stateIndex;
        public string actionId;
    }
#pragma warning restore 0649
}
