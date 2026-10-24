using System;
using System.Collections.Generic;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Sensors;
using ACunicombat.Simulation.Core;
using ACunicombat.Simulation.Driver;
using ACunicombat.Simulation.Kinematics;
using ACunicombat.Simulation.World;
using Unity.Mathematics;
using UnityEngine;

namespace ACunicombat.Mechs
{
    /// <summary>Scene-truth adapter and production radar owned by the missile, never by its launcher.
    /// Only anonymous Measurement values leave this sensor boundary.</summary>
    internal sealed class MechMissileSeekerRuntime : IDisposable
    {
        private readonly SimWorld world=new SimWorld();
        private readonly SpatialHashGrid grid=new SpatialHashGrid(SimWorld.MaxEntities*2);
        private readonly DwellScheduler scheduler=new DwellScheduler(16);
        private readonly RadarSensorSystem radar;
        private readonly EsmReceiverSystem passiveReceiver;
        private readonly EmissionEvent[] emissions=new EmissionEvent[64];
        private readonly Dictionary<MechRadarController,double> lastEmission=new();
        private readonly SimEntityId receiver;
        private readonly SensorId sensor=new SensorId(new Handle(0,1));
        private readonly ObserverEntityBindings bindings;
        private readonly Dictionary<GameObject,SceneEntityLease> targets=new Dictionary<GameObject,SceneEntityLease>();
        private double previousScanTime = double.NaN;
        private double scanTime;
        private readonly List<MechRadarDecoySignal> decoys=new List<MechRadarDecoySignal>();
        private readonly HashSet<GameObject> seen=new HashSet<GameObject>();
        private readonly List<GameObject> retired=new List<GameObject>();
        private readonly MechTestProjectile missile;
        private readonly List<CombatTestTarget> bodies=new List<CombatTestTarget>();

