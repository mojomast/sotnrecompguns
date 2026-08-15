using System;
using ImGuiNET;
using Recompiled;
using RecompOne.Runtime;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Hardware;
using RecompOne.Runtime.Hle;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using Sotn;

namespace GunsMod;

public sealed class Guns : IMod
{
    private const uint Marker = 0x534E5547; // "GUNS" in little endian guest memory.
    private const uint EntityNullAddress = 0x8011A4C8;
    private const uint AssignAttackerIdAddress = 0x80118894;
    private const int ProjectileStart = 17;
    private const int ProjectileEnd = 48;
    private const int MaxProjectiles = 14;
    private const int MarkerOffset = 0;
    private const int LifetimeOffset = 4;
    private const int GunKindOffset = 6;
    private const int EquipIdOffset = 0x32;
    private const ushort ProjectileEntityId = 0x3E;
    private const int PhysicalElement = 0x40;
    private const int HitboxState = 2;
    private const int HitEffect = 0x81;
    private const int StunFrames = 4;
    private const uint EquippedHandsAddress = 0x80097C00;
    private const int FireButtonMask = Controller.R2;
    private const int ReloadButtonMask = Controller.R1;
    private const uint CurrentBufferPointer = 0x8006C37C;
    private const uint BackbufferXAddress = 0x8006C39C;
    private const uint BackbufferYAddress = 0x8006C3A0;
    private const uint OrderingTableOffset = 0x474;
    private const int OrderingTableSize = 0x200;
    private const string SettingsPrefix = "mods.guns.";

    private static Guns? _instance;

    private readonly GunState[] _states = new GunState[GunCatalog.All.Length];
    private readonly byte[][] _originalNames = new byte[GunCatalog.All.Length][];
    private readonly uint[] _nameAddresses = new uint[GunCatalog.All.Length];
    private readonly byte[] _originalChainLimit = new byte[GunCatalog.All.Length];

    private float _aimX = 1f;
    private float _aimY;
    private float _deadzone = 0.18f;
    private float _spreadScale = 1f;
    private bool _autoReload = true;
    private bool _grantWeapons = true;
    private bool _namesPatched;
    private bool _equipmentPatched;
    private bool _fireHeld;
    private bool _firePressed;
    private bool _reloadPressed;
    private bool _previousFireHeld;
    private bool _previousReloadHeld;
    private int _fireBuffer;
    private uint _shotSequence;

    public void OnLoad()
    {
        _instance = this;
        LoadSettings();
        ResetAmmo();
        Event.AddListener<VSyncEvent>(OnVSync);
        Event.AddListener<PadReadEvent>(OnPadRead);
        Event.AddListener<PlayerLoadedEvent>(OnPlayerLoaded);
        Event.AddListener<RoomLayerLoadEvent>(OnRoomLoaded);
        if (Game.Available && Game.InAlucardMode()) ActivateForAlucard();
    }

    public void OnUnload()
    {
        Event.RemoveListener<VSyncEvent>(OnVSync);
        Event.RemoveListener<PadReadEvent>(OnPadRead);
        Event.RemoveListener<PlayerLoadedEvent>(OnPlayerLoaded);
        Event.RemoveListener<RoomLayerLoadEvent>(OnRoomLoaded);
        CleanupProjectiles();
        RestoreEquipment();
        RestoreNames();
        _instance = null;
    }

