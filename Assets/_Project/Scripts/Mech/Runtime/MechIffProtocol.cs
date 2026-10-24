using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using ACunicombat.Sensing.Contracts;
using Unity.Mathematics;
namespace ACunicombat.Mechs
{
    public enum IffReplyPolicy { TrustedOnly, AuthorizedDomains, Silent, Off }
    public sealed class IffCredential
    {
        public readonly Guid Subject; public readonly string Domain; public readonly double Expires;
        internal readonly byte[] Secret; public bool Revoked;
        public IffCredential(Guid subject,string domain,double expires,byte[] secret){Subject=subject;Domain=domain;Expires=expires;Secret=(byte[])secret.Clone();}
    }
    public sealed class IffChallenge {public Guid Transaction,Nonce,Requester;public string Domain;public double Sent,Deadline;public byte[] Proof;}
    public sealed class IffReply {public Guid Transaction,Nonce,Requester,Subject;public string Domain;public double Sent,Expires;public double3 Position,Velocity;public byte[] Proof;}
    // Simulation certificate authority. Issuance is mission setup; verification never consults faction/team truth.
    public sealed class IffAuthority
    {
        private readonly Dictionary<Guid,IffCredential> credentials=new Dictionary<Guid,IffCredential>();
        public void Register(IffCredential credential)=>credentials[credential.Subject]=credential;
        public void Remove(Guid subject)=>credentials.Remove(subject);
        public static byte[] NewSecret(){var b=new byte[32];using(var rng=RandomNumberGenerator.Create())rng.GetBytes(b);return b;}
        private static byte[] Bytes(Action<BinaryWriter> write){using(var stream=new MemoryStream()){using(var w=new BinaryWriter(stream,System.Text.Encoding.UTF8,true))write(w);return stream.ToArray();}}
        private static byte[] Data(IffChallenge q)=>Bytes(w=>{w.Write(q.Transaction.ToByteArray());w.Write(q.Nonce.ToByteArray());w.Write(q.Requester.ToByteArray());w.Write(q.Domain??"");w.Write(q.Sent);w.Write(q.Deadline);});
        private static byte[] Data(IffReply r)=>Bytes(w=>{w.Write(r.Transaction.ToByteArray());w.Write(r.Nonce.ToByteArray());w.Write(r.Requester.ToByteArray());w.Write(r.Subject.ToByteArray());w.Write(r.Domain??"");w.Write(r.Sent);w.Write(r.Expires);for(int i=0;i<3;i++){w.Write(r.Position[i]);w.Write(r.Velocity[i]);}});
        private static byte[] Sign(byte[] key,byte[] data){using(var mac=new HMACSHA256(key))return mac.ComputeHash(data);}
        private static bool Equal(byte[] a,byte[] b){if(a==null||b==null||a.Length!=b.Length)return false;int diff=0;for(int i=0;i<a.Length;i++)diff|=a[i]^b[i];return diff==0;}
        public IffChallenge Challenge(IffCredential c,double now,double timeout){var q=new IffChallenge{Transaction=Guid.NewGuid(),Nonce=Guid.NewGuid(),Requester=c.Subject,Domain=c.Domain,Sent=now,Deadline=now+timeout};q.Proof=Sign(c.Secret,Data(q));return q;}
        public IffReply Reply(IffCredential c,IffChallenge q,double now,double3 position,double3 velocity){var r=new IffReply{Transaction=q.Transaction,Nonce=q.Nonce,Requester=q.Requester,Subject=c.Subject,Domain=c.Domain,Sent=now,Expires=c.Expires,Position=position,Velocity=velocity};r.Proof=Sign(c.Secret,Data(r));return r;}
        public bool VerifyChallenge(IffChallenge q,double now)=>q!=null&&double.IsFinite(q.Sent)&&double.IsFinite(q.Deadline)&&q.Sent<=now&&now<=q.Deadline&&credentials.TryGetValue(q.Requester,out var c)&&!c.Revoked&&now<=c.Expires&&c.Domain==q.Domain&&Equal(q.Proof,Sign(c.Secret,Data(q)));
        public IffOutcome VerifyReply(IffReply r, IffChallenge q,Guid expectedRequester,double now)
        {
            if(r==null||r.Proof==null||r.Proof.Length!=32||!math.all(math.isfinite(r.Position))||!math.all(math.isfinite(r.Velocity))||!double.IsFinite(r.Sent)||!double.IsFinite(r.Expires))return IffOutcome.Corrupt;
            if(r.Transaction!=q.Transaction||r.Nonce!=q.Nonce||r.Requester!=expectedRequester)return IffOutcome.Replay;
            if(now>q.Deadline||now>r.Expires||r.Sent>now||r.Sent<q.Sent)return IffOutcome.Expired;
            if(!credentials.TryGetValue(r.Subject,out var c))return IffOutcome.Untrusted;
            if(c.Revoked||now>c.Expires||r.Domain!=c.Domain||!Equal(r.Proof,Sign(c.Secret,Data(r))))return IffOutcome.InvalidProof;
            return IffOutcome.Authenticated;
        }
    }
    public sealed class MechIffProtocol
    {
        private sealed class Pending {public TrackId Track;public IffChallenge Query;public readonly HashSet<Guid> Responders=new HashSet<Guid>();public bool AnyReply,SawReply;}
        private readonly Dictionary<Guid,Pending> pending=new Dictionary<Guid,Pending>();
        private readonly Dictionary<TrackId,int> attempts=new Dictionary<TrackId,int>();
        private readonly Dictionary<TrackId,double> next=new Dictionary<TrackId,double>();
        private readonly List<Guid> expired=new List<Guid>();
        public int Capacity=4,MaximumAttempts=3;public double Cooldown=5,ProcessingBudget=.25,DistanceUncertaintyMetres=5000;
        public readonly IffAuthority Authority;public readonly IffCredential Credential;
        public event Action<TrackId,IffOutcome,IffReply> Result;
        public int PendingCount=>pending.Count;
        public Guid LastTransaction {get;private set;}
        public MechIffProtocol(IffAuthority authority,IffCredential credential){Authority=authority;Credential=credential;}
        public static double TimeoutSeconds(double range,double uncertainty,double processing)=>2*Math.Max(0,range+uncertainty)/299792458.0+Math.Max(.1,processing)+.25;
        public IffChallenge Begin(TrackId track,double estimatedRange,double now)
        {
            if(!track.IsValid||!double.IsFinite(now)||!double.IsFinite(estimatedRange)||estimatedRange<0)return null;
            foreach(var p in pending.Values)if(p.Track.Equals(track))return null;
            if(next.TryGetValue(track,out var when)&&now<when)return null;
            if(pending.Count>=Capacity){LastTransaction=Guid.Empty;Result?.Invoke(track,IffOutcome.CapacityLimited,null);return null;}
            attempts.TryGetValue(track,out int count);
            if(count>=MaximumAttempts){attempts[track]=0;count=0;}
            var q=Authority.Challenge(Credential,now,TimeoutSeconds(estimatedRange,DistanceUncertaintyMetres,ProcessingBudget));pending.Add(q.Transaction,new Pending{Track=track,Query=q});attempts[track]=count+1;
            LastTransaction=q.Transaction;Result?.Invoke(track,IffOutcome.Waiting,null);return q;
        }
        public IffOutcome Receive(IffReply reply,double now)
        {
            if(reply==null||!pending.TryGetValue(reply.Transaction,out var p))return IffOutcome.Replay;
            var outcome=Authority.VerifyReply(reply,p.Query,Credential.Subject,now);
            if(outcome==IffOutcome.Authenticated&&!p.Responders.Add(reply.Subject))outcome=IffOutcome.Replay;
            if(outcome==IffOutcome.Authenticated)p.AnyReply=true;
            LastTransaction=reply.Transaction;p.SawReply=true;Result?.Invoke(p.Track,outcome,reply);return outcome;
        }
        public void Tick(double now)
        {
            expired.Clear();foreach(var pair in pending)if(now>pair.Value.Query.Deadline)expired.Add(pair.Key);
            foreach(var id in expired){LastTransaction=id;var p=pending[id];pending.Remove(id);if(!p.SawReply)Result?.Invoke(p.Track,IffOutcome.NoResponse,null);int n=attempts[p.Track];next[p.Track]=now+(p.AnyReply?15:n>=MaximumAttempts?60:Cooldown*Math.Pow(2,n-1));}
        }
        public void CancelMissing(HashSet<TrackId> active,double now)
        {
            expired.Clear();foreach(var pair in pending)if(!active.Contains(pair.Value.Track))expired.Add(pair.Key);
            foreach(var id in expired){LastTransaction=id;var p=pending[id];Result?.Invoke(p.Track,IffOutcome.Cancelled,null);pending.Remove(id);}
            var remove=new List<TrackId>();foreach(var id in attempts.Keys)if(!active.Contains(id))remove.Add(id);foreach(var id in remove){attempts.Remove(id);next.Remove(id);}
        }
    }
}
