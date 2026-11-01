using UnityEngine;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// Presents missiles at the current render epoch using bounded physics extrapolation.
    /// Guidance, collision and sensor state continue to use the simulation transform.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MechMissileVisualInterpolator : MonoBehaviour
    {
        private Transform simulationTransform;
        private MechTestProjectile projectile;
        private Vector3 previousPosition;
        private Vector3 currentPosition;
        private Quaternion previousRotation;
        private Quaternion currentRotation;
        private bool initialized;

        public void Configure(Transform source)
        {
            simulationTransform = source;
            projectile = source != null ? source.GetComponent<MechTestProjectile>() : null;
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

        private void LateUpdate() => PresentAt(Time.timeAsDouble);

        private void PresentAt(double renderTime)
        {
            if (!initialized)
            {
                return;
            }

            float fixedStep = Mathf.Max(Time.fixedDeltaTime, 0.0001f);
            float alpha = Mathf.Clamp01((float)(renderTime - (projectile != null ? projectile.PhysicsStateTime : Time.fixedTimeAsDouble)) / fixedStep);
            transform.SetPositionAndRotation(
                // The carrier/camera are rendered at the current frame, so a delayed physics
                // interpolation loses speed * fixedStep metres at high carrier velocity.
                // Predict only the presentation to that same epoch; physics is untouched.
                projectile != null ? projectile.PositionAtPhysicsTime(renderTime) :
                    currentPosition + (currentPosition - previousPosition) * alpha,
                Quaternion.SlerpUnclamped(previousRotation, currentRotation, 1f + alpha));
        }
    }
}
