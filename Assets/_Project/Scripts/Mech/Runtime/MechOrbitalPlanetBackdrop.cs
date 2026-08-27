using UnityEngine;

namespace ACunicombat.Mechs
{
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(1000)]
    public sealed class MechOrbitalPlanetBackdrop : MonoBehaviour
    {
        public const int OrbitalPlanetLayer = 29;

        [SerializeField] private Transform observer;
        [SerializeField] private Vector3 centerDirection = new(0f, -0.966f, 0.259f);
        [SerializeField] private Vector3 localReferenceOrigin;
        [SerializeField] private double planetRadiusMeters = 3_389_500.0;
        [SerializeField] private double orbitalAltitudeMeters = 400_000.0;
        [SerializeField, Min(0.000001f)] private float scaledSpaceFactor = 0.01f;
        [SerializeField] private bool rotatePlanet = true;
        [SerializeField, Min(1.0f)] private double rotationPeriodSeconds = 88_775.244;
        [SerializeField, Min(0.0f)] private float rotationTimeScale = 1f;

        private Quaternion baseLocalRotation;
        private double rotationEpochSeconds;
        private bool rotationInitialized;

        public double GetOrbitalAltitudeMeters(Vector3 worldPosition)
        {
            var localOffset = worldPosition - localReferenceOrigin;
            var radialOutward = -centerDirection.normalized;
            return System.Math.Max(
                0.0,
                orbitalAltitudeMeters + Vector3.Dot(localOffset, radialOutward));
        }

        public void Configure(
            Transform newObserver,
            Vector3 newCenterDirection,
            double newPlanetRadiusMeters,
            double newOrbitalAltitudeMeters,
            float newScaledSpaceFactor,
            Vector3 newLocalReferenceOrigin)
        {
            observer = newObserver;
            centerDirection = newCenterDirection.sqrMagnitude > 0.0001f
                ? newCenterDirection.normalized
                : Vector3.down;
            planetRadiusMeters = System.Math.Max(1.0, newPlanetRadiusMeters);
            orbitalAltitudeMeters = System.Math.Max(1.0, newOrbitalAltitudeMeters);
            scaledSpaceFactor = Mathf.Max(0.000001f, newScaledSpaceFactor);
            localReferenceOrigin = newLocalReferenceOrigin;
            RefreshNow();
        }

        private void Awake()
        {
            SetLayerRecursively(transform, OrbitalPlanetLayer);
            InitializeRotation();
            ResolveObserver();
            RefreshNow();
        }

        private void LateUpdate()
        {
            ResolveObserver();
            RefreshNow();
        }

        public void RefreshNow()
        {
            var observerPosition = observer != null ? observer.position : localReferenceOrigin;
            var observerOffset = observerPosition - localReferenceOrigin;
            var centerDistance = planetRadiusMeters + orbitalAltitudeMeters;
            var relativeX = centerDirection.x * centerDistance - observerOffset.x;
            var relativeY = centerDirection.y * centerDistance - observerOffset.y;
            var relativeZ = centerDirection.z * centerDistance - observerOffset.z;
            var scaledRelative = new Vector3(
                (float)(relativeX * scaledSpaceFactor),
                (float)(relativeY * scaledSpaceFactor),
                (float)(relativeZ * scaledSpaceFactor));

            transform.position = observerPosition + scaledRelative;
            transform.localScale = Vector3.one * (float)(planetRadiusMeters * scaledSpaceFactor);
            RefreshRotation();
        }

        private void InitializeRotation()
        {
            baseLocalRotation = transform.localRotation;
            rotationEpochSeconds = Time.unscaledTimeAsDouble;
            rotationInitialized = true;
        }

        private void RefreshRotation()
        {
            if (!Application.isPlaying || !rotatePlanet || rotationTimeScale <= 0f)
            {
                return;
            }

            if (!rotationInitialized)
            {
                InitializeRotation();
            }

            var elapsedSeconds = Time.unscaledTimeAsDouble - rotationEpochSeconds;
            var turns = elapsedSeconds * rotationTimeScale / rotationPeriodSeconds;
            var spinDegrees = (float)(turns * 360.0 % 360.0);
            transform.localRotation = baseLocalRotation *
                Quaternion.AngleAxis(spinDegrees, Vector3.up);
        }

        private void ResolveObserver()
        {
            if (observer == null && Camera.main != null)
            {
                observer = Camera.main.transform;
            }
        }

        private static void SetLayerRecursively(Transform root, int layer)
        {
            root.gameObject.layer = layer;
            for (var index = 0; index < root.childCount; index++)
            {
                SetLayerRecursively(root.GetChild(index), layer);
            }
        }
    }
}
