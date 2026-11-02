using Unity.Burst;
using Unity.Mathematics;

namespace ACunicombat.Tracking
{
    /// <summary>
    /// Burst entry points for the hottest filter kernels. The bodies are the managed Cmkf
    /// code compiled by Burst (strict float mode, no fused multiply-add reassociation), so
    /// decisions match the managed path; set Enabled=false to compare.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
    public static unsafe class CmkfBurst
    {
        public static bool Enabled = true;
        /// <summary>Diagnostics: score cooperative pairs with the managed kernel while the rest stays on Burst.</summary>
        public static bool PairScoresEnabled = true;

        /// <summary>
        /// Gated position+velocity NIS for every (report j, track i) pair:
        /// scores[j*trackCount+i]. Ineligible tracks or unhealthy reports score +inf.
        /// Same arithmetic as scoring each pair through Cmkf.TryCalculatePositionInnovationNis.
        /// </summary>
        public static void PairNisScores(FilterState[] tracks, bool[] trackEligible, int trackCount,
            FilterState[] reports, bool[] reportHealthy, int reportCount, double gate, double[] scores)
        {
            if (trackCount <= 0 || reportCount <= 0) return;
            fixed (FilterState* t = tracks) fixed (bool* te = trackEligible) fixed (FilterState* r = reports)
            fixed (bool* rh = reportHealthy) fixed (double* output = scores)
            {
                if (Enabled && PairScoresEnabled) PairNisScoresBurst(t, te, trackCount, r, rh, reportCount, gate, output);
                else PairNisScoresManaged(t, te, trackCount, r, rh, reportCount, gate, output);
            }
        }

        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
        private static void PairNisScoresBurst(FilterState* tracks, bool* trackEligible, int trackCount,
            FilterState* reports, bool* reportHealthy, int reportCount, double gate, double* scores)
            => PairNisScoresManaged(tracks, trackEligible, trackCount, reports, reportHealthy, reportCount, gate, scores);

        private static void PairNisScoresManaged(FilterState* tracks, bool* trackEligible, int trackCount,
            FilterState* reports, bool* reportHealthy, int reportCount, double gate, double* scores)
        {
            for (int j = 0; j < reportCount; j++)
                for (int i = 0; i < trackCount; i++)
                    scores[j * trackCount + i] = reportHealthy[j] && trackEligible[i]
                        ? PairNis(in tracks[i], in reports[j], gate) : double.PositiveInfinity;
        }

        private static double PairNis(in FilterState a, in FilterState b, double gate)
        {
            if (!Cmkf.TryCalculatePositionInnovationNis(a, b.Position, b.P, out double positionNis)) return double.PositiveInfinity;
            var av = new FilterState { Position = a.Velocity }; var bv = new Matrix6();
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) { av.P[i, j] = a.P[i + 3, j + 3]; bv[i, j] = b.P[i + 3, j + 3]; }
            if (!Cmkf.TryCalculatePositionInnovationNis(av, b.Velocity, bv, out double velocityNis)) return double.PositiveInfinity;
            double sum = positionNis + velocityNis; return sum <= gate ? sum : double.PositiveInfinity;
        }

        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
        public static bool TryUpdateRadarMeasurement(ref FilterState state, in double3 observer, in double3 observerVelocity,
            float range, in float3 direction, float rangeVariance, float angleVariance, float radialVelocity, float velocityVariance)
            => Cmkf.TryUpdateRadarMeasurementManaged(ref state, observer, observerVelocity, range, direction,
                rangeVariance, angleVariance, radialVelocity, velocityVariance);

        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
        public static bool TryUpdateRadarPosition(ref FilterState state, in double3 observer, float range, in float3 direction,
            float rangeVariance, float angleVariance)
            => Cmkf.TryUpdateRadarPositionManaged(ref state, observer, range, direction, rangeVariance, angleVariance);

        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
        public static bool TryUpdatePosition(ref FilterState state, in double3 z, in Matrix6 r3x3)
            => Cmkf.TryUpdatePositionManaged(ref state, z, r3x3);

        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
        public static bool TryUpdateRadarDoppler(ref FilterState state, in double3 observerPosition, in double3 observerVelocity,
            float measured, float variance)
            => Cmkf.TryUpdateRadarDopplerManaged(ref state, observerPosition, observerVelocity, measured, variance);

        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
        public static void UpdateRadialVelocity(ref FilterState state, in float3 direction, float measured, float variance)
            => Cmkf.UpdateRadialVelocityManaged(ref state, direction, measured, variance);

        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
        public static void PredictCv(ref FilterState state, double dt, double qPosition, double qVelocity, bool integrateAccelerationNoise)
            => Cmkf.PredictCvManaged(ref state, dt, qPosition, qVelocity, integrateAccelerationNoise);
    }
}
