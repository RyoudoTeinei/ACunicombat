using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEditor;
using UnityEditor.SceneManagement;
public class MechPlumeInstancingTests
{
 [UnityTest] public IEnumerator InstancedPlumesPreservePerNozzleAppearance()
 {
  EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
  var root=new GameObject("Plume image regression");var before=AssetDatabase.LoadAssetAtPath<Shader>("Assets/_Project/Editor/PerformanceFixtures/ThrusterPlumeReference.shader");var after=Shader.Find("ACunicombat/Mech Thruster Plume");
  var oldMaterial=new Material(before);var newMaterial=new Material(after){enableInstancing=true};var targets=new List<Renderer>();var rt=new RenderTexture(384,192,24,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear);var oldPixels=new Texture2D(384,192,TextureFormat.RGBA32,false,true);var newPixels=new Texture2D(384,192,TextureFormat.RGBA32,false,true);
  var previous=RenderTexture.active;
  try
  {
   Assert.IsTrue(after.isSupported);var cam=root.AddComponent<Camera>();cam.enabled=false;cam.transform.position=new Vector3(0,0,-10);cam.orthographic=true;cam.orthographicSize=2;cam.aspect=2;cam.clearFlags=CameraClearFlags.SolidColor;cam.backgroundColor=Color.black;cam.targetTexture=rt;
   for(int i=0;i<3;i++){var go=GameObject.CreatePrimitive(PrimitiveType.Quad);go.transform.SetParent(root.transform,false);go.transform.position=new Vector3((i-1)*2,0,0);go.transform.localScale=new Vector3(1,2,1);var renderer=go.GetComponent<Renderer>();targets.Add(renderer);renderer.sharedMaterial=oldMaterial;var block=new MaterialPropertyBlock();block.SetColor("_CoreColor",i==0?new Color(.8f,.2f,.1f):i==1?new Color(.1f,.8f,.2f):new Color(.2f,.1f,.8f));block.SetColor("_EdgeColor",new Color(.05f,.15f,.2f));block.SetFloat("_Intensity",.6f+i*.2f);block.SetFloat("_Opacity",.3f+i*.2f);block.SetFloat("_Softness",1.5f+i*.5f);block.SetFloat("_FlowSpeed",20);block.SetFloat("_Seed",i*3.17f);renderer.SetPropertyBlock(block);}
   cam.Render();RenderTexture.active=rt;oldPixels.ReadPixels(new Rect(0,0,384,192),0,0);oldPixels.Apply();foreach(var renderer in targets)renderer.sharedMaterial=newMaterial;
   cam.Render();RenderTexture.active=rt;newPixels.ReadPixels(new Rect(0,0,384,192),0,0);newPixels.Apply();var a=oldPixels.GetPixels32();var b=newPixels.GetPixels32();int max=0,visible=0;for(int i=0;i<a.Length;i++){if(a[i].r+a[i].g+a[i].b>10)visible++;max=System.Math.Max(max,System.Math.Abs(a[i].r-b[i].r));max=System.Math.Max(max,System.Math.Abs(a[i].g-b[i].g));max=System.Math.Max(max,System.Math.Abs(a[i].b-b[i].b));}Assert.Greater(visible,1000,"A blank render cannot pass equivalence");Assert.LessOrEqual(max,2,"Per-instance properties and shape must match the reference shader");TestContext.WriteLine($"pixels visible={visible}, maximum channel difference={max}");
  }
  finally{RenderTexture.active=previous;Object.DestroyImmediate(root);rt.Release();Object.DestroyImmediate(rt);Object.DestroyImmediate(oldPixels);Object.DestroyImmediate(newPixels);Object.DestroyImmediate(oldMaterial);Object.DestroyImmediate(newMaterial);}
  yield return new ExitPlayMode();
 }
}
