# Silksong DiveBind

A tiny [BepInEx](https://docs.bepinex.dev/) mod for **Hollow Knight: Silksong** that binds a controller button to a **forward dive attack that always goes in your direction of motion** — no more diving the wrong way.

## The problem

Silksong's downward attack (a directional *DownSpike* for crests that have one) dives in the direction Hornet is **facing**. But facing is set from the **held stick** at the moment you attack (`HeroController.TrySetCorrectFacing` reads `move_input`). So if you're flying forward through the air but holding *back* — or nothing at all — you dive **backwards**. In a fast fight that's a death sentence.

## What DiveBind does

Bind a button (default **R2 / Right Trigger**). While airborne, pressing it performs your crest's real down attack, but forces Hornet to face your **actual horizontal motion** first — so the dive always launches the way you're already travelling.

- 🎯 Dives in your **direction of travel**, not the direction you happen to be holding
- 🎮 Reads the game's own controller layer, so it works with **triggers** (R2/L2), bumpers, face buttons, d-pad, and stick clicks
- ⌨️ **F4** in-game menu to rebind and toggle options
- 🪶 Uses whatever down attack your **current crest** has — it just fixes the direction

## Install

1. Install [BepInEx](https://docs.bepinex.dev/) (5.x / the build your other Silksong mods use) into your Silksong folder.
2. Download `DiveBind.dll` from the [latest release](../../releases/latest).
3. Drop it into `Hollow Knight Silksong/BepInEx/plugins/`.
4. Launch the game.

## Usage

- **Hold-nothing-and-still-dive-right:** jump, move forward, then press your bound button — you dive forward even if the stick is neutral or pulled back.
- Press **F4** to open the bind menu:
  - **Rebind** — press any controller button/trigger to bind it.
  - **Only while airborne** — when on (default), a grounded press is ignored so it won't hijack that button on the ground.
  - Live readout of airborne state and the last dive's direction.

## Configuration

Settings live in `BepInEx/config/com.will.silksong.divebind.cfg` (also editable from the F4 menu):

| Key | Default | Meaning |
|-----|---------|---------|
| `Dive / Control` | `RightTrigger` | Controller control that triggers the dive (any `InControl.InputControlType`). |
| `Dive / OnlyInAir` | `true` | Only fire while airborne. |
| `Dive / MenuKey` | `F4` | Key that opens the menu. |

## How it works

On the bound control's press (airborne), the mod reads `HeroController.Body.linearVelocity.x` to decide your true forward direction, then invokes the game's private `Attack(AttackDirection.downward)`. A Harmony prefix on `TrySetCorrectFacing` runs only during that synthetic attack and flips Hornet to face your motion direction instead of the stick — so the DownSpike's facing-derived horizontal launch comes out correct. Falls back to held input, then current facing, when you have no horizontal speed.

## Build from source

Requires the [.NET SDK](https://dotnet.microsoft.com/download) and a Silksong install (for the game assemblies it references).

```sh
dotnet build src/DiveMod.csproj -c Release
```

The project auto-detects the default Windows Steam path. If yours differs, override it:

```sh
dotnet build src/DiveMod.csproj -c Release -p:GameRoot="D:\Games\Hollow Knight Silksong"
```

The built `DiveBind.dll` lands in `src/bin/Release/`.

## Caveats

- It triggers the down attack directly, bypassing the game's normal attack-input gating; the mod adds its own airborne check, a short debounce, and guards against firing mid-attack/dash.
- It reads the *active* controller, so in local co-op setups with two pads it can't distinguish pad 1 from pad 2.
- Crests whose down attack isn't a directional dive still work — you just get that crest's down attack, facing your motion.

## License

[MIT](LICENSE).
