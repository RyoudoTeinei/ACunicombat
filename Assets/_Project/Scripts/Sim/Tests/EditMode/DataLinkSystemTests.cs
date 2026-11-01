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
        [Test] public void ARenderFrameReportCannotTransmitBeforeItsStateExists()
        {
            using var world=new SimWorld();using var link=new DataLinkSystem(world);
            var a=Terminal(1,world.Register(State(double3.zero)),0,100);var b=Terminal(2,world.Register(State(new double3(100,0,0))),0,100);
            world.SwapSnapshots(0);link.RegisterTerminal(a);link.RegisterTerminal(b);
            Assert.IsTrue(link.Send(Message(a.TerminalId,b.TerminalId,1)));
            var inbox=new LinkMessage[1];link.Tick(.98);Assert.That(link.CopyInbox(b.TerminalId,inbox),Is.Zero);
            link.Tick(1);Assert.That(link.CopyInbox(b.TerminalId,inbox),Is.EqualTo(1));Assert.That(inbox[0].TrackPayload.LastUpdateTime,Is.EqualTo(1));
            link.Tick(1.02);Assert.That(link.CopyInbox(b.TerminalId,inbox),Is.Zero,"The deferred message is delivered once.");
        }

        [Test] public void LowBandwidthCanAccumulateEnoughCreditForCombatReport()
        {
            using var world=new SimWorld();using var link=new DataLinkSystem(world);
            var a=Terminal(1,world.Register(State(double3.zero)),0,2);var b=Terminal(2,world.Register(State(new double3(100,0,0))),0,2);world.SwapSnapshots(0);link.RegisterTerminal(a);link.RegisterTerminal(b);
            var message=Message(a.TerminalId,b.TerminalId,0);message.Kind=LinkMessageKind.CombatBehaviorReport;
            Assert.That(link.Send(message),Is.False);message.SendTime=1;Assert.That(link.Send(message),Is.True);
        }

        [Test] public void CombatEvidenceUsesPhysicalLinkLatencyAndPreservesOriginalEvent()
        {
            using var world=new SimWorld();using var link=new DataLinkSystem(world);
            var a=Terminal(1,world.Register(State(double3.zero)),.01f,100);var b=Terminal(2,world.Register(State(new double3(100,0,0))),.01f,100);
            world.SwapSnapshots(0);link.RegisterTerminal(a);link.RegisterTerminal(b);
            var original=System.Guid.NewGuid();var message=Message(a.TerminalId,b.TerminalId,0);message.Kind=LinkMessageKind.CombatBehaviorReport;
            message.BehaviorPayload=new CombatBehaviorReport{OriginalId=original,Kind=CombatBehaviorKind.MissileAttack,ObservedAt=0,ExpiresAt=15,Quality=.95f,Association=.95f};
            Assert.That(link.Send(message),Is.True);link.Tick(0);var inbox=new LinkMessage[1];Assert.That(link.CopyInbox(b.TerminalId,inbox),Is.Zero);
            link.Tick(.03);Assert.That(link.CopyInbox(b.TerminalId,inbox),Is.EqualTo(1));Assert.That(inbox[0].BehaviorPayload.OriginalId,Is.EqualTo(original));Assert.That(inbox[0].BehaviorPayload.Kind,Is.EqualTo(CombatBehaviorKind.MissileAttack));
        }

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
            var inbox = new LinkMessage[1];
            Assert.That(link.CopyInbox(receiver.TerminalId, inbox), Is.EqualTo(1));
            Assert.That(inbox[0].DeliveryTime, Is.EqualTo(0.021).Within(0.0001));
            Assert.That(inbox[0].TrackPayload.LastUpdateTime, Is.EqualTo(0.0));
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

        [Test] public void SixteenMissilesReceiveSustainedGuidanceThroughPhysicalLink()
        {
            using var world=new SimWorld();using var link=new DataLinkSystem(world);
            var sender=Terminal(1,world.Register(State(double3.zero)),.005f,512);sender.MaxNetworkNodes=24;
            link.RegisterTerminal(sender);
            var receivers=new LinkTerminalProfile[16];
            for(int i=0;i<receivers.Length;i++)
            {
                receivers[i]=Terminal(i+2,world.Register(State(new double3(100+i,0,0))),.005f,512);
                receivers[i].MaxNetworkNodes=24;link.RegisterTerminal(receivers[i]);
            }
            var inbox=new LinkMessage[1];
            for(int update=0;update<20;update++)
            {
                double now=update*.25;world.SwapSnapshots(now);
                foreach(var receiver in receivers)
                {
                    var message=Message(sender.TerminalId,receiver.TerminalId,now);message.Kind=LinkMessageKind.GuidanceUpdate;
                    Assert.That(link.Send(message),Is.True);
                }
                link.Tick(now);link.Tick(now+.03);
                foreach(var receiver in receivers)
                {
                    Assert.That(link.CopyInbox(receiver.TerminalId,inbox),Is.EqualTo(1));
                    Assert.That(inbox[0].Kind,Is.EqualTo(LinkMessageKind.GuidanceUpdate));
                    Assert.That(inbox[0].TrackPayload.LastUpdateTime,Is.EqualTo(now));
                }
            }
        }

        [Test] public void FleetPayloadTraversesPhysicalLatencyWithOriginalTimestamp()
        {
            using var world=new SimWorld();using var link=new DataLinkSystem(world);
            var a=Terminal(1,world.Register(State(double3.zero)),.1f,100);var b=Terminal(2,world.Register(State(new double3(100,0,0))),.1f,100);
            world.SwapSnapshots(0);link.RegisterTerminal(a);link.RegisterTerminal(b);var message=Message(a.TerminalId,b.TerminalId,0);message.Kind=LinkMessageKind.FleetCoordination;
            var operation=System.Guid.NewGuid();message.FleetPayload=new FleetPacket{Kind=FleetPacketKind.Order,Operation=operation,Revision=7,ObservedAt=0,ExpiresAt=3,Target=new TrackSnapshot{LastUpdateTime=-1}};
            Assert.IsTrue(link.Send(message));link.Tick(0);var inbox=new LinkMessage[1];Assert.AreEqual(0,link.CopyInbox(b.TerminalId,inbox));link.Tick(.21);Assert.AreEqual(1,link.CopyInbox(b.TerminalId,inbox));Assert.AreEqual(operation,inbox[0].FleetPayload.Operation);Assert.AreEqual(-1,inbox[0].FleetPayload.Target.LastUpdateTime);
        }
        [Test] public void FleetMessageCannotBypassPhysicalRange()
        {
            using var world=new SimWorld();using var link=new DataLinkSystem(world);var a=Terminal(1,world.Register(State(double3.zero)),0,100);var b=Terminal(2,world.Register(State(new double3(200000,0,0))),0,100);world.SwapSnapshots(0);link.RegisterTerminal(a);link.RegisterTerminal(b);
            var message=Message(a.TerminalId,b.TerminalId,0);message.Kind=LinkMessageKind.FleetCoordination;Assert.IsTrue(link.Send(message));link.Tick(0);link.Tick(1);Assert.AreEqual(0,link.CopyInbox(b.TerminalId,new LinkMessage[2]));
        }
        [Test] public void FleetPacketPaysLargerBandwidthCost()
        {
            using var world=new SimWorld();using var link=new DataLinkSystem(world);var a=Terminal(1,world.Register(State(double3.zero)),0,2);var b=Terminal(2,world.Register(State(new double3(100,0,0))),0,2);world.SwapSnapshots(0);link.RegisterTerminal(a);link.RegisterTerminal(b);
            var message=Message(a.TerminalId,b.TerminalId,0);message.Kind=LinkMessageKind.FleetCoordination;message.FleetPayload=new FleetPacket{Kind=FleetPacketKind.Order};Assert.IsFalse(link.Send(message));message.SendTime=1;Assert.IsFalse(link.Send(message));message.SendTime=3;Assert.IsTrue(link.Send(message));Assert.IsFalse(link.Send(message));
        }

        [Test] public void EmissionReportTraversesPhysicalLinkWithoutGeometryLoss()
        {
            using var world=new SimWorld();using var link=new DataLinkSystem(world);
            var a=Terminal(1,world.Register(State(double3.zero)),.01f,100);var b=Terminal(2,world.Register(State(new double3(100,0,0))),.01f,100);
            world.SwapSnapshots(0);link.RegisterTerminal(a);link.RegisterTerminal(b);
            var message=Message(a.TerminalId,b.TerminalId,0);message.Kind=LinkMessageKind.EmissionReport;
            message.EmissionPayload=new EmissionReport{ObservedAt=0,ExpiresAt=1,Emission=new EmissionEvent{EmissionType=EmissionType.Jamming,Origin=new double3(25,0,0),Direction=new float3(0,0,1),BeamWidth=.3f,MaxRangeMeters=90000}};
            Assert.IsTrue(link.Send(message));link.Tick(0);link.Tick(.03);
            var inbox=new LinkMessage[1];Assert.AreEqual(1,link.CopyInbox(b.TerminalId,inbox));
            Assert.AreEqual(EmissionType.Jamming,inbox[0].EmissionPayload.Emission.EmissionType);
            Assert.AreEqual(.3f,inbox[0].EmissionPayload.Emission.BeamWidth,.0001);
            Assert.AreEqual(90000,inbox[0].EmissionPayload.Emission.MaxRangeMeters,.001);
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
                SendTime = time,
                TrackPayload = new TrackSnapshot
                {
                    LastUpdateTime = time,
                    LastPredictTime = time,
                },
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
