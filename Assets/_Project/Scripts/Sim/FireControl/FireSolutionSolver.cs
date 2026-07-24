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

    public struct MissileKinematicProfile
    {
        public double EjectionSpeed;
        public double BoostAcceleration;
        public double MotorBurnDuration;

        public double BurnoutSpeed =>
            EjectionSpeed +
            BoostAcceleration * MotorBurnDuration;

        public double RelativeTravelDistance(double flightTime)
        {
            var time = math.max(0.0, flightTime);
            var burnTime = math.min(time, MotorBurnDuration);
            var distance =
                EjectionSpeed * burnTime +
                0.5 * BoostAcceleration * burnTime * burnTime;
            if (time > MotorBurnDuration)
            {
                distance +=
                    BurnoutSpeed * (time - MotorBurnDuration);
            }

            return distance;
        }

        public bool IsValid =>
            EjectionSpeed >= 0.0 &&
            BoostAcceleration > 0.0 &&
            MotorBurnDuration > 0.0 &&
            BurnoutSpeed > 1.0;
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

            var currentPosition = track.PredictPosition(requestTime);
            var timeOfFlight = 0.0;
            for (var i = 0; i < 3; i++)
            {
                var predicted =
                    currentPosition + track.Velocity * timeOfFlight;
                timeOfFlight = math.length(predicted - shooterPosition) / projectileSpeed;
            }

            solution.InterceptTime = requestTime + timeOfFlight;
            solution.InterceptPoint =
                currentPosition + track.Velocity * timeOfFlight;
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

        public static FireSolution SolveMissile(
            in TrackSnapshot track,
            double3 shooterPosition,
            double3 shooterVelocity,
            in MissileKinematicProfile kinematics,
            double requestTime,
            double maximumFlightTime = 300.0)
        {
            var solution = default(FireSolution);
            if (track.Lifecycle < TrackLifecycle.Confirmed)
            {
                solution.Failure =
                    FireSolutionFailure.LifecycleBelowConfirmed;
                return solution;
            }

            var trackAge = requestTime - track.LastUpdateTime;
            if (trackAge < 0.0 || trackAge > 3.0)
            {
                solution.Failure = FireSolutionFailure.TrackTooOld;
                return solution;
            }

            if (!kinematics.IsValid || maximumFlightTime <= 0.0)
            {
                solution.Failure =
                    FireSolutionFailure.OutOfWeaponEnvelope;
                return solution;
            }

            var currentTargetPosition =
                track.PredictPosition(requestTime);
            var initialRelativePosition =
                currentTargetPosition - shooterPosition;
            var relativeVelocity =
                track.Velocity - shooterVelocity;

            var low = 0.0;
            var high = 0.05;
            var bracketed = ReachabilityGap(
                initialRelativePosition,
                relativeVelocity,
                kinematics,
                high) <= 0.0;
            while (!bracketed && high < maximumFlightTime)
            {
                low = high;
                high = math.min(maximumFlightTime, high * 2.0);
                bracketed = ReachabilityGap(
                    initialRelativePosition,
                    relativeVelocity,
                    kinematics,
                    high) <= 0.0;
            }

            if (!bracketed)
            {
                solution.Failure =
                    FireSolutionFailure.OutOfWeaponEnvelope;
                return solution;
            }

            for (var iteration = 0; iteration < 48; iteration++)
            {
                var middle = (low + high) * 0.5;
                if (ReachabilityGap(
                        initialRelativePosition,
                        relativeVelocity,
                        kinematics,
                        middle) > 0.0)
                {
                    low = middle;
                }
                else
                {
                    high = middle;
                }
            }

            var timeOfFlight = high;
            solution.InterceptTime = requestTime + timeOfFlight;
            solution.InterceptPoint =
                currentTargetPosition +
                track.Velocity * timeOfFlight;
            solution.MidcourseAimPoint = solution.InterceptPoint;

            var propagationSeconds = math.max(
                0.0,
                solution.InterceptTime - track.LastPredictTime);
            var largestSigma = 0.0;
            for (var axis = 0; axis < 3; axis++)
            {
                var positionVariance =
                    track.GetCovariance(axis, axis);
                var crossVariance =
                    track.GetCovariance(axis, axis + 3);
                var velocityVariance =
                    track.GetCovariance(axis + 3, axis + 3);
                var propagatedVariance = math.max(
                    0.0,
                    positionVariance +
                    2.0 * propagationSeconds * crossVariance +
                    propagationSeconds * propagationSeconds *
                    velocityVariance);
                var sigma = math.sqrt(propagatedVariance);
                solution.SearchBasketSemiAxes[axis] =
                    (float)(3.0 * sigma);
                largestSigma = math.max(largestSigma, sigma);
            }

            if (largestSigma > 5_000.0)
            {
                solution.Failure =
                    FireSolutionFailure.CovarianceTooLarge;
                return solution;
            }

            var freshness = math.saturate(
                1f - (float)(trackAge / 3.0));
            var uncertainty = math.saturate(
                1f - (float)(largestSigma / 5_000.0));
            solution.Quality = math.saturate(
                track.Confidence * freshness * uncertainty);
            solution.SeekerActivationTime = math.max(
                requestTime,
                solution.InterceptTime - 2.0);
            solution.WeaponReleaseAuthorized =
                solution.Quality >= 0.15f;
            solution.Failure = solution.WeaponReleaseAuthorized
                ? FireSolutionFailure.None
                : FireSolutionFailure.CovarianceTooLarge;
            return solution;
        }

        private static double ReachabilityGap(
            double3 initialRelativePosition,
            double3 relativeVelocity,
            in MissileKinematicProfile kinematics,
            double flightTime)
        {
            var requiredDistance = math.length(
                initialRelativePosition +
                relativeVelocity * flightTime);
            return requiredDistance -
                   kinematics.RelativeTravelDistance(flightTime);
        }
    }
}
