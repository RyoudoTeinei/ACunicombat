using System;
using ACunicombat.Simulation.World;
using UnityEngine;

namespace ACunicombat.Simulation.Driver
{
    [DisallowMultipleComponent]
    public sealed class BattlefieldEntityIdentity : MonoBehaviour
    {
        private static BattlefieldEntityRegistry registry = new BattlefieldEntityRegistry();
        private BattlefieldEntityId identity;
        internal static BattlefieldEntityRegistry Registry => registry;
        public static ObserverEntityBindings CreateObserverBindings(SimWorld world) => new ObserverEntityBindings(world, registry);
        public BattlefieldEntityId Identity
        {
            get
            {
                if (!isActiveAndEnabled) throw new InvalidOperationException("Inactive entity cannot be registered as live.");
                if (!registry.IsAlive(identity)) identity = registry.Register(this);
                return identity;
            }
        }
        public static void CopyActive(System.Collections.Generic.List<BattlefieldEntityIdentity> destination) => registry.CopyOwners(destination);
        public static BattlefieldEntityIdentity GetOrAdd(GameObject actor)
        {
            if (actor == null) throw new ArgumentNullException(nameof(actor));
            var component = actor.GetComponent<BattlefieldEntityIdentity>();
            return component != null ? component : actor.AddComponent<BattlefieldEntityIdentity>();
        }
        private void OnEnable() { identity = registry.Register(this); }
        private void OnDisable() { registry.Release(this, identity); identity = default; }
        private void OnDestroy() { registry.Release(this, identity); identity = default; }
        public void RestartLifetime()
        {
            registry.Release(this, identity); identity = default;
            if (isActiveAndEnabled) identity = registry.Register(this);
        }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetSession() { registry = new BattlefieldEntityRegistry(); }
    }
}