    public void DrawSettings()
    {
        bool autoReload = _autoReload;
        if (ImGui.Checkbox("Automatically reload", ref autoReload))
        {
            _autoReload = autoReload;
            SaveSettings();
        }

        bool grantWeapons = _grantWeapons;
        if (ImGui.Checkbox("Grant guns when a game loads", ref grantWeapons))
        {
            _grantWeapons = grantWeapons;
            SaveSettings();
        }

        float deadzone = _deadzone;
        if (ImGui.SliderFloat("Aim deadzone", ref deadzone, 0.05f, 0.45f, "%.2f"))
        {
            _deadzone = deadzone;
            SaveSettings();
        }

        float spread = _spreadScale;
        if (ImGui.SliderFloat("Spread scale", ref spread, 0f, 2f, "%.2f"))
        {
            _spreadScale = spread;
            SaveSettings();
        }

        if (ImGui.Button("Refill ammunition")) ResetAmmo();

        ImGui.Separator();
        int equipped = EquippedGunIndex();
        ImGui.Text(equipped >= 0 ? $"Equipped: {GunCatalog.All[equipped].Name}" : "Equip one of the four gun items to fire");
        for (int i = 0; i < GunCatalog.All.Length; i++)
        {
            ref readonly var gun = ref GunCatalog.All[i];
            ref var state = ref _states[i];
            string reload = state.ReloadRemaining > 0 ? $" (reloading: {state.ReloadRemaining})" : "";
            ImGui.Text($"{gun.Name}: {state.Magazine}/{state.Reserve}{reload}");
        }
        ImGui.TextDisabled("Aim: right stick | Fire: R2 | Reload: R1");
        ImGui.TextDisabled("Gun item names replace Shuriken, Cross shuriken, Buffalo star, and Flame star.");
    }

    private void OnPlayerLoaded(PlayerLoadedEvent e)
    {
        CleanupProjectiles();
        ResetInput();
        if (e.Character != PlayableCharacter.Alucard)
        {
            RestoreEquipment();
            RestoreNames();
            return;
        }
        ActivateForAlucard();
    }

    private void ActivateForAlucard()
    {
        ApplyEquipmentPatches();
        PatchNames();
        if (_grantWeapons)
        {
            foreach (var gun in GunCatalog.All)
                if (!OwnsGunItem(gun.ItemId)) Inventory.GrantHandItem(gun.ItemId);
        }
        ResetAmmo();
    }

    private static bool OwnsGunItem(ushort itemId) =>
        Inventory.HasHandItem(itemId) ||
        Inventory.GetWornEquipment(ItemSlot.LeftHand) == itemId ||
        Inventory.GetWornEquipment(ItemSlot.RightHand) == itemId;

    private void OnRoomLoaded(RoomLayerLoadEvent e)
    {
        CleanupProjectiles();
        ResetInput();
        for (int i = 0; i < _states.Length; i++)
            _states[i].ReloadRemaining = 0;
    }

    private void OnPadRead(PadReadEvent e)
    {
        if (e.Port != 0 || !CanUseGuns() || EquippedGunIndex() < 0) return;
        // PadReadEvent uses the game's byte-swapped button masks from Sotn.Button.
        e.Buttons |= (ushort)(Button.R1 | Button.R2);
        if (GunCatalog.IndexOfItem(Inventory.GetWornEquipment(ItemSlot.LeftHand)) >= 0)
            e.Buttons |= (ushort)Button.Square;
        if (GunCatalog.IndexOfItem(Inventory.GetWornEquipment(ItemSlot.RightHand)) >= 0)
            e.Buttons |= (ushort)Button.Circle;
    }

    private void OnVSync(VSyncEvent e)
    {
        if (!CanUseGuns())
        {
            ResetInput();
            return;
        }

        UpdateAim();
        SampleButtons();
        int gunIndex = EquippedGunIndex();
        if (gunIndex < 0)
        {
            _fireBuffer = 0;
            return;
        }

        ref readonly var gun = ref GunCatalog.All[gunIndex];
        ref var state = ref _states[gunIndex];
        if (state.Cooldown > 0) state.Cooldown--;

        if (state.ReloadRemaining > 0)
        {
            if (--state.ReloadRemaining == 0) CompleteReload(gunIndex);
            return;
        }

        if (_reloadPressed)
        {
            BeginReload(gunIndex);
            return;
        }

        bool wantsShot = gun.Automatic ? _fireHeld : _fireBuffer > 0;
        if (!wantsShot || state.Cooldown > 0) return;
        if (state.Magazine <= 0)
        {
            if (_autoReload) BeginReload(gunIndex);
            return;
        }

        if (Fire(gunIndex) > 0)
        {
            state.Magazine--;
            state.Cooldown = gun.FireInterval;
            _fireBuffer = 0;
        }
    }

    private static bool CanUseGuns()
    {
        if (!Game.Available || !Game.InGame || Game.IsLoading || Game.MenuOpen || Game.MapOpen) return false;
        if (!Player.IsAlucard || !Player.HasControl || Player.HasStatus(PlayerStatus.Transform | PlayerStatus.Dead)) return false;
        return Player.Step is PlayerStep.Standing or PlayerStep.Walking or PlayerStep.Crouching or PlayerStep.Aerial;
    }

