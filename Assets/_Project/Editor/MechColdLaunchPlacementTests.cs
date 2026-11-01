using System.IO;
using UnityEngine;
public class MechColdLaunchPlacementV2Tests
{
 static void Capture(Camera camera,string path){var rt=new RenderTexture(1000,800,24);camera.targetTexture=rt;camera.Render();var old=RenderTexture.active;RenderTexture.active=rt;var tex=new Texture2D(1000,800,TextureFormat.RGB24,false);tex.ReadPixels(new Rect(0,0,1000,800),0,0);tex.Apply();File.WriteAllBytes(path,tex.EncodeToPNG());RenderTexture.active=old;camera.targetTexture=null;UnityEngine.Object.DestroyImmediate(tex);UnityEngine.Object.DestroyImmediate(rt);}
}
