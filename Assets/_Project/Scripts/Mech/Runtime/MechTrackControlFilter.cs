using ACunicombat.Sensing.Contracts;
using UnityEngine;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// Converts a discrete, noisy sensor track into a continuous control reference.
    /// The public track remains untouched; this state is only for steering and flight control.
    /// </summary>
    internal struct MechTrackControlFilter
    {
        private TrackId trackId;
        private Vector3 position;
        private Vector3 velocity;
        private bool initialized;

        public Vector3 Position => position;
        public Vector3 Velocity => velocity;

        public void Reset()
        {
            trackId = default;
            position = Vector3.zero;
            velocity = Vector3.zero;
            initialized = false;
        }

        public void Reset(
            TrackId newTrackId,
            Vector3 newPosition,
            Vector3 newVelocity)
        {
            trackId = newTrackId;
            position = newPosition;
            velocity = newVelocity;
            initialized = true;
        }

        public Vector3 Update(
            TrackId newTrackId,
            Vector3 measuredPosition,
            Vector3 measuredVelocity,
            float deltaTime,
            float positionResponseSeconds,
            float velocityResponseSeconds,
            float maximumCorrectionSpeed,
            float snapDistanceMeters = 800f)
        {
            if (!IsFinite(measuredPosition) || !IsFinite(measuredVelocity))
            {
                return initialized ? position : Vector3.zero;
            }

            if (!initialized || !newTrackId.Equals(trackId) ||
                !IsFinite(position) || !IsFinite(velocity))
            {
                Reset(newTrackId, measuredPosition, measuredVelocity);
                return position;
            }

            var dt = Mathf.Clamp(deltaTime, 0f, 0.1f);
            var velocityBlend = ExponentialBlend(
                dt,
                velocityResponseSeconds);
            velocity = Vector3.Lerp(
                velocity,
                measuredVelocity,
                velocityBlend);

            // Propagate every rendered frame, then absorb only part of a new sensor
            // innovation. This removes sample-and-hold steps without inventing truth data.
            position += velocity * dt;
            var innovation = measuredPosition - position;
            if (innovation.sqrMagnitude >
                snapDistanceMeters * snapDistanceMeters)
            {
                position = measuredPosition;
                return position;
            }

            var positionBlend = ExponentialBlend(
                dt,
                positionResponseSeconds);
            var correction = innovation * positionBlend;
            if (maximumCorrectionSpeed > 0f && dt > 0f)
            {
                correction = Vector3.ClampMagnitude(
                    correction,
                    maximumCorrectionSpeed * dt);
            }

            position += correction;
            return position;
        }

        private static float ExponentialBlend(
            float deltaTime,
            float responseSeconds) =>
            1f - Mathf.Exp(
                -Mathf.Max(0f, deltaTime) /
                Mathf.Max(0.01f, responseSeconds));

        private static bool IsFinite(Vector3 value) =>
            float.IsFinite(value.x) &&
            float.IsFinite(value.y) &&
            float.IsFinite(value.z);
    }
}