    private void UpdateAim()
    {
        float x = NormalizeAxis(Controller.RightX);
        float y = -NormalizeAxis(Controller.RightY);
        float magnitude = MathF.Sqrt(x * x + y * y);
        if (magnitude <= _deadzone)
        {
            if (MathF.Abs(_aimX) < 0.001f && MathF.Abs(_aimY) < 0.001f)
                _aimX = Player.FacingLeft ? -1f : 1f;
            return;
        }

        _aimX = x / magnitude;
        _aimY = y / magnitude;
        if (MathF.Abs(_aimX) > 0.08f) Player.FacingLeft = _aimX < 0f;
    }

    private static float NormalizeAxis(byte value)
    {
        int delta = value - 128;
        return delta >= 0 ? delta / 127f : delta / 128f;
    }

    private void SampleButtons()
    {
        _fireHeld = (Controller.State & FireButtonMask) == 0;
        bool reloadHeld = (Controller.State & ReloadButtonMask) == 0;
        _firePressed = _fireHeld && !_previousFireHeld;
        _reloadPressed = reloadHeld && !_previousReloadHeld;
        if (_firePressed) _fireBuffer = 6;
        else if (_fireBuffer > 0) _fireBuffer--;
        _previousFireHeld = _fireHeld;
        _previousReloadHeld = reloadHeld;
    }

    private int EquippedGunIndex()
    {
        if (!Game.Available) return -1;
        int index = GunCatalog.IndexOfItem(Inventory.GetWornEquipment(ItemSlot.RightHand));
        return index >= 0 ? index : GunCatalog.IndexOfItem(Inventory.GetWornEquipment(ItemSlot.LeftHand));
    }

    private int Fire(int gunIndex)
    {
        ref readonly var gun = ref GunCatalog.All[gunIndex];
        _shotSequence++;
        int count = gun.PelletCount;
        int spawned = 0;
        for (int i = 0; i < count; i++)
        {
            float offset = count == 1 ? RandomSpread(gun.SpreadDegrees) : SpreadOffset(i, count, gun.SpreadDegrees);
            if (SpawnProjectile(gunIndex, RotateX(_aimX, _aimY, offset), RotateY(_aimX, _aimY, offset)))
                spawned++;
        }
        if (spawned > 0) GameApi.PlaySfx(gun.SoundId);
        return spawned;
    }

    private float RandomSpread(float spreadDegrees)
    {
        if (spreadDegrees <= 0f || _spreadScale <= 0f) return 0f;
        uint value = _shotSequence * 1664525u + 1013904223u;
        float unit = ((value >> 8) & 0xFFFF) / 65535f;
        return (unit * 2f - 1f) * spreadDegrees * _spreadScale;
    }

    private float SpreadOffset(int pellet, int count, float spreadDegrees)
    {
        if (count <= 1) return 0f;
        float t = pellet / (float)(count - 1);
        return (t * 2f - 1f) * spreadDegrees * _spreadScale;
    }

    private static float RotateX(float x, float y, float degrees)
    {
        float radians = degrees * (MathF.PI / 180f);
        return x * MathF.Cos(radians) - y * MathF.Sin(radians);
    }

    private static float RotateY(float x, float y, float degrees)
    {
        float radians = degrees * (MathF.PI / 180f);
        return x * MathF.Sin(radians) + y * MathF.Cos(radians);
    }

