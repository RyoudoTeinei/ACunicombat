using UnityEngine;
using UnityEngine.Rendering;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// 以当前速度、持续操纵加速度、短时 QB 与环境重力绘制航轨预测线。
    /// 它只负责可视化，不参与运动积分，也不会把预测结果反馈给飞控。
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(LineRenderer))]
    public sealed class MechTrajectoryPredictor : MonoBehaviour
    {
        [SerializeField] private MechFlightMotionController motion;
        [SerializeField] [Range(16, 192)] private int sampleCount = 128;
        [SerializeField] private float predictionSeconds = 12f;
        [SerializeField] private float maximumDisplayDistance = 3000f;
        [SerializeField] private float minimumReferenceSpeed = 8f;
        [SerializeField] private float originHeight = 4f;
        [SerializeField] private float originForwardOffset = 3f;
        [SerializeField] private float lineWidth = 0.16f;
        [SerializeField] private Color trajectoryColor = new(0.12f, 1f, 0.32f, 0.95f);

        private LineRenderer line;
        private Material runtimeMaterial;

        public void Configure(MechFlightMotionController controller)
        {
            motion = controller;
            EnsureRenderer();
        }

        private void Awake()
        {
            if (motion == null)
            {
                motion = GetComponent<MechFlightMotionController>();
            }

            EnsureRenderer();
        }

        private void LateUpdate()
        {
            if (motion == null)
            {
                line.positionCount = 0;
                return;
            }

            var logicalForward = motion.LogicalRotation * Vector3.forward;
            var predictedVelocity = motion.Velocity;
            if (predictedVelocity.magnitude < minimumReferenceSpeed &&
                motion.CurrentControlAcceleration.sqrMagnitude < 0.0001f)
            {
                predictedVelocity = logicalForward * minimumReferenceSpeed;
            }

            var duration = Mathf.Max(0.1f, predictionSeconds);
            var stepSeconds = duration / (sampleCount - 1f);
            var controlAcceleration = motion.CurrentControlAcceleration;
            var quickBoostAcceleration = motion.CurrentQuickBoostAcceleration;
            var quickBoostRemaining = motion.CurrentQuickBoostRemaining;
            var gravity = motion.Grounded
                ? Vector3.zero
                : Vector3.down * motion.GravityAcceleration;
            var displayOffset = Vector3.up * originHeight
                                + logicalForward * originForwardOffset;
            var predictedPosition = transform.position;
            var travelledDistance = 0f;
            var previousDisplayPosition = predictedPosition + displayOffset;

            line.positionCount = sampleCount;
            line.SetPosition(0, previousDisplayPosition);
            for (var i = 1; i < sampleCount; i++)
            {
                predictedVelocity += (controlAcceleration + gravity) * stepSeconds;

                if (quickBoostRemaining > 0f)
                {
                    var quickBoostStep = Mathf.Min(stepSeconds, quickBoostRemaining);
                    predictedVelocity += quickBoostAcceleration * quickBoostStep;
                    quickBoostRemaining -= quickBoostStep;
                }

                if (!motion.Grounded && controlAcceleration.sqrMagnitude < 0.0001f &&
                    quickBoostRemaining <= 0f && motion.AirCoastingDragAcceleration > 0f)
                {
                    predictedVelocity = Vector3.MoveTowards(
                        predictedVelocity,
                        Vector3.zero,
                        motion.AirCoastingDragAcceleration * stepSeconds);
                }

                predictedPosition += predictedVelocity * stepSeconds;
                var displayPosition = predictedPosition + displayOffset;
                var segmentDistance = Vector3.Distance(previousDisplayPosition, displayPosition);
                if (travelledDistance + segmentDistance >= maximumDisplayDistance)
                {
                    var remainingDistance = Mathf.Max(
                        0f, maximumDisplayDistance - travelledDistance);
                    var cappedPosition = segmentDistance > Mathf.Epsilon
                        ? Vector3.Lerp(
                            previousDisplayPosition,
                            displayPosition,
                            remainingDistance / segmentDistance)
                        : previousDisplayPosition;
                    for (var tail = i; tail < sampleCount; tail++)
                    {
                        line.SetPosition(tail, cappedPosition);
                    }

                    break;
                }

                travelledDistance += segmentDistance;
                line.SetPosition(i, displayPosition);
                previousDisplayPosition = displayPosition;
            }
        }

        private void EnsureRenderer()
        {
            line ??= GetComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.loop = false;
            line.alignment = LineAlignment.View;
            line.textureMode = LineTextureMode.Stretch;
            line.numCapVertices = 4;
            line.numCornerVertices = 2;
            line.startWidth = lineWidth;
            line.endWidth = lineWidth * 0.35f;
            line.startColor = trajectoryColor;
            line.endColor = new Color(
                trajectoryColor.r,
                trajectoryColor.g,
                trajectoryColor.b,
                0f);
            line.shadowCastingMode = ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.lightProbeUsage = LightProbeUsage.Off;
            line.reflectionProbeUsage = ReflectionProbeUsage.Off;

            var gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(trajectoryColor, 0f),
                    new GradientColorKey(trajectoryColor, 1f),
                },
                new[]
                {
                    new GradientAlphaKey(trajectoryColor.a, 0f),
                    new GradientAlphaKey(trajectoryColor.a * 0.75f, 0.65f),
                    new GradientAlphaKey(0f, 1f),
                });
            line.colorGradient = gradient;

            if (runtimeMaterial == null)
            {
                var shader = Shader.Find("Sprites/Default")
                             ?? Shader.Find("Universal Render Pipeline/Particles/Unlit")
                             ?? Shader.Find("Universal Render Pipeline/Unlit");
                if (shader != null)
                {
                    runtimeMaterial = new Material(shader)
                    {
                        name = "Runtime Trajectory HUD Material",
                        hideFlags = HideFlags.HideAndDontSave,
                    };
                    if (runtimeMaterial.HasProperty("_Color"))
                    {
                        runtimeMaterial.SetColor("_Color", Color.white);
                    }

                    if (runtimeMaterial.HasProperty("_BaseColor"))
                    {
                        runtimeMaterial.SetColor("_BaseColor", Color.white);
                    }

                    line.sharedMaterial = runtimeMaterial;
                }
            }
        }

        private void OnDestroy()
        {
            if (runtimeMaterial != null)
            {
                Destroy(runtimeMaterial);
            }
        }
    }
}
