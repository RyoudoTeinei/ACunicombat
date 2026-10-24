using System.Reflection;
using ACunicombat.Mechs;
using NUnit.Framework;
using UnityEngine;

namespace ACunicombat.Editor.Tests
{
    public sealed class MechFlightReferenceFrameTests
    {
        private GameObject mechObject;
        private MechFlightMotionController motion;

        [SetUp]
        public void SetUp()
        {
            mechObject = new GameObject("FlightReferenceFrameTest");
            motion = mechObject.AddComponent<MechFlightMotionController>();
            motion.ConfigureModelForwardYawOffset(0f);
        }

        private void AddPhysicalTranslationNozzles()
        {
            // Translation requires real installed nozzle channels; no hidden fallback thrust.
            motion.ConfigurePropulsionForceModel(false,false,0f);
            var channels=(System.Collections.Generic.List<MechThrusterChannelState>)typeof(MechFlightMotionController)
                .GetField("thrusterChannels",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(motion);
            var directions=new[]{Vector3.forward,Vector3.back,Vector3.right,Vector3.left};
            var roles=new[]{MechThrusterRole.Main,MechThrusterRole.Reverse,MechThrusterRole.StrafePositive,MechThrusterRole.StrafeNegative};
            for(var i=0;i<directions.Length;i++)
            {
                var anchor=new GameObject("ReferenceNozzle"+i).transform;anchor.SetParent(mechObject.transform,false);
                anchor.localRotation=Quaternion.LookRotation(-directions[i],Vector3.up);
                channels.Add(new MechThrusterChannelState("reference."+i,roles[i],MechThrusterSide.Center,anchor,Vector3.zero,false));
            }
        }
        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(mechObject);
        }

        [Test]
        public void ResetToRotation_UsesRequestedForwardAndUp()
        {
            var requested = Quaternion.Euler(32f, 71f, 0f);

            motion.ResetReferencePlaneToRotation(requested);

            Assert.That(
                Vector3.Angle(
                    motion.LogicalRotation * Vector3.forward,
                    requested * Vector3.forward),
                Is.LessThan(0.01f));
            Assert.That(
                Vector3.Angle(motion.ReferenceUp, requested * Vector3.up),
                Is.LessThan(0.01f));
            Assert.That(motion.LogicalPitchDegrees, Is.Zero.Within(0.001f));
        }

        [Test]
        public void ResetToward_PlacesTargetDirectionOnNewHorizon()
        {
            mechObject.transform.position = new Vector3(10f, -4f, 7f);
            var target = new Vector3(43f, 29f, 101f);
            var expectedDirection =
                (target - mechObject.transform.position).normalized;

            motion.ResetReferencePlaneToward(target);

            Assert.That(
                Vector3.Angle(
                    motion.LogicalRotation * Vector3.forward,
                    expectedDirection),
                Is.LessThan(0.01f));
            Assert.That(
                Mathf.Abs(Vector3.Dot(motion.ReferenceUp, expectedDirection)),
                Is.LessThan(0.0001f));
        }

        [Test]
        public void ResetToward_WithModelYawOffset_PreservesLogicalHeading()
        {
            motion.ConfigureModelForwardYawOffset(180f);
            mechObject.transform.position = new Vector3(0f, 11f, 150f);
            var target = Vector3.zero;
            var expectedDirection =
                (target - mechObject.transform.position).normalized;

            motion.ResetReferencePlaneToward(target);

            Assert.That(
                Vector3.Angle(
                    motion.LogicalRotation * Vector3.forward,
                    expectedDirection),
                Is.LessThan(0.01f));
            Assert.That(
                Vector3.Angle(
                    mechObject.transform.rotation * Vector3.back,
                    expectedDirection),
                Is.LessThan(0.01f));
        }

        [Test]
        public void ReferenceViewPitch_IsMeasuredFromDynamicHorizon()
        {
            motion.ResetReferencePlaneToRotation(
                Quaternion.Euler(-24f, 58f, 17f));

            var viewRotation = motion.ResolveReferenceViewRotation(35f);
            var elevation = Mathf.Asin(
                Vector3.Dot(viewRotation * Vector3.forward, motion.ReferenceUp)) *
                Mathf.Rad2Deg;

            Assert.That(elevation, Is.EqualTo(-35f).Within(0.01f));
        }

