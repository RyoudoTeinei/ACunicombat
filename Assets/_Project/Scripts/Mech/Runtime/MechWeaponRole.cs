using System;

namespace ACunicombat.Mechs
{
	public enum MechWeaponRole : byte
	{
		None = 0,
		HandCannon = 10,
		AutomaticCannon = 20,
		InterceptorMissile = 30,
		AirToAirMissile = 40,
		PointDefenseCannon = 50
	}

	public readonly struct MechWeaponHudState
	{
		public MechWeaponHudState(
			MechWeaponRole role,
			string label,
			int roundsRemaining,
			int magazineCapacity,
			int magazinesRemaining,
			bool reloading,
			float reloadRemainingSeconds,
			float reloadDurationSeconds,
			string reloadLabel)
		{
			Role = role;
			Label = label;
			RoundsRemaining = roundsRemaining;
			MagazineCapacity = magazineCapacity;
			MagazinesRemaining = magazinesRemaining;
			Reloading = reloading;
			ReloadRemainingSeconds = reloadRemainingSeconds;
			ReloadDurationSeconds = reloadDurationSeconds;
			ReloadLabel = reloadLabel;
		}

		public MechWeaponRole Role { get; }
		public string Label { get; }
		public int RoundsRemaining { get; }
		public int MagazineCapacity { get; }
		public int MagazinesRemaining { get; }
		public bool Reloading { get; }
		public float ReloadRemainingSeconds { get; }
		public float ReloadDurationSeconds { get; }
		public string ReloadLabel { get; }
	}

	public static class MechWeaponRoleResolver
	{
		public static MechWeaponRole Resolve(string partId)
		{
			if (Matches(partId, "WP_L_1310") || Matches(partId, "WP_R_1310"))
			{
				return MechWeaponRole.HandCannon;
			}

			if (Matches(partId, "WP_L_1610") || Matches(partId, "WP_R_1610") ||
				Matches(partId, "WP_L_1630") || Matches(partId, "WP_R_1630") ||
				Matches(partId, "WP_L_1720") || Matches(partId, "WP_R_1720"))
			{
				return MechWeaponRole.AutomaticCannon;
			}

			if (Matches(partId, "WP_L_4250") || Matches(partId, "WP_R_4250"))
			{
				return MechWeaponRole.InterceptorMissile;
			}

			if (Matches(partId, "WP_L_4290") || Matches(partId, "WP_R_4290"))
			{
				return MechWeaponRole.AirToAirMissile;
			}

			if (Matches(partId, "WP_L_5110") || Matches(partId, "WP_R_5110"))
			{
				return MechWeaponRole.PointDefenseCannon;
			}

			return MechWeaponRole.None;
		}

		private static bool Matches(string partId, string sourceKey)
		{
			return !string.IsNullOrWhiteSpace(partId) &&
				partId.EndsWith(sourceKey, StringComparison.OrdinalIgnoreCase);
		}
	}
}
