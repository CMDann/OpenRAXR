# OpenRA XR: tabletop mode

XR mode puts the battlefield on a real table in front of you, like a board game. The sidebar (radar, money, power and build palette) stands on a tilted panel to the right of the board. Menus, the lobby and dialogs appear on an upright screen behind the board. You play with the controllers: point at things and pull the trigger, just like clicking a mouse.

It uses [OpenXR](https://www.khronos.org/openxr/), so it runs on SteamVR and other OpenXR runtimes.

## How it works

The game still renders its normal 2D frame, but to a fixed-size offscreen "virtual screen" (2048×1280 by default). The game UI is laid out in the top 2048×1024. The bottom 256px band is reserved for XR-only controls:

| Region of the virtual screen | Shown as |
| --- | --- |
| Left part of the layout (`VirtualScreenSize.X - SidebarWidth` wide) | The **board**, lying flat on the table |
| Right strip (`SidebarWidth`, 256px) | The **sidebar panel**, standing to the right of the board |
| The whole layout, while a menu, the lobby, the main menu or the map editor is open | The **menu screen**, upright behind the board |
| Left of the bottom band | The **hotkey menu**, above your left hand |
| Right of the bottom band | The **keyboard**, at the near edge of the table |

Regular games and replays use the board. The map editor and the main menu's background map fill the whole layout on the upright screen.

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
| Right thumbstick up/down | Zoom the board, or act as the mouse wheel on the sidebar, menus and lists |
| Right **B** (hold) | Alt: force-move, and Alt-click control groups to jump to them |
| Left thumbstick | Scroll the map (also in the map editor) |
| Left trigger (hold) | Shift: add to the selection, queue orders, build 5 at a time, place several walls |
| Left **X** (hold) | Ctrl: force-attack, create control groups, editor shortcuts |
| Left thumbstick click | Open or close the hotkey menu |
| Left grip (hold) | Pick up the board and move or rotate it; the position is saved on release |
| Left **Y** | Recenter the board in front of you |
| Left **menu** | Open the game menu (Escape) |

The held modifiers apply to everything, including the command bar, the production palette, and keyboard shortcuts typed on the desktop keyboard.

### Hotkey menu

Click the left thumbstick to open the hotkey menu above your left hand, then point at a button and pull the trigger:

* **1–0**: control groups. Tap to select a group, hold Ctrl (left X) to create one, Shift (left trigger) to add the group to your selection, Ctrl+Shift to add the selection to the group, and Alt (right B) to jump to the group.
* **All, Type, Event, Base, Harv, View Sel, Pause, Save**: select all units, select all units of the same type, jump to the last event, cycle bases, cycle harvesters, jump to the selection, pause, and quick save.
* In the map editor: **Undo, Redo, Copy, Paste, Delete, Grid, Save**.

The buttons send the key currently bound in the hotkey settings, so rebinding a hotkey also changes the menu.

### Keyboard

When a text field has focus (chat, the lobby player name, server passwords, save names), a keyboard appears at the near edge of the table. It has letters and digits, **Shift** (capitalises the next letter), **Sym** (symbols), **Bksp**, **Enter**, **Esc**, **Space** and **Close**. A physical keyboard on the desktop mirror window works too.

Set `Xr.LeftHanded: true` to swap the hands. Index, Vive and generic controllers have similar bindings, and you can remap everything in SteamVR's controller binding UI.

## Settings

These go in the `Xr:` section of `settings.yaml`, or on the command line as `Xr.<Name>=<value>`:

| Setting | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `False` | Turn XR mode on |
| `Passthrough` | `Auto` | `Auto`, `ChromaKey`, `AlphaBlend` or `Off` |
| `KeyColor` | `FF00FF` | Chroma-key background color |
| `VirtualScreenSize` | `2048,1280` | Size of the offscreen virtual screen, including the hand panel band |
| `HandPanelHeight` | `256` | Height of the band at the bottom that holds the hotkey menu and keyboard |
| `SidebarWidth` | `256` | Width of the HUD strip that becomes the sidebar panel |
| `BoardWidth` | `0.9` | Board width in meters |
| `BoardPlaced`, `BoardX/Y/Z`, `BoardYaw` | | Saved board position; delete these to reset |
| `LeftHanded` | `False` | Point with the left controller |
| `StickPanSpeed` | `1.0` | Thumbstick scroll speed |
| `Haptics` | `True` | Controller vibration |

While XR is running, the desktop UI scale, hardware cursors and edge scrolling are ignored. Your saved settings are not changed.

## Code map

* `OpenRA.Game/Graphics/XrInterfaces.cs`: the platform-neutral `IXrDevice` interface, poses and quads.
* `OpenRA.Game/Graphics/XrTabletop.cs`: board and panel layout, ray-to-mouse input, controller modifiers, the thumbstick mouse wheel, board placement, thumbstick scrolling and zoom.
* `OpenRA.Game/Graphics/XrHandPanel.cs`: the hotkey menu and keyboard.
* `Renderer.UIPanels`: the panel rectangles, so tooltips and dropdowns stay on one panel.
* `OpenRA.Platforms.Default/Xr/OpenXrDevice.cs`: the OpenXR session (Silk.NET.OpenXR), frame loop, swapchains, actions and haptics.
* `OpenRA.Platforms.Default/Xr/NativeGL.cs`: native WGL/GLX context handles for the OpenXR graphics binding.
* `Renderer.WorldViewport`: limits world rendering and mouse mapping to the board region. It defaults to the whole window, so desktop play is unchanged.

## Tests

The unit tests run headless in CI with `make tests`. A fake platform (`OpenRA.Test/Fakes`) builds the `Renderer` without a GPU, and a fake headset drives `XrTabletop` frame by frame. The tests cover:

* Desktop behavior with XR off: world viewport, viewport math against the original formulas, widget layout variables, settings, and keyboard modifiers.
* Every XR feature: board placement and grabbing, the panel layout, ray-to-pixel mapping, clicks, double clicks, drag capture, modifiers, the wheel, stick panning and zoom, haptics, the hotkey menu and the keyboard.
* Each mod's HUD layout. The world controllers must follow the world viewport, and the sidebar must fit in the XR strip.

## Known limitations and next steps

* The board is flat. A small 3D tilt or height effect could be added later with a projection layer.
* Not implemented yet:
  * Hand tracking: `XR_EXT_hand_interaction`, with pinch as click.
  * Controller models.
  * A GL→Vulkan bridge for SteamVR on Linux.
* The command bar and support power buttons stay on the board edges. Only the classic sidebar is moved to its own panel.
