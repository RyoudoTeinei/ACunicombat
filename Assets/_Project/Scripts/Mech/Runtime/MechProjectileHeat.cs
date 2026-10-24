using UnityEngine;
namespace ACunicombat.Mechs
{
    // Propulsion is the only writer. Both thermal rendering and infrared signals consume this state.
    public sealed class MechProjectileHeat
    {
        public float TemperatureK {get;private set;}=300;
        public bool Burning {get;private set;}
        public float IntensityDb => 28+30*Mathf.Clamp01((TemperatureK-300)/900);
        public void Reset(){TemperatureK=300;Burning=false;}
        public void Step(bool burning,float dt)
        {
            if(!float.IsFinite(dt)||dt<0)return;
            Burning=burning;
            TemperatureK=burning?1200:Mathf.Lerp(TemperatureK,300,1-Mathf.Exp(-dt/1.5f));
        }
    }
}
