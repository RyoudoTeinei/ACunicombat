using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
namespace ACunicombat.Simulation.Driver
{
    public sealed class RadarSensorAllocator
    {
        private readonly DeviceHandleAllocator pool=new DeviceHandleAllocator(64);
        public SensorId PrimaryRadar {get;}
        public SensorId WarningReceiver {get;}
        public SensorId OpticalReceiver {get;}
        public SensorId LaserRangefinder {get;}
        public RadarSensorAllocator()
        {
            pool.TryAllocate(out var radar);PrimaryRadar=new SensorId(radar);
            pool.TryAllocate(out var warning);WarningReceiver=new SensorId(warning);
            pool.TryAllocate(out var optical);OpticalReceiver=new SensorId(optical);
            pool.TryAllocate(out var laser);LaserRangefinder=new SensorId(laser);
        }
        public bool TryAllocate(out SensorId sensor){var result=pool.TryAllocate(out var handle);sensor=new SensorId(handle);return result;}
        public bool TryReserve(SensorId sensor)=>pool.TryReserve(sensor.Value);
        public bool Owns(SensorId sensor)=>pool.Owns(sensor.Value);
        public bool Release(SensorId sensor)
        {
            if(sensor.Equals(PrimaryRadar)||sensor.Equals(WarningReceiver)||sensor.Equals(OpticalReceiver)||sensor.Equals(LaserRangefinder))return false;
            return pool.Release(sensor.Value);
        }
    }
}