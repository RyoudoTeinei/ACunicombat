using System;
using ACunicombat.ElectronicWarfare;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Driver;
using Unity.Mathematics;
using UnityEngine;

namespace ACunicombat.Mechs
{
    public enum MechElectronicWarfareMode : byte { Silent, SelfDefense, Directed }

    public static class MechElectronicWarfareTargeting
    {
        public static bool TrySelectSelfDefenseThreat(
            ThreatSnapshot[] threats, int count, double now, out ThreatSnapshot selected)
        {
            selected = default;
            float best = float.NegativeInfinity;
            if (threats == null) return false;
            for (int i = 0; i < count && i < threats.Length; i++)
            {
                var candidate = threats[i];
                if (candidate.WaveformClass != EsmWaveformClass.SearchRadar &&
                    candidate.WaveformClass != EsmWaveformClass.FireControlRadar ||
                    now < candidate.LastSeenTime || now - candidate.LastSeenTime > 1.5 ||
                    candidate.ContactCount < 2 || candidate.IdentificationConfidence < .5f ||
                    !math.all(math.isfinite(candidate.DirectionWorld)) ||
                    math.lengthsq(candidate.DirectionWorld) < .5f) continue;
                float score = (candidate.WaveformClass == EsmWaveformClass.FireControlRadar ? 100f : 0f) +
                              (int)candidate.Level * 20f + candidate.ReceivedPowerDb;
                if (score <= best) continue;
                best = score;
                selected = candidate;
            }
            return best > float.NegativeInfinity;
        }
    }

