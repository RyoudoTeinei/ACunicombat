using System;
using System.Collections.Generic;
using UnityEngine;

namespace ACunicombat.Mechs
{
    [Serializable]
    public sealed class MechThrusterNozzleLayoutData
    {
        public const int CurrentFormatVersion = 2;
        public const string VisualPackageKind = "thruster-nozzle-configuration";

        public int formatVersion = CurrentFormatVersion;
        public string packageKind = VisualPackageKind;
        public List<MechThrusterNozzleLayoutEntry> nozzles = new();

        public void NormalizePackageMetadata()
        {
            formatVersion = CurrentFormatVersion;
            packageKind = VisualPackageKind;
            nozzles ??= new List<MechThrusterNozzleLayoutEntry>();
            for (var i = 0; i < nozzles.Count; i++)
            {
                nozzles[i]?.NormalizePhysicalSettings();
            }
        }

        public MechThrusterNozzleLayoutData Clone()
        {
            var clone = JsonUtility.FromJson<MechThrusterNozzleLayoutData>(
                JsonUtility.ToJson(this));
            clone ??= new MechThrusterNozzleLayoutData();
            clone.NormalizePackageMetadata();
            return clone;
        }
    }

    [Serializable]
    public sealed class MechThrusterNozzleLayoutEntry
    {
        public string id;
        public string sourceId;
        public string displayName;
        public bool enabled = true;
        public Vector3 localPositionOffset;
        public Vector3 localEulerOffset;
        [Min(0.05f)] public float radiusScale = 1f;
        [Min(0.05f)] public float lengthScale = 1f;
        public bool hasPhysicalSettings;
        [Min(0f)] public float ratedThrustKn = 600f;
        public bool vectoringEnabled;
        [Range(0f, 89f)] public float maximumVectorAngleDegrees;

        public void NormalizePhysicalSettings()
        {
            if (!hasPhysicalSettings)
            {
                hasPhysicalSettings = true;
                ratedThrustKn = 600f;
                vectoringEnabled = false;
                maximumVectorAngleDegrees = 0f;
            }

            ratedThrustKn = Mathf.Max(0f, ratedThrustKn);
            maximumVectorAngleDegrees = Mathf.Clamp(maximumVectorAngleDegrees, 0f, 89f);
            if (!vectoringEnabled)
            {
                maximumVectorAngleDegrees = 0f;
            }
        }

        public MechThrusterNozzleLayoutEntry Clone(string newId, string newDisplayName)
        {
            return new MechThrusterNozzleLayoutEntry
            {
                id = newId,
                sourceId = sourceId,
                displayName = newDisplayName,
                enabled = enabled,
                localPositionOffset = localPositionOffset,
                localEulerOffset = localEulerOffset,
                radiusScale = radiusScale,
                lengthScale = lengthScale,
                hasPhysicalSettings = true,
                ratedThrustKn = ratedThrustKn,
                vectoringEnabled = vectoringEnabled,
                maximumVectorAngleDegrees = maximumVectorAngleDegrees,
            };
        }
    }

    public readonly struct MechThrusterNozzleEditorState
    {
        public MechThrusterNozzleEditorState(
            string id,
            string sourceId,
            string displayName,
            Vector3 worldPosition,
            Quaternion worldRotation,
            float radiusScale,
            float lengthScale,
            float ratedThrustKn,
            bool vectoringEnabled,
            float maximumVectorAngleDegrees)
        {
            Id = id;
            SourceId = sourceId;
            DisplayName = displayName;
            WorldPosition = worldPosition;
            WorldRotation = worldRotation;
            RadiusScale = radiusScale;
            LengthScale = lengthScale;
            RatedThrustKn = ratedThrustKn;
            VectoringEnabled = vectoringEnabled;
            MaximumVectorAngleDegrees = maximumVectorAngleDegrees;
        }

        public string Id { get; }
        public string SourceId { get; }
        public string DisplayName { get; }
        public Vector3 WorldPosition { get; }
        public Quaternion WorldRotation { get; }
        public float RadiusScale { get; }
        public float LengthScale { get; }
        public float RatedThrustKn { get; }
        public bool VectoringEnabled { get; }
        public float MaximumVectorAngleDegrees { get; }
    }
}
