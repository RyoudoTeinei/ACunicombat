using System;
using NUnit.Framework;
using ACunicombat.Mechs;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
public sealed class MechFleetMissileAITests
{
 [TestCase("DPL-17")][TestCase("LRA-4230")][TestCase("MTR-4340")][TestCase("MTR-4340-ER")]
 public void SuppressionFamiliesExposePassiveCapabilityAndLongRange(string id)
 {
  var weapon=MechMissileAmmunitionCatalog.Create(id);var shortRange=MechMissileAmmunitionCatalog.Create("AAM-4290");
  Assert.NotNull(weapon);Assert.IsTrue(weapon.supportsPassiveRfHoming);Assert.AreEqual(MechWeaponRole.AirToAirMissile,weapon.role);
  Assert.Greater(weapon.effectiveRangeMeters,shortRange.effectiveRangeMeters);
  Assert.GreaterOrEqual(weapon.missileFlightProfile.MaximumRangeMeters,weapon.effectiveRangeMeters);
 }
 [Test] public void SuppressionPreferenceDoesNotDisableNormalFocusedLaunches()
 {
  Assert.AreEqual(MechTargetTrackingMode.Focused,MechMissileLaunchSource.Resolve(default,true,true,true,true,0));
  Assert.AreEqual(MechTargetTrackingMode.Passive,MechMissileLaunchSource.Resolve(default,true,true,true,true,0,true));
  Assert.IsFalse(MechMissileLaunchSource.CanUse(MechMissileLaunchSource.Resolve(default,true,true,false,true,0,true),false));
 }
 [Test] public void ConfirmedModeRequiresMissionAuthorizationAndReturnRevokesIt()
 {
  var leader=Guid.NewGuid();var wing=Guid.NewGuid();var fleet=Guid.NewGuid();
  var state=new MechFleetState(wing,fleet,leader,false,false,FleetAttackMode.Confirmed);
  var mission=new FleetPacket{Kind=FleetPacketKind.Order,Order=FleetOrderKind.AssignMission,Sender=leader,Recipient=wing,Fleet=fleet,Operation=Guid.NewGuid(),Revision=1,ObservedAt=1,ExpiresAt=4,TaskDeadline=30,TaskGroup=FleetTaskGroup.AirDefenseSuppression,MissionGeometry=FleetMissionGeometry.Target,Target=new TrackSnapshot{TrackId=new TrackId(new Handle(1,1))}};
  Assert.IsTrue(state.Receive(mission,1));Assert.IsFalse(state.AllowsOffensiveMissile(Guid.NewGuid()));
  mission.Operation=Guid.NewGuid();mission.Revision=2;mission.AuthorizedWeaponSelection=true;
  Assert.IsTrue(state.Receive(mission,2));Assert.IsTrue(state.AllowsOffensiveMissile(Guid.NewGuid()));
  state.SetPhase(FleetTaskPhase.Returning,FleetTaskReason.Cancelled,3);
  Assert.IsFalse(state.AllowsOffensiveMissile(Guid.NewGuid()));
 }
 [Test] public void UnrelatedCommanderCannotAuthorizeSuppression()
 {
  var leader=Guid.NewGuid();var wing=Guid.NewGuid();var state=new MechFleetState(wing,Guid.NewGuid(),leader,false,false,FleetAttackMode.Confirmed);
  var mission=new FleetPacket{Kind=FleetPacketKind.Order,Order=FleetOrderKind.AssignMission,Sender=Guid.NewGuid(),Recipient=wing,Operation=Guid.NewGuid(),Revision=1,ObservedAt=1,ExpiresAt=4,TaskGroup=FleetTaskGroup.AirDefenseSuppression,AuthorizedWeaponSelection=true};
  Assert.IsFalse(state.Receive(mission,1));Assert.IsFalse(state.AllowsOffensiveMissile(Guid.NewGuid()));
 }
}
