using ACunicombat.DataLink;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using ACunicombat.Simulation.Kinematics;
using ACunicombat.Simulation.World;
using NUnit.Framework;
using Unity.Mathematics;

namespace ACunicombat.Tests.EditMode
{
    public class DataLinkSystemTests
    {
        [Test]
        public void ProcessingLatency_DelaysDeliveryUntilAvailableTime()
        {
            using var world = new SimWorld();
            using var link = new DataLinkSystem(world);
            var senderEntity = world.Register(State(double3.zero));
            var receiverEntity = world.Register(State(new double3(100.0, 0.0, 0.0)));
            world.SwapSnapshots(0.0);
            var sender = Terminal(1, senderEntity, 0.01f, 100f);
            var receiver = Terminal(2, receiverEntity, 0.01f, 100f);
            link.RegisterTerminal(sender);
            link.RegisterTerminal(receiver);

            Assert.That(link.Send(Message(sender.TerminalId, receiver.TerminalId, 0.0)), Is.True);
            link.Tick(0.0);
            Assert.That(link.CopyInbox(receiver.TerminalId, new LinkMessage[1]), Is.Zero);
            link.Tick(0.019);
            Assert.That(link.CopyInbox(receiver.TerminalId, new LinkMessage[1]), Is.Zero);
            link.Tick(0.021);
            Assert.That(link.CopyInbox(receiver.TerminalId, new LinkMessage[1]), Is.EqualTo(1));
        }

        [Test]
        public void BandwidthBudget_RejectsBurstBeyondConfiguredRate()
        {
            using var world = new SimWorld();
            using var link = new DataLinkSystem(world);
            var senderEntity = world.Register(State(double3.zero));
            var receiverEntity = world.Register(State(new double3(100.0, 0.0, 0.0)));
            world.SwapSnapshots(0.0);
            var sender = Terminal(1, senderEntity, 0f, 2f);
            var receiver = Terminal(2, receiverEntity, 0f, 2f);
            link.RegisterTerminal(sender);
            link.RegisterTerminal(receiver);

            Assert.That(link.Send(Message(sender.TerminalId, receiver.TerminalId, 0.0)), Is.True);
            Assert.That(link.Send(Message(sender.TerminalId, receiver.TerminalId, 0.0)), Is.False);
            Assert.That(link.Send(Message(sender.TerminalId, receiver.TerminalId, 1.0)), Is.True);
        }

        private static LinkTerminalProfile Terminal(
            int index,
            SimEntityId owner,
            float processingLatencySeconds,
            float bandwidthKbps)
        {
            return new LinkTerminalProfile
            {
                TerminalId = new LinkTerminalId(new Handle(index, 1)),
                OwnerEntityId = owner,
                EmitterId = new EmitterId(new Handle(index, 1)),
                NetworkId = 0,
                TransmitPowerDb = 100f,
                SidelobeLevelDb = -30f,
                BeamWidthRadians = 2f * math.PI,
                ReceiveSensitivityDb = -100f,
                MaxRangeMeters = 10_000.0,
                BandwidthKilobitsPerSecond = bandwidthKbps,
                MaxNetworkNodes = 8,
                ProcessingLatencySeconds = processingLatencySeconds,
                FrequencyBand = 2,
                WaveformId = 10
            };
        }

        private static LinkMessage Message(LinkTerminalId source, LinkTerminalId target, double time)
        {
            return new LinkMessage
            {
                Kind = LinkMessageKind.TrackShare,
                NetworkId = 0,
                SourceTerminal = source,
                TargetTerminal = target,
                SendTime = time
            };
        }

        private static KinematicState State(double3 position)
        {
            return new KinematicState
            {
                PositionWorld = position,
                Rotation = quaternion.identity,
                Mass = 1.0
            };
        }
    }
}
