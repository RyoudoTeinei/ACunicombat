using System;
using ACunicombat.Mechs;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

public sealed class SolarSystemOrbitMathTests
{
    private const string ModelPath =
        "Assets/_Project/Environment/SolarSystem/Resources/SolarSystem/solar_system_real_scale_2k_textures.glb";

    [Test]
    public void JulianDate_MapsJ2000EpochExactly()
    {
        double result = SolarSystemOrbitMath.ToJulianDate(
            new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc));

        Assert.That(result, Is.EqualTo(2451545d).Within(1e-9d));
    }

    [Test]
    public void EarthBarycenter_IsNearPerihelionAtJ2000()
    {
        var earth = new SolarOrbitalElements(
            1.00000018d, 0.01673163d, -0.00054346d,
            100.46691572d, 102.93005885d, -5.11260389d,
            -0.00000003d, -0.00003661d, -0.01337178d,
            35999.37306329d, 0.31795260d, -0.24123856d);

        Vector3 position = SolarSystemOrbitMath.PositionAtJulianDate(
            in earth,
            SolarSystemOrbitMath.J2000JulianDate);

        Assert.That(position.magnitude, Is.InRange(0.98f, 0.99f));
    }

    [Test]
    public void KeplerSolver_ReconstructsMeanAnomaly()
    {
        const double eccentricity = 0.4382385347971672d;
        const double meanAnomaly = 2.1d;
        double eccentricAnomaly = SolarSystemOrbitMath.SolveKepler(
            meanAnomaly,
            eccentricity);

        double reconstructed = eccentricAnomaly -
            eccentricity * Math.Sin(eccentricAnomaly);
        Assert.That(reconstructed, Is.EqualTo(meanAnomaly).Within(1e-10d));
    }

    [Test]
    public void ImportedModel_ContainsEveryRequiredBodyNode()
    {
        GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
        Assert.That(model, Is.Not.Null, "glTFast did not import the solar-system GLB as a GameObject.");

        string[] requiredNodes =
        {
            "Earth_0", "Venus_1", "Mercury_2", "Mars_3", "Jupiter_4",
            "Saturn_5", "Uranus_6", "Neptune_7", "Sun_8", "moon_9",
            "ceres_10", "eris_11", "haumea_12", "makemake_13", "SaturnRing_14",
        };
        foreach (string node in requiredNodes)
        {
            Assert.That(FindDescendant(model.transform, node), Is.Not.Null,
                $"Required solar-system body node is missing: {node}");
        }
    }

    private static Transform FindDescendant(Transform root, string name)
    {
        if (root.name == name)
        {
            return root;
        }
        for (int index = 0; index < root.childCount; index++)
        {
            Transform found = FindDescendant(root.GetChild(index), name);
            if (found != null)
            {
                return found;
            }
        }
        return null;
    }
}
