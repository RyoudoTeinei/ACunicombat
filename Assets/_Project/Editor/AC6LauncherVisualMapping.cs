using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using ACunicombat.Mechs;
using UnityEditor;
using UnityEngine;

// Stable source mapping: part IDs and weapon performance do not change.
public static class AC6LauncherVisualMapping
{
    public static string SourceKey(string key)
    {
        if(key.EndsWith("_4310",StringComparison.OrdinalIgnoreCase))return key.Substring(0,key.Length-4)+"4302";
        if(key.EndsWith("_4302",StringComparison.OrdinalIgnoreCase))return key.Substring(0,key.Length-4)+"4310";
        return key;
    }
    public static void ApplyVisual(MechPartDefinition part)
    {
        if(part==null||!Affected(part.PartId))return;
        string key=part.PartId.Substring(part.PartId.Length-9);
        object[] args={key,null,null};
        var loader=typeof(AC6FullPartCatalogBuilder).GetMethod("TryLoadNativeEquipment",BindingFlags.Static|BindingFlags.NonPublic);
        if(!(bool)loader.Invoke(null,args))throw new InvalidOperationException("Missing mapped visual: "+key);
        var so=new SerializedObject(part);
        so.FindProperty("modelPrefab").objectReferenceValue=(GameObject)args[1];
        so.FindProperty("previewModelPrefab").objectReferenceValue=(GameObject)args[1];
        so.FindProperty("materialOverrides").arraySize=0;
        so.FindProperty("defaultEquipmentActionId").stringValue="a000_800000";
        so.ApplyModifiedPropertiesWithoutUndo();
        typeof(MechPartDefinition).GetField("equipmentAnimations",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(part,(List<MechEquipmentAnimationDefinition>)args[2]);
        EditorUtility.SetDirty(part);
    }
    public static void Apply()
    {
        const string output="Artifacts/LauncherModelSwap-20261022";Directory.CreateDirectory(output);
        using(var log=new StreamWriter(output+"/asset-checks.txt"))
        foreach(string guid in AssetDatabase.FindAssets("t:MechPartDefinition"))
        {
            var part=AssetDatabase.LoadAssetAtPath<MechPartDefinition>(AssetDatabase.GUIDToAssetPath(guid));
            if(!Affected(part.PartId))continue;
            string before=JsonUtility.ToJson(part.WeaponPerformance),stats=JsonUtility.ToJson(part.Stats);
            string oldModel=AssetDatabase.GetAssetPath(part.ModelPrefab);
            ApplyVisual(part);
            if(before!=JsonUtility.ToJson(part.WeaponPerformance)||stats!=JsonUtility.ToJson(part.Stats))throw new Exception("Performance changed: "+part.PartId);
            log.WriteLine(part.PartId+" | "+oldModel+" -> "+AssetDatabase.GetAssetPath(part.ModelPrefab)+" | performance/stats unchanged");
        }
        AssetDatabase.SaveAssets();
    }
    private static bool Affected(string id)=>id.EndsWith("_4302")||id.EndsWith("_4310")||id.EndsWith("_4320");
}