        [Test]
        public void ReferenceViewPitch_CanPassVerticalWithoutClamping()
        {
            motion.ResetReferencePlaneToRotation(Quaternion.identity);

            var viewRotation = motion.ResolveReferenceViewRotation(-120f);
            var expectedRotation = Quaternion.AngleAxis(-120f, Vector3.right);

            Assert.That(
                Quaternion.Angle(viewRotation, expectedRotation),
                Is.LessThan(0.01f));
        }

        [Test]
        public void AirbornePitchEnvelope_RemainsUnlimited()
        {
            Assert.That(
                motion.ConstrainPitchForCurrentMode(120f),
                Is.EqualTo(120f).Within(0.001f));
        }

        [Test]
        public void EngineAvailability_GatesAllPropulsionCommands()
        {
            AddPhysicalTranslationNozzles();
            motion.ConfigureGravity(0f);
            motion.SetEngineAvailable(false);
            var forward = new MechPlayerInputFrame { MoveLocal = Vector2.up };
            var initialPosition = mechObject.transform.position;

            motion.Tick(in forward, 0.1f);

            Assert.That(mechObject.transform.position, Is.EqualTo(initialPosition));
            Assert.That(motion.MainEngineOutput, Is.Zero.Within(0.0001f));
            Assert.That(motion.BeginQuickBoost(Vector2.up), Is.False);
            Assert.That(motion.ToggleAssaultBoost(), Is.False);

            motion.SetEngineAvailable(true);
            motion.Tick(in forward, 0.1f);

            Assert.That(motion.MainEngineOutput, Is.GreaterThan(0f));
            Assert.That(mechObject.transform.position.z, Is.GreaterThan(initialPosition.z));
        }

        [Test]
        public void ForwardThrust_PreservesExistingLateralVelocity()
        {
            AddPhysicalTranslationNozzles();
            motion.ConfigureGravity(0f);
            var strafe = new MechPlayerInputFrame { MoveLocal = Vector2.right };
            motion.Tick(in strafe, 0.1f);
            var lateralVelocity = motion.Velocity.x;
            Assert.That(lateralVelocity, Is.GreaterThan(0f));

            var forward = new MechPlayerInputFrame { MoveLocal = Vector2.up };
            motion.Tick(in forward, 0.1f);

            Assert.That(motion.Velocity.x, Is.EqualTo(lateralVelocity).Within(0.0001f));
            Assert.That(motion.Velocity.z, Is.GreaterThan(0f));
        }

        [Test]
        public void LateralThrust_PreservesExistingForwardVelocity()
        {
            AddPhysicalTranslationNozzles();
            motion.ConfigureGravity(0f);
            var forward = new MechPlayerInputFrame { MoveLocal = Vector2.up };
            motion.Tick(in forward, 0.1f);
            var forwardVelocity = motion.Velocity.z;
            Assert.That(forwardVelocity, Is.GreaterThan(0f));

            var strafe = new MechPlayerInputFrame { MoveLocal = Vector2.right };
            motion.Tick(in strafe, 0.1f);

            Assert.That(motion.Velocity.z, Is.EqualTo(forwardVelocity).Within(0.0001f));
            Assert.That(motion.Velocity.x, Is.GreaterThan(0f));
        }

