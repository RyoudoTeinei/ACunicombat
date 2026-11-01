using UnityEngine;
namespace ACunicombat.Mechs
{
    public sealed class MechWeaponDeployment
    {
        private enum Phase { Stowed, Deploying, Ready, Opening, Firing, Closing, Stowing, Fallback }
        private Phase phase;
        private readonly MechEquipmentAnimationPlayer player;
        private readonly bool canonical;
        private readonly float cyclePlaybackSpeed=1f;
        private readonly string fallback;
        private bool requested, release;
        private double fallbackAt;
        public bool CanRequest => phase == Phase.Stowed || phase == Phase.Ready || (phase == Phase.Deploying && !requested);
        public MechWeaponDeployment(MechAssemblyRecord record)
        {
            player = record.instance.GetComponentInChildren<MechEquipmentAnimationPlayer>(true);
            if (player == null && record.part.EquipmentAnimations.Count > 0) player = record.instance.AddComponent<MechEquipmentAnimationPlayer>();
            if (player != null && player.Actions.Count == 0)
                player.Configure(record.part.EquipmentAnimations, record.part.DefaultEquipmentActionId, false);
            float naturalCycle=0;
            foreach(var action in record.part.EquipmentAnimations)
                if(action.Clip!=null&&(action.ActionId=="a000_800120"||action.ActionId=="a000_800210"||action.ActionId=="a000_800320"))naturalCycle+=action.Clip.length;
            float requestedCycle=MechMissileLauncherCatalog.Find(record.part.PartId)?.LaunchCycleSeconds??record.part.WeaponPerformance.missileLaunchCycleSeconds;
            if(requestedCycle>0&&naturalCycle>0)cyclePlaybackSpeed=naturalCycle/requestedCycle;
            canonical = player != null;
            foreach (var id in new[] { "a000_800110", "a000_800120", "a000_800210", "a000_800320", "a000_800310" })
                if (player == null || !player.HasAction(id)) canonical = false;
            if (player != null)
                foreach (var id in new[] { "a000_800210", "a000_000100", "a000_800410", player.DefaultActionId })
                    if (!string.IsNullOrEmpty(id) && player.HasAction(id)) { fallback = id; break; }
        }
        public bool Request(double now)
        {
            if (!CanRequest) return false;
            requested = true; release = false;
            if (phase == Phase.Deploying) return true;
            if (player == null) { phase = Phase.Fallback; fallbackAt = now + 0.18; return true; }
            if (!canonical)
            {
                if (string.IsNullOrEmpty(fallback) || !Play(fallback, Phase.Fallback)) return false;
                release = true; return true;
            }
            return phase == Phase.Ready ? Play("a000_800120", Phase.Opening) : Play("a000_800110", Phase.Deploying);
        }
        public void Tick(double now, bool keepReady)
        {
            if (player == null)
            { if (requested && phase == Phase.Fallback && now >= fallbackAt) release = true; return; }
            if (player.IsPlaying) return;
            switch (phase)
            {
                case Phase.Stowed: if (keepReady && canonical) Play("a000_800110", Phase.Deploying); break;
                case Phase.Deploying:
                    phase = Phase.Ready;
                    if (requested) Play("a000_800120", Phase.Opening);
                    else if (!keepReady) Play("a000_800310", Phase.Stowing);
                    break;
                case Phase.Ready: if (!keepReady && !requested) Play("a000_800310", Phase.Stowing); break;
                case Phase.Opening: release = requested; Play("a000_800210", Phase.Firing); break;
                case Phase.Firing: Play("a000_800320", Phase.Closing); break;
                case Phase.Closing: if (keepReady) phase = Phase.Ready; else Play("a000_800310", Phase.Stowing); break;
                case Phase.Stowing: Rest(); break;
                case Phase.Fallback: if (!requested) Rest(); break;
            }
        }
        public bool Ready => release;
        public void Consume()
        { release = false; requested = false; if (player == null) phase = Phase.Stowed; }
        public void Cancel() { requested = release = false; Rest(); }
        private bool Play(string id, Phase next)
        { if(player!=null)player.SetPlaybackSpeed(next==Phase.Opening||next==Phase.Firing||next==Phase.Closing?cyclePlaybackSpeed:1f); if (player != null && player.PlayActionPreservingPose(id)) { phase = next; return true; } Cancel(); return false; }
        private void Rest()
        {
            if (player != null)
            {
                player.StopPlayback(); player.SampleActionAtEnd("a000_800320"); player.SampleActionAtEnd("a000_800310");
                if (player.HasAction("a000_800000")) player.SampleActionAtTime("a000_800000", 0);
            }
            phase = Phase.Stowed;
        }
    }
}
