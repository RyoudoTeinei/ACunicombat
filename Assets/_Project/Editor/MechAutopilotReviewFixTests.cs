using NUnit.Framework;
using ACunicombat.Mechs;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using Unity.Mathematics;
public sealed class MechAutopilotReviewFixTests
{
 [Test] public void UnauthenticatedCloseHostileCannotBeDeletedAsOwnship()
 {
  var t=new TrackSnapshot{TrackId=new TrackId(new Handle(1,1)),ObjectClass=TrackObjectClass.AirVehicle,Affiliation=TrackAffiliation.Hostile,Position=new double3(20,0,0),Velocity=new double3(300,0,0),LastUpdateTime=10,LastPredictTime=10,Lifecycle=TrackLifecycle.Confirmed};
  for(int i=0;i<6;i++)t.SetCovariance(i,i,100);
  Assert.IsFalse(MechSharedTargetTracks.MatchesOwnNavigation(t,double3.zero,new double3(300,0,0),10));
  Assert.IsTrue(MechSharedTargetTracks.MatchesOwnNavigation(t,double3.zero,new double3(300,0,0),10,true));
  t.Position.x=100;Assert.IsFalse(MechSharedTargetTracks.MatchesOwnNavigation(t,double3.zero,new double3(300,0,0),10,true));
 }
 [TestCase(600,.1)][TestCase(1200,.05)]
 public void GunCadenceUsesActualWeaponRate(float rpm,double seconds)
 {Assert.That(MechFleetController.PlayerWeaponInterval(MechWeaponRole.AutomaticCannon,rpm),Is.EqualTo(seconds).Within(.00001));}
 [Test] public void ReadyFarTargetBeatsUnreadyNearTarget()
 {
  Assert.IsTrue(MechFleetController.BetterOpportunisticPair(true,1,true,false,100));
  Assert.IsFalse(MechFleetController.BetterOpportunisticPair(false,100,true,true,1));
  Assert.IsTrue(MechFleetController.BetterOpportunisticPair(true,2,true,true,1));
 }
}
