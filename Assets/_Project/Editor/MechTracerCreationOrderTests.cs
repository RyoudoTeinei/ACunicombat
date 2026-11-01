using System;
using System.IO;
using System.Reflection;
using ACunicombat.Mechs;
using NUnit.Framework;
using UnityEngine;
public class MechTracerCreationOrderTests
{
 [TestCase(800f)] [TestCase(2000f)] [TestCase(10000f)]
 public void ProductionOrderBindsLateProjectileAndMatchesCurrentRenderTime(float climbSpeed)
 {
  var go=new GameObject("Real creation order");try{
   go.transform.position=new Vector3(3,5000,7);var tracer=go.AddComponent<MechTracerRound>();tracer.Build(Color.white,12.7f,1800f);
   var p=go.AddComponent<MechTestProjectile>();var flags=BindingFlags.NonPublic|BindingFlags.Instance;
   typeof(MechTestProjectile).GetField("velocity",flags).SetValue(p,new Vector3(0,climbSpeed,1800));typeof(MechTestProjectile).GetField("physicsStateTime",flags).SetValue(p,Time.timeAsDouble-.01);
   var before=go.transform.position;typeof(MechTracerRound).GetMethod("LateUpdate",flags).Invoke(tracer,null);
   Assert.AreSame(p,typeof(MechTracerRound).GetField("projectile",flags).GetValue(tracer));
   Assert.Less(Vector3.Distance(p.PositionAtPhysicsTime(Time.timeAsDouble),go.transform.Find("TracerPresentation").position),.002f);
   Assert.AreEqual(before,go.transform.position);
  }finally{UnityEngine.Object.DestroyImmediate(go);}
 }
}
