using UnityEngine;

namespace ACunicombat.Mechs
{
    // Uses observed engagement geometry, never scene target transforms.
    public static class MissileSecondPulsePolicy
    {
        public static string Evaluate(float range, float closingSpeed, float speed,
            float turnDegrees, float burnSeconds, float remainingLife)
        {
            if(!float.IsFinite(range)||!float.IsFinite(closingSpeed)||!float.IsFinite(speed)||
                !float.IsFinite(turnDegrees)||range<=0||burnSeconds<=0)return null;
            float timeToGo=range/Mathf.Max(50f,closingSpeed);
            // Start early enough to burn, then retain a terminal correction window.
            if(timeToGo<=burnSeconds*2f+8f)return "terminal_energy";
            // A fleeing target/low closing speed needs energy before the terminal leg.
            if(closingSpeed<Mathf.Max(100f,speed*.25f))return "insufficient_closure";
            if(turnDegrees>=8f&&timeToGo<=burnSeconds*4f+8f)return "maneuver_recovery";
            if(timeToGo+burnSeconds>=remainingLife)return "flight_time_margin";
            return null;
        }
    }
}
