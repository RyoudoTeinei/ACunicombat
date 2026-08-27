using System;
using UnityEngine;

namespace ACunicombat.Mechs
{
    public readonly struct SolarOrbitalElements
    {
        public SolarOrbitalElements(
            double semiMajorAxis,
            double eccentricity,
            double inclinationDegrees,
            double meanLongitudeDegrees,
            double longitudeOfPerihelionDegrees,
            double longitudeOfAscendingNodeDegrees,
            double semiMajorAxisRate,
            double eccentricityRate,
            double inclinationRate,
            double meanLongitudeRate,
            double longitudeOfPerihelionRate,
            double longitudeOfAscendingNodeRate,
            double quadraticMeanAnomaly = 0d,
            double cosineMeanAnomaly = 0d,
            double sineMeanAnomaly = 0d,
            double periodicFrequency = 0d)
        {
            SemiMajorAxis = semiMajorAxis;
            Eccentricity = eccentricity;
            InclinationDegrees = inclinationDegrees;
            MeanLongitudeDegrees = meanLongitudeDegrees;
            LongitudeOfPerihelionDegrees = longitudeOfPerihelionDegrees;
            LongitudeOfAscendingNodeDegrees = longitudeOfAscendingNodeDegrees;
            SemiMajorAxisRate = semiMajorAxisRate;
            EccentricityRate = eccentricityRate;
            InclinationRate = inclinationRate;
            MeanLongitudeRate = meanLongitudeRate;
            LongitudeOfPerihelionRate = longitudeOfPerihelionRate;
            LongitudeOfAscendingNodeRate = longitudeOfAscendingNodeRate;
            QuadraticMeanAnomaly = quadraticMeanAnomaly;
            CosineMeanAnomaly = cosineMeanAnomaly;
            SineMeanAnomaly = sineMeanAnomaly;
            PeriodicFrequency = periodicFrequency;
        }

        public double SemiMajorAxis { get; }
        public double Eccentricity { get; }
        public double InclinationDegrees { get; }
        public double MeanLongitudeDegrees { get; }
        public double LongitudeOfPerihelionDegrees { get; }
        public double LongitudeOfAscendingNodeDegrees { get; }
        public double SemiMajorAxisRate { get; }
        public double EccentricityRate { get; }
        public double InclinationRate { get; }
        public double MeanLongitudeRate { get; }
        public double LongitudeOfPerihelionRate { get; }
        public double LongitudeOfAscendingNodeRate { get; }
        public double QuadraticMeanAnomaly { get; }
        public double CosineMeanAnomaly { get; }
        public double SineMeanAnomaly { get; }
        public double PeriodicFrequency { get; }
    }

    public readonly struct SolarResolvedOrbit
    {
        public SolarResolvedOrbit(
            double semiMajorAxis,
            double eccentricity,
            double inclinationRadians,
            double argumentOfPerihelionRadians,
            double ascendingNodeRadians)
        {
            SemiMajorAxis = semiMajorAxis;
            Eccentricity = eccentricity;
            InclinationRadians = inclinationRadians;
            ArgumentOfPerihelionRadians = argumentOfPerihelionRadians;
            AscendingNodeRadians = ascendingNodeRadians;
        }

        public double SemiMajorAxis { get; }
        public double Eccentricity { get; }
        public double InclinationRadians { get; }
        public double ArgumentOfPerihelionRadians { get; }
        public double AscendingNodeRadians { get; }
    }

    public readonly struct SolarSmallBodyOrbit
    {
        public SolarSmallBodyOrbit(
            double epochJulianDate,
            double semiMajorAxis,
            double eccentricity,
            double inclinationDegrees,
            double argumentOfPerihelionDegrees,
            double ascendingNodeDegrees,
            double meanAnomalyDegrees,
            double orbitalPeriodDays)
        {
            EpochJulianDate = epochJulianDate;
            SemiMajorAxis = semiMajorAxis;
            Eccentricity = eccentricity;
            InclinationDegrees = inclinationDegrees;
            ArgumentOfPerihelionDegrees = argumentOfPerihelionDegrees;
            AscendingNodeDegrees = ascendingNodeDegrees;
            MeanAnomalyDegrees = meanAnomalyDegrees;
            OrbitalPeriodDays = orbitalPeriodDays;
        }

        public double EpochJulianDate { get; }
        public double SemiMajorAxis { get; }
        public double Eccentricity { get; }
        public double InclinationDegrees { get; }
        public double ArgumentOfPerihelionDegrees { get; }
        public double AscendingNodeDegrees { get; }
        public double MeanAnomalyDegrees { get; }
        public double OrbitalPeriodDays { get; }
    }

    public static class SolarSystemOrbitMath
    {
        public const double J2000JulianDate = 2451545.0d;
        public const double AstronomicalUnitKilometers = 149597870.7d;

        public static double ToJulianDate(DateTime utcDateTime)
        {
            DateTime utc = utcDateTime.Kind == DateTimeKind.Utc
                ? utcDateTime
                : utcDateTime.ToUniversalTime();
            return (utc - DateTime.UnixEpoch).TotalDays + 2440587.5d;
        }

        public static DateTime FromJulianDate(double julianDate)
        {
            return DateTime.UnixEpoch.AddDays(julianDate - 2440587.5d);
        }

