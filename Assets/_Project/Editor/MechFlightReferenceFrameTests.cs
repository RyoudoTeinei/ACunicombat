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
    }
}
