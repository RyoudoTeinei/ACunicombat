using System.Reflection;
using ACunicombat.Mechs;
using NUnit.Framework;
using UnityEngine;
public class MechMissileRenderEpochTests
{
 [TestCase(30,800)] [TestCase(60,800)] [TestCase(120,800)]
 [TestCase(30,2000)] [TestCase(60,2000)] [TestCase(120,10000)]
 public void MovingCarrierAndMissileUseTheSameRenderEpoch(int hz,float speed)
 {
  var body=new GameObject("physics");var visual=new GameObject("visual");
  float old=Time.fixedDeltaTime;Time.fixedDeltaTime=1f/hz;
  try{
   var p=body.AddComponent<MechTestProjectile>();var v=new Vector3(speed*.2f,speed,40);body.transform.position=new Vector3(5,5000,10);
   const BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Instance;
   typeof(MechTestProjectile).GetField("velocity",flags).SetValue(p,v);
   typeof(MechTestProjectile).GetField("physicsStateTime",flags).SetValue(p,10d);
   var presenter=visual.AddComponent<MechMissileVisualInterpolator>();presenter.Configure(body.transform);
   presenter.CaptureSimulationPose(body.transform.position-v/hz,Quaternion.identity);
   presenter.CaptureSimulationPose(body.transform.position,Quaternion.identity);
   var original=body.transform.position;
   foreach(double fraction in new[]{0d,.25d,.5d,.99d}){
    double now=10+fraction/hz;typeof(MechMissileVisualInterpolator).GetMethod("PresentAt",flags).Invoke(presenter,new object[]{now});
    var expected=original+v*(float)(fraction/hz);
    Assert.Less(Vector3.Distance(expected,visual.transform.position),.002f,"Must not lag one physics frame behind a moving carrier");
    Assert.AreEqual(original,body.transform.position,"Presentation must not move the simulated projectile");
   }
  }finally{Time.fixedDeltaTime=old;Object.DestroyImmediate(visual);Object.DestroyImmediate(body);}
 }
}
