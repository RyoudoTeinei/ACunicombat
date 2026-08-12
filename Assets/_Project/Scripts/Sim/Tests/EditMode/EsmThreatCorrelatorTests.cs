using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using ACunicombat.Tracking;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

namespace ACunicombat.Tests.EditMode
{
    public class EsmThreatCorrelatorTests
    {
        [Test]
        public void ContinuousMainlobeContacts_ProduceDedicatedIntentEvidence()
        {
            var receiverId = new SensorId(new Handle(1, 1));
            var correlator = new EsmThreatCorrelator();
            correlator.RegisterWaveformClass(
                1,
                2,
                EsmWaveformClass.AirToAirMissileSeeker);
            using var contacts =
                new NativeList<WarningObservation>(Allocator.Temp);

            for (var i = 0; i < 4; i++)
            {
                var time = 10.0 + i * 0.05;
                contacts.Clear();
                contacts.Add(Contact(
                    receiverId,
                    time,
                    isMainlobe: true,
                    receivedPowerDb: -20f + i));
                correlator.Process(contacts, receiverId, time);
            }

            var threats = new ThreatSnapshot[1];
            Assert.That(correlator.CopyThreats(threats), Is.EqualTo(1));
            Assert.That(threats[0].MainlobeContactCount, Is.EqualTo(4));
            Assert.That(
                threats[0].ContinuousMainlobeIlluminationSeconds,
                Is.EqualTo(0.15f).Within(0.001f));
            Assert.That(
                threats[0].MainlobePowerTrendDbPerSecond,
                Is.GreaterThan(0f));
            Assert.That(
                threats[0].LastMainlobeSeenTime,
                Is.EqualTo(10.15).Within(0.001));
        }

        [Test]
        public void SidelobeContacts_NeverCreateMainlobeIntentEvidence()
        {
            var receiverId = new SensorId(new Handle(1, 1));
            var correlator = new EsmThreatCorrelator();
            correlator.RegisterWaveformClass(
                1,
                2,
                EsmWaveformClass.AirToAirMissileSeeker);
            using var contacts =
                new NativeList<WarningObservation>(Allocator.Temp);

            for (var i = 0; i < 4; i++)
            {
                var time = 10.0 + i * 0.05;
                contacts.Clear();
                contacts.Add(Contact(
                    receiverId,
                    time,
                    isMainlobe: false,
                    receivedPowerDb: -35f + i));
                correlator.Process(contacts, receiverId, time);
            }

            var threats = new ThreatSnapshot[1];
            Assert.That(correlator.CopyThreats(threats), Is.EqualTo(1));
            Assert.That(
                threats[0].WaveformClass,
                Is.EqualTo(EsmWaveformClass.AirToAirMissileSeeker));
            Assert.That(threats[0].MainlobeContactCount, Is.Zero);
            Assert.That(threats[0].LastMainlobeSeenTime, Is.LessThan(0.0));
            Assert.That(
                threats[0].ContinuousMainlobeIlluminationSeconds,
                Is.Zero);
        }

        private static WarningObservation Contact(
            SensorId receiverId,
            double timestamp,
            bool isMainlobe,
            float receivedPowerDb)
        {
            return new WarningObservation
            {
                ReceiverId = receiverId,
                Timestamp = timestamp,
                Direction = new float3(0f, 0f, 1f),
                BearingVar = math.pow(math.radians(8f), 2f),
                FrequencyBand = 1,
                WaveformId = 2,
                WaveformConfidence = 1f,
                EmissionType = EmissionType.Seeker,
                IsMainlobe = isMainlobe,
                ReceivedPowerDb = receivedPowerDb,
            };
        }
    }
}
