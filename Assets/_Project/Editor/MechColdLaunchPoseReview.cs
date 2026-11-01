using System.IO;
using System.Linq;
using ACunicombat.Mechs;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
public static class MechColdLaunchPoseReview
{
 public static void Run()
 {
  var output="Artifacts/ColdLaunchPlacement-20261026/pose";Directory.CreateDirectory(output);
  EditorSceneManager.OpenScene("Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Scenes/AC6PlayerControl.unity");
  using(var log=new StreamWriter(output+"/pose.txt")){log.AutoFlush=true;
   var root=GameObject.Find("Player_Melander_1010");log.WriteLine("root="+root);if(root==null)throw new System.Exception("No player");
   var a=root.GetComponent<MechAssembler>();log.WriteLine("assembler="+a);
   var loadout=Object.Instantiate(a.Loadout);var assign=loadout.Assignments.First(x=>x!=null&&x.slot==MechPartSlot.LeftBackWeapon);assign.part=Resources.Load<MechPartDefinition>("AC6Parts/AC6_FULL_WP_L_4302");a.Loadout=loadout;a.Assemble();
   var record=a.RuntimeState.AssembledParts.First(x=>x.slot==MechPartSlot.LeftBackWeapon);var player=record.instance.GetComponent<MechEquipmentAnimationPlayer>();
   foreach(var action in new[]{"a000_800000","a000_800110","a000_800120","a000_800210"}){
    player.SampleActionAtEnd(action);log.WriteLine("ACTION="+action);
    foreach(var t in record.instance.GetComponentsInChildren<Transform>(true).Where(t=>t.name.Contains("dmy")))log.WriteLine(t.name+" "+root.transform.InverseTransformPoint(t.position).ToString("F3"));
   }
  }
 }
}
