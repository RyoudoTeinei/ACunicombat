using System;
using System.Reflection;
using ACunicombat.Mechs;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using ACunicombat.Simulation.Driver;
using NUnit.Framework;
using UnityEngine;
public sealed class MechMissileDisplayTests
{
 [Test] public void OwnLaunchPresentationNeverInterpolatesBeforeBirth()
 {
  var track=new MissileTrackSnapshot{TrackId=new TrackId(new Handle(4096,1)),OwnLaunchIdentity=Guid.NewGuid(),FirstObservationTime=10,LastUpdateTime=10,Velocity=new Unity.Mathematics.double3(0,0,1000)};
  var presenter=new MechMissileMarkerPresentation();
  Assert.That(presenter.Sample(in track,9.98).Position.z,Is.EqualTo(0).Within(1e-5));
  Assert.That(presenter.Sample(in track,10.02).Position.z,Is.EqualTo(20).Within(1e-4));
 }
 const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
 static void Set(object obj,string field,object value)=>obj.GetType().GetField(field,Private).SetValue(obj,value);
 [TestCase(false,MissileTrackQuality.Warning,0.1,false,true)]
 [TestCase(true,MissileTrackQuality.Warning,0.1,false,true)]
 [TestCase(true,MissileTrackQuality.WeaponsQuality,0.1,false,true)]
 [TestCase(false,MissileTrackQuality.Warning,0.7,false,true)]
 [TestCase(true,MissileTrackQuality.WeaponsQuality,0.7,false,true)]
 [TestCase(false,MissileTrackQuality.Warning,1.6,false,false)]
 [TestCase(true,MissileTrackQuality.WeaponsQuality,1.6,false,false)]
 [TestCase(false,MissileTrackQuality.None,0.1,false,false)]
 [TestCase(false,MissileTrackQuality.Warning,-0.2,false,false)]
 [TestCase(false,MissileTrackQuality.None,0.7,true,true)]
 public void SameSensorSnapshotHasSameHudAndMapEligibility(bool confirmed,MissileTrackQuality quality,double age,bool friendly,bool expected)
 {
  var go=new GameObject("Missile presentation contract");go.SetActive(false);
  try {
   var driver=go.AddComponent<SimulationDriver>();Set(driver,"simulationTimeSource",(Func<double>)(()=>10));
   var radar=go.AddComponent<MechRadarController>();Set(radar,"driver",driver);
   var track=new MissileTrackSnapshot{TrackId=new TrackId(new Handle(1,1)),Confirmed=confirmed,Quality=quality,LastEvidenceTime=10-age,LastUpdateTime=10,Affiliation=friendly?TrackAffiliation.Friendly:TrackAffiliation.Unknown,IdentityConfidence=1};
   var cache=(MissileTrackSnapshot[])typeof(MechRadarController).GetField("missileTrackCache",Private).GetValue(radar);cache[0]=track;
   Set(radar,"missileTrackCacheCount",1);Set(radar,"missileTrackCacheTime",10d);Set(radar,"missileTrackCacheRevision",driver.ObservationRevision);
   var hud=go.AddComponent<MechRadarHudPresenter>();Set(hud,"radar",radar);
   bool hudShown=(bool)typeof(MechRadarHudPresenter).GetMethod(friendly?"ShouldDisplayFriendlyMissile":"ShouldDisplayWorldMissileMarker",Private).Invoke(hud,new object[]{track});
   var map=go.AddComponent<MechBattlefieldMap>();Set(map,"radar",radar);Set(map,"cooperative",go.AddComponent<MechCooperativeTelemetry>());
   typeof(MechBattlefieldMap).GetMethod("Gather",Private).Invoke(map,null);
   int missileCount=0;foreach(var c in map.Contacts)if(c.Missile)missileCount++;
   Assert.That(hudShown,Is.EqualTo(expected));Assert.That(missileCount,Is.EqualTo(expected?1:0));
   Assert.That(cache[0].Confirmed,Is.EqualTo(confirmed),"Presentation must not upgrade fire-control confirmation");
  } finally {UnityEngine.Object.DestroyImmediate(go);}
 }
 [Test] public void RetiredAndInvalidTracksAreNotDisplayed()
 {
  var t=new MissileTrackSnapshot{TrackId=new TrackId(new Handle(1,1)),Quality=MissileTrackQuality.Warning,LastEvidenceTime=10};
  Assert.That(MechMissileDisplayPolicy.ShouldDisplay(in t,10,true),Is.False);
  t.TrackId=default;Assert.That(MechMissileDisplayPolicy.ShouldDisplay(in t,10,false),Is.False);
 }
}
