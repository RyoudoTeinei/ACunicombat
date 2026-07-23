using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.World;

namespace ACunicombat.Sensors
{
    public struct RadarSensorProfile
    {
        public SensorId SensorId;
        public EmitterId EmitterId;
        public PlatformId PlatformId;
        public SimEntityId OwnerEntityId;
        public RadarDwellConfig DwellConfig;
        public SensorCapability Capability;
        public double MaxRangeMeters;
        public ushort FrequencyBand;
        public ushort WaveformId;
        public float MainlobePowerDb;
        public float SidelobeLevelDb;
        public float ClutterPowerDb;
        public ResolutionMerge.Resolutions Resolutions;
    }

    public struct RadarTargetProfile
    {
        public SimEntityId EntityId;
        public float RadarCrossSectionSquareMeters;
    }
}
