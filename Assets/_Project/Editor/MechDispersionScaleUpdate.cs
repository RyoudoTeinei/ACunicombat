using System;
using System.IO;
using System.Linq;
using ACunicombat.Mechs;
using UnityEditor;
using UnityEngine;
public static class MechDispersionScaleUpdate
{
    public static void Apply()
    {
        const string output="Artifacts/DispersionX2-20261023";
        Directory.CreateDirectory(output);
        var fields=new[]{"baseDispersionMilliradians","maximumDispersionMilliradians","lowRecoilPerShotMilliradians","sustainedRecoilPerShotMilliradians","dispersionRecoveryMilliradiansPerSecond"}.Select(n=>typeof(MechWeaponPerformance).GetField(n)).ToArray();
        var parts=Resources.Load<MechPartCatalog>("AC6FullPartCatalog").Entries.Where(e=>e.IsTested).Select(e=>e.LoadPart()).Where(p=>p!=null&&p.WeaponPerformance.IsConfigured).ToArray();
        foreach(var part in parts)
        {
            var target=AC6TestWeaponProfileFactory.Create(part.PartId);
            foreach(var f in fields)
            {
                float old=(float)f.GetValue(part.WeaponPerformance),next=(float)f.GetValue(target);
                if(!Mathf.Approximately(next,old*2)&&!Mathf.Approximately(next,old))throw new Exception("Unexpected existing dispersion: "+part.PartId+" "+f.Name);
            }
        }
        foreach(var part in parts)
        {
            var path=output+"/"+part.PartId+".before.json";
            if(!File.Exists(path))File.WriteAllText(path,JsonUtility.ToJson(part.WeaponPerformance,true));
            var target=AC6TestWeaponProfileFactory.Create(part.PartId);
            foreach(var f in fields)f.SetValue(part.WeaponPerformance,f.GetValue(target));
            EditorUtility.SetDirty(part);
            File.WriteAllText(output+"/"+part.PartId+".after.json",JsonUtility.ToJson(part.WeaponPerformance,true));
        }
        AssetDatabase.SaveAssets();Debug.Log("Dispersion updated and verified: "+parts.Length+" tested weapon assets");
    }
}