        public static SolarResolvedOrbit ResolveOrbit(
            in SolarOrbitalElements elements,
            double julianDate)
        {
            double centuries = (julianDate - J2000JulianDate) / 36525d;
            double semiMajorAxis =
                elements.SemiMajorAxis + elements.SemiMajorAxisRate * centuries;
            double eccentricity =
                elements.Eccentricity + elements.EccentricityRate * centuries;
            double inclination = DegreesToRadians(
                elements.InclinationDegrees + elements.InclinationRate * centuries);
            double longitudeOfPerihelion =
                elements.LongitudeOfPerihelionDegrees +
                elements.LongitudeOfPerihelionRate * centuries;
            double ascendingNode =
                elements.LongitudeOfAscendingNodeDegrees +
                elements.LongitudeOfAscendingNodeRate * centuries;
            return new SolarResolvedOrbit(
                semiMajorAxis,
                eccentricity,
                inclination,
                DegreesToRadians(longitudeOfPerihelion - ascendingNode),
                DegreesToRadians(ascendingNode));
        }

        public static Vector3 PositionAtJulianDate(
            in SolarOrbitalElements elements,
            double julianDate)
        {
            double centuries = (julianDate - J2000JulianDate) / 36525d;
            SolarResolvedOrbit orbit = ResolveOrbit(in elements, julianDate);
            double meanLongitude =
                elements.MeanLongitudeDegrees + elements.MeanLongitudeRate * centuries;
            double longitudeOfPerihelion =
                elements.LongitudeOfPerihelionDegrees +
                elements.LongitudeOfPerihelionRate * centuries;
            double periodicAngle = DegreesToRadians(
                elements.PeriodicFrequency * centuries);
            double meanAnomalyDegrees =
                meanLongitude - longitudeOfPerihelion +
                elements.QuadraticMeanAnomaly * centuries * centuries +
                elements.CosineMeanAnomaly * Math.Cos(periodicAngle) +
                elements.SineMeanAnomaly * Math.Sin(periodicAngle);
            double eccentricAnomaly = SolveKepler(
                DegreesToRadians(WrapDegrees(meanAnomalyDegrees)),
                orbit.Eccentricity);
            return PositionAtEccentricAnomaly(in orbit, eccentricAnomaly);
        }

        public static Vector3 PositionAtJulianDate(
            in SolarSmallBodyOrbit elements,
            double julianDate)
        {
            SolarResolvedOrbit orbit = ResolveOrbit(in elements);
            double elapsedDays = julianDate - elements.EpochJulianDate;
            double meanAnomaly = DegreesToRadians(
                elements.MeanAnomalyDegrees +
                360d * elapsedDays / elements.OrbitalPeriodDays);
            double eccentricAnomaly = SolveKepler(
                WrapRadians(meanAnomaly),
                orbit.Eccentricity);
            return PositionAtEccentricAnomaly(in orbit, eccentricAnomaly);
        }

        public static SolarResolvedOrbit ResolveOrbit(
            in SolarSmallBodyOrbit elements)
        {
            return new SolarResolvedOrbit(
                elements.SemiMajorAxis,
                elements.Eccentricity,
                DegreesToRadians(elements.InclinationDegrees),
                DegreesToRadians(elements.ArgumentOfPerihelionDegrees),
                DegreesToRadians(elements.AscendingNodeDegrees));
        }

        public static Vector3 PositionAtEccentricAnomaly(
            in SolarResolvedOrbit orbit,
            double eccentricAnomalyRadians)
        {
            double xPrime = orbit.SemiMajorAxis *
                (Math.Cos(eccentricAnomalyRadians) - orbit.Eccentricity);
            double yPrime = orbit.SemiMajorAxis *
                Math.Sqrt(1d - orbit.Eccentricity * orbit.Eccentricity) *
                Math.Sin(eccentricAnomalyRadians);

            double cosW = Math.Cos(orbit.ArgumentOfPerihelionRadians);
            double sinW = Math.Sin(orbit.ArgumentOfPerihelionRadians);
            double cosO = Math.Cos(orbit.AscendingNodeRadians);
            double sinO = Math.Sin(orbit.AscendingNodeRadians);
            double cosI = Math.Cos(orbit.InclinationRadians);
            double sinI = Math.Sin(orbit.InclinationRadians);

            double eclipticX =
                (cosW * cosO - sinW * sinO * cosI) * xPrime +
                (-sinW * cosO - cosW * sinO * cosI) * yPrime;
            double eclipticY =
                (cosW * sinO + sinW * cosO * cosI) * xPrime +
                (-sinW * sinO + cosW * cosO * cosI) * yPrime;
            double eclipticZ =
                sinW * sinI * xPrime + cosW * sinI * yPrime;

            // Unity: XZ is the J2000 ecliptic plane and +Y is ecliptic north.
            return new Vector3(
                (float)eclipticX,
                (float)eclipticZ,
                (float)eclipticY);
        }

        public static double SolveKepler(double meanAnomalyRadians, double eccentricity)
        {
            double meanAnomaly = WrapRadians(meanAnomalyRadians);
            double eccentricAnomaly = meanAnomaly + eccentricity * Math.Sin(meanAnomaly);
            for (int iteration = 0; iteration < 12; iteration++)
            {
                double residual =
                    eccentricAnomaly -
                    eccentricity * Math.Sin(eccentricAnomaly) -
                    meanAnomaly;
                double correction = residual /
                    (1d - eccentricity * Math.Cos(eccentricAnomaly));
                eccentricAnomaly -= correction;
                if (Math.Abs(correction) <= 1e-12d)
                {
                    break;
                }
            }

            return eccentricAnomaly;
        }

        private static double WrapDegrees(double value)
        {
            value %= 360d;
            if (value > 180d) value -= 360d;
            if (value < -180d) value += 360d;
            return value;
        }

        private static double WrapRadians(double value)
        {
            double twoPi = Math.PI * 2d;
            value %= twoPi;
            if (value > Math.PI) value -= twoPi;
            if (value < -Math.PI) value += twoPi;
            return value;
        }

        private static double DegreesToRadians(double value)
        {
            return value * Math.PI / 180d;
        }
    }
}
