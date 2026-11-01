using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ACunicombat.Mechs;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEditor.SceneManagement;
public class MechHotPathCacheTests
{
 const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
 [TestCase(4,1)] [TestCase(10,1.3333f)] [TestCase(100,2)] [TestCase(500,1.3333f)]
 public void SolidCircleTessellationStaysWithinQuarterPixel(float radius,float scale)
 {int n=MechRadarHudPresenter.CircleSegmentCount(radius,scale);double error=radius*scale*(1-System.Math.Cos(System.Math.PI/n));Assert.LessOrEqual(error,.251);Assert.Less(n,256);}
 static Transform Child(Transform parent,string name,Vector3 position){var t=new GameObject(name).transform;t.SetParent(parent,false);t.localPosition=position;return t;}
 [UnityTest] public IEnumerator CachedNozzlesSurviveSteadyFramesAndRecoverAfterDamageAndReassembly()
 {
  EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
  var root=new GameObject("Nozzle cache regression");
  try
  {
   var state=root.AddComponent<MechRuntimeState>();var arms=Child(root.transform,"Arms",Vector3.zero).gameObject;arms.AddComponent<MechAssembledPart>().Configure(MechPartSlot.Arms,null);
   Child(arms.transform,"AM_M_1010_dmy_25",Vector3.left);var right=Child(arms.transform,"R_ForeArm",Vector3.right);
   state.Apply(null,new MechAggregatedStats(),null,new List<MechAssemblyRecord>{new MechAssemblyRecord{slot=MechPartSlot.Arms,instance=arms}},new List<MechMountPoint>());
   var plume=root.AddComponent<MechMainThrusterPlume>();plume.ApplyNozzleLayout(new MechThrusterNozzleLayoutData());
   var sync=typeof(MechMainThrusterPlume).GetMethod("SynchronizeAuxiliaryEmitters",Flags);
   var field=typeof(MechMainThrusterPlume).GetField("auxiliaryEmitters",Flags);
   var emitters=(IDictionary)field.GetValue(plume);Assert.AreEqual(2,emitters.Count);
   var mirror=right.Find("AM_M_1010_dmy_25_MIRRORED");Assert.IsNotNull(mirror);
   var first=emitters.Values.Cast<object>().ToArray();for(int i=0;i<200;i++)sync.Invoke(plume,new object[]{false});CollectionAssert.AreEqual(first,emitters.Values.Cast<object>().ToArray());
   Object.DestroyImmediate(mirror.gameObject);sync.Invoke(plume,new object[]{false});yield return null;
   Assert.AreEqual(2,emitters.Count);Assert.IsNotNull(right.Find("AM_M_1010_dmy_25_MIRRORED"));
   plume.ApplyNozzleLayout(new MechThrusterNozzleLayoutData());yield return null;Assert.AreEqual(2,emitters.Count);
   Object.DestroyImmediate(arms);var core=Child(root.transform,"Core",Vector3.zero).gameObject;core.AddComponent<MechAssembledPart>().Configure(MechPartSlot.Core,null);Child(core.transform,"BD_M_1010_dmy_31",Vector3.zero);
   state.Apply(null,new MechAggregatedStats(),null,new List<MechAssemblyRecord>{new MechAssemblyRecord{slot=MechPartSlot.Core,instance=core}},new List<MechMountPoint>());
   sync.Invoke(plume,new object[]{false});yield return null;Assert.AreEqual(1,emitters.Count);
   Object.DestroyImmediate(core);state.Apply(null,new MechAggregatedStats(),null,new List<MechAssemblyRecord>(),new List<MechMountPoint>());sync.Invoke(plume,new object[]{false});yield return null;
   Assert.AreEqual(0,emitters.Count);Assert.IsTrue((bool)typeof(MechMainThrusterPlume).GetMethod("AuxiliaryEmitterAnchorsAreValid",Flags).Invoke(plume,null));
  }
  finally{Object.DestroyImmediate(root);}
  yield return new ExitPlayMode();
 }
}
