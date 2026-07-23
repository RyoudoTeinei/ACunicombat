using ACunicombat.Sensing.Contracts;
using Unity.Mathematics;

namespace ACunicombat.FireControl
{
    public enum FireSolutionFailure : byte
    {
        None = 0,
        TrackTooOld = 1,
        CovarianceTooLarge = 2,
        OutOfWeaponEnvelope = 3,
        LifecycleBelowConfirmed = 4,
    }

    public struct FireSolution
    {
        public double3 InterceptPoint;
        public double InterceptTime;
        public float Quality;
        public bool WeaponReleaseAuthorized;
        public double3 MidcourseAimPoint;
        public double SeekerActivationTime;
        public float3 SearchBasketSemiAxes;
        public FireSolutionFailure Failure;
    }

    /// <summary>
    /// Track-only fire-control solver. It never reads simulation truth or scene target transforms.
    /// </summary>
    public static class FireSolutionSolver
    {
        public static FireSolution Solve(
            in TrackSnapshot track,
            double3 shooterPosition,
            double projectileSpeed,
            double requestTime)
        {
            var solution = default(FireSolution);
            if (track.Lifecycle < TrackLifecycle.Confirmed)
            {
                solution.Failure = FireSolutionFailure.LifecycleBelowConfirmed;
                return solution;
            }

            var trackAge = requestTime - track.LastUpdateTime;
            if (trackAge < 0.0 || trackAge > 3.0)
            {
                solution.Failure = FireSolutionFailure.TrackTooOld;
                return solution;
            }

            if (projectileSpeed <= 1.0)
            {
                solution.Failure = FireSolutionFailure.OutOfWeaponEnvelope;
                return solution;
            }

            var timeOfFlight = 0.0;
            for (var i = 0; i < 3; i++)
            {
                var predictionAge = trackAge + timeOfFlight;
                var predicted = track.Position + track.Velocity * predictionAge;
                timeOfFlight = math.length(predicted - shooterPosition) / projectileSpeed;
            }

            solution.InterceptTime = requestTime + timeOfFlight;
            solution.InterceptPoint = track.Position
                + track.Velocity * (solution.InterceptTime - track.LastUpdateTime);
            solution.MidcourseAimPoint = solution.InterceptPoint;

            var propagationSeconds = math.max(0.0, solution.InterceptTime - track.LastPredictTime);
            var largestSigma = 0.0;
            for (var axis = 0; axis < 3; axis++)
            {
                var positionVariance = track.GetCovariance(axis, axis);
                var crossVariance = track.GetCovariance(axis, axis + 3);
                var velocityVariance = track.GetCovariance(axis + 3, axis + 3);
                var propagatedVariance = math.max(
                    0.0,
                    positionVariance
                    + 2.0 * propagationSeconds * crossVariance
                    + propagationSeconds * propagationSeconds * velocityVariance);
                var sigma = math.sqrt(propagatedVariance);
                solution.SearchBasketSemiAxes[axis] = (float)(3.0 * sigma);
                largestSigma = math.max(largestSigma, sigma);
            }

            if (largestSigma > 5_000.0)
            {
                solution.Failure = FireSolutionFailure.CovarianceTooLarge;
                return solution;
            }

            var freshness = math.saturate(1f - (float)(trackAge / 3.0));
            var uncertainty = math.saturate(1f - (float)(largestSigma / 5_000.0));
            solution.Quality = math.saturate(track.Confidence * freshness * uncertainty);
            solution.SeekerActivationTime = math.max(requestTime, solution.InterceptTime - 2.0);
            solution.WeaponReleaseAuthorized = solution.Quality >= 0.15f;
            solution.Failure = solution.WeaponReleaseAuthorized
                ? FireSolutionFailure.None
                : FireSolutionFailure.CovarianceTooLarge;
            return solution;
        }
    }
}
