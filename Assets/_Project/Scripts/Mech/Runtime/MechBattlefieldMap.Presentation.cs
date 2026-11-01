using System.Collections.Generic;
using UnityEngine;

namespace ACunicombat.Mechs
{
    public sealed partial class MechBattlefieldMap
    {
        private readonly Dictionary<string, Vector3> presentedContactPositions = new Dictionary<string, Vector3>();
        private readonly HashSet<string> livePresentationKeys = new HashSet<string>();
        private readonly List<string> stalePresentationKeys = new List<string>();

        private Vector3 PresentationPosition(Contact contact, double now)
        {
            // In the scenario editor Own means "player unit", not the live
            // observer transform. Rendering and picking must use the same edit.
            if(ScenarioEditing)return contact.Position;
            Vector3 observed = contact.Own ? transform.position :
                contact.Observation.TrackId.IsValid ? (Vector3)(Unity.Mathematics.float3)contact.Observation.PredictPosition(now) : contact.Position;
            if (!contact.Missile) return observed;
            if (!presentedContactPositions.TryGetValue(contact.Key, out var displayed)) displayed = observed;
            float blend = 1 - Mathf.Exp(-Time.unscaledDeltaTime / .055f);
            displayed = Vector3.Lerp(displayed, observed, blend);
            presentedContactPositions[contact.Key] = displayed;
            if (presentedContactPositions.Count > 128) PrunePresentationPositions();
            return displayed;
        }

        private void PrunePresentationPositions()
        {
            livePresentationKeys.Clear();
            foreach (var contact in contacts) livePresentationKeys.Add(contact.Key);
            stalePresentationKeys.Clear();
            foreach (var key in presentedContactPositions.Keys)
                if (!livePresentationKeys.Contains(key)) stalePresentationKeys.Add(key);
            foreach (var key in stalePresentationKeys) presentedContactPositions.Remove(key);
        }
    }
}
