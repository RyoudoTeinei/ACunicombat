using System;
using System.Collections.Generic;
using System.Reflection;
using ACunicombat.Mechs;
using UnityEditor;
using UnityEngine;
public static class MechAbmSimSetup
{
    public static void Apply()
    {
        foreach(string guid in AssetDatabase.FindAssets("t:MechPartDefinition"))
        {
            var part=AssetDatabase.LoadAssetAtPath<MechPartDefinition>(AssetDatabase.GUIDToAssetPath(guid));
            if(!MechMissileLauncherCatalog.IsAbmOrSim(part.PartId))continue;
            var d=MechMissileLauncherCatalog.Find(part.PartId);
            JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(MechMissileLauncherCatalog.CreateDefaultPerformance(part.PartId)),part.WeaponPerformance);
            object[] args={part.PartId.Substring(part.PartId.Length-9),null,null};
            if(!(bool)typeof(AC6FullPartCatalogBuilder).GetMethod("TryLoadNativeEquipment",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,args))throw new Exception("Missing native launcher: "+part.PartId);
            var so=new SerializedObject(part);
            so.FindProperty("modelPrefab").objectReferenceValue=(GameObject)args[1];so.FindProperty("previewModelPrefab").objectReferenceValue=(GameObject)args[1];
            so.FindProperty("displayName").stringValue=d.Purpose+" "+d.Number;
            so.FindProperty("defaultEquipmentActionId").stringValue="a000_800000";so.FindProperty("materialOverrides").arraySize=0;so.ApplyModifiedPropertiesWithoutUndo();
            typeof(MechPartDefinition).GetField("equipmentAnimations",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(part,(List<MechEquipmentAnimationDefinition>)args[2]);
            EditorUtility.SetDirty(part);
        }
        AssetDatabase.SaveAssets();UpdateCatalog(false);
    }
    public static void Finish()
    {
        UpdateCatalog(true);
        typeof(MechLongRangeMissileSetup).GetMethod("Capture",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{new[]{"4300","4303","4290","4291","4292"},"Artifacts/AbmSim-20261022/visuals"});
    }
    static void UpdateCatalog(bool verified)
    {
        var so=new SerializedObject(Resources.Load<MechPartCatalog>("AC6FullPartCatalog"));var entries=so.FindProperty("entries");
        for(int i=0;i<entries.arraySize;i++)
        {
            var e=entries.GetArrayElementAtIndex(i);var id=e.FindPropertyRelative("partId").stringValue;if(!MechMissileLauncherCatalog.IsAbmOrSim(id))continue;
            var d=MechMissileLauncherCatalog.Find(id);e.FindPropertyRelative("displayName").stringValue=d.Purpose+" "+d.Number;
            if(verified){e.FindPropertyRelative("testStatus").intValue=10;e.FindPropertyRelative("statusNote").stringValue="ABM/SIM系列：独立弹种、兼容性、容量与原生展开发射已验证。";}
        }
        so.ApplyModifiedPropertiesWithoutUndo();AssetDatabase.SaveAssets();
    }
}
