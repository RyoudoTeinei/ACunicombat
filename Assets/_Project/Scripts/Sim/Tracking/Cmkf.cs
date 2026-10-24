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
        public static void PredictCv(ref FilterState s, double dt, double qPosition, double qVelocity, bool integrateAccelerationNoise = false)
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
                if(integrateAccelerationNoise)
                {
                    // Continuous white acceleration: Qpp=q*dt^3/3, Qpv=q*dt^2/2.
                    // Injecting unrelated metres of position noise at every short tick
                    // erases precise range separation between neighbouring salvo rounds.
                    dst[i,i]+=qVelocity*dt*dt*dt/3;
                    dst[i,i+3]+=qVelocity*dt*dt/2;
                    dst[i+3,i]+=qVelocity*dt*dt/2;
                }
            }

            s.P = dst;
            Symmetrize(ref s.P);
        }

        /// <summary>
        /// 球→笛卡尔转换测量（14.1）：z = p_sensor + r·d̂；
        /// R_cart = σr²·d̂d̂ᵀ + r²σθ²·(I − d̂d̂ᵀ)（各向同性角误差的精确形式）。
        /// 使用二阶角度去偏和正半定外积构造；关联/递推更新使用预测视线坐标的新息。
        /// </summary>
        public static void ConvertMeasurement(
            double3 sensorPosition, float range, float3 direction, float rangeVar, float angleVar,
            out double3 zCartesian, out Matrix6 rCartesian3x3)
        {
            // float-normalized LOS is not exactly unit length in double precision.
            // Construct R from positive outer products, not I-ddT cancellation.
            double3 d = math.normalizesafe((double3)direction, new double3(0,0,1));
            double angular=math.max(0.0,(double)angleVar);
            double correctedRange=range*(1.0+angular);
            zCartesian = sensorPosition + d * correctedRange;
            double radial=math.max(1e-9,(double)rangeVar)+(double)range*range*angular*angular;
            var tangent=math.normalizesafe(math.cross(d,math.abs(d.y)<.9?new double3(0,1,0):new double3(1,0,0)));
            var bitangent=math.cross(d,tangent);
            var cross = correctedRange*correctedRange*angular;
            rCartesian3x3 = default;
            for (var i = 0; i < 3; i++)
            {
                for (var j = 0; j < 3; j++)
                {
                    var outer = d[i] * d[j];
                    rCartesian3x3[i, j] = radial * outer + cross * (tangent[i]*tangent[j]+bitangent[i]*bitangent[j]);
                }
            }
        }

        /// <summary>
        /// 位置伪测量 3D 更新，Joseph 形式：P = (I−KH)P(I−KH)ᵀ + KRKᵀ。
        /// 3×3 新息协方差采用缩放 Cholesky 求解；非正定输入拒绝更新。
        /// 实现时必须以 NEES/NIS（22.2）作为验收，不得用目测代替。
        /// </summary>
        public static void UpdatePosition(ref FilterState s, double3 z, in Matrix6 r3x3)
        {
            TryUpdatePosition(ref s,z,r3x3);
        }
        public static bool TryUpdatePosition(ref FilterState s,double3 z,in Matrix6 r3x3)
        {
            if(!math.all(math.isfinite(z))||!ValidCovariance(r3x3,3)||!Healthy(s))return false;
            var candidate=s;
            UpdatePositionCandidate(ref candidate,z,r3x3);
            if(!Healthy(candidate))return false;
            s=candidate;return true;
        }
        public static bool TryUpdateRadarPosition(ref FilterState state,double3 observer,float range,float3 direction,float rangeVariance,float angleVariance)
        {
            // Relinearize the curved range/bearing observation after a large angular
            // correction. Every candidate starts from the SAME prior: this is one
            // observation, not six independent hits or repeated covariance shrinkage.
            var prior=state;var linearization=state;var result=state;
            var priorLos=math.normalizesafe(state.Position-observer,new double3(0,0,1));
            var observedLos=math.normalizesafe((double3)direction,priorLos);
            double positionVariance=state.P[0,0]+state.P[1,1]+state.P[2,2];
            // The tangent-plane approximation is not valid across the sensor origin or
            // over a large bearing rotation. Use the unbiased converted observation there.
            if(math.dot(priorLos,observedLos)<math.cos(.35)||positionVariance>.25*range*range)
            {
                ConvertMeasurement(observer,range,direction,rangeVariance,angleVariance,out var observed,out var noise);
                return TryUpdatePosition(ref state,observed,noise);
            }
            for(int iteration=0;iteration<6;iteration++)
            {
                BuildSphericalInnovation(linearization,observer,range,direction,rangeVariance,angleVariance,out var position,out var covariance);
                var candidate=prior;
                if(!TryUpdatePosition(ref candidate,position,covariance))return false;
                double change=math.distance(candidate.Position,linearization.Position);
                result=candidate;linearization=candidate;
                if(change<1e-5)break;
            }
            state=result;return true;
        }

        public static bool TryUpdateRadarMeasurement(ref FilterState state,double3 observer,double3 observerVelocity,
            float range,float3 direction,float rangeVariance,float angleVariance,float radialVelocity,float velocityVariance)
        {
            var candidate=state;
            if(!TryUpdateRadarPosition(ref candidate,observer,range,direction,rangeVariance,angleVariance))return false;
            if(float.IsFinite(radialVelocity) && !TryUpdateRadarDoppler(ref candidate,observer,observerVelocity,
                radialVelocity,math.max(velocityVariance,1e-3f)))return false;
            state=candidate;return true;
        }

        private static void UpdatePositionCandidate(ref FilterState s, double3 z, in Matrix6 r3x3)
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
            // Commit only after full finite/PSD validation in the public wrapper.
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

        // -2 log likelihood (up to a common constant). NIS alone rewards stale,
        // diffuse tracks for having a huge gate and lets them steal precise returns.
        public static double PositionAssociationCost(in FilterState prior,in Matrix6 noise,double nis)
        {
            double a=prior.P[0,0]+noise[0,0];if(a<=0)return double.PositiveInfinity;
            double l00=Math.Sqrt(a),l10=(prior.P[1,0]+noise[1,0])/l00,l20=(prior.P[2,0]+noise[2,0])/l00;
            double b=prior.P[1,1]+noise[1,1]-l10*l10;if(b<=0)return double.PositiveInfinity;
            double l11=Math.Sqrt(b),l21=(prior.P[2,1]+noise[2,1]-l20*l10)/l11;
            double c=prior.P[2,2]+noise[2,2]-l20*l20-l21*l21;if(c<=0)return double.PositiveInfinity;
            return nis+2*(Math.Log(l00)+Math.Log(l11))+Math.Log(c);
        }

        private static bool TryInvertSymmetric3(ReadOnlySpan<double> m, Span<double> inverse)
        {
            double scale=math.max(m[0],math.max(m[4],m[8]));
            if(!double.IsFinite(scale)||scale<=0)return false;
            Span<double> l=stackalloc double[9];l.Clear();
            for(int i=0;i<3;i++)for(int j=0;j<=i;j++)
            {
                double value=(m[i*3+j]+m[j*3+i])*.5/scale;
                for(int k=0;k<j;k++)value-=l[i*3+k]*l[j*3+k];
                if(!double.IsFinite(value))return false;
                if(i==j){if(value<=0)return false;l[i*3+j]=Math.Sqrt(value);}
                else l[i*3+j]=value/l[j*3+j];
            }
            for(int col=0;col<3;col++)
            {
                Span<double> y=stackalloc double[3];Span<double> x=stackalloc double[3];
                for(int i=0;i<3;i++){double v=i==col?1:0;for(int k=0;k<i;k++)v-=l[i*3+k]*y[k];y[i]=v/l[i*3+i];}
                for(int i=2;i>=0;i--){double v=y[i];for(int k=i+1;k<3;k++)v-=l[k*3+i]*x[k];x[i]=v/l[i*3+i];inverse[i*3+col]=x[i]/scale;}
            }
            return true;
        }

        /// <summary>
        /// 径向速度标量第二阶段更新（14.1）：H = [0₃, d̂ᵀ]（视线方向对速度的投影）。
        /// 标量更新无需矩阵求逆，Joseph 形式完整展开。
        /// </summary>
        public static void UpdateRadialVelocity(
            ref FilterState s, float3 direction, float measuredRadialVelocity, float velocityVar)
        {
            if(!math.all(math.isfinite(direction))||math.lengthsq(direction)<1e-12f||!float.IsFinite(measuredRadialVelocity)||!float.IsFinite(velocityVar)||velocityVar<=0)return;
            var candidate=s;
            UpdateRadialVelocityCandidate(ref candidate,direction,measuredRadialVelocity,velocityVar);
            if(Healthy(candidate))s=candidate;
        }
        // Radar Doppler is h(p,v) = dot(v-v_sensor, normalize(p-p_sensor)).
        // Its LOS is uncertain STATE, not an exact axis supplied by a noisy angle measurement.
        public static void UpdateRadarDoppler(ref FilterState state,double3 observerPosition,double3 observerVelocity,float measured,float variance)
            =>TryUpdateRadarDoppler(ref state,observerPosition,observerVelocity,measured,variance);

        public static bool TryUpdateRadarDoppler(ref FilterState state,double3 observerPosition,double3 observerVelocity,float measured,float variance)
        {
            if(!float.IsFinite(measured)||!float.IsFinite(variance)||variance<=0)return false;
            var relative=state.Position-observerPosition;double range=math.length(relative);if(range<1)return false;
            var los=relative/range;var velocity=state.Velocity-observerVelocity;double predicted=math.dot(velocity,los);
            var angularDerivative=(velocity-los*predicted)/range;
            Span<double> h=stackalloc double[6]{angularDerivative.x,angularDerivative.y,angularDerivative.z,los.x,los.y,los.z};
            double innovation=measured-predicted,innovationVariance=variance;
            for(int row=0;row<6;row++)for(int col=0;col<6;col++)innovationVariance+=h[row]*state.P[row,col]*h[col];
            if(!double.IsFinite(innovationVariance)||innovationVariance<=0||innovation*innovation/innovationVariance>25)return false;
            Span<double> gain=stackalloc double[6];
            for(int row=0;row<6;row++){double value=0;for(int col=0;col<6;col++)value+=state.P[row,col]*h[col];gain[row]=value/innovationVariance;}
            var next=state;next.Position+=new double3(gain[0],gain[1],gain[2])*innovation;next.Velocity+=new double3(gain[3],gain[4],gain[5])*innovation;
            Matrix6 a=default,ap=default,covariance=default;
            for(int row=0;row<6;row++)for(int col=0;col<6;col++)a[row,col]=(row==col?1:0)-gain[row]*h[col];
            for(int row=0;row<6;row++)for(int col=0;col<6;col++)for(int k=0;k<6;k++)ap[row,col]+=a[row,k]*state.P[k,col];
            for(int row=0;row<6;row++)for(int col=0;col<6;col++){for(int k=0;k<6;k++)covariance[row,col]+=ap[row,k]*a[col,k];covariance[row,col]+=gain[row]*variance*gain[col];}
            next.P=covariance;Symmetrize(ref next.P);if(!Healthy(next))return false;state=next;return true;
        }
        private static void UpdateRadialVelocityCandidate(
            ref FilterState s, float3 direction, float measuredRadialVelocity, float velocityVar)
        {
            double3 d = math.normalize((double3)direction);

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
            // Commit only after full finite/PSD validation in the public wrapper.
        }

        /// <summary>Range/angle innovation about the predicted LOS. Avoids treating a curved range shell as a flat ellipsoid.</summary>
        public static void BuildSphericalInnovation(in FilterState state,double3 observer,float range,float3 direction,
            float rangeVar,float angleVar,out double3 pseudoPosition,out Matrix6 covariance)
        {
            var relative=state.Position-observer;double predictedRange=math.max(1,math.length(relative));
            var n=math.normalizesafe(relative,new double3(0,0,1));var d=math.normalizesafe((double3)direction,n);
            var t=math.normalizesafe(math.cross(n,math.abs(n.y)<.9?new double3(0,1,0):new double3(1,0,0)));
            var b=math.cross(n,t);double forward=math.dot(d,n);
            double a=math.atan2(math.dot(d,t),forward),e=math.atan2(math.dot(d,b),forward);
            pseudoPosition=state.Position+n*(range-predictedRange)+predictedRange*(t*a+b*e);
            covariance=default;double angular=predictedRange*predictedRange*math.max(1e-12,angleVar);
            double radialPrediction=0;for(int i=0;i<3;i++)for(int j=0;j<3;j++)radialPrediction+=n[i]*state.P[i,j]*n[j];
            double transversePrediction=math.max(0,state.P[0,0]+state.P[1,1]+state.P[2,2]-radialPrediction);
            double angularLinearization=math.max(0,angleVar)+transversePrediction/(2*predictedRange*predictedRange);
            // Second-order shell curvature prevents coarse-only updates from manufacturing
            // precise angular constraints through successive, rotating range Jacobians.
            double radial=math.max(1e-9,rangeVar)+predictedRange*predictedRange*angularLinearization*angularLinearization;
            for(int i=0;i<3;i++)for(int j=0;j<3;j++)
                covariance[i,j]=radial*n[i]*n[j]+angular*(t[i]*t[j]+b[i]*b[j]);
        }

        public static bool Healthy(in FilterState state) => math.all(math.isfinite(state.Position)) &&
            math.all(math.isfinite(state.Velocity)) && double.IsFinite(state.Time) && ValidCovariance(state.P,6);
        private static bool ValidCovariance(in Matrix6 p,int size)
        {
            double scale=1;
            for(int i=0;i<size;i++){if(!double.IsFinite(p[i,i])||p[i,i]<0)return false;scale=math.max(scale,p[i,i]);}
            Span<double> l=stackalloc double[36];l.Clear();
            for(int i=0;i<size;i++)for(int j=0;j<=i;j++)
            {
                double value=(p[i,j]+p[j,i])*.5/scale;
                if(!double.IsFinite(value))return false;
                if(i==j)value+=1e-14; // Validation tolerance only; never mutate or clamp published covariance.
                for(int k=0;k<j;k++)value-=l[i*6+k]*l[j*6+k];
                if(i==j){if(value<=0)return false;l[i*6+j]=Math.Sqrt(value);}
                else l[i*6+j]=value/l[j*6+j];
            }
            return true;
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
