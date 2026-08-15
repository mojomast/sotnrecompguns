namespace GunsMod;

internal enum GunKind : byte
{
    Pistol,
    Shotgun,
    AssaultRifle,
    MachineGun,
}

internal readonly record struct GunDefinition(
    GunKind Kind,
    ushort ItemId,
    string Name,
    string ItemName,
    int MagazineSize,
    int StartingReserve,
    int FireInterval,
    int ReloadFrames,
    int Damage,
    int PelletCount,
    float SpreadDegrees,
    float Speed,
    int Lifetime,
    bool Automatic,
    int EnemyInvincibilityFrames,
    int SoundId);

internal struct GunState
{
    public int Magazine;
    public int Reserve;
    public int Cooldown;
    public int ReloadRemaining;
}

internal static class GunCatalog
{
    // Four adjacent throwing weapons share overlay 15, minimizing the hook surface.
    internal static readonly GunDefinition[] All =
    [
        new(GunKind.Pistol,       0x4B, "Pistol",        "Pistol",        12, 72, 10, 60, 18, 1,  0f, 10f, 42, false, 5, 0x685),
        new(GunKind.AssaultRifle, 0x4C, "Assault rifle", "Assault rifle", 30, 180, 4, 82, 10, 1,  2f, 11f, 46, true,  2, 0x625),
        new(GunKind.Shotgun,      0x4D, "Shotgun",       "Shotgun",        6, 36, 34, 96, 11, 7, 18f,  9f, 24, false, 6, 0x6AC),
        new(GunKind.MachineGun,   0x4E, "Machine gun",   "Machinegun",    50, 250, 2, 95,  6, 1,  6f, 12f, 38, true,  1, 0x685),
    ];

    internal static int IndexOfItem(uint itemId)
    {
        for (int i = 0; i < All.Length; i++)
            if (All[i].ItemId == itemId) return i;
        return -1;
    }
}
