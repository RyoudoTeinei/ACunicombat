using System.Collections.Generic;
using ACunicombat.Simulation.Driver;
namespace ACunicombat.Mechs
{
    /// <summary>World-side scene adapter shared by platform radar and independent seekers.
    /// It is never an AI target-selection API; anonymous observations remain the decision input.</summary>
    internal static class MechSensorScene
    {
        private static readonly List<BattlefieldEntityIdentity> identities=new List<BattlefieldEntityIdentity>();
        public static void CopyDecoys(List<MechRadarDecoySignal> destination)
        {
            destination.Clear();BattlefieldEntityIdentity.CopyActive(identities);
            foreach(var identity in identities)
                if(identity!=null && identity.gameObject.activeInHierarchy &&
                    identity.TryGetComponent<MechRadarDecoySignal>(out var signal) && signal.SignalActive)destination.Add(signal);
            identities.Clear();
        }
        public static void CopyBodies(List<CombatTestTarget> destination)
        {
            destination.Clear();BattlefieldEntityIdentity.CopyActive(identities);
            foreach(var identity in identities)
                if(identity!=null && identity.gameObject.activeInHierarchy &&
                    identity.TryGetComponent<CombatTestTarget>(out var body)) destination.Add(body);
            identities.Clear();
        }
    }
}