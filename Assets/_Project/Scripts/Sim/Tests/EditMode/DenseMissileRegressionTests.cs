using ACunicombat.Tracking;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using Unity.Mathematics;
using Unity.Collections;
using NUnit.Framework;
namespace ACunicombat.Tests.EditMode
{
 public class DenseMissileRegressionTests
 {
  [Test] public void CoarseMissileDetectionNeedsIndependentRevisitBeforePublicIdentity()
  {
   var manager=new RadarTrackManager();using var measurements=new NativeList<Measurement>(2,Allocator.Temp);
   var coarse=Return(.04,new double3(1200,0,50000),new double3(0,0,300));coarse.Flags=MeasurementFlags.None;
   coarse.AngleVar=.0008f;coarse.DwellSequence=1;
   measurements.Add(coarse);var correlated=coarse;correlated.SensorId=new SensorId(new Handle(1,1));correlated.DwellSequence=2;measurements.Add(correlated);
   manager.Process(measurements,double3.zero,.04);Assert.AreEqual(0,manager.Count,"Two correlated looks must remain a detection candidate, not two public tracks.");
   measurements.Clear();var precise=Return(.08,new double3(0,0,50012),new double3(0,0,300));precise.Flags=MeasurementFlags.None;precise.DwellSequence=3;
   measurements.Add(precise);manager.Process(measurements,double3.zero,.08);
   Assert.AreEqual(1,manager.Count);Assert.IsTrue(manager.TryGetBestTrack(out var track));
   Assert.Less(math.distance(track.Position,new double3(0,0,50012)),1);
  }

  [Test] public void DiffuseRevisitCannotReversePreciseDopplerHistory()
  {
   var manager=new RadarTrackManager();using var measurements=new NativeList<Measurement>(1,Allocator.Temp);
   for(int i=1;i<=8;i++)
   {
    double t=i*.04;var m=Return(t,new double3(0,0,4547+2000*(.32-t)),new double3(0,0,-2000));
    m.Flags=MeasurementFlags.None;m.AngleVar=.0007915427f;m.RangeVar=.56f;m.VelocityVar=.035f;
    measurements.Clear();measurements.Add(m);manager.Process(measurements,double3.zero,t);
   }
   manager.TryGetBestTrack(out var original);measurements.Clear();manager.Process(measurements,double3.zero,1.20);
   var outgoing=Return(1.24,new double3(0,0,2300),new double3(0,0,800));
   outgoing.Flags=MeasurementFlags.None;outgoing.AngleVar=.121847f;outgoing.VelocityVar=.035f;outgoing.DwellSequence=50;
   measurements.Add(outgoing);manager.Process(measurements,double3.zero,1.24);
   Assert.IsTrue(manager.TryGetTrack(original.TrackId,out var retained));
   Assert.AreEqual(original.HitCount,retained.HitCount,"A weak bearing must not erase the independent inbound Doppler history.");
   Assert.Less(retained.Velocity.z,-1500);
  }

  [Test] public void GenuineFlybyCanChangeRadialSignWithoutChangingTrack()
  {
   var manager=new RadarTrackManager();using var measurements=new NativeList<Measurement>(1,Allocator.Temp);
   TrackId original=default;
   for(int i=1;i<=40;i++)
   {
    double t=i*.04;measurements.Clear();measurements.Add(Return(t,new double3(600,0,1200-1600*t),new double3(0,0,-1600)));
    manager.Process(measurements,double3.zero,t);
    if(i==8){manager.TryGetBestTrack(out var prior);original=prior.TrackId;}
   }
   Assert.IsTrue(manager.TryGetTrack(original,out var track));Assert.AreEqual(40,track.HitCount);
   Assert.Less(track.Velocity.z,-1500);Assert.AreEqual(1,manager.Count);
  }

  [Test] public void FirstResolvedAircraftReturnDoesNotLookLikeARejectedZeroDistanceMatch()
  {
   var manager=new RadarTrackManager();using var measurements=new NativeList<Measurement>(1,Allocator.Temp);
   var m=Return(.04,new double3(0,0,2000),double3.zero);m.ObjectClass=TrackObjectClass.AirVehicle;m.DwellSequence=1;
   measurements.Add(m);manager.Process(measurements,double3.zero,.04);
   Assert.That(manager.Count,Is.EqualTo(1),"An empty association set has infinite residual, not a fabricated zero-distance neighbour.");
  }