    /// <summary>
    /// Scene adapter for one directional noise channel. Tactical selection consumes only
    /// public ESM/mission observations; scene transforms are used solely for physical delivery.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MechElectronicWarfareController : MonoBehaviour
    {
        public const ushort NoiseWaveformId = 30;
        private const int MaximumObservers = 16;

        private sealed class ObserverRegistration
        {
            public SimulationDriver Driver;
            public Transform Observer;
            public MechRadarController Radar;
            public SensorId SensorId;
        }

        private readonly ObserverRegistration[] observers = new ObserverRegistration[MaximumObservers];
        private readonly EmissionEvent[] ownEmissions = new EmissionEvent[MaximumObservers];
        private readonly ThreatSnapshot[] threats = new ThreatSnapshot[32];
        private readonly Guid liveEmitterIdentity = Guid.NewGuid();
        private MechRadarController radar;
        private MechRuntimeState runtime;
        private MechFleetController fleet;
        private CombatTestTarget body;
        private MechElectronicWarfarePerformance performance = new MechElectronicWarfarePerformance();
        private double nextEmissionTime;
        private double nextConfigurationTime;
        private int ownEmissionCount;

        public bool Configured { get; private set; }
        public bool IsJamming => ownEmissionCount > 0;
        public int ActiveVictimCount => ownEmissionCount;
        public float StandoffRangeMeters => performance.maximumRangeMeters * .8f;
        public MechElectronicWarfareMode CurrentMode { get; private set; }
        public string LastDecision { get; private set; } = "NotConfigured";

        private void Awake()
        {
            radar = GetComponent<MechRadarController>();
            runtime = GetComponent<MechRuntimeState>();
            fleet = GetComponent<MechFleetController>();
            body = GetComponent<CombatTestTarget>();
            RefreshConfiguration();
        }

        private void RefreshConfiguration()
        {
            Configured = false;
            if (runtime?.CurrentLoadout == null ||
                MechSpecialistKits.Kind(runtime.CurrentLoadout) != MechSpecialistKitKind.ElectronicWarfare) return;
            foreach (var assignment in runtime.CurrentLoadout.Assignments)
            {
                if (assignment?.part == null ||
                    assignment.part.SpecialistKit != MechSpecialistKitKind.ElectronicWarfare) continue;
                performance = assignment.part.ElectronicWarfarePerformance.Clone();
                Configured = performance.HasData;
                LastDecision = Configured ? "Ready" : "InvalidEquipment";
                return;
            }
        }

        public void RegisterObserverDriver(
            SimulationDriver observerDriver, Transform observer, MechRadarController observerRadar)
        {
            if (observerDriver == null || observer == null || observerRadar == null) return;
            for (int i = 0; i < observers.Length; i++)
            {
                if (observers[i] == null || observers[i].Driver == observerDriver)
                {
                    observers[i] = new ObserverRegistration
                    {
                        Driver = observerDriver,
                        Observer = observer,
                        Radar = observerRadar,
                        SensorId = observerDriver.PrimaryRadarIdentity,
                    };
                    return;
                }
            }
        }

        public void UnregisterObserverDriver(SimulationDriver observerDriver)
        {
            for (int i = 0; i < observers.Length; i++)
            {
                if (observers[i] == null || observers[i].Driver != observerDriver) continue;
                observerDriver.ReleaseExternalIffEmitter(liveEmitterIdentity);
                observers[i] = null;
            }
        }

        private void FixedUpdate()
        {
            ownEmissionCount = 0;
            if (radar == null || runtime == null || body != null && !body.IsAlive) return;
            double now = radar.SimTimeSeconds;
            if (now >= nextConfigurationTime)
            {
                nextConfigurationTime = now + .5;
                RefreshConfiguration();
            }
            if (!Configured || now + 1e-6 < nextEmissionTime) return;
            nextEmissionTime = now + Math.Max(.04, performance.emissionIntervalSeconds);
            if (!TrySelectEmission(now, out var direction, out var band))
            {
                CurrentMode = fleet != null && fleet.HasSpatialMission &&
                              fleet.TaskGroup == FleetTaskGroup.ElectronicWarfare
                    ? MechElectronicWarfareMode.Directed
                    : MechElectronicWarfareMode.SelfDefense;
                LastDecision = "NoFreshEmitterEvidence";
                return;
            }

            var ownPosition = (double3)(float3)transform.position;
            float beamWidth = performance.beamWidthDegrees * Mathf.Deg2Rad;
            float mainlobePowerDb = 10f * Mathf.Log10(performance.equivalentRadiatedPowerWatts);
            for (int i = 0; i < observers.Length && ownEmissionCount < ownEmissions.Length; i++)
            {
                var registration = observers[i];
                if (registration?.Driver == null || registration.Observer == null || registration.Radar == null) continue;
                var victimBody = registration.Radar.GetComponent<CombatTestTarget>();
                if (victimBody != null && body != null && victimBody.TeamId == body.TeamId) continue;
                var hardware = registration.Radar.RadarHardware;
                if (!hardware.IsValid) continue;
                if (!EwInjectionSystem.TryCreateNoiseContribution(
                        registration.SensorId, default, ownPosition,
                        (double3)(float3)registration.Observer.position, direction, beamWidth,
                        performance.maximumRangeMeters, performance.equivalentRadiatedPowerWatts,
                        hardware.GainLinear, band, hardware.FrequencyHz,
                        performance.noiseBandwidthMHz * 1e6f, out var contribution)) continue;
                registration.Driver.QueueJamContribution(contribution);
                var emission = new EmissionEvent
                {
                    StartTime = now,
                    EndTime = now + performance.emissionIntervalSeconds * 1.5,
                    Origin = ownPosition,
                    Direction = direction,
                    BeamWidth = beamWidth,
                    MaxRangeMeters = performance.maximumRangeMeters,
                    FrequencyBand = band,
                    WaveformId = NoiseWaveformId,
                    EmissionType = EmissionType.Jamming,
                    MainlobePowerDb = mainlobePowerDb,
                    SidelobeLevelDb = performance.sidelobeLevelDb,
                };
                registration.Driver.QueueExternalEmission(liveEmitterIdentity, emission);
                ownEmissions[ownEmissionCount++] = emission;
            }
            LastDecision = ownEmissionCount > 0 ? "NoiseJamming" : "NoVictimInBeam";
        }

        private bool TrySelectEmission(double now, out float3 direction, out ushort band)
        {
            direction = default;
            band = 1;
            if (fleet != null && fleet.HasSpatialMission &&
                fleet.TaskGroup == FleetTaskGroup.ElectronicWarfare)
            {
                CurrentMode = MechElectronicWarfareMode.Directed;
                if (fleet.TryGetElectronicWarfareEmitter(out var source))
                {
                    direction = math.normalizesafe(source.DirectionWorld);
                    band = source.FrequencyBand;
                    return math.lengthsq(direction) > .5f;
                }
            }
            else CurrentMode = MechElectronicWarfareMode.SelfDefense;
            int count = radar.CopyThreats(threats);
            if (!MechElectronicWarfareTargeting.TrySelectSelfDefenseThreat(threats, count, now, out var selected))
                return false;
            direction = math.normalizesafe(selected.DirectionWorld);
            band = selected.FrequencyBand;
            return math.lengthsq(direction) > .5f;
        }

        public int CopyOwnEmissions(EmissionEvent[] destination)
        {
            if (destination == null) return 0;
            int count = Math.Min(ownEmissionCount, destination.Length);
            Array.Copy(ownEmissions, destination, count);
            return count;
        }

        private void OnDestroy()
        {
            for (int i = 0; i < observers.Length; i++)
            {
                if (observers[i]?.Driver == null) continue;
                observers[i].Driver.ReleaseExternalIffEmitter(liveEmitterIdentity);
                observers[i] = null;
            }
        }
    }
}
