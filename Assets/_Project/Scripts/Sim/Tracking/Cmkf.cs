using System;
using Unity.Mathematics;

namespace ACunicombat.Tracking
{
    /// <summary>
    /// 滤波器工作状态：6 维（位置+速度）+ 满 6×6 协方差（内部用满阵便于矩阵运算，
    /// 快照导出时压为上三角 21 个 double）。
    /// </summary>
    public struct FilterState
    {
        public double3 Position;
        public double3 Velocity;
        /// <summary>满 6×6 行主序。始终对称（每次更新后 Symmetrize）。</summary>
        public Matrix6 P;
        public double Time;
    }

    /// <summary>6×6 双精度矩阵，行主序，blittable。</summary>
    public unsafe struct Matrix6
    {
        public fixed double M[36];

        public double this[int r, int c]
        {
            get => M[r * 6 + c];
            set => M[r * 6 + c] = value;
        }
    }

    /// <summary>
    /// CMKF（转换测量卡尔曼滤波，设计文档 14.1）。
    /// 数值纪律（强制）：更新用 Joseph 形式；每次更新后对称化；对角线断言下限。
    /// 本程序集不引用 World/Sensors（asmdef 防火墙），只消费 Contracts.Measurement。
    /// </summary>
    public static class Cmkf
    {
        /// <summary>常速度模型预测：x' = x + v·dt；P' = FPFᵀ + Q（Q 对角，按机动档位）。</summary>
        public static void PredictCv(ref FilterState s, double dt, double qPosition, double qVelocity)
        {
            s.Position += s.Velocity * dt;
            s.Time += dt;

            // F = [I dtI; 0 I] ⇒ FPFᵀ 分块展开：
            // Ppp' = Ppp + dt(Ppv + Pvp) + dt²Pvv;  Ppv' = Ppv + dtPvv;  Pvv' = Pvv
            // 必须从原始矩阵读、写到新矩阵：就地更新会让后续迭代把新值当旧值读，
            // 非对角块多算过程项（Matrix6 是 struct，赋值即拷贝）。
            var src = s.P;
            var dst = s.P;
            for (var i = 0; i < 3; i++)
            {
                for (var j = 0; j < 3; j++)
                {
                    var ppv = src[i, j + 3];
                    var pvp = src[i + 3, j];
                    var pvv = src[i + 3, j + 3];
                    dst[i, j] = src[i, j] + dt * (ppv + pvp) + dt * dt * pvv;
                    dst[i, j + 3] = ppv + dt * pvv;
                    dst[j + 3, i] = dst[i, j + 3];
                }
            }

            for (var i = 0; i < 3; i++)
            {
                dst[i, i] += qPosition * dt;
                dst[i + 3, i + 3] += qVelocity * dt;
            }

            s.P = dst;
            Symmetrize(ref s.P);
        }

        /// <summary>
        /// 球→笛卡尔转换测量（14.1）：z = p_sensor + r·d̂；
        /// R_cart = σr²·d̂d̂ᵀ + r²σθ²·(I − d̂d̂ᵀ)（各向同性角误差的精确形式）。
        /// TODO(阶段 1)：大角度误差时的标准 CMKF 去偏均值/协方差修正项。
        /// </summary>
        public static void ConvertMeasurement(
            double3 sensorPosition, float range, float3 direction, float rangeVar, float angleVar,
            out double3 zCartesian, out Matrix6 rCartesian3x3)
        {
            double3 d = direction;
            zCartesian = sensorPosition + d * range;

            var cross = (double)range * range * angleVar;
            rCartesian3x3 = default;
            for (var i = 0; i < 3; i++)
            {
                for (var j = 0; j < 3; j++)
                {
                    var outer = d[i] * d[j];
                    rCartesian3x3[i, j] = rangeVar * outer + cross * ((i == j ? 1.0 : 0.0) - outer);
                }
            }
        }

