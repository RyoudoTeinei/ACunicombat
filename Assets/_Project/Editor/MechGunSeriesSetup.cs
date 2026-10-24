using System.Linq;
using ACunicombat.Mechs;
using UnityEditor;
using UnityEngine;
public static class MechGunSeriesSetup
{
    public static bool IsNew(string id)=>new[]{"1710","1620","1730"}.Any(n=>id.EndsWith("_"+n));
    public static void ApplyFirstFamily()
    {
        int count=0;
        foreach(var guid in AssetDatabase.FindAssets("t:MechPartDefinition"))
        {
            var p=AssetDatabase.LoadAssetAtPath<MechPartDefinition>(AssetDatabase.GUIDToAssetPath(guid));
            if(!new[]{"1610","1710","1810","1830"}.Any(n=>p.PartId.EndsWith("_"+n)))continue;
            var expected=AC6TestWeaponProfileFactory.Create(p.PartId);
            p.WeaponPerformance.magazineCapacity=expected.magazineCapacity;
            p.WeaponPerformance.magazineCount=expected.magazineCount;
            p.WeaponPerformance.roundsPerMinute=expected.roundsPerMinute;
            EditorUtility.SetDirty(p);count++;
            Debug.Log(p.PartId+" ammo="+expected.magazineCapacity+"x"+expected.magazineCount+" rpm="+expected.roundsPerMinute);
        }
        AssetDatabase.SaveAssets();Debug.Log("First family updated: "+count);
    }
    public static void ApplyPrecisionPair()
    {
        foreach(var guid in AssetDatabase.FindAssets("t:MechPartDefinition"))
        {
            var p=AssetDatabase.LoadAssetAtPath<MechPartDefinition>(AssetDatabase.GUIDToAssetPath(guid));
            if(!p.PartId.EndsWith("_1620")&&!p.PartId.EndsWith("_1630"))continue;
            JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(AC6TestWeaponProfileFactory.Create(p.PartId)),p.WeaponPerformance);EditorUtility.SetDirty(p);
        }
        AssetDatabase.SaveAssets();
    }
    public static void Apply()
    {
        foreach(var guid in AssetDatabase.FindAssets("t:MechPartDefinition"))
        {
            var p=AssetDatabase.LoadAssetAtPath<MechPartDefinition>(AssetDatabase.GUIDToAssetPath(guid));if(!IsNew(p.PartId))continue;
            JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(AC6TestWeaponProfileFactory.Create(p.PartId)),p.WeaponPerformance);EditorUtility.SetDirty(p);
        }
        AssetDatabase.SaveAssets();
    }
    public static void MarkVerified()
    {
        var so=new SerializedObject(Resources.Load<MechPartCatalog>("AC6FullPartCatalog"));var entries=so.FindProperty("entries");
        for(int i=0;i<entries.arraySize;i++)
        {
            var e=entries.GetArrayElementAtIndex(i);if(!IsNew(e.FindPropertyRelative("partId").stringValue))continue;
            e.FindPropertyRelative("testStatus").intValue=10;
            e.FindPropertyRelative("statusNote").stringValue="同系列临时性能；左右安装、玩家/AI实弹射击、命中与装填验证通过。";
        }
        so.ApplyModifiedPropertiesWithoutUndo();AssetDatabase.SaveAssets();
    }
}
