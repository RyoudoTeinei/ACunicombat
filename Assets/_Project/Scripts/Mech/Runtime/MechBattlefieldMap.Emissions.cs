using System;
using System.Collections.Generic;
using ACunicombat.Sensing.Contracts;
using Unity.Mathematics;
using UnityEngine;

namespace ACunicombat.Mechs
{
    public sealed partial class MechBattlefieldMap
    {
        private readonly List<EmissionReport> friendlyEmissions = new List<EmissionReport>();
        private readonly EmissionEvent[] ownJammingEmissions = new EmissionEvent[16];
        private MechElectronicWarfareController ownElectronicWarfare;

        public static Color EmissionColor(EmissionType type, bool hostile)
        {
            if (type == EmissionType.Jamming) return new Color(.96f, .74f, .12f, .9f);
            return hostile ? new Color(.96f, .22f, .16f, .9f) : new Color(.16f, .55f, 1f, .9f);
        }

        public static float EmissionFlashAlpha(double time)
        {
            return .35f + .6f * (.5f + .5f * Mathf.Sin((float)(time * math.PI_DBL * 3.2)));
        }

        public static bool HasDisplayableEmissionGeometry(float3 direction, float beamWidth, double range)
        {
            return math.all(math.isfinite(direction)) && math.lengthsq(direction) > .5f &&
                   float.IsFinite(beamWidth) && beamWidth > 0 && beamWidth < math.PI * 2 &&
                   double.IsFinite(range) && range > 0;
        }

        private void DrawObservedEmissions(double now)
        {
            if (radar == null) return;
            cooperative.CopyEmissionReports(friendlyEmissions);
            foreach (var report in friendlyEmissions)
                DrawEmissionSector(report.Emission, (Vector3)(float3)report.Emission.Origin,
                    EmissionColor(report.Emission.EmissionType, false));

            ownElectronicWarfare ??= GetComponent<MechElectronicWarfareController>();
            if (ownElectronicWarfare != null)
            {
                int count = ownElectronicWarfare.CopyOwnEmissions(ownJammingEmissions);
                if (MechCooperativeTelemetry.TryLatestEmission(ownJammingEmissions, count,
                    EmissionType.Jamming, now, out var jamming))
                    DrawEmissionSector(jamming, (Vector3)(float3)jamming.Origin,
                        EmissionColor(EmissionType.Jamming, false));
            }

            foreach (var contact in contacts)
            {
                if (contact.Own || contact.Missile || contact.Affiliation != TrackAffiliation.Hostile ||
                    !contact.Observation.TrackId.IsValid ||
                    !radar.TryAssociatePassiveTarget(contact.Observation, out var threat) ||
                    !HasDisplayableEmissionGeometry(threat.EmissionDirection, threat.EmissionBeamWidth,
                        threat.EmissionMaxRangeMeters)) continue;
                var type = threat.WaveformClass == EsmWaveformClass.NoiseJammer
                    ? EmissionType.Jamming
                    : EmissionType.RadarDwell;
                var emission = new EmissionEvent
                {
                    Direction = threat.EmissionDirection,
                    BeamWidth = threat.EmissionBeamWidth,
                    MaxRangeMeters = threat.EmissionMaxRangeMeters,
                    EmissionType = type,
                };
                DrawEmissionSector(emission, contact.Position, EmissionColor(type, true));
            }
        }

        private void DrawEmissionSector(in EmissionEvent emission, Vector3 origin, Color color)
        {
            if (!HasDisplayableEmissionGeometry(emission.Direction, emission.BeamWidth,
                emission.MaxRangeMeters)) return;
            var direction = math.normalizesafe(emission.Direction);
            var across = Vector3.Cross((Vector3)direction,
                Mathf.Abs(direction.y) < .9f ? Vector3.up : Vector3.right).normalized;
            var vertical = Vector3.Cross(across, (Vector3)direction).normalized;
            var start = ScreenToContent(Project(origin));
            float half = Mathf.Min(emission.BeamWidth * .5f, Mathf.PI * .49f);
            float reach = (float)emission.MaxRangeMeters;
            color.a *= EmissionFlashAlpha(Time.unscaledTimeAsDouble);
            var rib = new Color(color.r, color.g, color.b, color.a * .14f);
            var rim = new Color(color.r, color.g, color.b, color.a * .72f);
            Vector2 previousRim = default;
            for (int side = 0; side <= 12; side++)
            {
                float angle = side * Mathf.PI * 2 / 12;
                var edge = (Vector3)direction * Mathf.Cos(half) +
                           (across * Mathf.Cos(angle) + vertical * Mathf.Sin(angle)) * Mathf.Sin(half);
                var endpoint = origin + (Vector3)math.normalizesafe(edge) * reach;
                var projected = ScreenToContent(Project(endpoint));
                if (side < 12) RadarRay(start, projected, rib, .8f);
                if (side > 0) Line(previousRim, projected, rim, 1f);
                previousRim = projected;
            }
            RadarRay(start, ScreenToContent(Project(origin + (Vector3)direction * reach)), color, 1.1f);
        }
    }
}
