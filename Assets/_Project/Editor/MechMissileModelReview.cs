using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using ACunicombat.Mechs;

public static class MechMissileModelReview
{
    public static void Run()
    {
        string dir=Environment.GetEnvironmentVariable("MISSILE_MODEL_REVIEW")??"E:/UnityDev/ACunicombat/Artifacts/MissileModels-20261031/after";
        Directory.CreateDirectory(dir);
        try
        {
            foreach(var path in new[]{"r27_white","r77_gridfin","aam4290_white_blackband","wp4300_interceptor_hellfire","wp4301_dogfight_aim7","kh38mt_meteor_proxy"})
            {
                var prefab=Resources.Load<GameObject>("Projectiles/"+path);
                if(prefab==null) throw new Exception("Missing model "+path);
                var instance=UnityEngine.Object.Instantiate(prefab);
                var renderers=instance.GetComponentsInChildren<Renderer>(true);
                File.AppendAllText(dir+"/models.txt",path+" rootActive="+instance.activeSelf+" renderers="+renderers.Length+"\n");
                foreach(var r in renderers)File.AppendAllText(dir+"/models.txt",$"  {r.name} enabled={r.enabled} active={r.gameObject.activeInHierarchy} bounds={r.bounds} materials={r.sharedMaterials.Length}\n");
                UnityEngine.Object.DestroyImmediate(instance);
                var host=new GameObject("Preview test");
                var preview=host.AddComponent<MechHangarPreviewRenderer>();
                preview.ShowStandaloneModel(prefab,path,new Vector3(-90,58,-6));
                var flags=BindingFlags.NonPublic|BindingFlags.Instance;
                typeof(MechHangarPreviewRenderer).GetMethod("EnsureTarget",flags).Invoke(preview,new object[]{640,480});
                var camera=(Camera)typeof(MechHangarPreviewRenderer).GetField("previewCamera",flags).GetValue(preview);
                camera.Render();
                var old=RenderTexture.active;RenderTexture.active=camera.targetTexture;
                var image=new Texture2D(640,480,TextureFormat.RGB24,false);image.ReadPixels(new Rect(0,0,640,480),0,0);image.Apply();
                File.WriteAllBytes(dir+"/"+path+".png",image.EncodeToPNG());
                RenderTexture.active=old;UnityEngine.Object.DestroyImmediate(image);UnityEngine.Object.DestroyImmediate(host);
                CaptureFlightModel(dir,path);
            }
            EditorApplication.Exit(0);
        }
        catch(Exception e){File.WriteAllText(dir+"/error.txt",e.ToString());EditorApplication.Exit(1);}
    }
    private static void CaptureFlightModel(string dir,string path)
    {
        var root=new GameObject("Flight presentation");
        root.transform.rotation=Quaternion.Euler(23,47,11);
        var placeholder=GameObject.CreatePrimitive(PrimitiveType.Sphere);
        var cameraObject=new GameObject("Flight model camera");
        var camera=cameraObject.AddComponent<Camera>();camera.enabled=false;
        camera.transform.position=new Vector3(6,3,-7);camera.transform.LookAt(Vector3.zero);
        camera.nearClipPlane=.05f;camera.farClipPlane=30;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.black;
        var flags=BindingFlags.NonPublic|BindingFlags.Static;
        var axis=path=="kh38mt_meteor_proxy"?Vector3.right:Vector3.down;
        typeof(MechTestWeaponSystem).GetMethod("TryAttachMissileVisual",flags).Invoke(null,new object[]{root.transform,placeholder.GetComponent<Renderer>(),camera,"Projectiles/"+path,path,4f,axis});
        if(placeholder.GetComponent<Renderer>().enabled)throw new Exception("Flight model attachment failed: "+path);
        var holder=root.transform.GetChild(0);
        var renderers=holder.GetComponentsInChildren<Renderer>();
        object[] args={holder,renderers,default(Bounds)};
        if(!(bool)typeof(MechTestWeaponSystem).GetMethod("TryCalculateLocalRendererBounds",flags).Invoke(null,args))throw new Exception("No flight bounds: "+path);
        var bounds=(Bounds)args[2];
        float length=Mathf.Max(bounds.size.x,bounds.size.y,bounds.size.z);
        if(Mathf.Abs(length-4f)>.02f||bounds.center.magnitude>.02f)throw new Exception("Incorrect flight scale/center: "+path+" "+bounds);
        File.AppendAllText(dir+"/flight-validation.txt",path+" PASS "+bounds+"\n");
        var target=new RenderTexture(640,480,24);target.Create();camera.targetTexture=target;camera.Render();
        var old=RenderTexture.active;RenderTexture.active=target;
        var image=new Texture2D(640,480,TextureFormat.RGB24,false);image.ReadPixels(new Rect(0,0,640,480),0,0);image.Apply();
        File.WriteAllBytes(dir+"/flight-"+path+".png",image.EncodeToPNG());RenderTexture.active=old;
        camera.targetTexture=null;UnityEngine.Object.DestroyImmediate(target);UnityEngine.Object.DestroyImmediate(image);
        UnityEngine.Object.DestroyImmediate(root);UnityEngine.Object.DestroyImmediate(placeholder);UnityEngine.Object.DestroyImmediate(cameraObject);
    }
}
