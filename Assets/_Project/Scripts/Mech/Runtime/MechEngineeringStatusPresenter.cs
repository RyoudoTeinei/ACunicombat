using UnityEngine;

namespace ACunicombat.Mechs
{
    // Retained for serialized scenes/prefabs that referenced the retired thrust HUD.
    // New player rigs no longer attach it; existing references render nothing.
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class MechEngineeringStatusPresenter : MonoBehaviour
    {
        private void Awake() { enabled = false; }
    }
}
