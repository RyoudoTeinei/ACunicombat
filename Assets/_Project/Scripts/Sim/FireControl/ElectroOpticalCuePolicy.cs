using ACunicombat.Sensing.Contracts;
using Unity.Mathematics;

namespace ACunicombat.FireControl
{
    /// <summary>Selects the most urgent existing missile track for an EO follow cue.</summary>
    public static class ElectroOpticalCuePolicy
    {
        public static bool TrySelectThreat(
            MissileTrackSnapshot[] tracks,
            int trackCount,
            double3 observerPosition,
            double now,
            double maximumRangeMeters,
            out TrackId selectedTrack,
            out double3 selectedPosition)
        {
            selectedTrack = default;
            selectedPosition = default;
            if (tracks == null || maximumRangeMeters <= 0) return false;
            var bestTime = double.PositiveInfinity;
            var bestRange = double.PositiveInfinity;
            var count = math.min(trackCount, tracks.Length);
            for (var i = 0; i < count; i++)
            {
                var track = tracks[i];
                if (!track.TrackId.IsValid || track.IsFriendly || !track.Incoming ||
                    now < track.LastEvidenceTime || now - track.LastEvidenceTime > .65 ||
                    track.TimeToClosestApproachSeconds < 0 ||
                    track.ClosestApproachDistanceMeters >
                        math.max(500f, track.CpaUncertaintyMeters + 250f)) continue;
                var position = track.PredictPosition(now);
                var range = math.distance(position, observerPosition);
                if (range > maximumRangeMeters) continue;
                var time = (double)track.TimeToClosestApproachSeconds;
                if (time > bestTime + 1e-6 ||
                    (math.abs(time - bestTime) <= 1e-6 && range >= bestRange)) continue;
                bestTime = time;
                bestRange = range;
                selectedTrack = track.TrackId;
                selectedPosition = position;
            }
            return selectedTrack.IsValid;
        }
    }
}
