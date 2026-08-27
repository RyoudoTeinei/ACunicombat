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

	public readonly struct MechWeaponSlotStatus
	{
		public MechWeaponSlotStatus(
			MechPartSlot slot,
			MechWeaponRole role,
			int roundsRemaining,
			int magazineCapacity,
			int magazinesRemaining,
			bool reloading,
			float reloadRemainingSeconds,
			float minimumRangeMeters,
			float effectiveRangeMeters,
			float maximumRangeMeters,
			float roundsPerMinute,
			float maximumDispersionMilliradians,
			float burstResetSeconds,
			int heldBurstShotCount,
			float heldBurstPauseSeconds,
			float spinUpSeconds,
			float directHitDamage = 0f,
			float muzzleVelocityMetersPerSecond = 0f,
			float reloadSeconds = 0f,
			int tapBurstShotCount = 1)
		{
			Slot = slot;
			Role = role;
			RoundsRemaining = roundsRemaining;
			MagazineCapacity = magazineCapacity;
			MagazinesRemaining = magazinesRemaining;
			Reloading = reloading;
			ReloadRemainingSeconds = reloadRemainingSeconds;
			MinimumRangeMeters = minimumRangeMeters;
			EffectiveRangeMeters = effectiveRangeMeters;
			MaximumRangeMeters = maximumRangeMeters;
			RoundsPerMinute = roundsPerMinute;
			MaximumDispersionMilliradians = maximumDispersionMilliradians;
			BurstResetSeconds = burstResetSeconds;
			HeldBurstShotCount = heldBurstShotCount;
			HeldBurstPauseSeconds = heldBurstPauseSeconds;
			SpinUpSeconds = spinUpSeconds;
			DirectHitDamage = directHitDamage;
			MuzzleVelocityMetersPerSecond = muzzleVelocityMetersPerSecond;
			ReloadSeconds = reloadSeconds;
			TapBurstShotCount = tapBurstShotCount;
		}

		public MechPartSlot Slot { get; }
		public MechWeaponRole Role { get; }
		public int RoundsRemaining { get; }
		public int MagazineCapacity { get; }
		public int MagazinesRemaining { get; }
		public bool Reloading { get; }
		public float ReloadRemainingSeconds { get; }
		public float MinimumRangeMeters { get; }
		public float EffectiveRangeMeters { get; }
		public float MaximumRangeMeters { get; }
		public float RoundsPerMinute { get; }
		public float MaximumDispersionMilliradians { get; }
		public float BurstResetSeconds { get; }
		public int HeldBurstShotCount { get; }
		public float HeldBurstPauseSeconds { get; }
		public float SpinUpSeconds { get; }
		public float DirectHitDamage { get; }
		public float MuzzleVelocityMetersPerSecond { get; }
		public float ReloadSeconds { get; }
		public int TapBurstShotCount { get; }

		public bool IsConfigured => Role != MechWeaponRole.None;
		public bool HasAmmunition =>
			RoundsRemaining > 0 && MagazinesRemaining > 0;
		public bool HasReserveAmmunition =>
			RoundsRemaining > 0 || MagazinesRemaining > 0;
		public bool CanAttemptFire => HasAmmunition && !Reloading;
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
