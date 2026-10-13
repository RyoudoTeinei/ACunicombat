using System;
using UnityEngine;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// Bounded translation allocator. Torque is only a nozzle-selection penalty; it is not
    /// applied to the mech's kinematic attitude controller.
    /// </summary>
    public static class MechTranslationAllocator
    {
        public static void Solve(
            Vector3 requestedForce,
            Vector3[] directions,
            Vector3[] torquePerNewton,
            float[] capacities,
            bool[] lockedOutputs,
            float[] outputs,
            int iterations,
            float torqueWeight,
            float referenceArmMeters,
            out Vector3 achievedForce,
            out Vector3 achievedTorque)
        {
            ValidateBuffers(
                directions,
                torquePerNewton,
                capacities,
                lockedOutputs,
                outputs);

            var count = directions.Length;
            achievedForce = Vector3.zero;
            achievedTorque = Vector3.zero;
            for (var i = 0; i < count; i++)
            {
                outputs[i] = Mathf.Clamp(outputs[i], 0f, Mathf.Max(0f, capacities[i]));
                if (!lockedOutputs[i])
                {
                    outputs[i] = 0f;
                    continue;
                }

                achievedForce += directions[i] * outputs[i];
                achievedTorque += torquePerNewton[i] * outputs[i];
            }

            SeedBalancedOutputs(
                requestedForce,
                directions,
                torquePerNewton,
                capacities,
                lockedOutputs,
                outputs,
                ref achievedForce,
                ref achievedTorque);

            var normalizedTorqueWeight = Mathf.Max(0f, torqueWeight) /
                                         Mathf.Pow(Mathf.Max(0.1f, referenceArmMeters), 2f);
            var iterationCount = Mathf.Clamp(iterations, 1, 64);
            for (var iteration = 0; iteration < iterationCount; iteration++)
            {
                var reverseOrder = (iteration & 1) != 0;
                for (var step = 0; step < count; step++)
                {
                    var index = reverseOrder ? count - 1 - step : step;
                    if (lockedOutputs[index])
                    {
                        continue;
                    }

                    var capacity = Mathf.Max(0f, capacities[index]);
                    var direction = directions[index];
                    if (capacity <= Mathf.Epsilon || direction.sqrMagnitude <= Mathf.Epsilon)
                    {
                        outputs[index] = 0f;
                        continue;
                    }

                    var torque = torquePerNewton[index];
                    var previousOutput = outputs[index];
                    var forceWithout = achievedForce - direction * previousOutput;
                    var torqueWithout = achievedTorque - torque * previousOutput;
                    var denominator = direction.sqrMagnitude +
                                      normalizedTorqueWeight * torque.sqrMagnitude;
                    if (denominator <= Mathf.Epsilon)
                    {
                        continue;
                    }

                    var numerator = Vector3.Dot(requestedForce - forceWithout, direction) -
                                    normalizedTorqueWeight * Vector3.Dot(torqueWithout, torque);
                    var output = Mathf.Clamp(numerator / denominator, 0f, capacity);
                    outputs[index] = output;
                    achievedForce = forceWithout + direction * output;
                    achievedTorque = torqueWithout + torque * output;
                }
            }
        }

        private static void SeedBalancedOutputs(
            Vector3 requestedForce,
            Vector3[] directions,
            Vector3[] torquePerNewton,
            float[] capacities,
            bool[] lockedOutputs,
            float[] outputs,
            ref Vector3 achievedForce,
            ref Vector3 achievedTorque)
        {
            var residual = requestedForce - achievedForce;
            if (residual.sqrMagnitude <= Mathf.Epsilon)
            {
                return;
            }

            var requestedDirection = residual.normalized;
            var projectedCapacity = 0f;
            for (var i = 0; i < directions.Length; i++)
            {
                if (lockedOutputs[i] || capacities[i] <= Mathf.Epsilon)
                {
                    continue;
                }

                projectedCapacity += Mathf.Max(0f, Vector3.Dot(directions[i], requestedDirection)) *
                                     capacities[i];
            }

            if (projectedCapacity <= Mathf.Epsilon)
            {
                return;
            }

            var scale = Mathf.Clamp01(residual.magnitude / projectedCapacity);
            for (var i = 0; i < directions.Length; i++)
            {
                if (lockedOutputs[i])
                {
                    continue;
                }

                var alignment = Mathf.Max(0f, Vector3.Dot(directions[i], requestedDirection));
                var output = Mathf.Clamp(capacities[i] * alignment * scale, 0f, capacities[i]);
                outputs[i] = output;
                achievedForce += directions[i] * output;
                achievedTorque += torquePerNewton[i] * output;
            }
        }

        private static void ValidateBuffers(
            Vector3[] directions,
            Vector3[] torquePerNewton,
            float[] capacities,
            bool[] lockedOutputs,
            float[] outputs)
        {
            if (directions == null || torquePerNewton == null || capacities == null ||
                lockedOutputs == null || outputs == null)
            {
                throw new ArgumentNullException(nameof(directions));
            }

            var count = directions.Length;
            if (torquePerNewton.Length != count || capacities.Length != count ||
                lockedOutputs.Length != count || outputs.Length != count)
            {
                throw new ArgumentException("Thruster allocator buffers must have matching lengths.");
            }
        }
    }
}