    private bool SpawnProjectile(int gunIndex, float dirX, float dirY)
    {
        ref readonly var gun = ref GunCatalog.All[gunIndex];
        if (ProjectileCount() >= MaxProjectiles) return false;
        Entity entity = GameApi.GetFreeEntity(ProjectileStart, ProjectileEnd);
        if (!entity.IsValid) return false;

        IMemory memory = Runtime.Mem!;
        for (uint offset = 0; offset < Entity.Stride; offset += 4)
            memory.WriteU32(entity.Addr + offset, 0);

        int muzzleX = Player.PosX + (int)MathF.Round(dirX * 14f);
        int muzzleY = Player.PosY - 10 + (int)MathF.Round(dirY * 8f);
        entity.PosX = muzzleX;
        entity.PosY = muzzleY;
        entity.VelocityX = (int)MathF.Round(dirX * gun.Speed * 65536f);
        entity.VelocityY = (int)MathF.Round(dirY * gun.Speed * 65536f);
        entity.FacingLeft = (ushort)(dirX < 0 ? 1 : 0);
        entity.Update = EntityNullAddress;
        entity.Step = 1;
        entity.Flags = (int)(EntityFlags.PosCameraLocked | EntityFlags.Unk100000 | EntityFlags.NotAnEnemy);
        entity.HitboxState = HitboxState;
        entity.HitboxWidth = gun.Kind == GunKind.Shotgun ? (byte)5 : (byte)4;
        entity.HitboxHeight = gun.Kind == GunKind.Shotgun ? (byte)5 : (byte)3;
        entity.Attack = (short)gun.Damage;
        entity.AttackElement = PhysicalElement;
        entity.NFramesInvincibility = (byte)gun.EnemyInvincibilityFrames;
        entity.StunFrames = StunFrames;
        entity.HitEffect = HitEffect;
        entity.SetExtU32(MarkerOffset, Marker);
        entity.SetExtU16(LifetimeOffset, (ushort)gun.Lifetime);
        entity.SetExtU8(GunKindOffset, (byte)gun.Kind);

        memory.WriteU16(entity.Addr + 0x26, ProjectileEntityId);
        GameApi.Call(AssignAttackerIdAddress, entity.Addr);
        return true;
    }

    private static int ProjectileCount()
    {
        int count = 0;
        for (int slot = ProjectileStart; slot < ProjectileEnd; slot++)
            if (IsProjectile(Entities.At(slot))) count++;
        return count;
    }

    private void BeginReload(int gunIndex)
    {
        ref readonly var gun = ref GunCatalog.All[gunIndex];
        ref var state = ref _states[gunIndex];
        if (state.ReloadRemaining > 0 || state.Magazine >= gun.MagazineSize || state.Reserve <= 0) return;
        state.ReloadRemaining = gun.ReloadFrames;
    }

    private void CompleteReload(int gunIndex)
    {
        ref readonly var gun = ref GunCatalog.All[gunIndex];
        ref var state = ref _states[gunIndex];
        int amount = Math.Min(gun.MagazineSize - state.Magazine, state.Reserve);
        state.Magazine += amount;
        state.Reserve -= amount;
    }

    private void ResetAmmo()
    {
        for (int i = 0; i < GunCatalog.All.Length; i++)
        {
            ref readonly var gun = ref GunCatalog.All[i];
            _states[i] = new GunState { Magazine = gun.MagazineSize, Reserve = gun.StartingReserve };
        }
    }

    private void ResetInput()
    {
        _fireHeld = false;
        _firePressed = false;
        _reloadPressed = false;
        _previousFireHeld = false;
        _previousReloadHeld = false;
        _fireBuffer = 0;
    }

    private void ApplyEquipmentPatches()
    {
        if (!Game.Available) return;
        IMemory memory = Runtime.Mem!;
        uint definitions = GameApi.EquipDefs;
        if (definitions == 0) return;

        for (int i = 0; i < GunCatalog.All.Length; i++)
        {
            uint record = definitions + (uint)(GunCatalog.All[i].ItemId * 0x32);
            if (!_equipmentPatched)
            {
                _originalChainLimit[i] = memory.ReadU8(record + 0x16);
            }
            memory.WriteU8(record + 0x16, 31);
        }
        _equipmentPatched = true;
    }

    private void RestoreEquipment()
    {
        if (!_equipmentPatched || !Game.Available) return;
        IMemory memory = Runtime.Mem!;
        uint definitions = GameApi.EquipDefs;
        if (definitions == 0) return;
        for (int i = 0; i < GunCatalog.All.Length; i++)
        {
            uint record = definitions + (uint)(GunCatalog.All[i].ItemId * 0x32);
            if (memory.ReadU8(record + 0x16) == 31)
                memory.WriteU8(record + 0x16, _originalChainLimit[i]);
        }
        _equipmentPatched = false;
    }