        [Test]
        public void GroundedPitchEnvelope_PreventsPointingIntoSurface()
        {
            var groundedField = typeof(MechFlightMotionController).GetField(
                "isGrounded",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(groundedField, Is.Not.Null);
            groundedField.SetValue(motion, true);

            Assert.That(
                motion.ConstrainPitchForCurrentMode(25f),
                Is.Zero.Within(0.001f));
            Assert.That(
                motion.ConstrainPitchForCurrentMode(-120f),
                Is.EqualTo(-85f).Within(0.001f));
        }

        [Test]
        public void OrbitSync_MatchesTargetVelocityWithoutTeleporting()
        {
            var acceleration = MechOrbitSynchronizer.CalculateSynchronizationAcceleration(
                Vector3.zero,
                Vector3.zero,
                new Vector3(100f, 20f, 0f),
                new Vector3(80f, 0f, 0f),
                new Vector3(-100f, -20f, 0f),
                Vector3.right,
                0f,
                false,
                false,
                1f,
                0.35f,
                45f,
                30f);

            Assert.That(acceleration.x, Is.EqualTo(30f).Within(0.001f));
            Assert.That(acceleration.y, Is.Zero.Within(0.001f));
            Assert.That(acceleration.z, Is.Zero.Within(0.001f));
        }

        [Test]
        public void OrbitSync_LateralOverrideDoesNotFightPlayerTranslation()
        {
            var acceleration = MechOrbitSynchronizer.CalculateSynchronizationAcceleration(
                new Vector3(0f, 20f, 0f),
                new Vector3(40f, 15f, 0f),
                Vector3.zero,
                new Vector3(40f, 0f, 0f),
                Vector3.zero,
                Vector3.right,
                0f,
                true,
                false,
                1f,
                0.5f,
                45f,
                100f);

            Assert.That(acceleration.y, Is.Zero.Within(0.001f));
            Assert.That(acceleration.z, Is.Zero.Within(0.001f));
        }

        [Test]
        public void OrbitSync_LongitudinalBiasAllowsIntentionalRangeChange()
        {
            var acceleration = MechOrbitSynchronizer.CalculateSynchronizationAcceleration(
                Vector3.zero,
                new Vector3(120f, 0f, 0f),
                Vector3.zero,
                new Vector3(100f, 0f, 0f),
                Vector3.zero,
                Vector3.right,
                20f,
                false,
                false,
                1f,
                0.5f,
                45f,
                100f);

            Assert.That(acceleration, Is.EqualTo(Vector3.zero));
        }

        [Test]
        public void OrbitSync_LongitudinalOverrideDoesNotCounterThrottle()
        {
            var acceleration = MechOrbitSynchronizer.CalculateSynchronizationAcceleration(
                Vector3.zero,
                new Vector3(20f, 8f, 0f),
                Vector3.zero,
                new Vector3(100f, 0f, 0f),
                Vector3.zero,
                Vector3.right,
                0f,
                false,
                true,
                1f,
                0.5f,
                45f,
                100f);

            Assert.That(acceleration.x, Is.Zero.Within(0.001f));
            Assert.That(acceleration.y, Is.EqualTo(-8f).Within(0.001f));
        }

        [Test]
        public void OrbitSync_BrakesBeforeClosingSpeedExceedsStoppingDistance()
        {
            var acceleration = MechOrbitSynchronizer.CalculateSynchronizationAcceleration(
                Vector3.zero,
                new Vector3(30f, 0f, 0f),
                new Vector3(100f, 0f, 0f),
                Vector3.zero,
                Vector3.zero,
                Vector3.right,
                0f,
                false,
                false,
                1f,
                0.35f,
                45f,
                100f,
                positiveLongitudinalAcceleration: 10f,
                negativeLongitudinalAcceleration: 5f,
                brakingSafety: 0.78f,
                holdRadius: 3f);

            Assert.That(acceleration.x, Is.LessThan(0f));
            Assert.That(acceleration.y, Is.Zero.Within(0.001f));
            Assert.That(acceleration.z, Is.Zero.Within(0.001f));
        }

        [Test]
        public void OrbitSync_ParallelDirectionFallsBackWhenTargetIsStationary()
        {
            Assert.That(
                MechOrbitSynchronizer.ResolveParallelDirection(
                    Vector3.zero,
                    Vector3.up * 20f,
                    Vector3.forward),
                Is.EqualTo(Vector3.up));
            Assert.That(
                MechOrbitSynchronizer.ResolveParallelDirection(
                    Vector3.zero,
                    Vector3.zero,
                    Vector3.left * 4f),
                Is.EqualTo(Vector3.left));
        }
    }
}
