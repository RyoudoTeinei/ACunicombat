using System.Collections.Generic;
using System.IO;
using System.Reflection;
using ACunicombat.Mechs;
using NUnit.Framework;
using UnityEngine;
public class MechAuxiliaryThrustTests
{
 const BindingFlags P=BindingFlags.NonPublic|BindingFlags.Instance;
 [Test] public void ConfiguredAuxiliaryNozzlesAreExactlyOneAndAHalfTimesPreviousRatings()
 {
  const string path="Assets/_Project/Resources/Mech/ThrusterNozzleLayout.json";
  var old=JsonUtility.FromJson<MechThrusterNozzleLayoutData>(File.ReadAllText("Artifacts/AuxiliaryThrust50-20261026/before/"+path));var current=JsonUtility.FromJson<MechThrusterNozzleLayoutData>(File.ReadAllText(path));
  Assert.AreEqual(old.nozzles.Count,current.nozzles.Count);
  for(int i=0;i<old.nozzles.Count;i++){var a=old.nozzles[i];var b=current.nozzles[i];Assert.AreEqual(a.id,b.id);Assert.AreEqual(a.ratedThrustKn*1.5f,b.ratedThrustKn,.001f);b.ratedThrustKn=a.ratedThrustKn;Assert.AreEqual(JsonUtility.ToJson(a),JsonUtility.ToJson(b),"Only thrust changes");}
 }
 [Test] public void PhysicalAllocationIncreasesAuxiliaryForceOnceAndKeepsMainCapacity()
 {
  var go=new GameObject("Aux thrust test");try{
   var motion=go.AddComponent<MechFlightMotionController>();motion.ConfigureModelForwardYawOffset(0);
   var channels=(List<MechThrusterChannelState>)typeof(MechFlightMotionController).GetField("thrusterChannels",P).GetValue(motion);channels.Clear();
   var main=new GameObject("main").transform;main.SetParent(go.transform);main.rotation=Quaternion.LookRotation(Vector3.back);
   var aux=new GameObject("aux").transform;aux.SetParent(go.transform);aux.rotation=Quaternion.LookRotation(Vector3.left);
   channels.Add(new MechThrusterChannelState("main.test",MechThrusterRole.Main,MechThrusterSide.Center,main,Vector3.zero,false));
   channels.Add(new MechThrusterChannelState("aux.test",MechThrusterRole.Translation,MechThrusterSide.Center,aux,Vector3.zero,false));
   var allocate=typeof(MechFlightMotionController).GetMethod("AllocateTranslationForce",P);
   var actual=(Vector3)allocate.Invoke(motion,new object[]{Vector3.right*1e8f,4e6f,.02f,1f});
   var caps=(float[])typeof(MechFlightMotionController).GetField("translationNozzleCapacities",P).GetValue(motion);
   Assert.AreEqual(4e6f,caps[0],1);Assert.AreEqual(900000,caps[1],1);
   typeof(MechFlightMotionController).GetField("auxiliaryNozzleThrustKn",P).SetValue(motion,600f);
   foreach(var c in channels)c.ClearForce();
   var baseline=(Vector3)allocate.Invoke(motion,new object[]{Vector3.right*1e8f,4e6f,.02f,1f});
   Assert.AreEqual(baseline.x*1.5f,actual.x,10,"No second 1.5 multiplier in net force");
   Assert.AreEqual(4e6f,caps[0],1);
  }finally{Object.DestroyImmediate(go);}
 }
 [Test] public void AuxiliaryControlBudgetIncreasesWithoutRaisingMainBudget()
 {
  var go=new GameObject("Demand test");try{
   var m=go.AddComponent<MechFlightMotionController>();object[] values={0f,0f,0f,0f,0f};typeof(MechFlightMotionController).GetMethod("ResolveThrusterForces",P).Invoke(m,values);
   float main=(float)typeof(MechFlightMotionController).GetField("fallbackMainThrustKn",P).GetValue(m);
   float side=(float)typeof(MechFlightMotionController).GetField("fallbackLateralThrustKn",P).GetValue(m);
   Assert.AreEqual(main*1000,(float)values[0],1);Assert.AreEqual(side*1500,(float)values[1],1);
  }finally{Object.DestroyImmediate(go);}
 }
}
