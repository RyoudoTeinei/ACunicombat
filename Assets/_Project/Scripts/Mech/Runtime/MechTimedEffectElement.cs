using UnityEngine;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// Disables a non-particle FXR element after its source-defined lifetime.
    /// Static model carriers and lights otherwise remain visible until the effect root is destroyed.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MechTimedEffectElement : MonoBehaviour
    {
        [SerializeField, Min(0.01f)] private float lifetimeSeconds = 0.25f;

        private float elapsedSeconds;

        public float LifetimeSeconds => lifetimeSeconds;

        public void Configure(float lifetime)
        {
            lifetimeSeconds = Mathf.Max(lifetime, 0.01f);
        }

        private void OnEnable()
        {
            elapsedSeconds = 0f;
        }

        private void Update()
        {
            elapsedSeconds += Time.deltaTime;
            if (elapsedSeconds >= lifetimeSeconds)
            {
                gameObject.SetActive(false);
            }
        }
    }
}
