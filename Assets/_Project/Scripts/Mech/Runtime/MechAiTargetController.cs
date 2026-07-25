using UnityEngine;

namespace ACunicombat.Mechs
{
    [DefaultExecutionOrder(-150)]
    [DisallowMultipleComponent]
    public sealed class MechAiTargetController : MonoBehaviour
    {
        [SerializeField] private CombatTestTarget objective;
        [SerializeField] private Camera sensorCamera;
        [SerializeField] private float desiredRangeMeters = 900f;
        [SerializeField] private float handWeaponIntervalSeconds = 1.4f;
        [SerializeField] private float missileIntervalSeconds = 7f;
        [SerializeField] private float lateralWeavePeriodSeconds = 5f;
        [SerializeField] private Color identificationTint =
            new Color(0.55f, 0.12f, 0.08f, 1f);

        private MechPlayerInputAdapter input;
        private MechFlightMotionController motion;
        private MechRadarController radar;
        private MechTestWeaponSystem weapons;
        private float nextHandWeaponTime;
        private float nextMissileTime;
        private bool fireCannonNext;
        private bool fireLeftMissileNext;

        public void Configure(CombatTestTarget newObjective, Camera newSensorCamera)
        {
            objective = newObjective;
            sensorCamera = newSensorCamera;
        }

        private void Awake()
        {
            input = GetComponent<MechPlayerInputAdapter>();
            motion = GetComponent<MechFlightMotionController>();
            radar = GetComponent<MechRadarController>();
            weapons = GetComponent<MechTestWeaponSystem>();
            input?.SetExternalControl(true);
            radar?.SetTrackOperationMode(true);
            nextHandWeaponTime = Time.time + Random.Range(0.8f, 1.8f);
            nextMissileTime = Time.time + Random.Range(3f, 6f);
        }

        private void Start() => ApplyIdentificationTint();

        private void Update()
        {
            if (input == null || motion == null || objective == null ||
                !objective.IsAlive)
            {
                return;
            }

            var aimPoint = objective.AimPoint.position;
            var toTarget = aimPoint - transform.position;
            var range = toTarget.magnitude;
            if (range <= 0.01f)
            {
                return;
            }

            var direction = toTarget / range;
            UpdateSensorCamera(direction);
            CommandAttitude(direction);

            var frame = BuildFlightFrame(range);
            input.SubmitExternalFrame(in frame);

            if (radar != null && sensorCamera != null)
            {
                radar.UpdateDesignation(
                    sensorCamera,
                    new Vector2(0.5f, 0.5f),
                    Time.deltaTime);
            }

            if (weapons == null || radar == null || !radar.HasStt)
            {
                return;
            }

            if (Time.time >= nextHandWeaponTime && range <= 5000f)
            {
                weapons.CommandFireHandWeapon(
                    fireCannonNext
                        ? TestWeaponKind.Cannon
                        : TestWeaponKind.Rifle);
                fireCannonNext = !fireCannonNext;
                nextHandWeaponTime =
                    Time.time + handWeaponIntervalSeconds;
            }

            if (Time.time >= nextMissileTime && range <= 30000f)
            {
                weapons.CommandFireMissile(
                    fireLeftMissileNext
                        ? MechPartSlot.LeftBackWeapon
                        : MechPartSlot.RightBackWeapon);
                fireLeftMissileNext = !fireLeftMissileNext;
                nextMissileTime = Time.time + missileIntervalSeconds;
            }
        }

        private MechPlayerInputFrame BuildFlightFrame(float range)
        {
            var rangeError =
                (range - desiredRangeMeters) /
                Mathf.Max(100f, desiredRangeMeters);
            var weave = Mathf.Sin(
                Time.time * Mathf.PI * 2f /
                Mathf.Max(1f, lateralWeavePeriodSeconds));
            return new MechPlayerInputFrame
            {
                MoveLocal = new Vector2(weave * 0.32f, 1f),
                MainThrottle = Mathf.Clamp01(0.38f + rangeError * 0.32f),
                BrakeHeld = range < desiredRangeMeters * 0.58f,
                FlightEnvelopeOverrideHeld = true,
            };
        }

        private void CommandAttitude(Vector3 direction)
        {
            var referenceUp = motion.ReferenceUp;
            var flatDirection =
                Vector3.ProjectOnPlane(direction, referenceUp);
            if (flatDirection.sqrMagnitude > 0.0001f)
            {
                var currentForward = Vector3.ProjectOnPlane(
                    motion.LogicalRotation * Vector3.forward,
                    referenceUp);
                var yawError = Vector3.SignedAngle(
                    currentForward,
                    flatDirection,
                    referenceUp);
                motion.SetYawTurnInput(Mathf.Clamp(yawError / 35f, -1f, 1f));
            }

            var elevation = Mathf.Asin(
                Mathf.Clamp(Vector3.Dot(direction, referenceUp), -1f, 1f)) *
                Mathf.Rad2Deg;
            motion.SetAimPitch(elevation);
        }

        private void UpdateSensorCamera(Vector3 direction)
        {
            if (sensorCamera == null)
            {
                return;
            }

            sensorCamera.transform.position =
                transform.position + motion.ReferenceUp * 6f;
            sensorCamera.transform.rotation = Quaternion.LookRotation(
                direction,
                motion.ReferenceUp);
        }

        private void ApplyIdentificationTint()
        {
            var block = new MaterialPropertyBlock();
            var renderers = GetComponentsInChildren<Renderer>(true);
            for (var i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer == null)
                {
                    continue;
                }

                renderer.GetPropertyBlock(block);
                block.SetColor("_PaintColor", identificationTint);
                block.SetFloat("_PaintInfluence", 0.34f);
                renderer.SetPropertyBlock(block);
            }
        }
    }
}