        /// <summary>
        /// 位置伪测量 3D 更新，Joseph 形式：P = (I−KH)P(I−KH)ᵀ + KRKᵀ。
        /// TODO(阶段 1)：3×3 新息协方差求逆（对称正定，Cholesky）+ 增益与 Joseph 展开。
        /// 实现时必须以 NEES/NIS（22.2）作为验收，不得用目测代替。
        /// </summary>
        public static void UpdatePosition(ref FilterState s, double3 z, in Matrix6 r3x3)
        {
            var innovation = z - s.Position;

            Span<double> covariance = stackalloc double[9];
            for (var i = 0; i < 3; i++)
            {
                for (var j = 0; j < 3; j++)
                {
                    covariance[i * 3 + j] = s.P[i, j] + r3x3[i, j];
                }

                covariance[i * 3 + i] += 1e-9;
            }

            Span<double> inverse = stackalloc double[9];
            if (!TryInvertSymmetric3(covariance, inverse))
            {
                return;
            }

            Span<double> gain = stackalloc double[18];
            for (var i = 0; i < 6; i++)
            {
                for (var j = 0; j < 3; j++)
                {
                    var sum = 0.0;
                    for (var k = 0; k < 3; k++)
                    {
                        sum += s.P[i, k] * inverse[k * 3 + j];
                    }

                    gain[i * 3 + j] = sum;
                }
            }

            Span<double> correction = stackalloc double[6];
            for (var i = 0; i < 6; i++)
            {
                correction[i] = gain[i * 3] * innovation.x
                    + gain[i * 3 + 1] * innovation.y
                    + gain[i * 3 + 2] * innovation.z;
            }

            s.Position += new double3(correction[0], correction[1], correction[2]);
            s.Velocity += new double3(correction[3], correction[4], correction[5]);

            var previous = s.P;
            Matrix6 a = default;
            for (var i = 0; i < 6; i++)
            {
                for (var j = 0; j < 6; j++)
                {
                    a[i, j] = (i == j ? 1.0 : 0.0) - (j < 3 ? gain[i * 3 + j] : 0.0);
                }
            }

            Matrix6 ap = default;
            for (var i = 0; i < 6; i++)
            {
                for (var j = 0; j < 6; j++)
                {
                    var sum = 0.0;
                    for (var k = 0; k < 6; k++)
                    {
                        sum += a[i, k] * previous[k, j];
                    }

                    ap[i, j] = sum;
                }
            }

            Matrix6 next = default;
            for (var i = 0; i < 6; i++)
            {
                for (var j = 0; j < 6; j++)
                {
                    var value = 0.0;
                    for (var k = 0; k < 6; k++)
                    {
                        value += ap[i, k] * a[j, k];
                    }

                    for (var row = 0; row < 3; row++)
                    {
                        for (var col = 0; col < 3; col++)
                        {
                            value += gain[i * 3 + row] * r3x3[row, col] * gain[j * 3 + col];
                        }
                    }

                    next[i, j] = value;
                }
            }

            s.P = next;
            Symmetrize(ref s.P);
            AssertDiagonal(in s.P);
        }

        /// <summary>
        /// Calculates the normalized innovation squared for a 3D position
        /// measurement. Callers use this before a high-gain refinement update
        /// so an incorrectly associated observation cannot pull a track onto
        /// another target.
        /// </summary>
        public static bool TryCalculatePositionInnovationNis(
            in FilterState state,
            double3 measurement,
            in Matrix6 measurementCovariance,
            out double nis)
        {
            Span<double> innovationCovariance = stackalloc double[9];
            for (var row = 0; row < 3; row++)
            {
                for (var column = 0; column < 3; column++)
                {
                    innovationCovariance[row * 3 + column] =
                        state.P[row, column] +
                        measurementCovariance[row, column];
                }

                innovationCovariance[row * 3 + row] += 1e-9;
            }

            Span<double> inverse = stackalloc double[9];
            if (!TryInvertSymmetric3(innovationCovariance, inverse))
            {
                nis = double.PositiveInfinity;
                return false;
            }

            var innovation = measurement - state.Position;
            var weightedX = inverse[0] * innovation.x +
                            inverse[1] * innovation.y +
                            inverse[2] * innovation.z;
            var weightedY = inverse[3] * innovation.x +
                            inverse[4] * innovation.y +
                            inverse[5] * innovation.z;
            var weightedZ = inverse[6] * innovation.x +
                            inverse[7] * innovation.y +
                            inverse[8] * innovation.z;
            nis = innovation.x * weightedX +
                  innovation.y * weightedY +
                  innovation.z * weightedZ;
            return !double.IsNaN(nis) &&
                   !double.IsInfinity(nis) &&
                   nis >= 0.0;
        }

