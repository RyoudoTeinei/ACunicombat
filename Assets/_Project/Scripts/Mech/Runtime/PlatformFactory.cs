using System;
using System.Collections.Generic;
using ACunicombat.Simulation.Driver;
using UnityEngine;
namespace ACunicombat.Mechs
{
    /// <summary>Shared, idempotent platform initialization. Input, HUD and tactical policy stay
    /// outside this factory; both playable rigs and demonstrations use the same runtime entry.</summary>
    public static class PlatformFactory
    {
        public static T Ensure<T>(GameObject platform) where T:Component
        {
            if(platform==null)throw new ArgumentNullException(nameof(platform));
            return platform.GetComponent<T>()??platform.AddComponent<T>();
        }
        public static MechBodyActionPlayer ConfigureBodyPlayback(GameObject platform,MechActionRegistry registry,
            MechRuntimeState state,IEnumerable<string> upperBones,string initialAction=null,float speed=1f)
        {
            if(platform==null)throw new ArgumentNullException(nameof(platform));
            if(registry==null || state==null)return null;
            var legacy=platform.GetComponent<MechSynchronizedAnimationPlayer>();
            if(legacy!=null){legacy.StopPlayback();legacy.enabled=false;}
            var player=Ensure<MechBodyActionPlayer>(platform);
            player.Configure(registry,state,upperBones,initialAction,speed);
            return player;
        }
        public static void ConfigurePhysicalPresentation(GameObject platform,MechFlightMotionController motion,
            ref MechThrustVisualizer thrust,ref MechMainThrusterPlume plume,ref MechThermalSignature thermal)
        {
            BattlefieldEntityIdentity.GetOrAdd(platform);
            thrust=thrust!=null?thrust:Ensure<MechThrustVisualizer>(platform);thrust.Configure(motion);
            plume=plume!=null?plume:Ensure<MechMainThrusterPlume>(platform);plume.Configure(motion);
            thermal=thermal!=null?thermal:Ensure<MechThermalSignature>(platform);thermal.Configure(motion);
        }
    }
}