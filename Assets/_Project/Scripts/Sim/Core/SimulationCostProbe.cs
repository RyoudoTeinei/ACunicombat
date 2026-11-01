using System;
using System.Diagnostics;
namespace ACunicombat.Simulation.Core
{
    // Main-thread, opt-in diagnostic counters. Disabled in normal gameplay.
    public static class SimulationCostProbe
    {
        public enum SystemId {Driver,RadarPhysics,RadarPresentation,AI,Projectile,Weapons,WeaponPresentation,Identity,Fleet,Telemetry,AiInventory,AiTargetSelection,WeaponInventory,FleetRange,Hud,PlayerHud,Plume,EquipmentAnimation,PlayerUpdate,PlayerLate,Count}
        public struct Reading {public long Calls;public double Milliseconds,MaximumMilliseconds;}
        static readonly long[] calls=new long[(int)SystemId.Count],ticks=new long[(int)SystemId.Count],maximum=new long[(int)SystemId.Count];
        static readonly string[] labels=System.Enum.GetNames(typeof(SystemId));
        public static Action<string> BeginSample;
        public static Action EndSample;
        public static bool Enabled {get;set;}
        public readonly struct Scope:IDisposable
        {
            readonly int index;readonly long started;
            internal Scope(SystemId id){index=(int)id;started=Enabled?Stopwatch.GetTimestamp():0;if(started!=0)BeginSample?.Invoke(labels[index]);}
            public void Dispose(){if(started==0)return;EndSample?.Invoke();long dt=Stopwatch.GetTimestamp()-started;calls[index]++;ticks[index]+=dt;if(dt>maximum[index])maximum[index]=dt;}
        }
        public static Scope Measure(SystemId id)=>new Scope(id);
        public static Reading Take(SystemId id){int i=(int)id;var r=new Reading{Calls=calls[i],Milliseconds=ticks[i]*1000.0/Stopwatch.Frequency,MaximumMilliseconds=maximum[i]*1000.0/Stopwatch.Frequency};calls[i]=ticks[i]=maximum[i]=0;return r;}
        public static void Reset(){Array.Clear(calls,0,calls.Length);Array.Clear(ticks,0,ticks.Length);Array.Clear(maximum,0,maximum.Length);}
    }
}