        private static bool TryInvertSymmetric3(ReadOnlySpan<double> m, Span<double> inverse)
        {
            var a = m[0];
            var b = m[1];
            var c = m[2];
            var d = m[4];
            var e = m[5];
            var f = m[8];

            var cofactor00 = d * f - e * e;
            var cofactor01 = c * e - b * f;
            var cofactor02 = b * e - c * d;
            var cofactor11 = a * f - c * c;
            var cofactor12 = b * c - a * e;
            var cofactor22 = a * d - b * b;
            var determinant = a * cofactor00 + b * cofactor01 + c * cofactor02;
            if (double.IsNaN(determinant) || double.IsInfinity(determinant) || Math.Abs(determinant) < 1e-18)
            {
                return false;
            }

            var scale = 1.0 / determinant;
            inverse[0] = cofactor00 * scale;
            inverse[1] = inverse[3] = cofactor01 * scale;
            inverse[2] = inverse[6] = cofactor02 * scale;
            inverse[4] = cofactor11 * scale;
            inverse[5] = inverse[7] = cofactor12 * scale;
            inverse[8] = cofactor22 * scale;
            return true;
        }

        /// <summary>
        /// 径向速度标量第二阶段更新（14.1）：H = [0₃, d̂ᵀ]（视线方向对速度的投影）。
        /// 标量更新无需矩阵求逆，Joseph 形式完整展开。
        /// </summary>
        public static void UpdateRadialVelocity(
            ref FilterState s, float3 direction, float measuredRadialVelocity, float velocityVar)
        {
            double3 d = direction;

            // 新息与新息方差 S = HPHᵀ + R（H 只作用于速度块）。
            var predicted = math.dot(d, s.Velocity);
            var innovation = measuredRadialVelocity - predicted;
            var hph = 0.0;
            for (var i = 0; i < 3; i++)
            {
                for (var j = 0; j < 3; j++)
                {
                    hph += d[i] * s.P[i + 3, j + 3] * d[j];
                }
            }

            var sVar = hph + velocityVar;
            if (sVar < 1e-12)
            {
                return;
            }

            // K = PHᵀ/S（6 维）；x += K·innovation。
            Span<double> k = stackalloc double[6];
            for (var i = 0; i < 6; i++)
            {
                var phT = 0.0;
                for (var j = 0; j < 3; j++)
                {
                    phT += s.P[i, j + 3] * d[j];
                }

                k[i] = phT / sVar;
            }

            s.Position += new double3(k[0], k[1], k[2]) * innovation;
            s.Velocity += new double3(k[3], k[4], k[5]) * innovation;

            // Joseph：P = (I−KH)P(I−KH)ᵀ + K R Kᵀ，H = eᵀ（e = [0₃, d̂]）。
            Span<double> e = stackalloc double[6] { 0, 0, 0, d.x, d.y, d.z };
            var p = s.P;
            Matrix6 a = default; // A = I − K eᵀ
            for (var i = 0; i < 6; i++)
            {
                for (var j = 0; j < 6; j++)
                {
                    a[i, j] = (i == j ? 1.0 : 0.0) - k[i] * e[j];
                }
            }

            Matrix6 ap = default;
            for (var i = 0; i < 6; i++)
            {
                for (var j = 0; j < 6; j++)
                {
                    var sum = 0.0;
                    for (var m = 0; m < 6; m++)
                    {
                        sum += a[i, m] * p[m, j];
                    }

                    ap[i, j] = sum;
                }
            }

            Matrix6 next = default;
            for (var i = 0; i < 6; i++)
            {
                for (var j = 0; j < 6; j++)
                {
                    var sum = 0.0;
                    for (var m = 0; m < 6; m++)
                    {
                        sum += ap[i, m] * a[j, m];
                    }

                    next[i, j] = sum + k[i] * velocityVar * k[j];
                }
            }

            s.P = next;
            Symmetrize(ref s.P);
            AssertDiagonal(in s.P);
        }

        public static void Symmetrize(ref Matrix6 p)
        {
            for (var i = 0; i < 6; i++)
            {
                for (var j = i + 1; j < 6; j++)
                {
                    var avg = 0.5 * (p[i, j] + p[j, i]);
                    p[i, j] = avg;
                    p[j, i] = avg;
                }
            }
        }

        /// <summary>对角线断言下限（14.1 数值纪律）。</summary>
        public static void AssertDiagonal(in Matrix6 p, double floor = 1e-12)
        {
            for (var i = 0; i < 6; i++)
            {
                if (p[i, i] < floor)
                {
                    throw new InvalidOperationException(
                        $"Covariance diagonal [{i}] = {p[i, i]:E3} below floor — filter divergence.");
                }
            }
        }
    }
}
