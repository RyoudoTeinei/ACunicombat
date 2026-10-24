using System;
using System.Collections.Generic;
using ACunicombat.DataLink;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Driver;
using ACunicombat.Simulation.World;
using ACunicombat.Simulation.Kinematics;
using ACunicombat.Simulation.Core;
using Unity.Mathematics;
using UnityEngine;
namespace ACunicombat.Mechs
{
    // Cooperative own-position reports pass through the observer's physical link transport.
    // Only authenticated same-team participants are registered here. The map never reads their transforms.
    public sealed class MechCooperativeTelemetry : MonoBehaviour
    {
        public struct Report { public string Name;public Guid Member;public TrackSnapshot Track; }
        private sealed class Peer { public CombatTestTarget Body;public MechRadarController Radar;public LinkTerminalId Terminal;public SceneEntityLease Lease;public Report Received;public double NextSend;public readonly System.Collections.Generic.HashSet<ulong> FleetReceived=new(); }
        private readonly List<Peer> peers=new List<Peer>();
        private readonly List<CombatTestTarget> bodies=new List<CombatTestTarget>();
        private readonly LinkMessage[] inbox=new LinkMessage[128];
        private readonly List<MechFleetOutbound> fleetPackets=new List<MechFleetOutbound>();
        private MechRadarController radar;private SimulationDriver driver;private float nextDiscovery;
        private readonly MechCooperativeTrackFusion fusion=new MechCooperativeTrackFusion();
        private readonly MechSharedTargetTracks sharedTargets=new();
        private readonly TrackSnapshot[] outgoingTargets=new TrackSnapshot[MechRadarController.MaximumPublicTracks];
        private readonly TrackSnapshot[] lookupTargets=new TrackSnapshot[MechRadarController.MaximumPublicTracks];
        private readonly TrackSnapshot[] publishedTargets=new TrackSnapshot[MechRadarController.MaximumPublicTracks];
        private int publishedCount,publishedReports=-1;
        private ulong publishedRevision=ulong.MaxValue;
        private double publishedTime=double.NaN;
        public bool TraceTargetSharing;
        private readonly List<string> targetShareTrace=new();
        public IReadOnlyList<string> TargetShareDiagnostics=>targetShareTrace;
        private void TraceTarget(string text){if(!TraceTargetSharing)return;if(targetShareTrace.Count>=32)targetShareTrace.RemoveAt(0);targetShareTrace.Add(text);}
        private readonly TrackSnapshot[] localTracks=new TrackSnapshot[MechRadarController.MaximumPublicTracks];
        private readonly List<TrackSnapshot> receivedReports=new List<TrackSnapshot>();
        private readonly List<CombatBehaviorReport> behaviorReports=new List<CombatBehaviorReport>();
        private double fusedTime=double.NaN;private ulong fusedRevision=ulong.MaxValue;private int reportRevision,fusedReportRevision=-1;
        public IReadOnlyList<string> AssociationDiagnostics=>fusion.Decisions;
        private void RefreshFusion()
        {
            radar??=GetComponent<MechRadarController>();if(radar==null)return;
            var context=radar.TelemetryDriver;double now=radar.SimTimeSeconds;ulong revision=context!=null?context.ObservationRevision:0;
            if(now==fusedTime&&revision==fusedRevision&&reportRevision==fusedReportRevision)return;
            receivedReports.Clear();foreach(var peer in peers)receivedReports.Add(peer.Received.Track);
            fusion.Update(localTracks,radar.CopyLocalSensorTracks(localTracks),receivedReports,now);
            fusedTime=now;fusedRevision=revision;fusedReportRevision=reportRevision;
        }
        // Legacy display stream: cooperative contacts now come through radar.CopyTracks exactly once.
        // Keep this adapter for the unchanged map. Diagnostics can request raw received reports explicitly.
        public int CopyReports(List<Report> output){output.Clear();return 0;}
        public bool TryGetCooperativeMember(ObserverTrackRef reference,out Guid member)
        {
            member=Guid.Empty;RefreshFusion();if(radar==null||reference.ObserverScope!=radar.ObserverScope||!reference.IsValid||!fusion.TryGetReportId(reference.TrackId,out var report))return false;
            foreach(var peer in peers)if(peer.Received.Track.TrackId.Equals(report)&&radar.SimTimeSeconds-peer.Received.Track.LastUpdateTime<=3){member=peer.Received.Member;return member!=Guid.Empty;}return false;
        }
        public int CopyReceivedReports(List<Report> output)
        {
            output.Clear();radar??=GetComponent<MechRadarController>();if(radar==null)return 0;
            foreach(var peer in peers)if(peer.Received.Track.TrackId.IsValid&&radar.SimTimeSeconds-peer.Received.Track.LastUpdateTime<=3)output.Add(peer.Received);
            return output.Count;
        }
        public int CopyFusedTracks(TrackSnapshot[] output)
        {
            RefreshFusion();if(radar==null)return 0;
            if(publishedTime!=fusedTime||publishedRevision!=fusedRevision||publishedReports!=reportRevision)
            {publishedCount=sharedTargets.Merge(publishedTargets,fusion.CopyTo(publishedTargets),radar.SimTimeSeconds);publishedTime=fusedTime;publishedRevision=fusedRevision;publishedReports=reportRevision;}
            int count=Math.Min(output.Length,publishedCount);Array.Copy(publishedTargets,output,count);return count;
        }
        public bool TryGetFusedTrack(TrackId id,out TrackSnapshot track)
        {var output=lookupTargets;int count=CopyFusedTracks(output);for(int i=0;i<count;i++)if(output[i].TrackId.Equals(id)){track=output[i];return true;}track=default;return false;}
        public bool IdentifiesFriendly(in TrackSnapshot track)=>TryGetFusedTrack(track.TrackId,out var fused)&&fused.Affiliation==TrackAffiliation.Friendly;
        private void LateUpdate()
        {
            radar??=GetComponent<MechRadarController>();if(radar?.TelemetryDriver==null)return;
            driver=radar.TelemetryDriver;var owner=GetComponent<CombatTestTarget>();if(owner==null)return;
            double now=radar.SimTimeSeconds;
            for(int i=peers.Count-1;i>=0;i--)
                if((peers[i].Body==null||!peers[i].Body.IsAlive)&&now-peers[i].Received.Track.LastUpdateTime>3)
                {driver.UnregisterLinkTerminal(peers[i].Terminal);driver.ReleaseSceneEntity(in peers[i].Lease);peers.RemoveAt(i);reportRevision++;}
            if(Time.unscaledTime>=nextDiscovery)
            {
                nextDiscovery=Time.unscaledTime+1;MechSensorScene.CopyBodies(bodies);
                foreach(var body in bodies)
                {
                    if(body==null||body==owner||body.TeamId!=owner.TeamId||!body.IsAlive||peers.Exists(p=>p.Body==body))continue;
                    var sender=body.GetComponent<MechRadarController>();if(sender?.TelemetryDriver==null||!driver.TryAllocateLinkTerminal(out var terminal))continue;
                    var lease=driver.RegisterSceneEntity(BattlefieldEntityIdentity.GetOrAdd(body.gameObject),State(body));
                    var profile=sender.TelemetryLink;
                    driver.RegisterLinkTerminal(new LinkTerminalProfile{TerminalId=terminal,OwnerEntityId=lease.Entity,NetworkId=0,
                        TransmitPowerDb=profile.transmitPowerDb,SidelobeLevelDb=profile.sidelobeLevelDb,BeamWidthRadians=2*Mathf.PI,
                        ReceiveSensitivityDb=profile.receiveSensitivityDb,MaxRangeMeters=profile.referenceCommunicationRangeKm*1000,
                        BandwidthKilobitsPerSecond=profile.bandwidthKbps,MaxNetworkNodes=profile.maximumNetworkNodes,
                        ProcessingLatencySeconds=profile.endToEndLatencyMs*.0005f,FrequencyBand=(ushort)profile.frequencyBand,WaveformId=(ushort)profile.waveformId});
                    peers.Add(new Peer{Body=body,Radar=sender,Terminal=terminal,Lease=lease});
                }
            }
            foreach(var p in peers)
            {
                if(p.Body==null||!p.Body.IsAlive||p.Radar==null||!p.Radar.DataLinkTransmitEnabled)continue;
                var damage=p.Body.GetComponent<MechPartDamageController>();if(damage!=null&&damage.RoleEfficiency(MechPartSlot.DataLinkTerminal)<=0)continue;
                var state=State(p.Body);var sourceMotion=p.Body.GetComponent<MechFlightMotionController>();
                double sampleTime=sourceMotion!=null&&sourceMotion.HasKinematicSample?sourceMotion.KinematicSampleTime:Time.timeAsDouble;
                double transmitTime=Math.Max(now,Time.timeAsDouble);
                driver.SetTimedEntityState(p.Lease.Entity,state,sampleTime);
                var sourceFleet=p.Body.GetComponent<MechFleetController>();var receiverFleet=GetComponent<MechFleetController>();
                if(sourceFleet!=null&&receiverFleet!=null&&sourceFleet.Configured&&receiverFleet.Configured)
                {
                    sourceFleet.CopyOutboundFor(receiverFleet.MemberId,now,fleetPackets);
                    foreach(var packet in fleetPackets)
                        if(driver.SendLinkMessage(new LinkMessage{Kind=LinkMessageKind.FleetCoordination,NetworkId=0,SourceTerminal=p.Terminal,TargetTerminal=radar.TelemetryTerminal,SendTime=transmitTime,FleetPayload=packet.Packet}))sourceFleet.MarkTransportSent(packet.Token,receiverFleet.MemberId);
                }
                if(now<p.NextSend)continue;p.NextSend=now+.25;
                var t=new TrackSnapshot{TrackId=new TrackId(new Handle(1000+p.Terminal.Value.Index,p.Terminal.Value.Generation)),
                    Position=state.PositionWorld,Velocity=state.VelocityWorld,LastUpdateTime=sampleTime,LastPredictTime=sampleTime,
                    Affiliation=TrackAffiliation.Friendly,IdentityConfidence=1,Confidence=1,ObjectClass=TrackObjectClass.AirVehicle,
                    IsIdentifiedMech=true,SourceMask=TrackSourceMask.DataLink,Lifecycle=TrackLifecycle.Confirmed};
                var loadout=p.Body.GetComponent<MechRuntimeState>()?.CurrentLoadout;
                if(loadout!=null){t.IdentifiedWeight=loadout.TacticalWeight;t.IdentifiedRole=loadout.TacticalRole;}
                t.SourceEvidence.Record(TrackSourceMask.DataLink,1,sampleTime);
                for(int i=0;i<3;i++){t.SetCovariance(i,i,4);t.SetCovariance(i+3,i+3,25);}
                driver.SendLinkMessage(new LinkMessage{Kind=LinkMessageKind.TrackShare,NetworkId=0,SourceTerminal=p.Terminal,
                    TargetTerminal=radar.TelemetryTerminal,SendTime=transmitTime,TrackPayload=t});
                var identity=p.Body.GetComponent<MechIdentityController>();
                // Share only the sender's own sensor observations; never retransmit a
                // received track as new local evidence and create a feedback loop.
                int observedCount=p.Radar.CopyLocalSensorTracks(outgoingTargets);
                for(int observed=0;observed<observedCount;observed++)
                {
                    var observedTrack=outgoingTargets[observed];
                    if(observedTrack.ObjectClass!=TrackObjectClass.AirVehicle||observedTrack.Lifecycle!=TrackLifecycle.Confirmed||now-observedTrack.LastUpdateTime>1)continue;
                    identity?.Apply(ref observedTrack);
                    if(observedTrack.Affiliation==TrackAffiliation.Friendly)continue;
                    Guid subject=Guid.Empty;identity?.TryGetCertifiedSubject(observedTrack.TrackId,out subject);
                    bool sent=driver.SendLinkMessage(new LinkMessage{AuthenticatedTrackSubject=subject,Kind=LinkMessageKind.TargetObservation,NetworkId=0,SourceTerminal=p.Terminal,
                        TargetTerminal=radar.TelemetryTerminal,SendTime=transmitTime,TrackPayload=observedTrack});
                    if(TraceTargetSharing)TraceTarget($"send accepted={sent} observed={observedTrack.LastUpdateTime:R} now={now:R} sent={transmitTime:R}");
                }
                if(identity!=null){identity.CopyBehaviorReports(behaviorReports);for(int reportIndex=Math.Max(0,behaviorReports.Count-4);reportIndex<behaviorReports.Count;reportIndex++)
                    driver.SendLinkMessage(new LinkMessage{Kind=LinkMessageKind.CombatBehaviorReport,NetworkId=0,SourceTerminal=p.Terminal,TargetTerminal=radar.TelemetryTerminal,SendTime=transmitTime,BehaviorPayload=behaviorReports[reportIndex]});}
            }
            var localDamage=GetComponent<MechPartDamageController>();
            if(localDamage!=null&&localDamage.RoleEfficiency(MechPartSlot.DataLinkTerminal)<=0)return;
            int count=driver.CopyLinkInbox(radar.TelemetryTerminal,inbox);
            for(int i=0;i<count;i++)
            {
                var message=inbox[i];
                var peer=peers.Find(p=>p.Terminal.Equals(message.SourceTerminal));if(peer==null)continue;
                if(message.Kind==LinkMessageKind.TargetObservation)
                {
                    var ownIdentity=GetComponent<MechIdentityController>();
                    bool authenticatedSelf=message.AuthenticatedTrackSubject!=Guid.Empty&&ownIdentity?.Credential!=null&&message.AuthenticatedTrackSubject==ownIdentity.Credential.Subject;
                    bool self=MechSharedTargetTracks.MatchesOwnNavigation(message.TrackPayload,(double3)(float3)owner.AimPoint.position,radar.GetStatusSnapshot().OwnshipVelocity,now,authenticatedSelf);
                    if(self){sharedTargets.Remove(message.SourceTerminal,message.TrackPayload.TrackId);reportRevision++;}
                    bool accepted=!self&&sharedTargets.Receive(message.SourceTerminal,message.TrackPayload,now);
                    if(accepted)reportRevision++;if(TraceTargetSharing)TraceTarget($"receive accepted={accepted} self={self} observed={message.TrackPayload.LastUpdateTime:R} now={now:R}");continue;
                }
                if(message.Kind==LinkMessageKind.FleetCoordination)
                {if(!peer.FleetReceived.Add(message.Sequence))continue;if(peer.FleetReceived.Count>512){peer.FleetReceived.Clear();peer.FleetReceived.Add(message.Sequence);}var authenticated=peer.Body!=null?peer.Body.GetComponent<MechFleetController>():null;if(authenticated!=null&&authenticated.MemberId==message.FleetPayload.Sender)GetComponent<MechFleetController>()?.Receive(message.FleetPayload);continue;}
                if(message.Kind==LinkMessageKind.CombatBehaviorReport){var sourceIdentity=peer.Body!=null?peer.Body.GetComponent<MechIdentityController>():null;
                    GetComponent<MechIdentityController>()?.ReceiveBehaviorReport(message.BehaviorPayload,sourceIdentity!=null&&sourceIdentity.CommandAuthority);continue;}
                if(message.Kind!=LinkMessageKind.TrackShare)continue;
                var expectedId=new TrackId(new Handle(1000+peer.Terminal.Value.Index,peer.Terminal.Value.Generation));
                if(!message.TrackPayload.TrackId.Equals(expectedId)||message.TrackPayload.Affiliation!=TrackAffiliation.Friendly||
                    message.TrackPayload.ObjectClass!=TrackObjectClass.AirVehicle)continue;
                if(!peer.Received.Track.TrackId.IsValid||message.TrackPayload.LastUpdateTime>peer.Received.Track.LastUpdateTime)
                {peer.Received=new Report{Member=peer.Radar!=null?(peer.Radar.GetComponent<MechFleetController>()?.MemberId??Guid.Empty):Guid.Empty,Name=peer.Body!=null?peer.Body.TargetId:peer.Received.Name,Track=message.TrackPayload};reportRevision++;}
            }
        }
        private static KinematicState State(CombatTestTarget body)
        {
            var motion=body.GetComponent<MechFlightMotionController>();
            return new KinematicState{PositionWorld=(double3)(float3)body.transform.position,VelocityWorld=(double3)(float3)(motion!=null?motion.Velocity:body.ProceduralVelocity),Rotation=quaternion.identity,Mass=1};
        }
        private void OnDestroy(){if(driver==null)return;foreach(var p in peers){driver.UnregisterLinkTerminal(p.Terminal);driver.ReleaseSceneEntity(in p.Lease);}}
    }
}
