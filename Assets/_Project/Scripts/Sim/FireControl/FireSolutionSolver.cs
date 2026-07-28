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
        /// <summary>综合质量（置信×新鲜度×不确定度）未达开火门槛——与协方差超限区分开，避免调试误导。</summary>
        QualityBelowThreshold = 5,
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

    public struct InterceptorEngagementSolution
    {
        public bool Reachable;
        public double3 InterceptPoint;
        public double InterceptTime;
        public float FlightTimeSeconds;
        public float ThreatTimeMarginSeconds;
        public float RequiredTurnDegrees;
        public float RequiredTravelMeters;
    }

    public struct MissileKinematicProfile
    {
        public double EjectionSpeed;
        public double BoostAcceleration;
        public double MotorBurnDuration;
        public double InertMass;
        public double MainPropellantMass;
        public double MainEngineThrust;
        public double MainMassFlow;

        public bool UsesVariableMass =>
            InertMass > 0.0 &&
            MainPropellantMass > 0.0 &&
            MainEngineThrust > 0.0 &&
            MainMassFlow > 0.0;

        public double EffectiveMotorBurnDuration =>
            UsesVariableMass
                ? MainPropellantMass / MainMassFlow
                : MotorBurnDuration;

        public double BurnoutSpeed
        {
            get
            {
                if (!UsesVariableMass)
                {
                    return EjectionSpeed +
                           BoostAcceleration * MotorBurnDuration;
                }

                var initialMass = InertMass + MainPropellantMass;
                var exhaustVelocity =
                    MainEngineThrust / MainMassFlow;
                return EjectionSpeed +
                       exhaustVelocity *
                       math.log(initialMass / InertMass);
            }
        }

        public double RelativeTravelDistance(double flightTime)
        {
            var time = math.max(0.0, flightTime);
            if (!UsesVariableMass)
            {
                var legacyBurnTime =
                    math.min(time, MotorBurnDuration);
                var legacyDistance =
                    EjectionSpeed * legacyBurnTime +
                    0.5 * BoostAcceleration *
                    legacyBurnTime * legacyBurnTime;
                if (time > MotorBurnDuration)
                {
                    legacyDistance +=
                        BurnoutSpeed *
                        (time - MotorBurnDuration);
                }

                return legacyDistance;
            }

            var burnDuration = EffectiveMotorBurnDuration;
            var burnTime = math.min(time, burnDuration);
            var initialMass = InertMass + MainPropellantMass;
            var remainingMass = math.max(
                InertMass,
                initialMass - MainMassFlow * burnTime);
            var exhaustVelocity =
                MainEngineThrust / MainMassFlow;
            var massRatioLog =
                math.log(initialMass / remainingMass);
            var distance =
                EjectionSpeed * burnTime +
                exhaustVelocity *
                (burnTime -
                 remainingMass / MainMassFlow *
                 massRatioLog);
            if (time > burnDuration)
            {
                distance +=
                    BurnoutSpeed * (time - burnDuration);
            }

            return distance;
        }

        public bool IsValid =>
            EjectionSpeed >= 0.0 &&
            (UsesVariableMass ||
             (BoostAcceleration > 0.0 &&
              MotorBurnDuration > 0.0)) &&
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
            return Solve(
                in track,
                shooterPosition,
                double3.zero,
                projectileSpeed,
                requestTime);
        }

        public static FireSolution Solve(
            in TrackSnapshot track,
            double3 shooterPosition,
            double3 shooterVelocity,
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
            var relativeVelocity =
                track.Velocity - shooterVelocity;
            var timeOfFlight = 0.0;
            for (var i = 0; i < 3; i++)
            {
                var predicted =
                    currentPosition +
                    relativeVelocity * timeOfFlight;
                timeOfFlight =
                    math.length(predicted - shooterPosition) /
                    projectileSpeed;
            }

            solution.InterceptTime = requestTime + timeOfFlight;
            solution.InterceptPoint =
                currentPosition + track.Velocity * timeOfFlight;
            // The projectile inherits the shooter's inertial velocity. Aim at
            // the target's future position minus the shooter's displacement
            // during the projectile time of flight.
            solution.MidcourseAimPoint =
                solution.InterceptPoint -
                shooterVelocity * timeOfFlight;

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
                : FireSolutionFailure.QualityBelowThreshold;
            return solution;
        }

        public static InterceptorEngagementSolution SolveInterceptor(
            in MissileTrackSnapshot track,
            double3 shooterPosition,
            double3 shooterVelocity,
            float3 launchForward,
            in MissileKinematicProfile kinematics,
            double requestTime,
            float maximumAngularRateDegreesPerSecond,
            float maximumFlightTimeSeconds,
            float maximumTravelDistanceMeters,
            float responseReserveSeconds = 0.2f,
            float minimumMainEngineIgnitionDelaySeconds = 0f)
        {
            var solution = default(InterceptorEngagementSolution);
            if (!track.IsWeaponsQuality ||
                !track.Incoming ||
                !kinematics.IsValid ||
                maximumAngularRateDegreesPerSecond <= 0f ||
                maximumFlightTimeSeconds <= 0f ||
                maximumTravelDistanceMeters <= 0f)
            {
                return solution;
            }

            var currentTargetPosition =
                track.PredictPosition(requestTime);
            var initialRelativePosition =
                currentTargetPosition - shooterPosition;
            var relativeVelocity =
                track.Velocity - shooterVelocity;
            var timeAvailable = math.min(
                maximumFlightTimeSeconds,
                math.max(
                    0f,
                    track.TimeToClosestApproachSeconds -
                    math.max(0f, responseReserveSeconds)));
            if (timeAvailable <= 0.05f)
            {
                return solution;
            }

            var normalizedLaunchForward = math.normalizesafe(
                launchForward,
                new float3(0f, 0f, 1f));
            var angularRateRadians =
                math.radians(maximumAngularRateDegreesPerSecond);
            var uncertaintyAllowance = math.min(
                250f,
                math.max(0f, track.PositionUncertaintyMeters) *
                    0.12f +
                math.max(0f, track.CpaUncertaintyMeters) * 0.04f);

            const int sampleCount = 128;
            var low = 0.0;
            var high = 0.0;
            var previousTime = 0.0;
            var bracketed = false;
            for (var sample = 1; sample <= sampleCount; sample++)
            {
                var sampleTime =
                    timeAvailable * sample / sampleCount;
                if (InterceptorReachabilityGap(
                        initialRelativePosition,
                        relativeVelocity,
                        normalizedLaunchForward,
                        in kinematics,
                        angularRateRadians,
                        maximumTravelDistanceMeters,
                        uncertaintyAllowance,
                        minimumMainEngineIgnitionDelaySeconds,
                        sampleTime) <= 0.0)
                {
                    low = previousTime;
                    high = sampleTime;
                    bracketed = true;
                    break;
                }

                previousTime = sampleTime;
            }

            if (!bracketed)
            {
                return solution;
            }

            for (var iteration = 0; iteration < 40; iteration++)
            {
                var middle = (low + high) * 0.5;
                if (InterceptorReachabilityGap(
                        initialRelativePosition,
                        relativeVelocity,
                        normalizedLaunchForward,
                        in kinematics,
                        angularRateRadians,
                        maximumTravelDistanceMeters,
                        uncertaintyAllowance,
                        minimumMainEngineIgnitionDelaySeconds,
                        middle) > 0.0)
                {
                    low = middle;
                }
                else
                {
                    high = middle;
                }
            }

            var flightTime = high;
            var relativeIntercept =
                initialRelativePosition +
                relativeVelocity * flightTime;
            var interceptDirection = math.normalizesafe(
                (float3)relativeIntercept,
                normalizedLaunchForward);
            var turnRadians = math.acos(
                math.clamp(
                    math.dot(
                        normalizedLaunchForward,
                        interceptDirection),
                    -1f,
                    1f));
            solution.Reachable = true;
            solution.FlightTimeSeconds = (float)flightTime;
            solution.InterceptTime = requestTime + flightTime;
            solution.InterceptPoint =
                currentTargetPosition +
                track.Velocity * flightTime;
            solution.ThreatTimeMarginSeconds =
                track.TimeToClosestApproachSeconds -
                (float)flightTime;
            solution.RequiredTurnDegrees =
                math.degrees(turnRadians);
            solution.RequiredTravelMeters =
                (float)math.length(relativeIntercept);
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
                : FireSolutionFailure.QualityBelowThreshold;
            return solution;
        }

        // 直线相对运动假设：不计导弹转弯/追击几何，大离轴发射时 TOF 偏乐观、
        // 中段瞄准点偏前。首版可接受；引入机动模型时替换本可达性判据。
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

        private static double InterceptorReachabilityGap(
            double3 initialRelativePosition,
            double3 relativeVelocity,
            float3 launchForward,
            in MissileKinematicProfile kinematics,
            float maximumAngularRateRadians,
            float maximumTravelDistanceMeters,
            float uncertaintyAllowanceMeters,
            float minimumMainEngineIgnitionDelaySeconds,
            double flightTime)
        {
            var relativeIntercept =
                initialRelativePosition +
                relativeVelocity * flightTime;
            var requiredDistance =
                math.length(relativeIntercept) +
                uncertaintyAllowanceMeters;
            var interceptDirection = math.normalizesafe(
                (float3)relativeIntercept,
                launchForward);
            var turnRadians = math.acos(
                math.clamp(
                    math.dot(launchForward, interceptDirection),
                    -1f,
                    1f));
            var turnTime = maximumAngularRateRadians > 0f
                ? turnRadians / maximumAngularRateRadians
                : double.PositiveInfinity;
            double availableDistance;
            if (minimumMainEngineIgnitionDelaySeconds > 0f)
            {
                var ignitionDelay = math.max(
                    minimumMainEngineIgnitionDelaySeconds,
                    turnTime);
                var ballisticEjectionTime = math.min(
                    flightTime,
                    ignitionDelay);
                var poweredFlightTime = math.max(
                    0.0,
                    flightTime - ignitionDelay);
                availableDistance =
                    kinematics.EjectionSpeed * ballisticEjectionTime +
                    kinematics.RelativeTravelDistance(poweredFlightTime);
            }
            else
            {
                // Legacy direct-launch interceptors accelerate while slewing.
                var effectiveClosingTime =
                    math.max(0.0, flightTime - turnTime * 0.55);
                availableDistance =
                    kinematics.RelativeTravelDistance(
                        effectiveClosingTime);
            }
            availableDistance = math.min(
                maximumTravelDistanceMeters,
                availableDistance);
            return requiredDistance - availableDistance;
        }
    }
}
