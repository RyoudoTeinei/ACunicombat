using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.World;
using Unity.Mathematics;
namespace ACunicombat.Sensors
{
    public static class MissileSeekerProfile
    {
        public static RadarSensorProfile Create(SensorId sensor, SimEntityId entity, bool interceptor, float range, float beamDegrees)
        {
            var rangeResolution=interceptor?1.2f:3f;
            var velocityResolution=interceptor?.35f:.75f;
            return new RadarSensorProfile {
                SensorId=sensor,EmitterId=new EmitterId(sensor.Value),PlatformId=new PlatformId(sensor.Value),OwnerEntityId=entity,
                DwellConfig=new RadarDwellConfig {ConstantsDb=215,IntegrationGainDb=interceptor?10:8,NoisePowerDb=0,
                    DetectThresholdLowDb=3,DetectThresholdHighDb=10,RangeResolutionMeters=rangeResolution,
                    BeamWidthRadians=math.radians(beamDegrees),VelocityResolutionMps=velocityResolution},
                Capability=SensorCapability.Range|SensorCapability.Doppler|SensorCapability.LosRate,
                MaxRangeMeters=math.max(100,range),FrequencyBand=1,WaveformId=(ushort)(interceptor?4:2),
                EmissionType=EmissionType.Seeker,MainlobePowerDb=interceptor?52:56,SidelobeLevelDb=interceptor?-36:-32,
                ClutterPowerDb=-45,MeasurementPrecisionScale=interceptor?.65f:1,
                Resolutions=new ResolutionMerge.Resolutions {RangeMeters=rangeResolution,AngleRadians=math.radians(.5f),VelocityMps=velocityResolution}
            };
        }
    }
}