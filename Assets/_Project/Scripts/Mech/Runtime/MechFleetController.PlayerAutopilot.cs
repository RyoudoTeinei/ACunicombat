using System;
using System.Collections.Generic;
using ACunicombat.Sensing.Contracts;
using UnityEngine;
namespace ACunicombat.Mechs
{
 public sealed partial class MechFleetController
 {
  public bool PlayerAutopilotActive=>IsPlayer&&(State!=null&&State.CruiseCommandActive||HasTask&&(State.Task.Order.Order==FleetOrderKind.Move||State.Task.Order.Order==FleetOrderKind.Attack||State.Task.Order.Order==FleetOrderKind.AttackWithWeapon||State.Task.Order.Order==FleetOrderKind.AssignMission));
  private Vector3 playerNavigationGoal,playerAimDirection;
  private bool playerNavigationValid;
  private double playerLaunchStandOffUntil;
  private readonly Dictionary<Guid,double> playerAutoShotUntil=new();
  private float playerLaunchStandOff;private TrackId playerLaunchStandOffTrack;
  private bool previousPlayerRadarControl;
  private readonly HashSet<string> playerAutoFireSlots=new();
  private readonly List<MechWeaponSlotStatus> playerWeaponStatuses=new();
  private readonly List<MechEquipmentWeaponSnapshot> playerWeapons=new();
  public void CancelPlayerAutopilot()
  {
   if(!PlayerAutopilotActive)return;
   State.CancelCruise();if(!HasTask){playerNavigationValid=false;motion?.StopLocalAttitudeInput();CancelQueuedPlayerAutoFire();return;}
   var operation=State.Task.Order.Operation;
   State.SetPhase(FleetTaskPhase.Cancelled,FleetTaskReason.Cancelled,radar.SimTimeSeconds);
   playerNavigationValid=false;motion?.StopLocalAttitudeInput();
   CancelQueuedPlayerAutoFire();
   foreach(var pair in delegation)if(pair.Value==operation)CancelOrder(pair.Key);
   Log("player_autopilot_cancelled",State.Task.Order,"ManualTakeover");
  }
  public static bool ManualFlightTakeover(in MechPlayerInputFrame frame)=>frame.MoveLocal.sqrMagnitude>.0001f||frame.RiseHeld||frame.DescendHeld||frame.QuickBoostEdge||frame.AssaultBoostEdge||frame.BrakeHeld||frame.ReferencePlaneResetEdge||Mathf.Abs(frame.ReferencePlaneRollDeltaDegrees)>.001f;
  public void PreparePlayerAutopilot(ref MechPlayerInputFrame frame)
  {
   if(!PlayerAutopilotActive)return;
   var keyboard=UnityEngine.InputSystem.Keyboard.current;
   bool rawMovement=!MechSolarSystemMap.IsAnyMapVisible&&!MechAssemblyMenu.IsAnyMenuVisible&&!MechSystemsRadialMenu.IsAnyMenuVisible&&keyboard!=null&&(keyboard.wKey.isPressed||keyboard.aKey.isPressed||keyboard.sKey.isPressed||keyboard.dKey.isPressed);
   if(rawMovement||ManualFlightTakeover(in frame)||!motion.EngineAvailable){CancelPlayerAutopilot();return;}
   bool tactical=radar.TrackOperationMode;
   if(tactical&&!previousPlayerRadarControl)CancelQueuedPlayerAutoFire();
   previousPlayerRadarControl=tactical;
   frame.FreeLookHeld=!tactical;
   frame.PrimaryFireHeld=frame.PrimaryFireEdge=frame.SecondaryFireHeld=frame.SecondaryFireEdge=false;
   frame.PrimaryReloadEdge=frame.SecondaryReloadEdge=false;
   if(!tactical||!IsShoulderMissile(MechPartSlot.LeftBackWeapon))frame.LeftShoulderFireHeld=frame.LeftShoulderFireEdge=false;
   if(!tactical||!IsShoulderMissile(MechPartSlot.RightBackWeapon))frame.RightShoulderFireHeld=frame.RightShoulderFireEdge=false;
   playerNavigationValid=false;
   if(!HasTask){frame.HasAutonomousFlightCommand=true;frame.AutonomousAccelerationWorld=Vector3.ClampMagnitude((motion.LogicalRotation*Vector3.forward*State.CruiseSpeed-motion.Velocity)/2,30);return;}
   bool navigating=State.Task.Order.Order==FleetOrderKind.Move;
   if(navigating)
   {
    if(TryNavigation(out playerAimDirection,out var navigation))
    {frame.HasAutonomousFlightCommand=navigation.HasAutonomousFlightCommand;frame.AutonomousAccelerationWorld=navigation.AutonomousAccelerationWorld;playerNavigationGoal=MoveDestination();playerNavigationValid=true;}
    if(tactical||State.Mode!=FleetAttackMode.Free)return;
   }
   TrackSnapshot track;
   if(!(navigating?TryGetOpportunisticTrack(out track):TryGetOrderedTrack(out track)))
   {
    if(navigating)return;
    if(HasSpatialMission&&TryNavigation(out playerAimDirection,out var missionFrame))
    {frame.HasAutonomousFlightCommand=true;frame.AutonomousAccelerationWorld=missionFrame.AutonomousAccelerationWorld;playerNavigationValid=TryMissionNavigation(out playerNavigationGoal,out _);if(TryMissionLook(out var look))playerAimDirection=look;}
    else {frame.HasAutonomousFlightCommand=true;frame.AutonomousAccelerationWorld=Vector3.ClampMagnitude(-motion.Velocity/2,30);}return;
   }
   var target=radar.PredictTrackPosition(track);var delta=target-transform.position;float range=delta.magnitude;
   if(range<1)return;
   playerAimDirection=delta/range;
   equipment.Clear();weapons.CopyEquipmentWeaponSnapshots(equipment);playerWeapons.Clear();playerWeaponStatuses.Clear();
   foreach(var e in equipment)
   {
    if(!e.Enabled||!State.AllowsWeapon(e.InstanceId)||!weapons.TryGetWeaponSlotStatus(new MechEquipmentSlot(e.SlotId),out var status)||!MechAiRulePlanner.IsOffensiveRole(status.Role)||!status.HasReserveAmmunition)continue;
    if(!AllowsMissionEquipment(e.SlotId,status.Role))continue;
    if(status.Role==MechWeaponRole.AirToAirMissile&&!State.AllowsOffensiveMissile(e.InstanceId))continue;
    playerWeapons.Add(e);playerWeaponStatuses.Add(status);
   }
   if(playerWeapons.Count==0){if(!navigating)State.SetPhase(FleetTaskPhase.Rejected,FleetTaskReason.WeaponUnavailable,radar.SimTimeSeconds);return;}
   if(!navigating)
   {
   var doctrine=MechAiRulePlanner.ResolveCombatDoctrine(playerWeaponStatuses.ToArray(),playerWeaponStatuses.Count,range,default,100,1000);
   bool launchReady=false;for(int i=0;i<playerWeapons.Count;i++)if(playerWeaponStatuses[i].Role==MechWeaponRole.AirToAirMissile&&weapons.CanReleaseMissileAt(playerWeapons[i].SlotId,track.TrackId,out _,out _)){launchReady=true;break;}
   if(launchReady){playerLaunchStandOff=range;playerLaunchStandOffTrack=track.TrackId;playerLaunchStandOffUntil=radar.SimTimeSeconds+8;}
   float preferred=playerLaunchStandOffTrack.Equals(track.TrackId)&&radar.SimTimeSeconds<playerLaunchStandOffUntil?playerLaunchStandOff:Mathf.Max(100,doctrine.EngagementBand.PreferredRangeMeters);
   playerNavigationGoal=target-playerAimDirection*preferred;playerNavigationValid=true;
   var targetVelocity=(Vector3)(Unity.Mathematics.float3)track.Velocity;
   var desired=targetVelocity+Vector3.ClampMagnitude((playerNavigationGoal-transform.position)*.15f,State.CruiseSpeed);
   frame.HasAutonomousFlightCommand=true;frame.AutonomousAccelerationWorld=Vector3.ClampMagnitude((desired-motion.Velocity)/2,30);
   if(TryAttackNavigation(in track,out var plannedFrame,out var plannedPoint)){frame.AutonomousAccelerationWorld=plannedFrame.AutonomousAccelerationWorld;playerNavigationGoal=plannedPoint;}
   }
   // Tab temporarily reserves fire-control selection and firing for the player.
   if(tactical)return;
   float angle=Vector3.Angle(motion.LogicalRotation*Vector3.forward,playerAimDirection);
   if(angle>55)return;
   if(radar.RadarPowerEnabled&&TaskGroup!=FleetTaskGroup.AirDefenseSuppression)radar.TryAssignAutopilotFireControlTrack(track.TrackId);

   for(int i=0;i<playerWeapons.Count;i++)
   {
    var weapon=playerWeapons[i];if(playerAutoShotUntil.TryGetValue(weapon.InstanceId,out var until)&&radar.SimTimeSeconds<until)continue;
    var status=playerWeaponStatuses[i];if(!status.CanAttemptFire||range<status.MinimumRangeMeters||range>status.MaximumRangeMeters||playerWeapons[i].Pending)continue;
    if(status.Role==MechWeaponRole.AirToAirMissile&&!weapons.CanReleaseMissileAt(playerWeapons[i].SlotId,track.TrackId,out _,out _))continue;
    if(status.Role!=MechWeaponRole.AirToAirMissile&&angle>5)continue;
    if(weapons.CommandFireSlot(weapon.SlotId,MechActionSource.Player,radar.Reference(track.TrackId)))
    {playerAutoFireSlots.Add(weapon.SlotId);playerAutoShotUntil[weapon.InstanceId]=radar.SimTimeSeconds+PlayerWeaponInterval(status.Role,status.RoundsPerMinute);}

   }

  }
  public static double PlayerWeaponInterval(MechWeaponRole role,float rpm)=>rpm>0?60.0/rpm:role==MechWeaponRole.AirToAirMissile?8:.1;
  private void CancelQueuedPlayerAutoFire(){if(weapons!=null)foreach(var slot in playerAutoFireSlots)weapons.CancelEquipmentAction(slot);playerAutoFireSlots.Clear();}
  private bool IsShoulderMissile(MechPartSlot slot)
   =>weapons!=null&&weapons.TryGetWeaponSlotStatus(slot,out var status)&&(status.Role==MechWeaponRole.InterceptorMissile||status.Role==MechWeaponRole.AirToAirMissile);
  public void ApplyPlayerAutopilotAttitude()
  {
   if(!PlayerAutopilotActive||radar.TrackOperationMode||!playerNavigationValid||playerAimDirection.sqrMagnitude<.001f)return;
   var direction=Quaternion.Inverse(motion.LogicalRotation)*playerAimDirection.normalized;
   float yaw=Mathf.Atan2(direction.x,direction.z)*Mathf.Rad2Deg;
   float pitch=-Mathf.Atan2(direction.y,new Vector2(direction.x,direction.z).magnitude)*Mathf.Rad2Deg;
   motion.EnableLocalAttitudeControl(0);
   motion.SetLocalAttitudeInput(Mathf.Clamp(yaw/30,-1,1),Mathf.Clamp(pitch*2,-60,60));
  }
 }
}
