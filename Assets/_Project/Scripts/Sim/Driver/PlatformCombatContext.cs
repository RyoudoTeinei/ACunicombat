using ACunicombat.Sensing.Contracts;
using ACunicombat.Sensors;
using ACunicombat.Simulation.World;
using ACunicombat.Tracking;
namespace ACunicombat.Simulation.Driver
{
    /// <summary>Observer-local perception state. SimulationDriver schedules this state but does
    /// not share it with another platform. World snapshots remain outside the knowledge store.</summary>
    public sealed class PlatformCombatContext
    {
        public PlatformTrackContext RadarTracks { get; }
        internal EsmThreatCorrelator Threats { get; } = new EsmThreatCorrelator();
        internal SensorId WarningReceiver;
        internal SimEntityId WarningOwner;
        internal ElectroOpticalReceiverProfile OpticalReceiver;
        internal bool HasOpticalReceiver;
        public PlatformCombatContext(PlatformTrackContext radarTracks)
        { RadarTracks=radarTracks??throw new System.ArgumentNullException(nameof(radarTracks)); }
        public int CopyThreats(ThreatSnapshot[] destination)=>Threats.CopyThreats(destination);
        public bool TryGetHighestThreat(out ThreatSnapshot snapshot)=>Threats.TryGetHighestThreat(out snapshot);
    }
}