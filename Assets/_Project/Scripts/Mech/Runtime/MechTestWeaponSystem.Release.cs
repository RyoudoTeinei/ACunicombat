using System;
using System.Collections.Generic;
using ACunicombat.Sensing.Contracts;
namespace ACunicombat.Mechs
{
    public sealed partial class MechTestWeaponSystem
    {
        private readonly List<MechEquipmentWeaponSnapshot> releaseEquipment=new();
        public bool PreviewOffensiveTarget(in MechEquipmentWeaponSnapshot e,in TrackSnapshot track,out bool readyNow)
        {
            readyNow=false;
            if(radarController==null||!e.Enabled||!MechAiRulePlanner.IsOffensiveRole(e.Role)||!track.TrackId.IsValid||track.Affiliation==TrackAffiliation.Friendly||track.Identity.Available&&!track.Identity.CanAttackPlatform)return false;
            var slot=new MechEquipmentSlot(e.SlotId);
            if(!TryGetWeaponSlotStatus(slot,out var status)||!status.CanAttemptFire)return false;
            var p=BankPerformance(slot,track.TrackId)??GetEquippedWeaponPerformance(slot);if(p==null)return false;
            var target=radarController.PredictTrackPosition(track);float range=UnityEngine.Vector3.Distance(transform.position,target);
            if(range<status.MinimumRangeMeters||!p.IsGuidedMissile&&range>status.MaximumRangeMeters)return false;
            var scaled=MechFireControlPerformanceModel.ScaleTrackForFireSolution(track,radarController.EffectiveFireControlPerformance);
            var velocity=flightMotion!=null?flightMotion.Velocity:UnityEngine.Vector3.zero;
            var solution=p.IsGuidedMissile
                ?ACunicombat.FireControl.FireSolutionSolver.SolveMissile(scaled,ToDouble3(transform.position),ToDouble3(velocity),BuildMissileKinematics(p.missileFlightProfile),radarController.SimTimeSeconds)
                :ACunicombat.FireControl.FireSolutionSolver.Solve(scaled,ToDouble3(transform.position),ToDouble3(velocity),status.MuzzleVelocityMetersPerSecond,radarController.SimTimeSeconds);
            if(!solution.WeaponReleaseAuthorized)return false;
            if(p.IsGuidedMissile&&!HasMissileReleaseGeometry(p,track,solution.InterceptPoint))return false;
            if(!p.IsGuidedMissile&&p.projectileLifetimeSeconds>0&&solution.InterceptTime-radarController.SimTimeSeconds>p.projectileLifetimeSeconds)return false;
            float angle=UnityEngine.Vector3.Angle(flightMotion!=null?flightMotion.LogicalRotation*UnityEngine.Vector3.forward:transform.forward,target-transform.position);
            if(e.Pending||angle>(p.IsGuidedMissile?55:5))return true;
            if(p.IsGuidedMissile){readyNow=PreviewEquipmentMissileRelease(e,track.TrackId,out _,out _);return true;}
            ReleaseRequirements(p,track.TrackId,out int channels,out var emissions);
            var request=new MechActionProposal(new MechActionId(DefaultCommandSource,-1),e.InstanceId,slot,MechActionKind.Fire,radarController.Reference(track.TrackId),UnityEngine.Time.timeAsDouble+2,p.energyPerShotMj,channels,emissions);
            readyNow=radarController.RadarPowerEnabled&&Actions.Emissions.Allows(request)&&((IMechActionExecutor)this).Check(request,out _);
            return true;
        }
        public bool SupportsPassiveRfHoming(string slotId)
        {var slot=new MechEquipmentSlot(slotId);var p=BankPerformance(slot)??GetEquippedWeaponPerformance(slot);return p!=null&&p.role==MechWeaponRole.AirToAirMissile&&p.supportsPassiveRfHoming;}
        public float EstimatedProjectileSpeed(string slotId)
        {
            var slot=new MechEquipmentSlot(slotId);var p=BankPerformance(slot)??GetEquippedWeaponPerformance(slot);
            if(p==null)return 0;
            if(p.IsGuidedMissile)return (float)BuildMissileKinematics(p.missileFlightProfile).BurnoutSpeed;
            return TryGetWeaponSlotStatus(slot,out var status)?status.MuzzleVelocityMetersPerSecond:0;
        }
        private bool PreferPassiveRelease(MechWeaponPerformance p)=>p!=null&&p.supportsPassiveRfHoming&&GetComponent<MechFleetController>() is MechFleetController fleet&&fleet.HasSpatialMission&&fleet.TaskGroup==FleetTaskGroup.AirDefenseSuppression&&!(fleet.IsPlayer&&radarController!=null&&radarController.TrackOperationMode);
        public bool CanReleaseMissileAt(string slotId,TrackId target,out MechTargetTrackingMode mode,out string reason)
        {
            CopyEquipmentWeaponSnapshots(releaseEquipment);
            var e=releaseEquipment.Find(item=>item.SlotId==slotId);
            return PreviewEquipmentMissileRelease(e,target,out mode,out reason);
        }
        private bool PreviewEquipmentMissileRelease(in MechEquipmentWeaponSnapshot e,TrackId target,out MechTargetTrackingMode mode,out string reason)
        {
            mode=MechTargetTrackingMode.Search;reason="EquipmentUnavailable";
            if(radarController==null||!target.IsValid||e.InstanceId==Guid.Empty||e.Role!=MechWeaponRole.AirToAirMissile||!e.Enabled||e.Pending)return false;
            var slot=new MechEquipmentSlot(e.SlotId);var p=BankPerformance(slot,target)??GetEquippedWeaponPerformance(slot);if(p==null)return false;
            if(!radarController.PreviewMissileRelease(target,BuildMissileKinematics(p.missileFlightProfile),p.supportsPassiveRfHoming,out _,out _,out mode,out reason,PreferPassiveRelease(p)))return false;
            ReleaseRequirements(p,target,out int channels,out var emissions);
            var request=new MechActionProposal(new MechActionId(DefaultCommandSource,-1),e.InstanceId,slot,MechActionKind.Fire,radarController.Reference(target),UnityEngine.Time.timeAsDouble+8,p.energyPerShotMj,channels,emissions);
            var budget=((IMechActionExecutor)this).AvailableBudget;
            if(p.energyPerShotMj>budget.EnergyMj-Actions.ReservedEnergyMj){reason="EnergyUnavailable";return false;}
            if(channels>budget.GuidanceUnits-Actions.ReservedGuidanceUnits){reason="GuidanceCapacity";return false;}
            if(!Actions.Emissions.Allows(request)){reason="EmissionPolicyDenied";return false;}
            return ((IMechActionExecutor)this).Check(request,out reason);
        }
        private bool HasMissileReleaseGeometry(MechWeaponPerformance performance,in TrackSnapshot track,Unity.Mathematics.double3 intercept)
            => CheckMissileReleaseGeometry(performance,track,intercept,out _);
        private bool CheckMissileReleaseGeometry(MechWeaponPerformance performance,in TrackSnapshot track,Unity.Mathematics.double3 intercept,out string reason)
        {
            reason="FireControlUnavailable";
            if(radarController==null)return false;
            var flight=performance.missileFlightProfile.IsConfigured?performance.missileFlightProfile:airToAirMissileProfile;
            float minimum=UnityEngine.Mathf.Max(performance.proximityFuseSafeDistanceMeters,flight.ProximityFuseSafeDistanceMeters);
            var origin=(Unity.Mathematics.double3)(Unity.Mathematics.float3)transform.position;
            return MechMissileLaunchSource.CheckSafeGeometry(Unity.Mathematics.math.distance(track.PredictPosition(radarController.SimTimeSeconds),origin),
                track.PredictPositionUncertainty(radarController.SimTimeSeconds),minimum,Unity.Mathematics.math.distance(intercept,origin),double.PositiveInfinity,out reason);
        }
        private void ReleaseRequirements(MechWeaponPerformance performance,TrackId target,out int channels,out MechEmissionRequirement emissions)
        {
            channels=0;emissions=MechEmissionRequirement.None;
            if(NeedsInterceptorUplink(performance)){emissions=MechEmissionRequirement.DataLink;return;}
            if(performance.role!=MechWeaponRole.AirToAirMissile)return;
            var mode=MechTargetTrackingMode.Search;
            radarController?.PreviewMissileRelease(target,BuildMissileKinematics(performance.missileFlightProfile),performance.supportsPassiveRfHoming,out _,out _,out mode,out _,PreferPassiveRelease(performance));
            if(mode==MechTargetTrackingMode.Passive&&performance.supportsPassiveRfHoming)return;
            channels=radarController?.RequiredNewGuidanceChannels(target)??1;
            emissions=MechEmissionRequirement.DataLink;
            if(mode==MechTargetTrackingMode.Focused||mode==MechTargetTrackingMode.Search)emissions|=MechEmissionRequirement.Radar;
        }
        // Same executor preflight as a real request, without Submit/reservation/STT changes.
        private static bool NeedsInterceptorUplink(MechWeaponPerformance p)=>p!=null&&p.role==MechWeaponRole.InterceptorMissile&&p.maximumRangeMeters>p.missileFlightProfile.SeekerRangeMeters;
        public bool CanReleaseMissileAt(TrackId target,out MechTargetTrackingMode mode)
        {
            mode=MechTargetTrackingMode.Search;if(radarController==null||!target.IsValid)return false;
            var equipment=releaseEquipment;CopyEquipmentWeaponSnapshots(equipment);
            foreach(var e in equipment)
            {
                if(PreviewEquipmentMissileRelease(e,target,out var candidateMode,out _)){mode=candidateMode;return true;}
                if(candidateMode!=MechTargetTrackingMode.Search)mode=candidateMode;
            }
            if(mode==MechTargetTrackingMode.Search&&radarController.TryGetTrack(target,out var t))
                mode=MechMissileLaunchSource.Resolve(t,radarController.RadarPowerEnabled,target.Equals(radarController.SttTrackId),false,radarController.TryAssociatePassiveTarget(t,out _),radarController.SimTimeSeconds);
            return false;
        }
    }
}
