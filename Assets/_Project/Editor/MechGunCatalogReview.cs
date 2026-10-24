using System;
using System.IO;
using System.Linq;
using System.Reflection;
using ACunicombat.Mechs;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class MechGunCatalogReview
{
    [Serializable] public class Row
    {
        public string id,name,ammo,role,image;
        public string weaponType;
        public float caliber;
        public int rounds,magazines;
        public float effectiveKm,maximumKm;
        public bool passive;
    }
    public static void ApplyCalibers()
    {
        foreach(string guid in UnityEditor.AssetDatabase.FindAssets("t:MechPartDefinition"))
        {
            var part=UnityEditor.AssetDatabase.LoadAssetAtPath<MechPartDefinition>(UnityEditor.AssetDatabase.GUIDToAssetPath(guid));
            if(!IsGun(part.PartId))continue;
            var p=AC6TestWeaponProfileFactory.Create(part.PartId);if(p==null||p.projectileCaliberMillimeters<=0)continue;
            part.WeaponPerformance.projectileCaliberMillimeters=p.projectileCaliberMillimeters;
            part.WeaponPerformance.weaponSeriesId=p.weaponSeriesId;
            part.WeaponPerformance.weaponType=p.weaponType;part.WeaponPerformance.hudLabel=p.hudLabel;
            UnityEditor.EditorUtility.SetDirty(part);
        }
        UnityEditor.AssetDatabase.SaveAssets();
        int checkedParts=0;
        foreach(var entry in Resources.Load<MechPartCatalog>("AC6FullPartCatalog").Entries)
        {
            var part=entry.LoadPart();if(part==null||!IsGun(part.PartId)||part.WeaponPerformance.projectileCaliberMillimeters<=0)continue;
            var expected=AC6TestWeaponProfileFactory.Create(part.PartId);
            if(expected.projectileCaliberMillimeters!=part.WeaponPerformance.projectileCaliberMillimeters||expected.weaponType!=part.WeaponPerformance.weaponType||expected.weaponSeriesId!=part.WeaponPerformance.weaponSeriesId||expected.IsConfigured!=part.WeaponPerformance.IsConfigured)throw new Exception("Caliber mismatch "+part.PartId);
            Debug.Log("Caliber verified: "+part.PartId+" | "+part.WeaponPerformance.weaponType);checkedParts++;
        }
        Debug.Log("Caliber assets verified: "+checkedParts);
    }
    public static void Run()
    { Capture(false); Capture(true); }
    static bool IsGun(string id)
    {
        if(!id.Contains("WP_")||!int.TryParse(id.Substring(id.Length-4),out int n))return false;
        return (n>=1300&&n<2400)||n==4010||n==4110||n==4410||n==4510||n==4610||n==4710||n==4810||n==4820||n==4830||n==5010||n==5110;
    }
    public static void RunUnconfigured()
    { Capture(true); }
    private static void Capture(bool unconfigured)
    {
        string output=unconfigured?"Artifacts/GunCatalog-20261023/untested":"Artifacts/GunCatalog-20261023/tested";Directory.CreateDirectory(output);
        var entries=Resources.Load<MechPartCatalog>("AC6FullPartCatalog").Entries
            .Where(e=>e.IsTested!=unconfigured).Select(e=>e.LoadPart()).Where(p=>p!=null&&IsGun(p.PartId))
            .OrderBy(p=>p.PartId).ToArray();
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);
        var camera=new GameObject("Catalog camera").AddComponent<Camera>();camera.enabled=false;camera.fieldOfView=35;
        camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.025f,.035f,.05f);
        var light=new GameObject("Catalog light").AddComponent<Light>();light.type=LightType.Directional;light.intensity=2;
        RenderSettings.ambientMode=UnityEngine.Rendering.AmbientMode.Flat;RenderSettings.ambientLight=Color.gray;
        var capture=typeof(MechSurfaceRenderReview).GetMethod("Capture",BindingFlags.NonPublic|BindingFlags.Static);
        using(var log=new StreamWriter(output+"/inventory.jsonl"))
        foreach(var part in entries)
        {
            string number=part.PartId.Substring(part.PartId.Length-4);var p=part.WeaponPerformance;
            log.WriteLine(JsonUtility.ToJson(new Row{id=part.PartId,name=part.DisplayName,ammo=p.missileDisplayName,role=p.role.ToString(),
                weaponType=p.weaponType,caliber=p.projectileCaliberMillimeters,rounds=p.magazineCapacity,magazines=p.magazineCount,effectiveKm=p.effectiveRangeMeters/1000,maximumKm=p.maximumRangeMeters/1000,
                passive=p.supportsPassiveRfHoming,image=number+".png"}));
            if(!part.PartId.Contains("_L_"))continue;
            var model=UnityEngine.Object.Instantiate(part.ModelPrefab);MechAppearanceApplicator.PrepareSurfaces(model);
            if(part.EquipmentAnimations.Count>0){var a=model.AddComponent<MechEquipmentAnimationPlayer>();a.Configure(part.EquipmentAnimations,part.DefaultEquipmentActionId,false);a.SampleActionAtTime("a000_800000",0);}
            var rs=model.GetComponentsInChildren<Renderer>();var bounds=rs[0].bounds;foreach(var r in rs)bounds.Encapsulate(r.bounds);
            camera.transform.position=bounds.center+new Vector3(.65f,.4f,-1).normalized*bounds.size.magnitude*1.5f;
            camera.transform.LookAt(bounds.center);light.transform.rotation=camera.transform.rotation;
            capture.Invoke(null,new object[]{camera,output+"/"+number+".png"});
            UnityEngine.Object.DestroyImmediate(model);
        }
        if(entries.Select(p=>p.PartId).Distinct().Count()!=entries.Length)throw new Exception("Duplicate launcher IDs in catalog.");
    }
}