        private bool disposed;
        public MechMissileSeekerRuntime(MechTestProjectile missile,bool interceptor,float range,float beamDegrees)
        {
            this.missile=missile;
            var identity=BattlefieldEntityIdentity.GetOrAdd(missile.gameObject).Identity;
            radar=new RadarSensorSystem(world,grid,scheduler){SessionSeed=SimHash.Chain(1u,(uint)identity.Index,identity.Generation)};
            bindings=BattlefieldEntityIdentity.CreateObserverBindings(world);
            bindings.LocalEntityReleased += radar.UnregisterTarget;
            receiver=bindings.Acquire(BattlefieldEntityIdentity.GetOrAdd(missile.gameObject).Identity, State(missile.transform.position,missile.Velocity)).Entity;
            passiveReceiver=new EsmReceiverSystem(world);
            passiveReceiver.RegisterReceiver(new EsmReceiverProfile{ReceiverId=sensor,OwnerEntityId=receiver,SensitivityFloorDb=-80,BearingSigmaRadians=math.radians(2)});
            radar.RegisterSensor(MissileSeekerProfile.Create(sensor,receiver,interceptor,range,beamDegrees));
        }
        public bool Listen(double time,Vector3 expected,ushort band,ushort waveform,out Vector3 bearing)
        {
            bearing=default;world.TrySetState(receiver,State(missile.transform.position,missile.Velocity));world.SwapSnapshots(time);
            passiveReceiver.BeginTick();MechSensorScene.CopyBodies(bodies);
            using(var events=new Unity.Collections.NativeList<EmissionEvent>(64,Unity.Collections.Allocator.Temp))
            {
                foreach(var body in bodies)
                {
                    if(body==null||!body.IsAlive||missile.IsOwnedBy(body.transform))continue;
                    var source=body.GetComponent<MechRadarController>();if(source==null)continue;
                    int n=source.CopyOwnRadarEmissions(emissions);double latest=lastEmission.TryGetValue(source,out var prior)?prior:double.NegativeInfinity;
                    for(int i=0;i<n;i++)if(emissions[i].StartTime>latest&&emissions[i].StartTime<=time&&time-emissions[i].EndTime<.2)
                    {events.Add(emissions[i]);prior=System.Math.Max(prior,emissions[i].StartTime);}
                    if(n>0)lastEmission[source]=System.Math.Max(latest,prior);
                }
                passiveReceiver.Consume(events,time);
            }
            Vector3 sum=Vector3.zero;int samples=0;
            for(int i=0;i<passiveReceiver.Contacts.Length;i++)
            {
                var c=passiveReceiver.Contacts[i];if(c.FrequencyBand!=band||c.WaveformId!=waveform)continue;
                if(Vector3.Angle(expected,(Vector3)c.Direction)>25)continue;
                if(samples>0&&Vector3.Angle(sum,(Vector3)c.Direction)>6)return false;
                sum+=(Vector3)c.Direction;samples++;
            }
            bearing=sum.normalized;return samples>0;
        }
        public int Scan(double time,Vector3 direction,float beamDegrees,ulong sequence,Measurement[] destination)
        {
            if(disposed) throw new ObjectDisposedException(nameof(MechMissileSeekerRuntime));
            MechSensorScene.CopyBodies(bodies);
            scanTime=time;
            bindings.PruneRetired();
            seen.Clear();
            foreach(var body in bodies)
            {
                if(body==null || !body.gameObject.activeInHierarchy || !body.IsAlive)continue;
                var motion=body.GetComponent<MechFlightMotionController>();
                Observe(body.gameObject,body.AimPoint.position,motion!=null?motion.Velocity:EstimateBodyVelocity(body.gameObject,body.AimPoint.position),
                    body.RadarCrossSectionSquareMeters,TrackObjectClass.AirVehicle,.85f);
            }
            foreach(var projectile in MechTestProjectile.Registry)
            {
                if(projectile==null || projectile==missile || !projectile.IsMissile || !projectile.gameObject.activeInHierarchy)continue;
                Observe(projectile.gameObject,projectile.transform.position,projectile.Velocity,
                    projectile.IsInterceptorMissile?.02f:.04f,TrackObjectClass.Missile,.9f);
            }
            MechSensorScene.CopyDecoys(decoys);
            foreach(var decoy in decoys)if(decoy.VisibleToLegacyReceiver(missile.LaunchTeam))
                Observe(decoy.gameObject,decoy.transform.position,decoy.Velocity,decoy.CrossSection,TrackObjectClass.Decoy,.95f);
            retired.Clear();
            foreach(var pair in targets) if(!seen.Contains(pair.Key))retired.Add(pair.Key);
            foreach(var key in retired){var lease=targets[key];bindings.Release(lease);targets.Remove(key);}
            world.TrySetState(receiver,State(missile.transform.position,missile.Velocity));
            radar.ScheduleDwell(new Dwell {SensorId=sensor,DueTime=time,DwellTimeSeconds=.004f,
                BeamDirection=(float3)direction.normalized,BeamWidthRadians=math.radians(math.max(.1f,beamDegrees)),
                Priority=DwellPriority.MissileTerminal,Sequence=sequence});
            radar.Tick(time);
            var count=math.min(destination.Length,radar.Detections.Length);
            for(var i=0;i<count;i++)destination[i]=radar.Detections[i];
            previousScanTime=time;
            return count;
        }
        private void Observe(GameObject owner,Vector3 position,Vector3 velocity,float rcs,TrackObjectClass kind,float classification)
        {
            seen.Add(owner);
            if(!targets.TryGetValue(owner,out var lease) || !bindings.IsCurrent(lease))
            {
                bindings.Release(lease);
                lease=bindings.Acquire(BattlefieldEntityIdentity.GetOrAdd(owner).Identity,State(position,velocity));targets[owner]=lease;

            }
            radar.RegisterTarget(new RadarTargetProfile {EntityId=lease.Entity,RadarCrossSectionSquareMeters=rcs,ObjectClass=kind,ClassificationSignature=classification});
            world.TrySetState(lease.Entity,State(position,velocity));
        }
        private Vector3 EstimateBodyVelocity(GameObject owner, Vector3 position)
        {
            if (double.IsNaN(previousScanTime) || scanTime<=previousScanTime ||
                !targets.TryGetValue(owner,out var lease) || !bindings.IsCurrent(lease) ||
                !world.TryGetWriteState(lease.Entity,out var previous)) return Vector3.zero;
            return (position-(Vector3)(float3)previous.PositionWorld)/(float)(scanTime-previousScanTime);
        }
        private static KinematicState State(Vector3 position,Vector3 velocity)=>new KinematicState {
            PositionWorld=new double3(position.x,position.y,position.z),VelocityWorld=new double3(velocity.x,velocity.y,velocity.z),Rotation=quaternion.identity,Mass=1};
        public void Dispose(){if(disposed)return;disposed=true;bindings.Dispose();radar.Dispose();passiveReceiver.Dispose();scheduler.Dispose();grid.Dispose();world.Dispose();}
    }
}
