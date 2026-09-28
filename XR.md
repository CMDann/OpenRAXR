# OpenRA XR: tabletop mode

XR mode puts the battlefield on a real table in front of you, like a board game. The sidebar (radar, money, power and build palette) stands on a tilted panel to the right of the board. Menus, the lobby and dialogs appear on an upright screen behind the board. You play with the controllers: point at things and pull the trigger, just like clicking a mouse.

It uses [OpenXR](https://www.khronos.org/openxr/), so it runs on SteamVR and other OpenXR runtimes.

## How it works

The game still renders its normal 2D frame, but to a fixed-size offscreen "virtual screen" (2048×1024 by default):

| Region of the virtual screen | Shown as |
| --- | --- |
| Left part (`VirtualScreenSize.X - SidebarWidth` wide) | The **board**, lying flat on the table |
| Right strip (`SidebarWidth`, 256px) | The **sidebar panel**, standing to the right of the board |
| Whole screen, only while a menu, the lobby or the main menu is open | The **menu screen**, upright behind the board |

Each region is handed to the OpenXR compositor as a quad layer. The compositor reprojects quad layers at the headset's refresh rate, so head movement stays smooth even when the game renders slower. Text on quad layers also stays sharp. Controller rays are intersected with the quads and turned into ordinary mouse events, so every part of the UI works without changes.

The desktop window keeps showing the virtual screen as a mirror, and the desktop mouse and keyboard still work in it.

## Mixed reality

SteamVR itself has no passthrough API, so passthrough depends on how your headset connects:

* **Quest with Virtual Desktop** (recommended). OpenRA fills everything behind the panels with a key color, and Virtual Desktop's chroma-key passthrough replaces that color with the camera view. In Virtual Desktop, go to *Streaming → Passthrough*, turn on chroma key, and set the key color to the value of `Xr.KeyColor`. The default is magenta, `FF00FF`, because it never appears in the game's artwork.
* **Runtimes with native alpha-blend passthrough.** Examples are the Rectus [openxr-steamvr-passthrough](https://github.com/Rectus/openxr-steamvr-passthrough) layer for Index/Vive, and some standalone runtimes. When the runtime advertises `XR_ENVIRONMENT_BLEND_MODE_ALPHA_BLEND`, OpenRA uses it automatically (`Xr.Passthrough: Auto`).
* Set `Xr.Passthrough: Off` for plain VR with a black background.

## Requirements

* **Windows + SteamVR** is the primary target. SteamVR's OpenGL support is broken on Linux, see ValveSoftware/SteamVR-for-Linux [#546](https://github.com/ValveSoftware/SteamVR-for-Linux/issues/546) and [#782](https://github.com/ValveSoftware/SteamVR-for-Linux/issues/782).
* **Linux** works with runtimes whose OpenGL path works, such as [Monado](https://monado.freedesktop.org/) or [WiVRn](https://github.com/WiVRn/WiVRn). The game must run on X11/GLX: set `SDL_VIDEODRIVER=x11` under Wayland.
* The Khronos OpenXR loader:
  * Windows: `make.ps1 all` downloads `openxr_loader.dll` into `bin/`.
  * Linux: install your distribution's package, e.g. `libopenxr-loader1` on Debian/Ubuntu or `openxr` on Arch.

If no loader, runtime or headset is found, the game logs the reason to the console and `graphics.log` and starts in normal desktop mode.

## Running

Start SteamVR (or your streaming app) first, then:

```
launch-game.cmd Game.Mod=ra Xr.Enabled=true
./launch-game.sh Game.Mod=ra Xr.Enabled=true
```

The first time, the board is placed in front of you at roughly table height. Grab it with the left grip to move it onto your real table.

## Controls (right-handed)

| Input | Action |
| --- | --- |
| Right controller ray | Mouse cursor |
| Right trigger | Left click: select, drag-select, use the sidebar and menus |
| Right **A** | Right click: order or deselect, depending on your mouse style setting |
| Right grip, while pointing at the board | Grab the map and drag it to scroll |
| Right thumbstick up/down | Zoom in/out |
| Left thumbstick | Scroll the map |
| Left grip (hold) | Pick up the board and move or rotate it; the position is saved on release |
| Left **Y** | Recenter the board in front of you |
| Left **menu** | Open the game menu (Escape) |

Set `Xr.LeftHanded: true` to swap the hands. Index, Vive and generic controllers have similar bindings, and you can remap everything in SteamVR's controller binding UI.

## Settings

These go in the `Xr:` section of `settings.yaml`, or on the command line as `Xr.<Name>=<value>`:

| Setting | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `False` | Turn XR mode on |
| `Passthrough` | `Auto` | `Auto`, `ChromaKey`, `AlphaBlend` or `Off` |
| `KeyColor` | `FF00FF` | Chroma-key background color |
| `VirtualScreenSize` | `2048,1024` | Size of the offscreen virtual screen |
| `SidebarWidth` | `256` | Width of the HUD strip that becomes the sidebar panel |
| `BoardWidth` | `0.9` | Board width in meters |
| `BoardPlaced`, `BoardX/Y/Z`, `BoardYaw` | | Saved board position; delete these to reset |
| `LeftHanded` | `False` | Point with the left controller |
| `StickPanSpeed` | `1.0` | Thumbstick scroll speed |
| `Haptics` | `True` | Controller vibration |

While XR is running, the desktop UI scale, hardware cursors and edge scrolling are ignored. Your saved settings are not changed.

## Code map

* `OpenRA.Game/Graphics/XrInterfaces.cs`: the platform-neutral `IXrDevice` interface, poses and quads.
* `OpenRA.Game/Graphics/XrTabletop.cs`: board and panel layout, ray-to-mouse input, board placement, thumbstick scrolling and zoom.
* `OpenRA.Platforms.Default/Xr/OpenXrDevice.cs`: the OpenXR session (Silk.NET.OpenXR), frame loop, swapchains, actions and haptics.
* `OpenRA.Platforms.Default/Xr/NativeGL.cs`: native WGL/GLX context handles for the OpenXR graphics binding.
* `Renderer.WorldViewport`: limits world rendering and mouse mapping to the board region. It defaults to the whole window, so desktop play is unchanged.

## Known limitations and next steps

* The board is flat. A small 3D tilt or height effect could be added later with a projection layer.
* Not implemented yet:
  * Hand tracking: `XR_EXT_hand_interaction`, with pinch as click.
  * Controller models.
  * A GL→Vulkan bridge for SteamVR on Linux.
* The command bar and support power buttons stay on the board edges. Only the classic sidebar is moved to its own panel.
