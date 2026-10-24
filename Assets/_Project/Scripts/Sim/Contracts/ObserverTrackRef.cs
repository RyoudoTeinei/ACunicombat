using System;
namespace ACunicombat.Sensing.Contracts
{
    /// <summary>Observer-local track identity. This is not a world entity or a perfect cross-platform association.</summary>
    public readonly struct ObserverTrackRef : IEquatable<ObserverTrackRef>
    {
        public readonly Guid ObserverScope;
        public readonly TrackId TrackId;
        public ObserverTrackRef(Guid observerScope,TrackId trackId){ObserverScope=observerScope;TrackId=trackId;}
        public bool IsValid=>ObserverScope!=Guid.Empty && TrackId.IsValid;
        public bool Equals(ObserverTrackRef other)=>ObserverScope==other.ObserverScope && TrackId.Equals(other.TrackId);
        public override bool Equals(object other)=>other is ObserverTrackRef reference && Equals(reference);
        public override int GetHashCode()=>unchecked(ObserverScope.GetHashCode()*397^TrackId.GetHashCode());
    }
}