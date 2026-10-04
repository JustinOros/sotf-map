# Map

Press M in Sons of the Forest to show the GPS map full screen. Scroll the mouse
wheel to zoom in and out around your position. Press M again to close it.

The map is the same one the GPS uses, shown north up. Your position is the
yellow arrow, other players are blue arrows with their names, Kelvin is his K
and Virginia is her heart. Everything your GPS shows is on the map too, such as
discovered caves, GPS locators and other points of interest.

## Waypoints

While the map is open the mouse cursor is free. Left click anywhere on the map to
set a waypoint, left click it again or right click to clear it. Your waypoint is
yellow. It also shows in the world as a pin with the distance to it, visible
through trees and terrain.

In multiplayer your waypoint is shared through the game chat. Players with the
mod see it on their map and in the world in your color, which matches your arrow
on their map, and the chat line is hidden for them. Players without the mod see
a chat line with the coordinates. Each player has one waypoint at a time and
waypoints clear themselves after 5 minutes.

## The M key

In the base game M raises the GPS tracker. With this mod installed a quick press
of M opens the full map instead. Hold M for about half a second to switch M back
to raising the GPS, and hold it again to switch back to the map. The choice is
saved to `UserData/Map.txt`. The change only happens while the game is running with the mod loaded.
If M does not raise the GPS after you remove the mod, rebind it in the game's
control settings.

## Multiplayer

Client side only. Nothing gets installed on a dedicated server. Every player who
wants the full map needs the mod, and players without it are not affected.
Everyone in the game shows up on your map, whether or not they have the mod.

## Installation

### Step 1: Install RedLoader

[RedLoader](https://github.com/ToniMacaroni/RedLoader/releases/latest) is the mod
loader for Sons of the Forest. The game cannot load any mod without it, including
this one, so install it first. You only have to do this once.

1. Download `RedLoader.zip` from the
   [latest RedLoader release](https://github.com/ToniMacaroni/RedLoader/releases/latest)
2. Extract it into your Sons of the Forest folder, the one containing
   `SonsOfTheForest.exe`, usually
   `C:\Program Files (x86)\Steam\steamapps\common\Sons Of The Forest`
3. You should now see `version.dll` and a `_Redloader` folder sitting next to
   `SonsOfTheForest.exe`
4. Launch the game once and wait until you reach the main menu. The first launch
   takes a few minutes while RedLoader processes the game files
5. Check that `MODS` appears on the main menu, then quit

### Step 2: Install Map

1. Download `Map.zip` from the
   [latest release](https://github.com/JustinOros/sotf-map/releases/latest)
2. Open the `Mods` folder inside your Sons of the Forest install, usually
   `C:\Program Files (x86)\Steam\steamapps\common\Sons Of The Forest\Mods`
3. Extract the contents of the zip into that `Mods` folder

When you are done it should look like this:

```
Sons Of The Forest/
  Mods/
    Map.dll
    Map/
      manifest.json
```

## Updating

Run the installer again. It replaces the old version.

## Usage

| Input | What it does |
| --- | --- |
| `M` | Opens and closes the full map |
| Hold `M` | Switches M between the map and the GPS |
| Left click on the map | Sets your waypoint, or clears it if you click it |
| Right click on the map | Clears your waypoint |
| Mouse wheel | Zooms in and out around your position |

The map closes by itself when you open a menu, the console or chat.

## Building

This section is only for those who want to modify the source or build it
yourself.

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and a
[RedLoader](https://github.com/ToniMacaroni/RedLoader/releases/latest) install
that has been launched at least once, so the interop assemblies exist. The build
script offers to install both if they are missing.

```
.\build.ps1 -Install
```

The game folder is found through Steam. Override it with `-GameDir "path"` or the
`SOTF_PATH` environment variable.

To produce a release zip:

```
.\build.ps1 -Package
```

## Credits

Built against RedLoader by Toni Macaroni. Asset data from
[sotf-rigprobe](https://github.com/JustinOros/sotf-rigprobe).
