using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using ACunicombat.Mechs;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;

// Read-only scene/material review: never saves the scene or mutates source materials.
public static class MechSurfaceRenderReview
{
    [Serializable] class MaterialRow { public string actor,renderer,material,shader,path,normalFormat,normalPath,basePath;public int slot;public bool emission,normal,normalSrgb;public float strength;public Color emissionColor; }
    public static void Run()
    {
        var args=Environment.GetCommandLineArgs();int at=Array.IndexOf(args,"-surfaceOutput");
        string output=at>=0?args[at+1]:"Artifacts/SurfaceReview";Directory.CreateDirectory(output);
        try
        {
            EditorSceneManager.OpenScene("Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Scenes/AC6PlayerControl.unity");
            var assemblers=UnityEngine.Object.FindObjectsByType<MechAssembler>(FindObjectsSortMode.None);
            var actors=assemblers.Where(a=>a.name.StartsWith("Player_")||a.name=="AC6FriendlyMech_A").ToArray();
            if(actors.Length<2)throw new Exception("Expected saved player and friendly mech");
            foreach(var a in actors)a.Assemble();
            var all=UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None);
            var enabled=all.ToDictionary(r=>r,r=>r.enabled);
            var go=new GameObject("Surface review camera");var camera=go.AddComponent<Camera>();camera.enabled=false;
            camera.fieldOfView=35;camera.nearClipPlane=.1f;camera.farClipPlane=200000;camera.allowHDR=true;
            camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.025f,.03f,.04f);
            camera.GetUniversalAdditionalCameraData().renderPostProcessing=true;
            using(var log=new StreamWriter(output+"/materials.jsonl"))
            foreach(var a in actors)
            {
                var renderers=a.GetComponentsInChildren<Renderer>().Where(r=>r is SkinnedMeshRenderer||r is MeshRenderer).ToArray();
                var bounds=renderers[0].bounds;foreach(var r in renderers)bounds.Encapsulate(r.bounds);
                foreach(var r in all)if(r!=null)r.enabled=renderers.Contains(r)&&enabled[r];
                camera.transform.position=bounds.center+new Vector3(.4f,.12f,-1).normalized*bounds.size.magnitude*1.6f;
                camera.transform.LookAt(bounds.center);
                var originals=renderers.ToDictionary(r=>r,r=>r.sharedMaterials);
                foreach(var r in renderers)for(int i=0;i<r.sharedMaterials.Length;i++)
                {
                    var m=r.sharedMaterials[i];if(m==null)continue;
                    var n=m.HasProperty("_BumpMap")?m.GetTexture("_BumpMap"):null;
                    var b=m.HasProperty("_BaseMap")?m.GetTexture("_BaseMap"):null;
                    log.WriteLine(JsonUtility.ToJson(new MaterialRow{actor=a.name,renderer=r.name,slot=i,material=m.name,shader=m.shader.name,path=AssetDatabase.GetAssetPath(m),normalFormat=n!=null?n.graphicsFormat.ToString():"",normalSrgb=n!=null&&UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsSRGBFormat(n.graphicsFormat),normalPath=AssetDatabase.GetAssetPath(n),basePath=AssetDatabase.GetAssetPath(b),normal=m.IsKeywordEnabled("_NORMALMAP"),emission=m.IsKeywordEnabled("_EMISSION"),strength=m.HasProperty("_EmissionStrength")?m.GetFloat("_EmissionStrength"):0,emissionColor=m.HasProperty("_EmissionColor")?m.GetColor("_EmissionColor"):Color.black}));
                }
                foreach(string mode in new[]{"baseline","no-srp","no-emission","no-normal"})
                {
                    var copies=new List<Material>();
                    foreach(var r in renderers)
                    {
                        var mats=originals[r].Select(m=>m!=null?new Material(m):null).ToArray();
                        foreach(var m in mats)if(m!=null){copies.Add(m);if(mode=="no-emission"){m.DisableKeyword("_EMISSION");m.DisableKeyword("_EMISSIVE");if(m.HasProperty("_EmissionColor"))m.SetColor("_EmissionColor",Color.black);}if(mode=="no-normal")m.DisableKeyword("_NORMALMAP");}
                        r.sharedMaterials=mats;
                    }
                    bool batching=UnityEngine.Rendering.GraphicsSettings.useScriptableRenderPipelineBatching;
                    if(mode=="no-srp")UnityEngine.Rendering.GraphicsSettings.useScriptableRenderPipelineBatching=false;
                    Capture(camera,output+"/"+a.name+"-"+mode+".png");
                    UnityEngine.Rendering.GraphicsSettings.useScriptableRenderPipelineBatching=batching;
                    foreach(var r in renderers)r.sharedMaterials=originals[r];
                    foreach(var m in copies)UnityEngine.Object.DestroyImmediate(m);
                }
            }
            foreach(var r in all)if(r!=null)r.enabled=enabled[r];
            File.WriteAllText(output+"/status.txt","PASS: saved-scene assembly inspection; edit-mode controlled render, not gameplay proof.");EditorApplication.Exit(0);
        }
        catch(Exception e){File.WriteAllText(output+"/error.txt",e.ToString());Debug.LogException(e);EditorApplication.Exit(1);}
    }
    static void Capture(Camera camera,string path)
    {
        var rt=new RenderTexture(1024,1024,24,RenderTextureFormat.ARGBHalf);rt.Create();camera.targetTexture=rt;
        camera.Render();var previous=RenderTexture.active;RenderTexture.active=rt;
        var texture=new Texture2D(1024,1024,TextureFormat.RGB24,false);texture.ReadPixels(new Rect(0,0,1024,1024),0,0);texture.Apply();File.WriteAllBytes(path,texture.EncodeToPNG());
        RenderTexture.active=previous;camera.targetTexture=null;UnityEngine.Object.DestroyImmediate(texture);rt.Release();UnityEngine.Object.DestroyImmediate(rt);
    }
}