    private void PatchNames()
    {
        if (!Game.Available) return;
        IMemory memory = Runtime.Mem!;
        uint definitions = GameApi.EquipDefs;
        if (definitions == 0) return;

        var addresses = new uint[GunCatalog.All.Length];
        for (int i = 0; i < GunCatalog.All.Length; i++)
        {
            addresses[i] = memory.ReadU32(definitions + (uint)(GunCatalog.All[i].ItemId * 0x32));
            if (addresses[i] == 0 || Array.IndexOf(addresses, addresses[i], 0, i) >= 0)
            {
                Console.Error.WriteLine("[Guns] item name buffers are unavailable or aliased; names were not patched");
                return;
            }
        }

        if (!_namesPatched)
        {
            var originals = new byte[GunCatalog.All.Length][];
            for (int i = 0; i < GunCatalog.All.Length; i++)
            {
                originals[i] = ReadGameString(memory, addresses[i]);
                if (originals[i].Length < GunCatalog.All[i].ItemName.Length + 2)
                {
                    Console.Error.WriteLine($"[Guns] name buffer for {GunCatalog.All[i].Name} is too small");
                    return;
                }
            }
            for (int i = 0; i < GunCatalog.All.Length; i++)
            {
                _nameAddresses[i] = addresses[i];
                _originalNames[i] = originals[i];
            }
        }

        for (int i = 0; i < GunCatalog.All.Length; i++)
            WriteGameString(memory, addresses[i], GunCatalog.All[i].ItemName);
        _namesPatched = true;
    }

    private void RestoreNames()
    {
        if (!_namesPatched || !Game.Available) return;
        IMemory memory = Runtime.Mem!;
        for (int i = 0; i < _originalNames.Length; i++)
        {
            if (_nameAddresses[i] == 0 || _originalNames[i] == null) continue;
            if (!MatchesGameString(memory, _nameAddresses[i], GunCatalog.All[i].ItemName)) continue;
            for (int j = 0; j < _originalNames[i].Length; j++)
                memory.WriteU8(_nameAddresses[i] + (uint)j, _originalNames[i][j]);
        }
        _namesPatched = false;
    }

    private static byte[] ReadGameString(IMemory memory, uint address)
    {
        const int maxLength = 32;
        var bytes = new byte[maxLength];
        for (int i = 0; i < maxLength; i++)
        {
            bytes[i] = memory.ReadU8(address + (uint)i);
            if (i > 0 && bytes[i - 1] == 0xFF && bytes[i] == 0)
                return bytes[..(i + 1)];
        }
        throw new InvalidOperationException($"unterminated equipment name at 0x{address:X8}");
    }

    private static void WriteGameString(IMemory memory, uint address, string text)
    {
        for (int i = 0; i < text.Length; i++)
            memory.WriteU8(address + (uint)i, checked((byte)(text[i] - 0x20)));
        memory.WriteU8(address + (uint)text.Length, 0xFF);
        memory.WriteU8(address + (uint)text.Length + 1, 0);
    }

    private static bool MatchesGameString(IMemory memory, uint address, string text)
    {
        for (int i = 0; i < text.Length; i++)
            if (memory.ReadU8(address + (uint)i) != (byte)(text[i] - 0x20)) return false;
        return memory.ReadU8(address + (uint)text.Length) == 0xFF &&
               memory.ReadU8(address + (uint)text.Length + 1) == 0;
    }

    private void CleanupProjectiles()
    {
        if (!Game.Available) return;
        for (int slot = ProjectileStart; slot < ProjectileEnd; slot++)
        {
            Entity entity = Entities.At(slot);
            if (IsProjectile(entity)) entity.Destroy();
        }
    }

    private static bool IsProjectile(Entity entity) => entity.IsAlive && entity.ExtU32(MarkerOffset) == Marker;

    private void LoadSettings()
    {
        var view = Runtime.View;
        _autoReload = view.GetBool(SettingsPrefix + "autoReload", true);
        _grantWeapons = view.GetBool(SettingsPrefix + "grantWeapons", true);
        _deadzone = Math.Clamp(view.GetFloat(SettingsPrefix + "deadzone", 0.18f), 0.05f, 0.45f);
        _spreadScale = Math.Clamp(view.GetFloat(SettingsPrefix + "spreadScale", 1f), 0f, 2f);
    }

