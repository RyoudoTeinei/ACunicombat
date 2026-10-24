using System.IO;
using System.Reflection;
using ACunicombat.Mechs;
using UnityEditor;
using UnityEngine;
public static class MechRadarRingReview
{
 public static void Run()
 {
  const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
  var go=new GameObject("Radar ring material review");go.SetActive(false);
  var hud=go.AddComponent<MechRadarHudPresenter>();
  typeof(MechRadarHudPresenter).GetMethod("EnsureOperationRingTexture",flags).Invoke(hud,null);
  var texture=(Texture2D)typeof(MechRadarHudPresenter).GetField("operationRingTexture",flags).GetValue(hud);
  var rt=RenderTexture.GetTemporary(1024,1024,0,RenderTextureFormat.ARGB32);
  var previous=RenderTexture.active;Graphics.Blit(texture,rt);RenderTexture.active=rt;
  var image=new Texture2D(1024,1024,TextureFormat.RGBA32,false);image.ReadPixels(new Rect(0,0,1024,1024),0,0);image.Apply();
  var source=image.GetPixels();
  const int width=1280,height=640;var result=new Texture2D(width,height,TextureFormat.RGBA32,false);
  var output=new Color[width*height];for(int i=0;i<output.Length;i++)output[i]=new Color(.018f,.027f,.034f,1);
  var target=Quaternion.Euler(0,30,0)*Vector3.forward;
  var turning=MechRadarHudPresenter.SmoothOperationDirection(Vector3.forward,target,.1f);
  var settled=turning;for(int i=0;i<120;i++)settled=MechRadarHudPresenter.SmoothOperationDirection(settled,target,1f/60);
  if(Vector3.Angle(turning,target)<1||Vector3.Angle(turning,Vector3.forward)<1||Vector3.Angle(settled,target)>.1f)throw new System.Exception("Turn/settle presentation regression");
  for(int panel=0;panel<2;panel++)
  {
   var outer=new Vector2(panel==0?300:850,320);const float radius=210;
   var inner=outer+(panel==0?Vector2.zero:new Vector2(250,0));
   for(int layer=0;layer<2;layer++)
   {
    var center=layer==0?outer:inner;float half=radius*(layer==0?1:.74f)/.92f;float opacity=layer==0?.58f:1;
    for(int y=0;y<height;y++)for(int x=panel*640;x<(panel+1)*640;x++)
    {
     float u=(x-center.x)/(2*half)+.5f,v=(y-center.y)/(2*half)+.5f;if(u<0||u>=1||v<0||v>=1)continue;
     var c=image.GetPixelBilinear(u,v);int index=y*width+x;output[index]=Color.Lerp(output[index],new Color(c.r,c.g,c.b,1),c.a*opacity);
    }
   }
  }
  result.SetPixels(output);result.Apply();const string folder="Artifacts/RadarRing-20261018";Directory.CreateDirectory(folder);
  File.WriteAllBytes(folder+"/unbounded-ring.png",result.EncodeToPNG());Object.DestroyImmediate(result);
  File.WriteAllText(folder+"/unbounded-ring-review.txt",$"Presentation check passed. Turn lag after 0.1s: {Vector3.Angle(turning,target):F2} deg; residual after settling: {Vector3.Angle(settled,target):F4} deg. Left: aligned. Right: inner centre lies outside outer radius; no containment. Actual cached runtime texture; not an in-game screenshot.");
  RenderTexture.active=previous;RenderTexture.ReleaseTemporary(rt);Object.DestroyImmediate(image);Object.DestroyImmediate(go);
  Debug.Log("Radar ring runtime texture exported successfully");EditorApplication.Exit(0);
 }
}
