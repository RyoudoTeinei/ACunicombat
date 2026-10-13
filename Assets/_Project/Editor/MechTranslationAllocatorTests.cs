using ACunicombat.Mechs;
using NUnit.Framework;
using UnityEngine;

public sealed class MechTranslationAllocatorTests
{
    [Test]
    public void SymmetricNozzlesShareLoadAndCancelTorque()
    {
        var directions = new[] { Vector3.forward, Vector3.forward };
        var torquePerNewton = new[] { Vector3.up, Vector3.down };
        var capacities = new[] { 100f, 100f };
        var locked = new[] { false, false };
        var outputs = new float[2];

        MechTranslationAllocator.Solve(
            Vector3.forward * 100f,
            directions,
            torquePerNewton,
            capacities,
            locked,
            outputs,
            16,
            0.03f,
            1f,
            out var force,
            out var torque);

        Assert.That(outputs[0], Is.EqualTo(50f).Within(0.01f));
        Assert.That(outputs[1], Is.EqualTo(50f).Within(0.01f));
        Assert.That(force.z, Is.EqualTo(100f).Within(0.01f));
        Assert.That(torque.magnitude, Is.LessThan(0.01f));
    }

    [Test]
    public void TorquePenaltyRedistributesEquivalentNozzlesWithoutLosingForce()
    {
        var directions = new[]
        {
            Vector3.forward,
            Vector3.forward,
            Vector3.forward,
        };
        var torquePerNewton = new[]
        {
            Vector3.up,
            Vector3.down,
            Vector3.down * 3f,
        };
        var capacities = new[] { 100f, 100f, 100f };
        var locked = new[] { false, false, false };
        var outputs = new float[3];

        MechTranslationAllocator.Solve(
            Vector3.forward * 100f,
            directions,
            torquePerNewton,
            capacities,
            locked,
            outputs,
            32,
            0.03f,
            1f,
            out var force,
            out var torque);

        Assert.That(force.z, Is.EqualTo(100f).Within(0.5f));
        Assert.That(torque.magnitude, Is.LessThan(5f));
        Assert.That(outputs[2], Is.LessThan(outputs[0]));
    }

    [Test]
    public void LockedMainPairIsPreservedWhileAuxiliaryNozzlesFillResidual()
    {
        var directions = new[]
        {
            Vector3.forward,
            Vector3.forward,
            Vector3.right,
        };
        var torquePerNewton = new[]
        {
            Vector3.up,
            Vector3.down,
            Vector3.zero,
        };
        var capacities = new[] { 100f, 100f, 80f };
        var locked = new[] { true, true, false };
        var outputs = new[] { 40f, 40f, 0f };

        MechTranslationAllocator.Solve(
            Vector3.forward * 80f + Vector3.right * 60f,
            directions,
            torquePerNewton,
            capacities,
            locked,
            outputs,
            16,
            0.03f,
            1f,
            out var force,
            out var torque);

        Assert.That(outputs[0], Is.EqualTo(40f).Within(0.001f));
        Assert.That(outputs[1], Is.EqualTo(40f).Within(0.001f));
        Assert.That(outputs[2], Is.EqualTo(60f).Within(0.01f));
        Assert.That(force, Is.EqualTo(Vector3.forward * 80f + Vector3.right * 60f));
        Assert.That(torque.magnitude, Is.LessThan(0.01f));
    }
}
