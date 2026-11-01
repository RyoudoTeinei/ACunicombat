using System;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Sensors;
using ACunicombat.Simulation.Core;
using ACunicombat.Simulation.World;
using ACunicombat.Tracking;
using Unity.Collections;
using Unity.Mathematics;

namespace ACunicombat.Simulation.Driver
{
    /// <summary>
    /// One observer's primary-radar knowledge. Routing and tracking are independent of world
    /// ownership. No enemy entity identity or truth state is available to this context.
    /// This is the first P1 seam; legacy drivers still own their separate worlds and clocks.
    /// </summary>
    public sealed class PlatformTrackContext : IDisposable
    {
        private readonly System.Collections.Generic.Dictionary<SensorId,(EmitterId emitter,bool protection)> auxiliary=new();
        private readonly RadarTrackManager tracks = new RadarTrackManager();
        public Guid ObserverScope { get; } = Guid.NewGuid();
        public ObserverTrackRef Reference(TrackId track)=>new ObserverTrackRef(ObserverScope,track);
        public bool TryGetTrack(in ObserverTrackRef reference,out TrackSnapshot snapshot)
        {
            snapshot=default;
            return reference.ObserverScope==ObserverScope && tracks.TryGetTrack(reference.TrackId,out snapshot);
        }
        public int Count => tracks.Count;
        public int Capacity => tracks.Capacity;
        public event Action<RadarTrackDiagnostic> Diagnostic
        {
            add => tracks.Diagnostic += value;
            remove => tracks.Diagnostic -= value;
        }
        public void ConfigureLimits(int capacity, double memorySeconds) => tracks.ConfigureLimits(capacity, memorySeconds);
        public bool TryGetTrack(TrackId id, out TrackSnapshot snapshot) => tracks.TryGetTrack(id, out snapshot);
        public bool TryGetBestTrack(out TrackSnapshot snapshot) => tracks.TryGetBestTrack(out snapshot);
        public bool TryRefineMissileTelemetry(TrackId id,double3 position,double3 velocity,double reportTime,double now)=>tracks.TryRefineMissileTelemetry(id,position,velocity,reportTime,now);
        public bool CloseMissileAssociation(TrackId id,double now)=>tracks.CloseMissileAssociation(id,now);
        public int CopyTracks(TrackSnapshot[] destination) => tracks.CopyTracks(destination);
        public bool TryRefineConfirmedTrackWithLaser(in LaserRangeObservation observation,
            double3 observerPosition, double time, out double normalizedInnovationSquared) =>
            tracks.TryRefineConfirmedTrackWithLaser(in observation, observerPosition, time, out normalizedInnovationSquared);
        public bool TryRefineConfirmedTrackWithBearing(in ElectroOpticalObservation observation,
            double3 observerPosition, double time, out double normalizedAngle,
            double3 observerVelocity = default) =>
            tracks.TryRefineConfirmedTrackWithBearing(in observation, observerPosition, time,
                out normalizedAngle, observerVelocity);
        public SimEntityId OriginEntity { get; private set; }
        public SensorId Sensor { get; private set; }
        public EmitterId Emitter { get; private set; }
        public PlatformId Platform { get; private set; }
        private NativeList<Measurement> measurements;
        private NativeList<EmissionEvent> emissions;
        private bool bound;
        private bool disposed;

        public PlatformTrackContext()
        {
            measurements = new NativeList<Measurement>(RadarSensorSystem.DetectionRingCapacity, Allocator.Persistent);
            emissions = new NativeList<EmissionEvent>(1024, Allocator.Persistent);
        }

        public void BindPrimaryRadar(in RadarSensorProfile profile)
        {
            if (disposed) throw new ObjectDisposedException(nameof(PlatformTrackContext));
            if (!profile.SensorId.IsValid || !profile.OwnerEntityId.IsValid)
                throw new ArgumentException("Primary radar requires valid sensor and owner identities.");
            if (bound && (!Sensor.Equals(profile.SensorId) || !OriginEntity.Equals(profile.OwnerEntityId) ||
                          !Emitter.Equals(profile.EmitterId) || !Platform.Equals(profile.PlatformId)))
                throw new InvalidOperationException("A different observer requires a new track context; old tracks cannot be inherited.");
            Sensor = profile.SensorId;
            Emitter = profile.EmitterId;
            Platform = profile.PlatformId;
            OriginEntity = profile.OwnerEntityId;
            bound = true;
        }

        public void RegisterAuxiliary(in RadarSensorProfile profile,bool protection)
        {
            if(!bound||!profile.OwnerEntityId.Equals(OriginEntity))throw new ArgumentException("Auxiliary receiver must belong to this platform");
            auxiliary[profile.SensorId]=(profile.EmitterId,protection);
        }
        public void RemoveAuxiliary(SensorId sensor)=>auxiliary.Remove(sensor);
        public void Process(NativeList<Measurement> allMeasurements, NativeList<EmissionEvent> allEmissions,
            double3 observerPosition, double3 observerVelocity, double time)
        {
            if (disposed) throw new ObjectDisposedException(nameof(PlatformTrackContext));
            if (!bound) return;
            measurements.Clear();
            emissions.Clear();
            for (var i = 0; i < allMeasurements.Length; i++)
                {
                    var value=allMeasurements[i];value.Flags&=~MeasurementFlags.CloseProtectionReceiver;
                    if(value.SensorId.Equals(Sensor))measurements.Add(value);
                    else if(auxiliary.TryGetValue(value.SensorId,out var receiver))
                    {if(receiver.protection)value.Flags|=MeasurementFlags.CloseProtectionReceiver;measurements.Add(value);}
                }
            for (var i = 0; i < allEmissions.Length; i++)
                {
                    var value=allEmissions[i];bool owned=value.EmitterId.Equals(Emitter);
                    if(!owned)foreach(var receiver in auxiliary.Values)if(receiver.emitter.Equals(value.EmitterId)){owned=true;break;}
                    if(owned)emissions.Add(value);
                }
            tracks.Process(measurements, emissions, observerPosition, observerVelocity, time);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (measurements.IsCreated) measurements.Dispose();
            if (emissions.IsCreated) emissions.Dispose();
        }
    }
}