  [TestCase(false)] [TestCase(true)]
  public void MixedDwellSidelobeCannotPreemptAnotherDwellsPreciseReturn(bool reverse)
  {
   var manager=new RadarTrackManager();using var measurements=new NativeList<Measurement>(4,Allocator.Temp);
   for(int i=1;i<=8;i++)
   {
    measurements.Clear();measurements.Add(Return(i*.04,new double3(0,0,2000),double3.zero));measurements.Add(Return(i*.04,new double3(300,0,2000),double3.zero));
    manager.Process(measurements,double3.zero,i*.04);
   }
   var tracks=new TrackSnapshot[8];int count=manager.CopyTracks(tracks);TrackId target=default;
   for(int i=0;i<count;i++)if(math.abs(tracks[i].Position.x)<10)target=tracks[i].TrackId;
   Assert.IsTrue(target.IsValid);
   var strongOther=Return(.36,new double3(300,0,2000),double3.zero);strongOther.DwellSequence=10;strongOther.RangeVar=.001f;strongOther.AngleVar=1e-12f;
   var weak=Return(.36,new double3(100,0,2000),double3.zero);weak.DwellSequence=10;weak.AngleVar=.002f;weak.Flags=MeasurementFlags.None;
   var strongTarget=Return(.36,new double3(0,0,2000),double3.zero);strongTarget.DwellSequence=11;strongTarget.RangeVar=.01f;strongTarget.AngleVar=1e-10f;
   measurements.Clear();var batch=new[]{strongOther,weak,strongTarget};if(reverse)System.Array.Reverse(batch);foreach(var m in batch)measurements.Add(m);
   manager.Process(measurements,double3.zero,.36);
   Assert.IsTrue(manager.TryGetTrack(target,out var result));
   Assert.That(result.GetCovariance(0,0)+result.GetCovariance(1,1)+result.GetCovariance(2,2),Is.LessThan(.1),"The independent precise return must be committed before a correlated sidelobe.");
   Assert.That(math.distance(result.Position,new double3(0,0,2000)),Is.LessThan(.2));
  }

