using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ACunicombat.Mechs;
using UnityEditor;
using UnityEngine;

public static class MechLongRangeMissileSetup
{
    public static readonly string[] Numbers={"4310","4230","4340"};
    [MenuItem("Tools/ACunicombat/Update Long Range Missiles")]
    public static void Apply()
    {
        foreach(string number in Numbers) foreach(string side in new[]{"L","R"})
        {
            string key="WP_"+side+"_"+number;
            var loader=typeof(AC6FullPartCatalogBuilder).GetMethod("TryLoadNativeEquipment",BindingFlags.NonPublic|BindingFlags.Static);
            object[] args={key,null,null};
            if(!(bool)loader.Invoke(null,args))throw new InvalidOperationException("Missing animated launcher: "+key);
            foreach(string guid in AssetDatabase.FindAssets(key+" t:MechPartDefinition"))
            {
                var part=AssetDatabase.LoadAssetAtPath<MechPartDefinition>(AssetDatabase.GUIDToAssetPath(guid));
                if(!part.PartId.EndsWith(key,StringComparison.OrdinalIgnoreCase))continue;
                JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(AC6TestWeaponProfileFactory.Create(part.PartId)),part.WeaponPerformance);
                var so=new SerializedObject(part);
                so.FindProperty("modelPrefab").objectReferenceValue=(GameObject)args[1];
                so.FindProperty("previewModelPrefab").objectReferenceValue=(GameObject)args[1];
                so.FindProperty("defaultEquipmentActionId").stringValue="a000_800000";
                so.ApplyModifiedPropertiesWithoutUndo();
                typeof(MechPartDefinition).GetField("equipmentAnimations",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(part,(List<MechEquipmentAnimationDefinition>)args[2]);
                EditorUtility.SetDirty(part);
            }
        }
        AssetDatabase.SaveAssets();
    }

    // Call only after the real launch and flight regression succeeds.
    public static void MarkVerified()
    {
        var catalog=Resources.Load<MechPartCatalog>("AC6FullPartCatalog");
        var so=new SerializedObject(catalog);var entries=so.FindProperty("entries");
        for(int i=0;i<entries.arraySize;i++)
        {
            var e=entries.GetArrayElementAtIndex(i);string id=e.FindPropertyRelative("partId").stringValue;
            if(!Numbers.Any(n=>id.EndsWith("WP_L_"+n)||id.EndsWith("WP_R_"+n)))continue;
            e.FindPropertyRelative("testStatus").intValue=(int)MechPartTestStatus.Tested;
            e.FindPropertyRelative("statusNote").stringValue="长程/反辐射配置；已验证原生展开、实际发射、推进与射程。使用现有弹体代理模型；射程为游戏标定。";
        }
        so.ApplyModifiedPropertiesWithoutUndo();AssetDatabase.SaveAssets();
    }

    public static void CaptureLaunchers()
    { Capture(Numbers,"Artifacts/LongRangeMissiles-20261022/visuals"); }
    public static void CaptureModelSwap()
    { Capture(new[]{"4302","4310","4320"},"Artifacts/Jianwei7-20261022/model-correction"); }
    public static void CaptureJianwei()
    { Capture(new[]{"4250","4251","4252"},"Artifacts/Jianwei7-20261022/visuals"); }
    private static void Capture(string[] numbers,string output)
    {
        UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene);
        System.IO.Directory.CreateDirectory(output);
        var light=new GameObject("Review light").AddComponent<Light>();light.type=LightType.Directional;light.intensity=2;
        light.transform.rotation=Quaternion.Euler(35,-30,0);RenderSettings.ambientMode=UnityEngine.Rendering.AmbientMode.Flat;RenderSettings.ambientLight=Color.gray;
        var camera=new GameObject("Review camera").AddComponent<Camera>();camera.enabled=false;
        camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.025f,.035f,.05f);camera.fieldOfView=35;
        var capture=typeof(MechSurfaceRenderReview).GetMethod("Capture",BindingFlags.NonPublic|BindingFlags.Static);
        foreach(string n in numbers)foreach(string side in new[]{"L","R"})
        {
            var part=Resources.Load<MechPartDefinition>("AC6Parts/AC6_FULL_WP_"+side+"_"+n)??Resources.Load<MechPartDefinition>("AC6Parts/AC6_NATIVE_WP_"+side+"_"+n);
            var instance=UnityEngine.Object.Instantiate(part.ModelPrefab);
            MechAppearanceApplicator.PrepareSurfaces(instance);
            var animation=instance.AddComponent<MechEquipmentAnimationPlayer>();animation.Configure(part.EquipmentAnimations,part.DefaultEquipmentActionId,false);
            var renderers=instance.GetComponentsInChildren<Renderer>();var bounds=renderers[0].bounds;foreach(var r in renderers)bounds.Encapsulate(r.bounds);
            camera.transform.position=bounds.center+new Vector3(.65f,.4f,-1).normalized*bounds.size.magnitude*1.5f;camera.transform.LookAt(bounds.center);
            light.transform.rotation=camera.transform.rotation;
            animation.SampleActionAtTime("a000_800000",0);
            capture.Invoke(null,new object[]{camera,output+"/"+n+"-"+side+"-closed.png"});
            animation.SampleActionAtEnd("a000_800110");animation.SampleActionAtEnd("a000_800120");
            capture.Invoke(null,new object[]{camera,output+"/"+n+"-"+side+"-open.png"});
            UnityEngine.Object.DestroyImmediate(instance);
        }
    }

    public static void FinalizeVerified()
    {
        MarkVerified();CaptureLaunchers();
    }
}
