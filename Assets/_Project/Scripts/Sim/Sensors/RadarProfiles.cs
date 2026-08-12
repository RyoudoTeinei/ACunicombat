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
        public EmissionType EmissionType;
        public float MainlobePowerDb;
        public float SidelobeLevelDb;
        public float ClutterPowerDb;
        /// <summary>
        /// Multiplies range, angle and Doppler measurement sigma without changing the physical
        /// beam footprint. Values at or below zero preserve the legacy scale of 1.
        /// </summary>
        public float MeasurementPrecisionScale;
        public ResolutionMerge.Resolutions Resolutions;
    }

    public struct RadarTargetProfile
    {
        public SimEntityId EntityId;
        public float RadarCrossSectionSquareMeters;
        public TrackObjectClass ObjectClass;
        public float ClassificationSignature;
    }
}
