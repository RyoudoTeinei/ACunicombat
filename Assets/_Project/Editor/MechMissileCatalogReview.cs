using System;
using System.IO;
using System.Linq;
using System.Reflection;
using ACunicombat.Mechs;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class MechMissileCatalogReview
{
    [Serializable] public class Row
    {
        public string id,name,ammo,role,image;
        public int rounds,magazines;
        public float effectiveKm,maximumKm;
        public bool passive;
    }
    public static void Run()
    { Capture(false); }
    public static void RunUnconfigured()
    { Capture(true); }
    private static void Capture(bool unconfigured)
    {
        string output=unconfigured?"Artifacts/MissileCatalogUnused-20261022":"Artifacts/MissileCatalog-20261022";Directory.CreateDirectory(output);
        var entries=Resources.Load<MechPartCatalog>("AC6FullPartCatalog").Entries
            .Where(e=>e.IsTested!=unconfigured).Select(e=>e.LoadPart()).Where(p=>p!=null&&
                (unconfigured ? p.PartId.Contains("WP_")&&int.TryParse(p.PartId.Substring(p.PartId.Length-4),out int number)&&number>=4000&&number<=4350 : p.WeaponPerformance.IsGuidedMissile))
            .OrderBy(p=>p.WeaponPerformance.role).ThenBy(p=>p.PartId).ToArray();
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
                rounds=p.magazineCapacity,magazines=p.magazineCount,effectiveKm=p.effectiveRangeMeters/1000,maximumKm=p.maximumRangeMeters/1000,
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