  [Test] public void LargeBearingCorrectionDoesNotManufactureCentimetrePrecisionAtTheWrongPoint()
  {
   var prior=new FilterState{Position=new double3(593.5347145579728,14637.903538655744,30798.59421904012),Velocity=new double3(40.45410459575376,-466.3901372419366,-3491.249303027422),Time=71.79997456065759};
   double[] upper={155800.62343753516,-73.10808070903373,-653.5724885240326,219048.6875722167,-91.12864041204577,-700.6902027315634,153932.40962758538,-12284.637930374327,-87.95999662643278,217205.393321501,-12059.509265855635,77137.44215879707,-681.6078475133112,-12084.109126951886,141518.24529387604,440536.87659801054,-128.38957304632657,-900.147598981868,438321.90254967124,-14447.200856026797,347539.9451143312};
   int at=0;for(int i=0;i<6;i++)for(int j=i;j<6;j++)prior.P[i,j]=prior.P[j,i]=upper[at++];
   Cmkf.PredictCv(ref prior,.12,900,90000);
   var observer=new double3(570.0926513671875,14488.59375,29711.908203125);var direction=new float3(-.93255013227462769f,.19273866713047028f,-.3052901029586792f);
   Cmkf.ConvertMeasurement(observer,175.37013244628906f,direction,.0017639999f,1.31921275e-13f,out var observed,out _);
   Assert.IsTrue(Cmkf.TryUpdateRadarPosition(ref prior,observer,175.37013244628906f,direction,.0017639999f,1.31921275e-13f));
   Assert.Less(math.distance(prior.Position,observed),1);Assert.IsTrue(Cmkf.Healthy(prior));
  }
  static Measurement Return(double time,double3 p,double3 v)
  {
   var direction=math.normalize(p);return new Measurement{SensorId=new SensorId(new Handle(0,1)),Timestamp=time,Range=(float)math.length(p),Direction=(float3)direction,RadialVelocity=(float)math.dot(v,direction),LosRate=(float3)(math.cross(p,v)/math.lengthsq(p)),RangeVar=1,AngleVar=1e-8f,VelocityVar=1,LosRateVar=1e-8f,SnrDb=40,ObjectClass=TrackObjectClass.Missile,ClassificationConfidence=1,Flags=MeasurementFlags.CloseProtectionReceiver};
  }
  [Test] public void CrossingRoundCannotTakeAnotherRoundsTrackWhenLosRateSeparatesThem()
  {
   var manager=new RadarTrackManager();using var measurements=new NativeList<Measurement>(2,Allocator.Temp);
   for(int i=1;i<=8;i++){double t=i*.04;measurements.Clear();measurements.Add(Return(t,new double3(-300+1000*t,0,2000),new double3(1000,0,0)));manager.Process(measurements,double3.zero,t);}
   Assert.IsTrue(manager.TryGetBestTrack(out var original));
   measurements.Clear();measurements.Add(Return(.36,new double3(40,0,2000),new double3(-1000,0,0)));manager.Process(measurements,double3.zero,.36);
   Assert.IsTrue(manager.TryGetTrack(original.TrackId,out var retained));Assert.AreEqual(original.HitCount,retained.HitCount);Assert.Greater(retained.Velocity.x,500);
  }
  [Test] public void TwoResolvedReturnsInOneDwellCannotConsumeTheSameTrack()
  {
   var manager=new RadarTrackManager();using var measurements=new NativeList<Measurement>(2,Allocator.Temp);
   var a=Return(.04,new double3(0,0,2000),new double3(0,0,-100));a.DwellSequence=17;a.RangeVar=100;
   var b=a;b.Range+=5;measurements.Add(a);measurements.Add(b);manager.Process(measurements,double3.zero,.04);
   Assert.AreEqual(2,manager.Count,"Sensor-resolved returns need one-to-one association, even inside the same uncertainty gate");
  }
  [Test] public void NoisySidelobeBearingCannotContradictAnIndependentPreciseDopplerReturn()
  {
   var manager=new RadarTrackManager();using var measurements=new NativeList<Measurement>(2,Allocator.Temp);
   var position=new double3(-79.3944116,-114.5870777,939.6724047);
   var velocity=new double3(-196.51929093,-44.82479267,823.80168779);
   for(int i=1;i<=10;i++)
   {
    double t=i*.04;measurements.Clear();var precise=Return(t,position+velocity*(t-.4),velocity);
    precise.RangeVar=.001764f;precise.AngleVar=5.676754e-11f;precise.VelocityVar=.0001500625f;precise.LosRateVar=5.676754e-7f;
    measurements.Add(precise);
    if(i==10)
    {
     var coarse=precise;coarse.SensorId=new SensorId(new Handle(1,1));coarse.Flags=MeasurementFlags.None;
     coarse.Range=950.67578125f;coarse.Direction=new float3(-.1721882373f,-.0972317234f,.980253756f);
     coarse.RadialVelocity=836.5395508f;coarse.RangeVar=.56172198f;coarse.AngleVar=.0007915427f;coarse.VelocityVar=.03510762f;
     measurements.Add(coarse);
    }
    manager.Process(measurements,double3.zero,t);
   }
   Assert.AreEqual(1,manager.Count,"The same missile's independent Doppler remains consistent despite a noisy sidelobe bearing");
  }
  [TestCase(true)] [TestCase(false)]
  public void MissingReturnsNeedActualEnvelopeCoverageBeforeClosingMissileAssociation(bool covered)
  {
   var manager=new RadarTrackManager();using var returns=new NativeList<Measurement>(2,Allocator.Temp);
   using var dwells=new NativeList<EmissionEvent>(1,Allocator.Temp);
   for(int i=1;i<=5;i++){returns.Clear();returns.Add(Return(i*.04,new double3(0,0,2000),double3.zero));manager.Process(returns,double3.zero,i*.04);}
   manager.TryGetBestTrack(out var original);returns.Clear();
   for(int i=6;i<=32;i++)
   {
    dwells.Clear();if(covered)dwells.Add(new EmissionEvent{EmissionType=EmissionType.RadarDwell,Direction=new float3(0,0,1),BeamWidth=2,MaxRangeMeters=5000});
    manager.Process(returns,dwells,double3.zero,double3.zero,i*.04);
   }
   Assert.AreEqual(covered?0:1,manager.Count,"No scheduled revisit is not proof that a missile disappeared");
   var revisited=Return(1.32,new double3(0,0,2000),double3.zero);revisited.Flags=MeasurementFlags.None;returns.Add(revisited);manager.Process(returns,double3.zero,1.32);
   manager.TryGetBestTrack(out var reacquired);
   Assert.AreEqual(!covered,reacquired.TrackId.Equals(original.TrackId));
  }
  [Test] public void AccelerationCovarianceDoesNotDependOnTickSubdivision()
  {
   var whole=new FilterState();var divided=new FilterState();
   for(int i=0;i<6;i++)whole.P[i,i]=divided.P[i,i]=1;
   Cmkf.PredictCv(ref whole,.2,4,90000,true);
   for(int i=0;i<10;i++)Cmkf.PredictCv(ref divided,.02,4,90000,true);
   for(int r=0;r<6;r++)for(int c=0;c<6;c++)Assert.That(divided.P[r,c],Is.EqualTo(whole.P[r,c]).Within(1e-6));
  }
  [Test] public void NearbyNewLaunchCannotConsumeAnExistingPreciseRangeTrack()
  {
   var manager=new RadarTrackManager();using var measurements=new NativeList<Measurement>(2,Allocator.Temp);
   var first=Return(.04,new double3(0,0,2000),new double3(0,0,1000));first.Flags=MeasurementFlags.None;
   first.RangeVar=.56f;first.AngleVar=.00079154f;first.VelocityVar=.035f;
   // Establish the coarse track with an independent earlier detection before
   // testing whether another newly launched round can steal its identity.
   var earlier=first;earlier.Timestamp=0;earlier.Range=1960;measurements.Add(earlier);manager.Process(measurements,double3.zero,0);measurements.Clear();
   measurements.Add(first);manager.Process(measurements,double3.zero,.04);manager.TryGetBestTrack(out var original);
   measurements.Clear();
   var fresh=Return(.08,new double3(0,0,2025),new double3(0,0,950));fresh.SensorId=new SensorId(new Handle(1,1));fresh.DwellSequence=2;fresh.RangeVar=.001764f;fresh.AngleVar=1e-10f;
   var continuing=Return(.08,new double3(0,0,2040),new double3(0,0,1000));continuing.SensorId=fresh.SensorId;continuing.DwellSequence=2;continuing.RangeVar=fresh.RangeVar;continuing.AngleVar=fresh.AngleVar;
   measurements.Add(fresh);measurements.Add(continuing);manager.Process(measurements,double3.zero,.08);
   Assert.That(manager.TryGetTrack(original.TrackId,out var retained),Is.True);
   Assert.That(retained.Position.z,Is.GreaterThan(2038));Assert.That(manager.Count,Is.EqualTo(2));
  }
  [Test] public void LocalReacquisitionAfterLostContinuityDoesNotInheritOldMissileIdentity()
  {
   var manager=new RadarTrackManager();using var measurements=new NativeList<Measurement>(1,Allocator.Temp);
   for(int i=1;i<=5;i++){measurements.Clear();measurements.Add(Return(i*.04,new double3(0,0,2000),double3.zero));manager.Process(measurements,double3.zero,i*.04);}
   manager.TryGetBestTrack(out var old);measurements.Clear();manager.Process(measurements,double3.zero,1.5);
   measurements.Add(Return(1.54,new double3(0,0,2000),double3.zero));manager.Process(measurements,double3.zero,1.54);
   manager.TryGetBestTrack(out var fresh);Assert.AreNotEqual(old.TrackId,fresh.TrackId);Assert.AreEqual(1,manager.Count);
  }
  [Test] public void RejectedDopplerCannotPartiallyCommitPositionAndRefreshAMissile()
  {
   // Recorded crossing at 32.04 s: Doppler NIS after position is ~31, above the unchanged 25 gate.
   var state=new FilterState{Position=new double3(-3.0248424145543447,5306.4039052465969,5797.5540374446364),Velocity=new double3(-39.180454046811292,356.06604652493053,-2072.2366565100838),Time=31.959988676303855};
   double[] upper={49255.838047032245,1849.0372355689956,3581.1197473112957,93879.169106139729,2048.6737073389495,5114.3898385845423,47732.078835748893,-4457.4030375423645,2089.7694748003478,92239.321295785427,-6835.9460890273895,20819.636419726772,4163.8367720959286,-5319.6048687367447,63936.029749270332,347757.90355308773,2317.4832406906789,5885.0276154532539,345988.14834456105,-7900.3360499138134,314013.86248886172};int at=0;for(int i=0;i<6;i++)for(int j=i;j<6;j++)state.P[i,j]=state.P[j,i]=upper[at++];
   Cmkf.PredictCv(ref state,.08,4,90000,true);for(int i=3;i<6;i++)state.P[i,i]+=20736;
   var before=state;
   Assert.IsFalse(Cmkf.TryUpdateRadarMeasurement(ref state,new double3(-25.128192901611328,5447.9599609375,5041.6640625),new double3(-2.7750306129455566,235.62043762207031,397.43917846679688),1063.4329833984375f,new float3(-.14253056049f,-.08298350126f,.98630553484f),.56172198f,.0007915427f,622.880249f,.03510762f));
   Assert.AreEqual(before.Position,state.Position);Assert.AreEqual(before.Velocity,state.Velocity);
   for(int i=0;i<6;i++)for(int j=0;j<6;j++)Assert.AreEqual(before.P[i,j],state.P[i,j]);
  }
  [Test] public void SideOnReacquisitionCannotReverseMissileVelocityBeyondReachableAcceleration()
  {
   var manager=new RadarTrackManager();using var measurements=new NativeList<Measurement>(1,Allocator.Temp);
   for(int i=1;i<=5;i++)
   {
    double time=i*.04;measurements.Clear();measurements.Add(Return(time,new double3(1000,0,1176-1400*(time-.2)),new double3(0,0,-1400)));
    manager.Process(measurements,double3.zero,time);
   }
   manager.TryGetBestTrack(out var original);measurements.Clear();manager.Process(measurements,double3.zero,1.0);
   measurements.Add(Return(1.04,new double3(1000,0,10),new double3(0,0,650)));manager.Process(measurements,double3.zero,1.04);
   Assert.IsTrue(manager.TryGetTrack(original.TrackId,out var retained));Assert.AreEqual(original.HitCount,retained.HitCount);
   Assert.That(retained.Velocity.z,Is.LessThan(-1000));Assert.AreEqual(2,manager.Count);
  }
  [Test] public void UnobservedMissileTransverseVelocityCoversFastRoundsWithoutDilutingDoppler()
  {
   var manager=new RadarTrackManager();using var measurements=new NativeList<Measurement>(1,Allocator.Temp);
   var observation=Return(.04,new double3(0,0,50000),new double3(2500,0,0));
   observation.Flags=MeasurementFlags.None;observation.LosRate=new float3(float.NaN);observation.LosRateVar=0;
   measurements.Add(observation);manager.Process(measurements,double3.zero,.04);manager.TryGetBestTrack(out var track);
   Assert.That(3*math.sqrt(track.GetCovariance(3,3)),Is.GreaterThanOrEqualTo(2500),"No transverse measurement has been made yet");
   Assert.That(math.sqrt(track.GetCovariance(5,5)),Is.LessThan(2),"Measured radial speed remains precise");
  }
  [Test] public void DiffuseGhostDoesNotOutrankAConsistentPreciseObservation()
  {
   var ghost=new FilterState();var precise=new FilterState{Position=new double3(10,0,0)};Matrix6 noise=default;
   for(int axis=0;axis<3;axis++){ghost.P[axis,axis]=1000000;precise.P[axis,axis]=100;noise[axis,axis]=1;}
   Assert.IsTrue(Cmkf.TryCalculatePositionInnovationNis(ghost,double3.zero,noise,out var ghostNis));Assert.IsTrue(Cmkf.TryCalculatePositionInnovationNis(precise,double3.zero,noise,out var preciseNis));
   Assert.Greater(preciseNis,ghostNis);Assert.Less(Cmkf.PositionAssociationCost(precise,noise,preciseNis),Cmkf.PositionAssociationCost(ghost,noise,ghostNis));
  }
 }
}
