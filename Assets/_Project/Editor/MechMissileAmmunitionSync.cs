using System;
using System.IO;
using ACunicombat.Mechs;
using UnityEditor;
using UnityEngine;

public static class MechMissileAmmunitionSync
{
    public static void Run()
    {
        string output="E:/UnityDev/ACunicombat/Artifacts/MissileModels-20261031/sim77-asset-sync.txt";
        try
        {
            int updated=0;
            foreach(string guid in AssetDatabase.FindAssets("t:MechPartDefinition"))
            {
                string path=AssetDatabase.GUIDToAssetPath(guid);
                var part=AssetDatabase.LoadAssetAtPath<MechPartDefinition>(path);
                if(part==null || MechMissileLauncherCatalog.Find(part.PartId)==null)continue;
                var expected=AC6TestWeaponProfileFactory.Create(part.PartId);
                if(expected==null)throw new InvalidOperationException("Missing launcher defaults: "+part.PartId);
                string json=JsonUtility.ToJson(expected);
                if(JsonUtility.ToJson(part.WeaponPerformance)==json)continue;
                JsonUtility.FromJsonOverwrite(json,part.WeaponPerformance);
                EditorUtility.SetDirty(part);
                AssetDatabase.SaveAssetIfDirty(part);
                File.AppendAllText(output,path+"\n");
                updated++;
            }
            File.AppendAllText(output,"Updated weapon performance on "+updated+" launcher assets.\n");
            EditorApplication.Exit(0);
        }
        catch(Exception e){File.AppendAllText(output,e.ToString());EditorApplication.Exit(1);}
    }
}