    private void SaveSettings()
    {
        var view = Runtime.View;
        view.SetBool(SettingsPrefix + "autoReload", _autoReload);
        view.SetBool(SettingsPrefix + "grantWeapons", _grantWeapons);
        view.SetFloat(SettingsPrefix + "deadzone", _deadzone);
        view.SetFloat(SettingsPrefix + "spreadScale", _spreadScale);
        Runtime.SaveView();
    }

    [PreHook("w0_015", "EntityWeaponAttack")]
    [PreHook("w1_015", "EntityWeaponAttack")]
    private static bool SuppressOriginalGunAttack(CpuContext context, IMemory memory)
    {
        Entity entity = new(context.A0);
        if (GunCatalog.IndexOfItem(entity.ExtU16(EquipIdOffset)) < 0) return true;
        entity.Destroy();
        return false;
    }

    [PreHook("dra", "func_800FDD44")]
    private static bool PreserveGunInventory(CpuContext context, IMemory memory)
    {
        uint hand = context.A0;
        if (hand > 1) return true;
        uint itemId = memory.ReadU32(EquippedHandsAddress + hand * 4);
        if (GunCatalog.IndexOfItem(itemId) < 0) return true;
        context.V0 = 0;
        return false;
    }

    [PostHook("dra", "UpdatePlayerEntities")]
    private static void UpdateProjectiles(CpuContext context, IMemory memory)
    {
        if (_instance == null) return;
        for (int slot = ProjectileStart; slot < ProjectileEnd; slot++)
        {
            Entity entity = Entities.At(slot);
            if (!IsProjectile(entity)) continue;

            ushort lifetime = entity.ExtU16(LifetimeOffset);
            if (entity.HitFlags != 0 || lifetime <= 1)
            {
                entity.Destroy();
                continue;
            }

            entity.SetExtU16(LifetimeOffset, (ushort)(lifetime - 1));
            entity.PosXRaw += entity.VelocityX;
            entity.PosYRaw += entity.VelocityY;
            if (entity.PosX < -24 || entity.PosX > 280 || entity.PosY < -24 || entity.PosY > 264)
                entity.Destroy();
        }
    }

    [PostHook("dra", "RenderEntities")]
    private static void Render(CpuContext context, IMemory memory)
    {
        Guns? mod = _instance;
        if (mod == null || !CanUseGuns() || mod.EquippedGunIndex() < 0) return;

        int bufferX = (int)memory.ReadU32(BackbufferXAddress);
        int bufferY = (int)memory.ReadU32(BackbufferYAddress);
        GpuPrims.SetOrderingTable(memory.ReadU32(CurrentBufferPointer) + OrderingTableOffset, OrderingTableSize);

        int order = Math.Clamp(Player.Entity.ZPriority - 2, 0, OrderingTableSize - 1);
        foreach (Entity projectile in Entities.Range(ProjectileStart, ProjectileEnd))
        {
            if (!IsProjectile(projectile)) continue;
            float x = bufferX + projectile.PosX;
            float y = bufferY + projectile.PosY;
            float vx = projectile.VelocityX / 65536f;
            float vy = projectile.VelocityY / 65536f;
            DrawTracer(order, x - vx * 1.5f, y - vy * 1.5f, x, y, 1.25f, 255, 224, 96);
        }

        float anchorX = bufferX + Player.PosX;
        float anchorY = bufferY + Player.PosY - 10;
        DrawGun(order, anchorX, anchorY, mod._aimX, mod._aimY);
        DrawAimArrow(0, anchorX, anchorY, mod._aimX, mod._aimY);
    }

    private static void DrawGun(int order, float x, float y, float dirX, float dirY)
    {
        float normalX = -dirY;
        float normalY = dirX;
        DrawOrientedQuad(order, x + dirX * 2, y + dirY * 2, dirX, dirY, 18, 4, 112, 120, 132);
        DrawOrientedQuad(order, x - dirX * 2 + normalX * 4, y - dirY * 2 + normalY * 4,
            dirX * 0.45f + normalX * 0.9f, dirY * 0.45f + normalY * 0.9f, 8, 3, 72, 76, 84);
    }

