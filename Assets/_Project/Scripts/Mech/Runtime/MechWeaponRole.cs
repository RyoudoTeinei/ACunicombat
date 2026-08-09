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
		public static MechWeaponRole Resolve(MechPartDefinition part)
		{
			return part != null && part.WeaponPerformance != null && part.WeaponPerformance.IsConfigured
				? part.WeaponPerformance.role
				: MechWeaponRole.None;
		}

	}
}
