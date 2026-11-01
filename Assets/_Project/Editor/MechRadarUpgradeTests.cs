using System;
using NUnit.Framework;
using Unity.Mathematics;
using ACunicombat.Mechs;
using ACunicombat.Sensors;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using ACunicombat.FireControl;
public class MechRadarUpgradeTests
{
 static TrackSnapshot Missile(double range=100000)
 {var t=new TrackSnapshot{TrackId=new TrackId(new Handle(2,1)),Position=new double3(0,0,range),Velocity=new double3(0,0,-1500),ObjectClass=TrackObjectClass.Missile,Classification=.9f,Confidence=.9f,HitCount=5,FirstObservationTime=8,LastUpdateTime=10,LastPredictTime=10,Lifecycle=TrackLifecycle.Confirmed,SourceMask=TrackSourceMask.Radar};for(int i=0;i<3;i++){t.SetCovariance(i,i,400);t.SetCovariance(i+3,i+3,100);}return t;}
 [TestCase(50000)] [TestCase(100000)] [TestCase(200000)]
 public void PreciseRepeatedRadarAlonePublishesWeaponsQuality(float range)
 {var t=Missile(range);var output=new MissileTrackSnapshot[2];int n=MissileTrackFusion.Fuse(MissileTrackFusionConfig.Default,new[]{t},1,Array.Empty<ThreatSnapshot>(),0,Array.Empty<ElectroOpticalObservation>(),0,double3.zero,double3.zero,10,output);Assert.AreEqual(1,n);Assert.IsTrue(output[0].IsWeaponsQuality);Assert.AreEqual(MissileTrackSourceMask.Radar,output[0].SourceMask);}
 [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)] [TestCase(5)] [TestCase(6)]
 public void WeakOldOrUnresolvedRadarCannotApprove(int fault)
 {var t=Missile();switch(fault){case 0:t.HitCount=1;break;case 1:t.LastUpdateTime=9;break;case 2:t.SetCovariance(0,0,1e8);break;case 3:t.SetCovariance(3,3,1e6);break;case 4:t.ObjectClass=TrackObjectClass.Unknown;break;case 5:t.AssociationClosed=true;break;case 6:t.FirstObservationTime=9.99;break;}Assert.IsFalse(MissileTrackFusion.IsIndependentRadarWeaponsQuality(t,double3.zero,10));}
 [Test] public void WiderSearchPaysPhysicalGainPenaltyAndHasFewerCells()
 {var h=new MechRadarPerformance().Physical;var narrow=h.BuildDwell(.008f,h.BeamWidthRadians);var wide=h.BuildDwell(.004f,h.BeamWidthRadians*MechRadarTaskPolicy.AcquisitionBeamMultiplier);Assert.That(wide.ConstantsDb-narrow.ConstantsDb,Is.EqualTo(-40*Math.Log10(2.5)).Within(.001));Assert.That(wide.IntegrationGainDb-narrow.IntegrationGainDb,Is.EqualTo(-3.0103).Within(.001));var a=new RasterScanPattern(100,60,math.degrees(h.BeamWidthRadians)*.66f);var b=new RasterScanPattern(100,60,math.degrees(h.BeamWidthRadians)*.66f*2.5f);Assert.That(b.CellCount,Is.LessThan(a.CellCount/5));}
 [Test] public void UrgentIncomingMissileHasPriorityButFriendlyAndOrdinaryTracksRemainScheduled()
 {var t=Missile();Assert.AreEqual(3,MechRadarTaskPolicy.RevisitWeight(t,double3.zero,double3.zero,10));t.Affiliation=TrackAffiliation.Friendly;Assert.AreEqual(1,MechRadarTaskPolicy.RevisitWeight(t,double3.zero,double3.zero,10));t.ObjectClass=TrackObjectClass.AirVehicle;Assert.AreEqual(1,MechRadarTaskPolicy.RevisitWeight(t,double3.zero,double3.zero,10));}
 [Test] public void SearchBudgetAndDeepSearchShareRemainBoundedOverAnHour()
 {double now=0,cost=0;ulong seq=0;int deep=0,near=0;while(now<3600){double dt=MechRadarTaskPolicy.BatchInterval(5,seq,.85,.35,.04);for(int i=0;i<5;i++){if(MechRadarTaskPolicy.DeepSearch(seq++)){deep++;cost+=.008;}else{near++;cost+=.004;}}now+=dt;}Assert.That(cost,Is.LessThanOrEqualTo(now*.85*.35+.001));Assert.That(near/(double)deep,Is.EqualTo(2).Within(.01));}

 [Test] public void LargeCarrierSpeedCannotHidePoorRelativeVelocityOrRadialPrecision()
 {var t=Missile();t.Velocity=new double3(10000,0,-100);t.SetCovariance(3,3,10000);Assert.IsFalse(MissileTrackFusion.IsIndependentRadarWeaponsQuality(t,double3.zero,10,new double3(10000,0,0)));t=Missile();t.SetCovariance(5,5,900);Assert.IsFalse(MissileTrackFusion.IsIndependentRadarWeaponsQuality(t,double3.zero,10));}
 [Test] public void InvalidTimeAndConfidenceNeverAuthorizeFire()
 {var t=Missile();t.Confidence=float.NaN;Assert.IsFalse(MissileTrackFusion.IsIndependentRadarWeaponsQuality(t,double3.zero,10));t=Missile();t.FirstObservationTime=double.NaN;Assert.IsFalse(MissileTrackFusion.IsIndependentRadarWeaponsQuality(t,double3.zero,10));}
}
