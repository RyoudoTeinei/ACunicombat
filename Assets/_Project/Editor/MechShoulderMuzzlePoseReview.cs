using System.IO;
using System.Linq;
using System.Reflection;
using ACunicombat.Mechs;
using UnityEditor.SceneManagement;
using UnityEngine;
public static class MechShoulderMuzzlePoseReview
{
 public static void Run()
 {
  string dir="Artifacts/Endurance-20261027/shoulder-pose";Directory.CreateDirectory(dir);EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);
  var part=Resources.Load<MechPartDefinition>("AC6Parts/AC6_NATIVE_WP_R_5110");if(part==null)throw new System.Exception("5110 missing");
  var model=Object.Instantiate(part.ModelPrefab);MechAppearanceApplicator.PrepareSurfaces(model);var animator=model.AddComponent<MechEquipmentAnimationPlayer>();animator.Configure(part.EquipmentAnimations,part.DefaultEquipmentActionId,false);animator.SampleActionAtEnd("a000_800110");animator.SampleActionAtTime("a000_800201",.1f);
  var transforms=model.GetComponentsInChildren<Transform>(true);var muzzle=transforms.First(t=>t.name.EndsWith("_dmy_300"));var pivot=transforms.First(t=>t.name.EndsWith("LookAt_01"));
  var light=new GameObject("light").AddComponent<Light>();light.type=LightType.Directional;light.intensity=2;light.transform.rotation=Quaternion.Euler(35,20,0);RenderSettings.ambientMode=UnityEngine.Rendering.AmbientMode.Flat;RenderSettings.ambientLight=Color.gray;
  var marker=GameObject.CreatePrimitive(PrimitiveType.Sphere);marker.name="Actual muzzle node";marker.transform.position=muzzle.position;marker.transform.localScale=Vector3.one*.1f;var material=new Material(Shader.Find("Universal Render Pipeline/Unlit"));material.SetColor("_BaseColor",Color.green);marker.GetComponent<Renderer>().sharedMaterial=material;
  var camera=new GameObject("muzzle camera").AddComponent<Camera>();camera.enabled=false;camera.nearClipPlane=.01f;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.025f,.035f,.045f);camera.fieldOfView=45;
  var renderers=model.GetComponentsInChildren<Renderer>();var bounds=renderers[0].bounds;foreach(var r in renderers)bounds.Encapsulate(r.bounds);
  var direction=(muzzle.position-pivot.position).normalized;var right=Vector3.Cross(direction,Vector3.up).normalized;
  camera.transform.position=bounds.center+direction*bounds.size.magnitude+right*bounds.size.magnitude*.7f+Vector3.up*bounds.size.magnitude*.4f;camera.transform.LookAt(bounds.center);Capture(camera,dir+"/whole.png");
  camera.transform.position=muzzle.position+direction*2.3f+right*1.3f+Vector3.up*.5f;camera.transform.LookAt(muzzle.position-direction*.4f);Capture(camera,dir+"/tip.png");
  File.WriteAllText(dir+"/binding.txt",part.PartId+"\nnode="+muzzle.name+"\nposition="+muzzle.position.ToString("F5")+"\npivot="+pivot.position.ToString("F5")+"\nGreen sphere: exact production _dmy_300 node; pose sampled from native sustained-fire animation at 0.1s.");
 }
 static void Capture(Camera camera,string path)=>typeof(MechColdLaunchPlacementV2Tests).GetMethod("Capture",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{camera,path});
}
