using UnityEngine;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// Smooths only the missile presentation hierarchy between fixed simulation ticks.
    /// Guidance, collision and sensor state continue to use the simulation transform.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MechMissileVisualInterpolator : MonoBehaviour
    {
        private Transform simulationTransform;
        private Vector3 previousPosition;
        private Vector3 currentPosition;
        private Quaternion previousRotation;
        private Quaternion currentRotation;
        private bool initialized;

        public void Configure(Transform source)
        {
            simulationTransform = source;
            if (simulationTransform == null)
            {
                initialized = false;
                return;
            }

            previousPosition = currentPosition = simulationTransform.position;
            previousRotation = currentRotation = simulationTransform.rotation;
            transform.SetPositionAndRotation(currentPosition, currentRotation);
            initialized = true;
        }

        public void CaptureSimulationPose(Vector3 position, Quaternion rotation)
        {
            if (!initialized)
            {
                previousPosition = currentPosition = position;
                previousRotation = currentRotation = rotation;
                transform.SetPositionAndRotation(position, rotation);
                initialized = true;
                return;
            }

            previousPosition = currentPosition;
            previousRotation = currentRotation;
            currentPosition = position;
            currentRotation = rotation;
        }

        private void LateUpdate()
        {
            if (!initialized)
            {
                return;
            }

            float fixedStep = Mathf.Max(Time.fixedDeltaTime, 0.0001f);
            float alpha = Mathf.Clamp01((Time.time - Time.fixedTime) / fixedStep);
            transform.SetPositionAndRotation(
                Vector3.Lerp(previousPosition, currentPosition, alpha),
                Quaternion.Slerp(previousRotation, currentRotation, alpha));
        }
    }
}
