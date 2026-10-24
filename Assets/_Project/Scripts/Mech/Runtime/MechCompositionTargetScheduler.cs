using ACunicombat.Sensing.Contracts;

namespace ACunicombat.Mechs
{
    // Observer-local attention, separate from the camera's geometric escape solution.
    public sealed class MechCompositionTargetScheduler
    {
        public double CandidateDwellSeconds = .6, MinimumHoldSeconds = 1.5, LossGraceSeconds = .8;
        public TrackId CurrentId { get; private set; }
        public bool ProtectingLock { get; private set; }
        private TrackId pending;
        private double pendingSince, selectedAt, lastSeen, lastLock = double.NegativeInfinity;
        private TrackSnapshot retained;
        public void Reset()
        {CurrentId=default;pending=default;retained=default;ProtectingLock=false;lastLock=double.NegativeInfinity;}
        public bool Select(double now, in TrackSnapshot locked, in TrackSnapshot candidate,
            in TrackSnapshot fallback, in TrackSnapshot current, out TrackSnapshot selected)
        {
            if(locked.TrackId.IsValid)
            {
                Adopt(locked,now);lastLock=now;ProtectingLock=true;selected=retained;return true;
            }
            bool visible=current.TrackId.IsValid&&current.TrackId.Equals(CurrentId);
            if(visible){retained=current;lastSeen=now;}
            bool alive=CurrentId.IsValid&&(visible||now-lastSeen<LossGraceSeconds);
            ProtectingLock=alive&&now-lastLock<LossGraceSeconds;
            if(ProtectingLock){pending=default;selected=retained;return true;}
            // The ranking fallback only acquires attention when nothing is retained.
            var proposed=candidate.TrackId.IsValid?candidate:alive?retained:fallback;
            if(proposed.TrackId.IsValid&&proposed.TrackId.Equals(CurrentId))pending=default;
            else if(proposed.TrackId.IsValid)
            {
                if(!pending.Equals(proposed.TrackId)){pending=proposed.TrackId;pendingSince=now;}
                if(now-pendingSince>=CandidateDwellSeconds&&(!alive||now-selectedAt>=MinimumHoldSeconds))
                {Adopt(proposed,now);alive=true;}
            }
            else pending=default;
            if(!alive){CurrentId=default;retained=default;}
            selected=retained;return alive;
        }
        private void Adopt(in TrackSnapshot target,double now)
        {
            if(!CurrentId.Equals(target.TrackId))selectedAt=now;
            CurrentId=target.TrackId;retained=target;lastSeen=now;pending=default;
        }
    }
}
