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
    private const string BuildVersion = "0.1.3";
    private const uint Marker = 0x534E5547; // "GUNS" in little endian guest memory.
    private const uint EntityNullAddress = 0x8011A4C8;
    private const uint AssignAttackerIdAddress = 0x80118894;
    private const int ProjectileStart = 17;
    private const int ProjectileEnd = 48;
    private const int MaxProjectiles = 14;
    private const int MarkerOffset = 0;
    private const int LifetimeOffset = 4;
    private const int GunKindOffset = 6;
    private const int PendingDestroyOffset = 7;
    private const int EquipIdOffset = 0x32;
    private const uint EquipmentStride = 0x34;
    private const uint EquipmentWeaponIdOffset = 0x0F;
    private const uint EquipmentChainLimitOffset = 0x16;
    private const uint EquipmentConsumableOffset = 0x19;
    private const uint EquipmentIconOffset = 0x2C;
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
    private const int ForegroundOrder = OrderingTableSize - 1;
    private const int PatchCheckInterval = 60;
    private const string SettingsPrefix = "mods.guns.";

    private static Guns? _instance;

    private readonly GunState[] _states = new GunState[GunCatalog.All.Length];
    private readonly byte[][] _originalNames = new byte[GunCatalog.All.Length][];
    private readonly uint[] _nameAddresses = new uint[GunCatalog.All.Length];
    private readonly byte[] _originalChainLimit = new byte[GunCatalog.All.Length];
    private readonly byte[] _originalConsumable = new byte[GunCatalog.All.Length];
    private uint _equipmentDefinitions;

    private float _aimX = 1f;
    private float _aimY;
    private float _deadzone = 0.18f;
    private float _spreadScale = 1f;
    private bool _autoReload = true;
    private bool _namesPatched;
    private bool _equipmentPatched;
    private bool _fireHeld;
    private bool _firePressed;
    private bool _reloadPressed;
    private bool _previousFireHeld;
    private bool _previousReloadHeld;
    private bool _semiAutoShotQueued;
    private int _inputGunIndex = -1;
    private bool _aimEngaged;
    private int _patchCheckRemaining;
    private uint _renderCallbacks;
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
        ImGui.TextDisabled($"Guns v{BuildVersion}");

        bool autoReload = _autoReload;
        if (ImGui.Checkbox("Automatically reload", ref autoReload))
        {
            _autoReload = autoReload;
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
        ImGui.Text($"Right stick: {Controller.RightX}, {Controller.RightY} | Aim: {_aimX:F2}, {_aimY:F2}");
        ImGui.Text($"Runtime: names {(_namesPatched ? "patched" : "pending")} | render callbacks: {_renderCallbacks}");
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
        EnsureGunOwnership();
        ResetAmmo();
    }

    private static bool OwnsGunItem(ushort itemId) =>
        Inventory.HasHandItem(itemId) ||
        Inventory.GetWornEquipment(ItemSlot.LeftHand) == itemId ||
        Inventory.GetWornEquipment(ItemSlot.RightHand) == itemId;

    private void EnsureGunOwnership()
    {
        if (!Game.Available || !Game.InAlucardMode()) return;
        foreach (var gun in GunCatalog.All)
            if (!OwnsGunItem(gun.ItemId)) Inventory.GrantHandItem(gun.ItemId);
    }

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
        EnsureRuntimePatches();
        if (!CanUseGuns())
        {
            ResetInput();
            return;
        }

        UpdateAim();
        int gunIndex = EquippedGunIndex();
        SampleButtons(gunIndex);
        if (gunIndex < 0)
        {
            _semiAutoShotQueued = false;
            return;
        }

        ref readonly var gun = ref GunCatalog.All[gunIndex];
        ref var state = ref _states[gunIndex];
        if (state.Cooldown > 0) state.Cooldown--;

        if (state.ReloadRemaining > 0)
        {
            _semiAutoShotQueued = false;
            if (--state.ReloadRemaining == 0) CompleteReload(gunIndex);
            return;
        }

        if (_reloadPressed)
        {
            BeginReload(gunIndex);
            return;
        }

        bool wantsShot = gun.Automatic ? _fireHeld : _semiAutoShotQueued;
        if (!wantsShot || state.Cooldown > 0) return;
        if (state.Magazine <= 0)
        {
            if (_autoReload) BeginReload(gunIndex);
            return;
        }

        int spawned = Fire(gunIndex);
        if (!gun.Automatic) _semiAutoShotQueued = false;
        if (spawned > 0)
        {
            state.Magazine--;
            state.Cooldown = gun.FireInterval;
        }
    }

    private void EnsureRuntimePatches()
    {
        if (!Game.Available || !Game.InAlucardMode()) return;
        if (_equipmentPatched && _namesPatched && --_patchCheckRemaining > 0) return;

        ApplyEquipmentPatches();
        PatchNames();
        EnsureGunOwnership();
        _patchCheckRemaining = PatchCheckInterval;
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
        float y = NormalizeAxis(Controller.RightY);
        float magnitude = MathF.Sqrt(x * x + y * y);
        float threshold = _aimEngaged ? _deadzone * 0.7f : _deadzone;
        if (magnitude <= threshold)
        {
            _aimEngaged = false;
            if (MathF.Abs(_aimX) < 0.001f && MathF.Abs(_aimY) < 0.001f)
                _aimX = Player.FacingLeft ? -1f : 1f;
            return;
        }

        _aimEngaged = true;
        if (MathF.Abs(x) > MathF.Abs(y) * 2f) y = 0f;
        else if (MathF.Abs(y) > MathF.Abs(x) * 2f) x = 0f;
        magnitude = MathF.Sqrt(x * x + y * y);
        _aimX = x / magnitude;
        _aimY = y / magnitude;
        if (MathF.Abs(_aimX) > 0.08f) Player.FacingLeft = _aimX < 0f;
    }

    private static float NormalizeAxis(byte value)
    {
        int delta = value - 128;
        return delta >= 0 ? delta / 127f : delta / 128f;
    }

    private void SampleButtons(int gunIndex)
    {
        _fireHeld = (Controller.State & FireButtonMask) == 0;
        bool reloadHeld = (Controller.State & ReloadButtonMask) == 0;
        _firePressed = _fireHeld && !_previousFireHeld;
        _reloadPressed = reloadHeld && !_previousReloadHeld;
        if (_inputGunIndex != gunIndex)
        {
            _semiAutoShotQueued = false;
            _inputGunIndex = gunIndex;
        }
        if (_firePressed && gunIndex >= 0 && !GunCatalog.All[gunIndex].Automatic)
            _semiAutoShotQueued = true;
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
        entity.Flags = (int)(EntityFlags.PosCameraLocked | EntityFlags.KeepAliveOffCamera | EntityFlags.NotAnEnemy);
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
        entity.SetExtU8(PendingDestroyOffset, 0);

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
        _semiAutoShotQueued = false;
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
        _semiAutoShotQueued = false;
        _inputGunIndex = -1;
        _aimEngaged = false;
    }

    private bool ApplyEquipmentPatches()
    {
        if (!Game.Available) return false;
        IMemory memory = Runtime.Mem!;
        uint definitions = GameApi.EquipDefs;
        if (definitions == 0) return false;

        if (_equipmentPatched && _equipmentDefinitions != definitions)
            _equipmentPatched = false;

        var records = new uint[GunCatalog.All.Length];
        for (int i = 0; i < GunCatalog.All.Length; i++)
        {
            if (!TryGetEquipmentRecord(memory, definitions, GunCatalog.All[i], out uint record))
            {
                Console.Error.WriteLine($"[Guns] equipment record validation failed for {GunCatalog.All[i].Name}");
                return false;
            }
            records[i] = record;
            if (!_equipmentPatched)
            {
                _originalChainLimit[i] = memory.ReadU8(record + EquipmentChainLimitOffset);
                _originalConsumable[i] = memory.ReadU8(record + EquipmentConsumableOffset);
            }
        }

        for (int i = 0; i < records.Length; i++)
        {
            memory.WriteU8(records[i] + EquipmentChainLimitOffset, 31);
            memory.WriteU8(records[i] + EquipmentConsumableOffset, 0);
        }
        for (int i = 0; i < records.Length; i++)
        {
            if (memory.ReadU8(records[i] + EquipmentChainLimitOffset) == 31 &&
                memory.ReadU8(records[i] + EquipmentConsumableOffset) == 0) continue;
            Console.Error.WriteLine($"[Guns] equipment patch verification failed for {GunCatalog.All[i].Name}");
            return false;
        }
        _equipmentDefinitions = definitions;
        _equipmentPatched = true;
        return true;
    }

    private void RestoreEquipment()
    {
        if (!_equipmentPatched || !Game.Available) return;
        IMemory memory = Runtime.Mem!;
        uint definitions = GameApi.EquipDefs;
        if (definitions == 0 || definitions != _equipmentDefinitions)
        {
            _equipmentPatched = false;
            _equipmentDefinitions = 0;
            return;
        }
        for (int i = 0; i < GunCatalog.All.Length; i++)
        {
            if (!TryGetEquipmentRecord(memory, definitions, GunCatalog.All[i], out uint record)) continue;
            if (memory.ReadU8(record + EquipmentChainLimitOffset) == 31)
                memory.WriteU8(record + EquipmentChainLimitOffset, _originalChainLimit[i]);
            if (memory.ReadU8(record + EquipmentConsumableOffset) == 0)
                memory.WriteU8(record + EquipmentConsumableOffset, _originalConsumable[i]);
        }
        _equipmentPatched = false;
        _equipmentDefinitions = 0;
    }

    private static bool TryGetEquipmentRecord(IMemory memory, uint definitions, GunDefinition gun, out uint record)
    {
        record = definitions + gun.ItemId * EquipmentStride;
        return memory.ReadU8(record + EquipmentWeaponIdOffset) == 15 &&
               memory.ReadU16(record + EquipmentIconOffset) == gun.ItemId;
    }

    private bool PatchNames()
    {
        if (!Game.Available) return false;
        IMemory memory = Runtime.Mem!;
        uint definitions = GameApi.EquipDefs;
        if (definitions == 0) return false;

        var addresses = new uint[GunCatalog.All.Length];
        for (int i = 0; i < GunCatalog.All.Length; i++)
        {
            if (!TryGetEquipmentRecord(memory, definitions, GunCatalog.All[i], out uint record))
            {
                Console.Error.WriteLine($"[Guns] name record validation failed for {GunCatalog.All[i].Name}");
                return false;
            }
            addresses[i] = memory.ReadU32(record);
            if (addresses[i] == 0 || Array.IndexOf(addresses, addresses[i], 0, i) >= 0)
            {
                Console.Error.WriteLine("[Guns] item name buffers are unavailable or aliased; names were not patched");
                return false;
            }
        }

        if (_namesPatched)
        {
            bool sameBuffers = true;
            for (int i = 0; i < addresses.Length; i++)
                sameBuffers &= addresses[i] == _nameAddresses[i];
            if (!sameBuffers) _namesPatched = false;
        }

        if (!_namesPatched)
        {
            var originals = new byte[GunCatalog.All.Length][];
            for (int i = 0; i < GunCatalog.All.Length; i++)
            {
                if (!TryReadGameString(memory, addresses[i], out originals[i]))
                {
                    Console.Error.WriteLine($"[Guns] could not read name buffer for {GunCatalog.All[i].Name}; retrying later");
                    return false;
                }
                if (originals[i].Length < GunCatalog.All[i].ItemName.Length + 2)
                {
                    Console.Error.WriteLine($"[Guns] name buffer for {GunCatalog.All[i].Name} is too small");
                    return false;
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
        for (int i = 0; i < GunCatalog.All.Length; i++)
        {
            if (MatchesGameString(memory, addresses[i], GunCatalog.All[i].ItemName)) continue;
            Console.Error.WriteLine($"[Guns] name patch verification failed for {GunCatalog.All[i].Name}; retrying later");
            for (int original = 0; original < _originalNames.Length; original++)
                for (int b = 0; b < _originalNames[original].Length; b++)
                    memory.WriteU8(addresses[original] + (uint)b, _originalNames[original][b]);
            _namesPatched = false;
            return false;
        }
        _namesPatched = true;
        return true;
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

    private static bool TryReadGameString(IMemory memory, uint address, out byte[] bytes)
    {
        const int maxLength = 32;
        var buffer = new byte[maxLength];
        for (int i = 0; i < maxLength; i++)
        {
            buffer[i] = memory.ReadU8(address + (uint)i);
            if (i > 0 && buffer[i - 1] == 0xFF && buffer[i] == 0)
            {
                bytes = buffer[..(i + 1)];
                return true;
            }
        }
        bytes = [];
        return false;
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
        _deadzone = Math.Clamp(view.GetFloat(SettingsPrefix + "deadzone", 0.18f), 0.05f, 0.45f);
        _spreadScale = Math.Clamp(view.GetFloat(SettingsPrefix + "spreadScale", 1f), 0f, 2f);
    }

    private void SaveSettings()
    {
        var view = Runtime.View;
        view.SetBool(SettingsPrefix + "autoReload", _autoReload);
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

    [PostHook("dra", "func_800FB23C")]
    private static void RepairGunOwnershipAfterEquip(CpuContext context, IMemory memory)
    {
        _instance?.EnsureGunOwnership();
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
            if (entity.ExtU8(PendingDestroyOffset) != 0 || lifetime <= 1)
            {
                entity.Destroy();
                continue;
            }

            if (entity.HitFlags != 0)
            {
                entity.HitboxState = 0;
                entity.SetExtU8(PendingDestroyOffset, 1);
                continue;
            }

            entity.SetExtU16(LifetimeOffset, (ushort)(lifetime - 1));
            entity.PosXRaw += entity.VelocityX;
            entity.PosYRaw += entity.VelocityY;
            int margin = Display.WideMargin(256);
            if (entity.PosX < -32 - margin || entity.PosX > 288 + margin ||
                entity.PosY < -16 || entity.PosY > 256)
            {
                entity.HitboxState = 0;
                entity.SetExtU8(PendingDestroyOffset, 1);
            }
        }
    }

    [PostHook("dra", "RenderEntities")]
    private static void Render(CpuContext context, IMemory memory)
    {
        Guns? mod = _instance;
        if (mod == null || !Game.Available || !Game.InGame) return;
        mod._renderCallbacks++;

        int bufferX = (int)memory.ReadU32(BackbufferXAddress);
        int bufferY = (int)memory.ReadU32(BackbufferYAddress);
        uint currentBuffer = memory.ReadU32(CurrentBufferPointer);
        if (currentBuffer == 0) return;
        GpuPrims.SetOrderingTable(currentBuffer + OrderingTableOffset, OrderingTableSize);

        foreach (Entity projectile in Entities.Range(ProjectileStart, ProjectileEnd))
        {
            if (!IsProjectile(projectile)) continue;
            float x = bufferX + projectile.PosX;
            float y = bufferY + projectile.PosY;
            float vx = projectile.VelocityX / 65536f;
            float vy = projectile.VelocityY / 65536f;
            DrawTracer(ForegroundOrder, x - vx * 1.5f, y - vy * 1.5f, x, y, 1.25f, 255, 224, 96);
        }

        int gunIndex = mod.EquippedGunIndex();
        if (!CanUseGuns() || gunIndex < 0) return;

        float anchorX = bufferX + Player.PosX;
        float anchorY = bufferY + Player.PosY - 10;
        DrawGun(ForegroundOrder, anchorX, anchorY, GunCatalog.All[gunIndex].Kind, mod._aimX, mod._aimY);
        DrawAimArrow(ForegroundOrder, anchorX, anchorY, mod._aimX, mod._aimY);
    }

    private static void DrawGun(int order, float x, float y, GunKind kind, float dirX, float dirY)
    {
        float nx = -dirY;
        float ny = dirX;
        switch (kind)
        {
            case GunKind.Pistol:
                DrawOrientedQuad(order, x + dirX * 6f, y + dirY * 6f,
                    dirX, dirY, 12f, 2f, 150, 158, 170);
                DrawOrientedQuad(order, x + dirX * 1.5f + nx * 3f, y + dirY * 1.5f + ny * 3f,
                    dirX * 0.45f + nx * 0.9f, dirY * 0.45f + ny * 0.9f, 6f, 1.5f, 66, 70, 78);
                break;

            case GunKind.Shotgun:
                DrawOrientedQuad(order, x + dirX * 9f, y + dirY * 9f,
                    dirX, dirY, 22f, 1.5f, 102, 108, 116);
                DrawOrientedQuad(order, x - dirX * 3f, y - dirY * 3f,
                    dirX, dirY, 8f, 2.5f, 112, 72, 42);
                break;

            case GunKind.AssaultRifle:
                DrawOrientedQuad(order, x + dirX * 7.5f, y + dirY * 7.5f,
                    dirX, dirY, 17f, 2f, 94, 108, 92);
                DrawOrientedQuad(order, x - dirX * 2.5f, y - dirY * 2.5f,
                    dirX, dirY, 7f, 2.5f, 58, 64, 58);
                DrawOrientedQuad(order, x + dirX * 3f + nx * 3.5f, y + dirY * 3f + ny * 3.5f,
                    nx, ny, 6f, 1.5f, 54, 60, 54);
                break;

            case GunKind.MachineGun:
                DrawOrientedQuad(order, x + dirX * 8.5f, y + dirY * 8.5f,
                    dirX, dirY, 19f, 2.5f, 76, 82, 94);
                DrawOrientedQuad(order, x + dirX * 20f, y + dirY * 20f,
                    dirX, dirY, 7f, 1f, 126, 132, 142);
                DrawOrientedQuad(order, x + dirX * 4f + nx * 3.5f, y + dirY * 4f + ny * 3.5f,
                    nx, ny, 5f, 2.25f, 54, 58, 68);
                break;
        }
    }

    private static void DrawAimArrow(int order, float x, float y, float dirX, float dirY)
    {
        float nx = -dirY;
        float ny = dirX;
        float startX = x + dirX * 22f;
        float startY = y + dirY * 22f;
        float shaftX = x + dirX * 29f;
        float shaftY = y + dirY * 29f;
        float tipX = x + dirX * 34f;
        float tipY = y + dirY * 34f;

        DrawSolidTracer(order, startX, startY, shaftX, shaftY, 1.25f, 0, 0, 0);
        DrawSolidTracer(order, startX, startY, shaftX, shaftY, 0.5f, 255, 232, 48);

        var outlineA = new PrimVertex(tipX, tipY, 0, 0, 0);
        var outlineB = new PrimVertex(shaftX + nx * 3f, shaftY + ny * 3f, 0, 0, 0);
        var outlineC = new PrimVertex(shaftX - nx * 3f, shaftY - ny * 3f, 0, 0, 0);
        GpuPrims.Tri(order, outlineA, outlineB, outlineC);

        var arrowA = new PrimVertex(tipX - dirX, tipY - dirY, 255, 232, 48);
        var arrowB = new PrimVertex(shaftX + nx * 2f, shaftY + ny * 2f, 255, 232, 48);
        var arrowC = new PrimVertex(shaftX - nx * 2f, shaftY - ny * 2f, 255, 232, 48);
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
