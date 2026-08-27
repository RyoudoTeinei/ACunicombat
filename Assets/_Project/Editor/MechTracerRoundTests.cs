using ACunicombat.Mechs;
using NUnit.Framework;
using UnityEngine;

public sealed class MechTracerRoundTests
{
    [Test]
    public void VisualProfile_ScalesHeadAndWidthWithCaliber()
    {
        MechTracerRound.VisualProfile light = MechTracerRound.ResolveVisualProfile(7.62f, 1800f);
        MechTracerRound.VisualProfile medium = MechTracerRound.ResolveVisualProfile(30f, 1500f);
        MechTracerRound.VisualProfile heavy = MechTracerRound.ResolveVisualProfile(57f, 450f);

        Assert.That(light.HeadSizeMeters, Is.LessThan(medium.HeadSizeMeters));
        Assert.That(medium.HeadSizeMeters, Is.LessThan(heavy.HeadSizeMeters));
        Assert.That(light.TrailWidthMeters, Is.LessThan(medium.TrailWidthMeters));
        Assert.That(medium.TrailWidthMeters, Is.LessThan(heavy.TrailWidthMeters));
    }

    [Test]
    public void VisualProfile_LimitsHighVelocityTracerLength()
    {
        const float velocityMetersPerSecond = 1800f;
        MechTracerRound.VisualProfile profile = MechTracerRound.ResolveVisualProfile(7.62f, velocityMetersPerSecond);
        float visibleLengthMeters = profile.TrailLifetimeSeconds * velocityMetersPerSecond;

        Assert.That(visibleLengthMeters, Is.EqualTo(24f).Within(0.1f));
        Assert.That(profile.HeadSizeMeters, Is.InRange(0.18f, 0.2f));
        Assert.That(profile.TrailWidthMeters, Is.EqualTo(profile.HeadSizeMeters).Within(0.001f));
    }

    [Test]
    public void Build_CreatesCoreAndGlowOnSeparateObjects()
    {
        var projectile = new GameObject("TracerTestProjectile");
        try
        {
            projectile.AddComponent<MechTracerRound>().Build(Color.white, 20f, 1000f);

            Transform streak = projectile.transform.Find("TracerStreak");
            Assert.That(streak, Is.Not.Null);
            Assert.That(streak.GetComponents<TrailRenderer>(), Is.Empty);
            Assert.That(streak.Find("Core")?.GetComponents<TrailRenderer>(), Has.Length.EqualTo(1));
            Assert.That(streak.Find("Glow")?.GetComponents<TrailRenderer>(), Has.Length.EqualTo(1));
        }
        finally
        {
            Object.DestroyImmediate(projectile);
        }
    }
}
