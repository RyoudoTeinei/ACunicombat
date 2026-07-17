using Unity.Mathematics;

namespace ACunicombat.Simulation.World
{
    /// <summary>
    /// 全域解析遮挡（设计文档 8.2）：双精度线段-几何求交，与浮动原点无关。
    /// 气泡内 PhysX RaycastCommand 路径在 Sensors 层，不进本类型。
    /// </summary>
    public static class AnalyticOcclusion
    {
        /// <summary>线段 p0→p1 是否与球相交（小行星）。</summary>
        public static bool SegmentIntersectsSphere(double3 p0, double3 p1, double3 center, double radius)
        {
            var d = p1 - p0;
            var f = p0 - center;
            var a = math.dot(d, d);
            if (a < 1e-12)
            {
                return math.lengthsq(f) <= radius * radius;
            }

            var t = math.clamp(-math.dot(f, d) / a, 0.0, 1.0);
            var closest = f + d * t;
            return math.lengthsq(closest) <= radius * radius;
        }

        /// <summary>线段与胶囊（站体：轴 a0→a1，半径 r）相交 = 两线段最近距离 ≤ r。</summary>
        public static bool SegmentIntersectsCapsule(double3 p0, double3 p1, double3 a0, double3 a1, double radius)
        {
            var distSq = SegmentSegmentDistanceSq(p0, p1, a0, a1);
            return distSq <= radius * radius;
        }

        /// <summary>线段与 OBB（舰体）相交：转箱体局部系做 slab 测试。</summary>
        public static bool SegmentIntersectsObb(
            double3 p0, double3 p1, double3 boxCenter, quaternion boxRotation, double3 boxHalfExtents)
        {
            var inv = math.conjugate(boxRotation);
            var l0 = math.rotate(inv, (float3)(p0 - boxCenter));
            var l1 = math.rotate(inv, (float3)(p1 - boxCenter));
            double3 o = l0;
            double3 d = (double3)(float3)l1 - o;

            var tMin = 0.0;
            var tMax = 1.0;
            for (var axis = 0; axis < 3; axis++)
            {
                var e = boxHalfExtents[axis];
                if (math.abs(d[axis]) < 1e-12)
                {
                    if (o[axis] < -e || o[axis] > e)
                    {
                        return false;
                    }

                    continue;
                }

                var t1 = (-e - o[axis]) / d[axis];
                var t2 = (e - o[axis]) / d[axis];
                if (t1 > t2)
                {
                    (t1, t2) = (t2, t1);
                }

                tMin = math.max(tMin, t1);
                tMax = math.min(tMax, t2);
                if (tMin > tMax)
                {
                    return false;
                }
            }

            return true;
        }

        private static double SegmentSegmentDistanceSq(double3 p1, double3 q1, double3 p2, double3 q2)
        {
            // Ericson, Real-Time Collision Detection 5.1.9 的双精度移植。
            var d1 = q1 - p1;
            var d2 = q2 - p2;
            var r = p1 - p2;
            var a = math.dot(d1, d1);
            var e = math.dot(d2, d2);
            var f = math.dot(d2, r);
            double s, t;

            if (a < 1e-12 && e < 1e-12)
            {
                return math.lengthsq(r);
            }

            if (a < 1e-12)
            {
                s = 0.0;
                t = math.clamp(f / e, 0.0, 1.0);
            }
            else
            {
                var c = math.dot(d1, r);
                if (e < 1e-12)
                {
                    t = 0.0;
                    s = math.clamp(-c / a, 0.0, 1.0);
                }
                else
                {
                    var b = math.dot(d1, d2);
                    var denom = a * e - b * b;
                    s = denom > 1e-12 ? math.clamp((b * f - c * e) / denom, 0.0, 1.0) : 0.0;
                    t = (b * s + f) / e;
                    if (t < 0.0)
                    {
                        t = 0.0;
                        s = math.clamp(-c / a, 0.0, 1.0);
                    }
                    else if (t > 1.0)
                    {
                        t = 1.0;
                        s = math.clamp((b - c) / a, 0.0, 1.0);
                    }
                }
            }

            var c1 = p1 + d1 * s;
            var c2 = p2 + d2 * t;
            return math.lengthsq(c1 - c2);
        }
    }
}