    private static void DrawAimArrow(int order, float x, float y, float dirX, float dirY)
    {
        float nx = -dirY;
        float ny = dirX;
        float startX = x + dirX * 13f;
        float startY = y + dirY * 13f;
        float shaftX = x + dirX * 43f;
        float shaftY = y + dirY * 43f;
        float tipX = x + dirX * 52f;
        float tipY = y + dirY * 52f;

        DrawSolidTracer(order, startX, startY, shaftX, shaftY, 3f, 0, 0, 0);
        DrawSolidTracer(order, startX, startY, shaftX, shaftY, 1.5f, 255, 232, 48);

        var outlineA = new PrimVertex(tipX, tipY, 0, 0, 0);
        var outlineB = new PrimVertex(shaftX + nx * 7f, shaftY + ny * 7f, 0, 0, 0);
        var outlineC = new PrimVertex(shaftX - nx * 7f, shaftY - ny * 7f, 0, 0, 0);
        GpuPrims.Tri(order, outlineA, outlineB, outlineC);

        var arrowA = new PrimVertex(tipX - dirX * 2f, tipY - dirY * 2f, 255, 232, 48);
        var arrowB = new PrimVertex(shaftX + nx * 4.5f, shaftY + ny * 4.5f, 255, 232, 48);
        var arrowC = new PrimVertex(shaftX - nx * 4.5f, shaftY - ny * 4.5f, 255, 232, 48);
        GpuPrims.Tri(order, arrowA, arrowB, arrowC);
    }

    private static void DrawSolidTracer(int order, float x0, float y0, float x1, float y1, float halfWidth,
        byte r, byte g, byte b)
    {
        float dx = x1 - x0;
        float dy = y1 - y0;
        float magnitude = MathF.Max(0.001f, MathF.Sqrt(dx * dx + dy * dy));
        float nx = -dy / magnitude * halfWidth;
        float ny = dx / magnitude * halfWidth;
        var a = new PrimVertex(x0 - nx, y0 - ny, r, g, b);
        var b0 = new PrimVertex(x1 - nx, y1 - ny, r, g, b);
        var c = new PrimVertex(x0 + nx, y0 + ny, r, g, b);
        var d = new PrimVertex(x1 + nx, y1 + ny, r, g, b);
        GpuPrims.Quad(order, a, b0, c, d);
    }

    private static void DrawOrientedQuad(int order, float centerX, float centerY, float dirX, float dirY,
        float length, float halfWidth, byte r, byte g, byte b)
    {
        float magnitude = MathF.Max(0.001f, MathF.Sqrt(dirX * dirX + dirY * dirY));
        dirX /= magnitude;
        dirY /= magnitude;
        float nx = -dirY * halfWidth;
        float ny = dirX * halfWidth;
        float hx = dirX * length * 0.5f;
        float hy = dirY * length * 0.5f;
        var a = new PrimVertex(centerX - hx - nx, centerY - hy - ny, r, g, b);
        var b0 = new PrimVertex(centerX + hx - nx, centerY + hy - ny, r, g, b);
        var c = new PrimVertex(centerX - hx + nx, centerY - hy + ny, r, g, b);
        var d = new PrimVertex(centerX + hx + nx, centerY + hy + ny, r, g, b);
        GpuPrims.Quad(order, a, b0, c, d);
    }

    private static void DrawTracer(int order, float x0, float y0, float x1, float y1, float halfWidth,
        byte r, byte g, byte b)
    {
        float dx = x1 - x0;
        float dy = y1 - y0;
        float magnitude = MathF.Max(0.001f, MathF.Sqrt(dx * dx + dy * dy));
        float nx = -dy / magnitude * halfWidth;
        float ny = dx / magnitude * halfWidth;
        var a = new PrimVertex(x0 - nx, y0 - ny, r, g, b);
        var b0 = new PrimVertex(x1 - nx, y1 - ny, r, g, b);
        var c = new PrimVertex(x0 + nx, y0 + ny, r, g, b);
        var d = new PrimVertex(x1 + nx, y1 + ny, r, g, b);
        GpuPrims.Quad(order, a, b0, c, d, semiTrans: true, blend: 1, gouraud: true);
    }
}
