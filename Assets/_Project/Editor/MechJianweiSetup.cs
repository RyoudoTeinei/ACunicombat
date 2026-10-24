using System;
using System.Collections.Generic;
using System.Reflection;
using ACunicombat.Mechs;
using UnityEditor;
using UnityEngine;
public static class MechJianweiSetup
{
    public static void Apply()
    {
        AC6LauncherVisualMapping.Apply();
        foreach(string guid in AssetDatabase.FindAssets("t:MechPartDefinition"))
        {
            var part=AssetDatabase.LoadAssetAtPath<MechPartDefinition>(AssetDatabase.GUIDToAssetPath(guid));
            var definition=MechMissileLauncherCatalog.Find(part.PartId);if(definition==null)continue;
            JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(MechMissileLauncherCatalog.CreateDefaultPerformance(part.PartId)),part.WeaponPerformance);
            if(MechMissileLauncherCatalog.IsJianwei(part.PartId))
            {
                object[] args={part.PartId.Substring(part.PartId.Length-9),null,null};
                if(!(bool)typeof(AC6FullPartCatalogBuilder).GetMethod("TryLoadNativeEquipment",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,args))throw new Exception("Missing launcher animation: "+part.PartId);
                var so=new SerializedObject(part);
                so.FindProperty("modelPrefab").objectReferenceValue=(GameObject)args[1];so.FindProperty("previewModelPrefab").objectReferenceValue=(GameObject)args[1];
                so.FindProperty("displayName").stringValue=definition.Purpose+" "+definition.Number;
                so.FindProperty("defaultEquipmentActionId").stringValue="a000_800000";so.FindProperty("materialOverrides").arraySize=0;
                so.ApplyModifiedPropertiesWithoutUndo();
                typeof(MechPartDefinition).GetField("equipmentAnimations",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(part,(List<MechEquipmentAnimationDefinition>)args[2]);
            }
            EditorUtility.SetDirty(part);
        }
        AssetDatabase.SaveAssets();UpdateCatalog(false);
    }
    public static void MarkVerified()=>UpdateCatalog(true);
    public static void Finish(){MarkVerified();MechLongRangeMissileSetup.CaptureJianwei();MechLongRangeMissileSetup.CaptureModelSwap();}
    static void UpdateCatalog(bool verified)
    {
        var catalog=Resources.Load<MechPartCatalog>("AC6FullPartCatalog");var so=new SerializedObject(catalog);var entries=so.FindProperty("entries");
        for(int i=0;i<entries.arraySize;i++)
        {
            var e=entries.GetArrayElementAtIndex(i);var id=e.FindPropertyRelative("partId").stringValue;
            if(!MechMissileLauncherCatalog.IsJianwei(id))continue;
            var d=MechMissileLauncherCatalog.Find(id);e.FindPropertyRelative("displayName").stringValue=d.Purpose+" "+d.Number;
            if(verified){e.FindPropertyRelative("testStatus").intValue=10;e.FindPropertyRelative("statusNote").stringValue="剑卫7系列：独立弹种/仓容量；原生冷发射、分组装填及选弹已验证。";}
        }
        so.ApplyModifiedPropertiesWithoutUndo();AssetDatabase.SaveAssets();
    }
}
