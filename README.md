# SotN Recomp Guns

An experimental [SymphonyRecomp](https://github.com/BlackLabelHQ/SymphonyRecomp) mod that adds four aimable firearms to Castlevania: Symphony of the Night.

The mod provides right-stick aiming, distinct fire rates and spread, magazines, reserve ammunition, manual reloading, tracers, and an aim arrow. It uses SOTN's normal attack entities so enemy defense, damage, death, drops, experience, and boss behavior remain in the game's combat pipeline.

## Status

This is an early prototype targeting the current US SymphonyRecomp beta. Its source passes SymphonyRecomp's runtime Roslyn compilation path, but it still needs testing with a legally owned US PlayStation copy of the game. Expect bugs and balance changes.

## Weapons

The prototype repurposes four existing throwing-weapon inventory slots:

| Original item | Gun |
| --- | --- |
| Shuriken | Pistol |
| Cross shuriken | Assault rifle |
| Buffalo star | Shotgun |
| Flame star | Machine gun |

The items are renamed, made reusable, and automatically granted while the mod is active.

## Installation

1. Install or build the latest SymphonyRecomp using its official instructions and a legally owned US PSX copy of Symphony of the Night.
2. Open SymphonyRecomp's `mods` directory.
3. Clone this repository into that directory:

   ```bash
   git clone https://github.com/mojomast/sotnrecompguns.git
   ```

4. Start SymphonyRecomp and enable **Guns** from its mods menu.
5. Load an Alucard game. The four gun items will be added to the hand inventory by default.

SymphonyRecomp compiles the files under `source/` at runtime, so the mod does not require a separate build step.

When updating an existing clone, run `git pull`, reload the mod, and confirm `Guns v0.1.3` appears at the top of its settings panel.

## Controls

- Equip one of the four gun items in either hand.
- Aim with the right analog stick.
- Fire with `R2`.
- Reload with `R1`.
- With the stick neutral, the last aim direction is retained.

The pistol and shotgun are semi-automatic. The assault rifle and machine gun fire while `R2` is held.

A compact black-outlined yellow arrow shows the current aim direction, and each attached gun graphic rotates with the stick.

## Configuration

The SymphonyRecomp mods panel exposes options for:

- Automatic reloading.
- Right-stick deadzone.
- Weapon spread scaling.
- Refilling all ammunition.

The bottom of the settings panel shows the raw right-stick values, normalized aim direction, item-name patch status, and render-hook activity for controller and mod troubleshooting.

## Prototype limitations

- Gun and bullet visuals are geometric placeholders.
- Bullets currently stop on enemies or screen bounds, but not room geometry.
- Native enemy collision is frame-sampled, so very small targets and overlapping enemies still need gameplay validation.
- The mod caps itself at 14 live projectiles because gun rounds share SOTN's player-attack entity pool.
- Ammunition is session state and resets when a game is loaded.
- Controller aiming is implemented first. SymphonyRecomp does not currently expose cursor capture for reliable mouse aiming.
- This targets the current US SymphonyRecomp function maps and may need updates as the beta changes.

## Development

The mod is source-only C# and uses the public runtime types shipped with SymphonyRecomp and RecompOne. The current implementation hooks the shared weapon overlay for item IDs `0x4B` through `0x4E`, updates marked player-attack entities through `dra/UpdatePlayerEntities`, and renders placeholder geometry through `GpuPrims`.

The source has been compile-checked against SymphonyRecomp commit `78bea85f68b39afdef190f2e3186ab1a16de9c93` and RecompOne commit `08f1b5ca3d3bfec0113e48d76f84af8f8cec1a67` using .NET 10.

## Testing checklist

1. Load an Alucard save and confirm all four renamed items appear in inventory.
2. Equip every gun in each hand and verify `R2` firing and `R1` reloading.
3. Test horizontal, vertical, and diagonal aiming while standing, crouching, jumping, and walking.
4. Confirm normal enemy damage, death, drops, experience, and boss damage.
5. Change equipment while bullets are active and cross room boundaries.
6. Disable and reload the mod, confirming gun entities are removed and item properties are restored.

## Upstream policy

This is an independent mod repository and is not affiliated with Black Label HQ or Konami. SymphonyRecomp's maintainers explicitly prohibit AI-generated issues and pull requests in their repositories; do not submit this project upstream in violation of their contribution policy.
