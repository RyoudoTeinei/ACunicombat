using UnityEngine;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// Fixed-step hook ordered before every MechRadarController (-1100): advances each
    /// observer's cooperative fusion for the new step in parallel before the radar
    /// controllers read their tracks. One per scene, created on demand.
    /// </summary>
    [DefaultExecutionOrder(-1200)]
    [AddComponentMenu("")]
    public sealed class MechCooperativePrewarmClock : MonoBehaviour
    {
        private static MechCooperativePrewarmClock instance;

        public static void Ensure()
        {
            if (instance != null || !Application.isPlaying) return;
            var host = new GameObject("Cooperative fusion prewarm") { hideFlags = HideFlags.HideInHierarchy };
            instance = host.AddComponent<MechCooperativePrewarmClock>();
        }

        private void FixedUpdate() => MechCooperativeTelemetry.PrewarmBeforeStep();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetInstance() => instance = null;

        private void OnDestroy()
        {
            if (instance == this) instance = null;
        }
    }
}
