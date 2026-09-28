#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Numerics;
using OpenRA.Primitives;
using OpenRA.Widgets;

namespace OpenRA.Graphics
{
	/// <summary>The viewport operations that XR controllers can perform directly.</summary>
	public interface IXrViewportControls
	{
		/// <summary>Scrolls the view by the given amount of screen pixels.</summary>
		void Scroll(Vector2 screenPixels);

		/// <summary>Zooms in (positive) or out (negative) exponentially.</summary>
		void Zoom(float amount);
	}

	/// <summary>What the game is currently showing, which decides how the XR panels are laid out.</summary>
	public readonly record struct XrSceneState(bool HasWorld, WorldType WorldType, bool MenuOpen, bool TextInputFocused, IXrViewportControls Viewport)
	{
		/// <summary>A regular game (or replay) without a menu open is played on the board.</summary>
		public bool Tabletop => HasWorld && WorldType == WorldType.Regular && !MenuOpen;

		/// <summary>The map editor is shown on the upright screen but can still be scrolled with the sticks.</summary>
		public bool CanNavigate => Viewport != null && HasWorld && !MenuOpen && WorldType != WorldType.Shellmap;
	}

	/// <summary>
	/// Lays the game out as a board on a real table with floating HUD panels, and turns controller
	/// input into regular mouse, keyboard and modifier input on the virtual screen.
	/// </summary>
	public sealed class XrTabletop
	{
		public const string BoardQuad = "board";
		public const string SidebarQuad = "sidebar";
		public const string ScreenQuad = "screen";
		public const string HotkeysQuad = "hotkeys";
		public const string KeyboardQuad = "keyboard";
		public const string BeamQuad = "beam";

		const float StickDeadzone = 0.2f;
		const float DoubleClickSeconds = 0.35f;
		const int DoubleClickDistance = 6;
		const float BeamWidth = 0.004f;
		const float BeamMissLength = 0.6f;
		const float StickPanPixelsPerSecond = 1200f;
		const float StickZoomPerSecond = 1.5f;
		const float WheelNotchesPerSecond = 12f;

		// Panel placement relative to the board, in meters
		const float SidebarGap = 0.03f;
		const float SidebarScale = 1.2f;
		const float SidebarTilt = -15f;
		const float SidebarYaw = -25f;
		const float MenuScreenWidth = 1.2f;
		const float MenuScreenHeight = 0.45f;
		const float MenuScreenDistance = 0.2f;
		const float MenuScreenTilt = -10f;
		const float HotkeyPanelWidth = 0.26f;
		const float KeyboardWidth = 0.6f;

		static readonly Color BeamColor = Color.FromArgb(255, 255, 220, 64);
		static readonly Color BeamPressedColor = Color.FromArgb(255, 64, 255, 96);

		readonly IXrDevice device;
		readonly XrSettings settings;
		readonly Func<long> clock;
		readonly Action saveSettings;
		readonly Action exit;
		readonly Size screenSize;

		bool boardPlaced;

		// Board grab state
		bool grabbing;
		Vector3 grabOffset;
		float grabYawOffset;
		float gripYaw;

		// Pointer state
		MouseButton heldButtons;
		string captureQuad;
		int2 lastMousePos = new(-1, -1);
		readonly long[] lastDownTime = [long.MinValue / 2, long.MinValue / 2, long.MinValue / 2];
		readonly int2[] lastDownPos = new int2[3];
		readonly int[] tapCount = new int[3];
		XrHandState lastPointer;
		XrHandState lastOffHand;
		Widget lastHoverWidget;
		float wheelAccumulator;

		// Pointer-grip drag panning
		bool dragPanning;
		int2 dragPanLast;

		long lastUpdateTime = -1;

		readonly List<XrQuad> panels = [];

		/// <summary>Creates the tabletop layout for the given headset.</summary>
		/// <param name="device">The headset.</param>
		/// <param name="settings">XR settings. The board pose is stored here.</param>
		/// <param name="clock">Milliseconds since startup. Defaults to <see cref="Game.RunTime"/>.</param>
		/// <param name="saveSettings">Persists the board pose. Defaults to saving the settings file.</param>
		/// <param name="lookupHotkey">Returns the player's binding for a named hotkey. Defaults to the mod's hotkeys.</param>
		/// <param name="exit">Called when the runtime asks the game to quit. Defaults to <see cref="Game.Exit"/>.</param>
		public XrTabletop(IXrDevice device, XrSettings settings, Func<long> clock = null, Action saveSettings = null,
			Func<string, Hotkey> lookupHotkey = null, Action exit = null)
		{
			this.device = device;
			this.settings = settings;
			this.clock = clock ?? (() => Game.RunTime);
			this.saveSettings = saveSettings ?? settings.Save;
			this.exit = exit ?? Game.Exit;
			lookupHotkey ??= name => Game.ModData?.Hotkeys?[name].GetValue() ?? Hotkey.Invalid;

			screenSize = device.VirtualScreenSize;

			// The band below the game's layout area holds the XR-only hotkey menu and keyboard
			var mainHeight = device.LayoutSize.Height;
			var bandHeight = screenSize.Height - mainHeight;
			var sidebarWidth = Math.Clamp(settings.SidebarWidth, 0, screenSize.Width / 2);

			MainRect = new Rectangle(0, 0, screenSize.Width, mainHeight);
			BoardRect = new Rectangle(0, 0, screenSize.Width - sidebarWidth, mainHeight);
			SidebarRect = new Rectangle(BoardRect.Right, 0, sidebarWidth, mainHeight);
			HandPanel = new XrHandPanel(new Rectangle(0, mainHeight, BoardRect.Width, bandHeight), lookupHotkey);

			boardPlaced = settings.BoardPlaced;
			BoardPose = new XrPose(new Vector3(settings.BoardX, settings.BoardY, settings.BoardZ), YawRotationDegrees(settings.BoardYaw));
		}

		/// <summary>The part of the virtual screen shown on the board.</summary>
		public Rectangle BoardRect { get; }

		/// <summary>The strip of the virtual screen shown on the sidebar panel.</summary>
		public Rectangle SidebarRect { get; }

		/// <summary>The virtual screen without the reserved hand panel band. Shown on the upright menu screen.</summary>
		public Rectangle MainRect { get; }

		public XrHandPanel HandPanel { get; }

		public XrPose BoardPose { get; private set; }

		/// <summary>The panels shown this frame, not including the pointer beam.</summary>
		public IReadOnlyList<XrQuad> Panels => panels;

		/// <summary>Regular games are played on the board. The editor and shellmap use the whole screen.</summary>
		public Rectangle WorldRectFor(WorldType type) => type == WorldType.Regular ? BoardRect : MainRect;

		XrHand PointerHand => settings.LeftHanded ? XrHand.Left : XrHand.Right;
		XrHand OffHand => settings.LeftHanded ? XrHand.Right : XrHand.Left;

		float BoardWidth => Math.Max(settings.BoardWidth, 0.2f);
		float BoardDepth => BoardWidth * BoardRect.Height / BoardRect.Width;

		/// <summary>
		/// Updates the panel layout and feeds controller input into the game. Call once per render frame,
		/// after <see cref="IXrDevice.BeginFrame"/> and before the UI is drawn.
		/// </summary>
		public void Update(WorldRenderer worldRenderer, World inputWorld, List<XrQuad> quads, List<Rectangle> uiPanels)
		{
			var world = worldRenderer?.World;
			var hasWorld = world != null && !world.IsLoadingGameSave;
			var state = new XrSceneState(
				hasWorld,
				world?.Type ?? WorldType.Shellmap,
				IsMenuOpen(),
				Ui.KeyboardFocusWidget?.WantsTextInput ?? false,
				hasWorld ? new ViewportControls(worldRenderer.Viewport) : null);

			Update(state, new DefaultInputHandler(inputWorld), quads, uiPanels);
		}

		internal void Update(XrSceneState state, IInputHandler input, List<XrQuad> quads, List<Rectangle> uiPanels)
		{
			var now = clock();
			var dt = lastUpdateTime < 0 ? 0 : Math.Clamp((now - lastUpdateTime) / 1000f, 0, 0.1f);
			lastUpdateTime = now;

			if (device.ExitRequested)
			{
				exit();
				return;
			}

			if (!boardPlaced && device.HeadPoseValid)
				PlaceBoardInFrontOfHead();

			var pointer = device.GetHand(PointerHand);
			var offHand = device.GetHand(OffHand);

			Game.XrModifiers = GetModifiers(pointer, offHand);

			UpdateBoardGrab(offHand);

			// Recenter the board with the off-hand secondary button (Y on Touch controllers)
			if (offHand.ButtonB && !lastOffHand.ButtonB && device.HeadPoseValid)
			{
				PlaceBoardInFrontOfHead();
				saveSettings();
			}

			// The off-hand menu button opens the game menu (or closes dialogs) like Escape on the keyboard
			if (offHand.Menu && !lastOffHand.Menu)
			{
				input.OnKeyInput(new KeyInput { Event = KeyInputEvent.Down, Key = Keycode.ESCAPE, MultiTapCount = 1 });
				input.OnKeyInput(new KeyInput { Event = KeyInputEvent.Up, Key = Keycode.ESCAPE, MultiTapCount = 1 });
			}

			// Clicking the off-hand thumbstick toggles the hotkey menu above the off hand
			if (offHand.StickClick && !lastOffHand.StickClick)
				HandPanel.HotkeysOpen = !HandPanel.HotkeysOpen;

			HandPanel.EditorMode = state.HasWorld && state.WorldType == WorldType.Editor;

			// The keyboard appears whenever a text field is focused, unless the player closed it
			if (!state.TextInputFocused)
			{
				HandPanel.KeyboardOpen = false;
				HandPanel.KeyboardDismissed = false;
			}
			else if (!HandPanel.KeyboardDismissed)
				HandPanel.KeyboardOpen = true;

			BuildPanels(state.Tabletop, offHand);

			uiPanels.Clear();
			if (state.Tabletop)
			{
				uiPanels.Add(BoardRect);
				uiPanels.Add(SidebarRect);
			}
			else
				uiPanels.Add(MainRect);

			// Off-hand thumbstick scrolls the map (stick up scrolls towards the far edge of the board)
			if (state.CanNavigate && dt > 0)
			{
				var pan = ApplyDeadzone(offHand.Stick);
				if (pan != Vector2.Zero)
					state.Viewport.Scroll(new Vector2(pan.X, -pan.Y) * StickPanPixelsPerSecond * settings.StickPanSpeed * dt);
			}

			UpdatePointer(input, state, pointer, dt, quads);

			lastPointer = pointer;
			lastOffHand = offHand;
		}

		/// <summary>Controller buttons that act like holding Shift, Ctrl or Alt on the keyboard.</summary>
		static Modifiers GetModifiers(XrHandState pointer, XrHandState offHand)
		{
			var modifiers = Modifiers.None;
			if (offHand.IsActive && offHand.Select)
				modifiers |= Modifiers.Shift;

			if (offHand.IsActive && offHand.ButtonA)
				modifiers |= Modifiers.Ctrl;

			if (pointer.IsActive && pointer.ButtonB)
				modifiers |= Modifiers.Alt;

			return modifiers;
		}

		static bool IsMenuOpen()
		{
			if (Ui.CurrentWindow() != null)
				return true;

			var menuRoot = Ui.Root.GetOrNull("MENU_ROOT");
			return menuRoot != null && menuRoot.Children.Count > 0;
		}

		void BuildPanels(bool tabletop, XrHandState offHand)
		{
			panels.Clear();
			if (tabletop)
			{
				// Lay the world flat on the table: rotate the quad's +Z normal to point up and the top of the image away from the player
				var boardOrientation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2);
				panels.Add(new XrQuad(BoardQuad, BoardRect, BoardPose.Multiply(new XrPose(Vector3.Zero, boardOrientation)),
					new Vector2(BoardWidth, BoardDepth)));

				if (SidebarRect.Width > 0)
				{
					var pixelsPerMeter = BoardRect.Width / BoardWidth / SidebarScale;
					var size = new Vector2(SidebarRect.Width / pixelsPerMeter, SidebarRect.Height / pixelsPerMeter);
					var tilt = Quaternion.CreateFromAxisAngle(Vector3.UnitX, SidebarTilt * MathF.PI / 180);
					var yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, SidebarYaw * MathF.PI / 180);
					var center = new Vector3(BoardWidth / 2 + SidebarGap + size.X / 2, size.Y / 2 * MathF.Cos(-SidebarTilt * MathF.PI / 180), -BoardDepth / 4);
					panels.Add(new XrQuad(SidebarQuad, SidebarRect, BoardPose.Multiply(new XrPose(center, yaw * tilt)), size));
				}
			}
			else
			{
				// Menus, the lobby, dialogs and the map editor are shown on an upright screen behind the board
				var tilt = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MenuScreenTilt * MathF.PI / 180);
				var height = MenuScreenWidth * MainRect.Height / MainRect.Width;
				var center = new Vector3(0, MenuScreenHeight, -BoardDepth / 2 - MenuScreenDistance);
				panels.Add(new XrQuad(ScreenQuad, MainRect, BoardPose.Multiply(new XrPose(center, tilt)),
					new Vector2(MenuScreenWidth, height)));
			}

			// The hotkey menu floats above the off hand, tilted towards the player like a wristwatch
			var hotkeys = HandPanel.HotkeyRect;
			if (HandPanel.HotkeysOpen && offHand.IsActive && hotkeys.Height > 0)
			{
				var size = new Vector2(HotkeyPanelWidth, HotkeyPanelWidth * hotkeys.Height / hotkeys.Width);
				var faceUp = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -50 * MathF.PI / 180);
				var pose = offHand.Grip.Multiply(new XrPose(new Vector3(0, 0.06f, 0.02f), faceUp));
				panels.Add(new XrQuad(HotkeysQuad, hotkeys, pose, size));
			}

			// The keyboard lies at the near edge of the table like a laptop keyboard
			var keyboard = HandPanel.KeyboardRect;
			if (HandPanel.KeyboardOpen && keyboard.Height > 0)
			{
				var size = new Vector2(KeyboardWidth, KeyboardWidth * keyboard.Height / keyboard.Width);
				var tilt = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -60 * MathF.PI / 180);
				var center = new Vector3(0, 0.06f, BoardDepth / 2 + 0.02f);
				panels.Add(new XrQuad(KeyboardQuad, keyboard, BoardPose.Multiply(new XrPose(center, tilt)), size));
			}
		}

		void UpdatePointer(IInputHandler input, XrSceneState state, XrHandState pointer, float dt, List<XrQuad> quads)
		{
			quads.Clear();
			quads.AddRange(panels);
			HandPanel.Hover = null;

			if (!pointer.IsActive)
			{
				ReleaseButtons(input);
				return;
			}

			var hit = Raycast(pointer.Aim, captureQuad);
			var modifiers = Game.GetModifierKeys();

			if (hit != null && (hit.Value.Quad == HotkeysQuad || hit.Value.Quad == KeyboardQuad))
			{
				// The hand panel is XR-only UI and handles its own clicks
				HandPanel.Hover = hit.Value.Pixel;
				if (pointer.Select && !lastPointer.Select && HandPanel.Click(hit.Value.Pixel, input, modifiers))
					Vibrate(PointerHand, 0.35f, 0.02f);
			}
			else if (hit != null)
			{
				var pos = hit.Value.Pixel;
				if (pos != lastMousePos)
				{
					var delta = lastMousePos.X < 0 ? int2.Zero : pos - lastMousePos;
					input.OnMouseInput(new MouseInput(MouseInputEvent.Move, heldButtons, pos, delta, modifiers, 0));
					lastMousePos = pos;
				}

				UpdateButton(input, MouseButton.Left, pointer.Select, lastPointer.Select, hit.Value.Quad, modifiers);
				UpdateButton(input, MouseButton.Right, pointer.Order, lastPointer.Order, hit.Value.Quad, modifiers);

				if (hit.Value.Quad == BoardQuad)
				{
					wheelAccumulator = 0;

					// Pointer thumbstick zooms the board
					var zoom = ApplyDeadzone(pointer.Stick).Y;
					if (zoom != 0 && state.CanNavigate && dt > 0)
						state.Viewport.Zoom(zoom * StickZoomPerSecond * dt);

					// Squeezing the pointer grip over the board grabs the map and drags it like a sheet of paper
					if (pointer.Grab && state.CanNavigate)
					{
						if (dragPanning)
						{
							var d = dragPanLast - pos;
							if (d != int2.Zero)
								state.Viewport.Scroll(d.ToVector2());
						}

						dragPanning = true;
						dragPanLast = pos;
					}
				}
				else
					UpdateWheel(input, pointer, pos, dt, modifiers);
			}
			else
			{
				// Releasing a button while pointing away from every panel must still release it in the game
				if (!pointer.Select && lastPointer.Select)
					UpdateButton(input, MouseButton.Left, false, true, null, modifiers);

				if (!pointer.Order && lastPointer.Order)
					UpdateButton(input, MouseButton.Right, false, true, null, modifiers);
			}

			if (!pointer.Grab)
				dragPanning = false;

			// Gentle feedback when the pointer moves onto a new interactive widget
			var hover = Ui.MouseOverWidget;
			if (hover != lastHoverWidget)
			{
				if (hover != null && hover.Parent?.Id != "WORLD_ROOT")
					Vibrate(PointerHand, 0.15f, 0.01f);

				lastHoverWidget = hover;
			}

			quads.Add(BuildBeam(pointer.Aim, hit?.Distance ?? BeamMissLength, pointer.Select || pointer.Order));
		}

		/// <summary>On the sidebar and menus the pointer thumbstick acts as a mouse wheel.</summary>
		void UpdateWheel(IInputHandler input, XrHandState pointer, int2 pos, float dt, Modifiers modifiers)
		{
			var y = ApplyDeadzone(pointer.Stick).Y;
			if (y == 0)
			{
				wheelAccumulator = 0;
				return;
			}

			wheelAccumulator += y * WheelNotchesPerSecond * dt;
			while (MathF.Abs(wheelAccumulator) >= 1)
			{
				var notch = MathF.Sign(wheelAccumulator);
				input.OnMouseInput(new MouseInput(MouseInputEvent.Scroll, MouseButton.None, pos, new int2(0, notch), modifiers, 0));
				wheelAccumulator -= notch;
			}
		}

		void UpdateButton(IInputHandler input, MouseButton button, bool down, bool wasDown, string quad, Modifiers modifiers)
		{
			if (down == wasDown)
				return;

			var index = button == MouseButton.Left ? 0 : button == MouseButton.Right ? 1 : 2;
			if (down)
			{
				var now = clock();
				var isRepeat = (now - lastDownTime[index]) / 1000f < DoubleClickSeconds
					&& (lastMousePos - lastDownPos[index]).LengthSquared <= DoubleClickDistance * DoubleClickDistance;

				tapCount[index] = isRepeat ? tapCount[index] + 1 : 1;
				lastDownTime[index] = now;
				lastDownPos[index] = lastMousePos;

				heldButtons |= button;
				captureQuad = quad;
				input.OnMouseInput(new MouseInput(MouseInputEvent.Down, button, lastMousePos, int2.Zero, modifiers, tapCount[index]));
				Vibrate(PointerHand, 0.35f, 0.02f);
			}
			else
			{
				heldButtons &= ~button;
				if (heldButtons == MouseButton.None)
					captureQuad = null;

				input.OnMouseInput(new MouseInput(MouseInputEvent.Up, button, lastMousePos, int2.Zero, modifiers, tapCount[index]));
			}
		}

		void ReleaseButtons(IInputHandler input)
		{
			var modifiers = Game.GetModifierKeys();
			if (heldButtons.HasFlag(MouseButton.Left))
				UpdateButton(input, MouseButton.Left, false, true, null, modifiers);

			if (heldButtons.HasFlag(MouseButton.Right))
				UpdateButton(input, MouseButton.Right, false, true, null, modifiers);

			dragPanning = false;
		}

		void Vibrate(XrHand hand, float amplitude, float seconds)
		{
			if (settings.Haptics)
				device.Vibrate(hand, amplitude, seconds);
		}

		static Vector2 ApplyDeadzone(Vector2 stick)
		{
			var length = stick.Length();
			if (length < StickDeadzone)
				return Vector2.Zero;

			// Rescale so that movement starts smoothly at the edge of the deadzone
			return stick / length * Math.Min((length - StickDeadzone) / (1 - StickDeadzone), 1);
		}

		void UpdateBoardGrab(XrHandState offHand)
		{
			if (offHand.IsActive && offHand.Grab)
			{
				var yaw = HeadingOf(offHand.Grip.Forward, gripYaw);
				gripYaw = yaw;

				if (!grabbing)
				{
					grabbing = true;
					grabOffset = Vector3.Transform(BoardPose.Position - offHand.Grip.Position, Quaternion.Inverse(YawRotation(yaw)));
					grabYawOffset = HeadingOf(BoardPose.Forward, 0) - yaw;
					Vibrate(OffHand, 0.4f, 0.03f);
				}

				var boardYaw = yaw + grabYawOffset;
				var position = offHand.Grip.Position + Vector3.Transform(grabOffset, YawRotation(yaw));
				BoardPose = new XrPose(position, YawRotation(boardYaw));
				boardPlaced = true;
				StoreBoardPose();
			}
			else if (grabbing)
			{
				grabbing = false;
				saveSettings();
			}
		}

		void PlaceBoardInFrontOfHead()
		{
			var head = device.HeadPose;
			var yaw = HeadingOf(head.Forward, 0);
			var forward = Vector3.Transform(-Vector3.UnitZ, YawRotation(yaw));

			// A comfortable seated tabletop: the near edge a short reach in front of the player, below eye level
			var position = head.Position + forward * (0.25f + BoardDepth / 2) - new Vector3(0, 0.45f, 0);
			BoardPose = new XrPose(position, YawRotation(yaw));
			boardPlaced = true;
			StoreBoardPose();
		}

		void StoreBoardPose()
		{
			settings.BoardPlaced = true;
			settings.BoardX = BoardPose.Position.X;
			settings.BoardY = BoardPose.Position.Y;
			settings.BoardZ = BoardPose.Position.Z;
			settings.BoardYaw = HeadingOf(BoardPose.Forward, 0) * 180 / MathF.PI;
		}

		/// <summary>Returns the rotation around +Y (in radians) that turns -Z to face along the given direction projected onto the floor.</summary>
		static float HeadingOf(Vector3 direction, float fallback)
		{
			if (direction.X * direction.X + direction.Z * direction.Z < 1e-6f)
				return fallback;

			return MathF.Atan2(-direction.X, -direction.Z);
		}

		static Quaternion YawRotation(float radians) => Quaternion.CreateFromAxisAngle(Vector3.UnitY, radians);

		static Quaternion YawRotationDegrees(float degrees) => YawRotation(degrees * MathF.PI / 180);

		public void DrawHandPanel(Renderer renderer) => HandPanel.Draw(renderer);

		public readonly record struct RayHit(string Quad, int2 Pixel, float Distance);

		/// <summary>
		/// Finds the nearest panel hit by the ray. While a button is held the ray stays captured by the panel it
		/// was pressed on (extending the plane past its edges), so that drag-selections keep working.
		/// </summary>
		public RayHit? Raycast(XrPose ray, string capture)
		{
			RayHit? best = null;
			foreach (var quad in panels)
			{
				var captured = capture == quad.Name;
				if (capture != null && !captured)
					continue;

				if (!IntersectQuad(ray, quad, captured, out var uv, out var distance))
					continue;

				if (best == null || distance < best.Value.Distance)
				{
					var pixel = new int2(
						quad.Source.X + (int)(uv.X * quad.Source.Width),
						quad.Source.Y + (int)(uv.Y * quad.Source.Height));

					best = new RayHit(quad.Name, pixel, distance);
				}
			}

			return best;
		}

		/// <summary>
		/// Intersects a ray (along the pose's forward axis) with a quad. Returns texture coordinates with (0, 0)
		/// at the top-left of the quad's image. If clamp is set, hits beyond the edges are clamped instead of rejected.
		/// </summary>
		public static bool IntersectQuad(XrPose ray, XrQuad quad, bool clamp, out Vector2 uv, out float distance)
		{
			uv = default;
			distance = 0;

			var normal = quad.Pose.Forward * -1;
			var direction = ray.Forward;
			var denominator = Vector3.Dot(direction, normal);
			if (MathF.Abs(denominator) < 1e-5f)
				return false;

			distance = Vector3.Dot(quad.Pose.Position - ray.Position, normal) / denominator;
			if (distance < 0)
				return false;

			var local = quad.Pose.InverseTransformPoint(ray.Position + direction * distance);
			var u = local.X / quad.Size.X + 0.5f;
			var v = 0.5f - local.Y / quad.Size.Y;

			if (clamp)
			{
				u = Math.Clamp(u, 0, 0.9999f);
				v = Math.Clamp(v, 0, 0.9999f);
			}
			else if (u < 0 || u >= 1 || v < 0 || v >= 1)
				return false;

			uv = new Vector2(u, v);
			return true;
		}

		XrQuad BuildBeam(XrPose aim, float length, bool pressed)
		{
			// A thin ribbon along the ray, turned to face the player's eyes as much as possible
			var direction = aim.Forward;
			var toHead = device.HeadPoseValid ? device.HeadPose.Position - aim.Position : aim.Up;
			var normal = toHead - Vector3.Dot(toHead, direction) * direction;
			if (normal.LengthSquared() < 1e-8f)
				normal = aim.Up;

			normal = Vector3.Normalize(normal);
			var right = Vector3.Cross(direction, normal);
			var basis = new Matrix4x4(
				right.X, right.Y, right.Z, 0,
				direction.X, direction.Y, direction.Z, 0,
				normal.X, normal.Y, normal.Z, 0,
				0, 0, 0, 1);

			var orientation = Quaternion.CreateFromRotationMatrix(basis);
			var center = aim.Position + direction * (length / 2);
			return new XrQuad(BeamQuad, Rectangle.Empty, new XrPose(center, orientation), new Vector2(BeamWidth, length),
				pressed ? BeamPressedColor : BeamColor);
		}

		sealed class ViewportControls(Viewport viewport) : IXrViewportControls
		{
			public void Scroll(Vector2 screenPixels) => viewport.Scroll(screenPixels, false);
			public void Zoom(float amount) => viewport.AdjustZoom(amount);
		}
	}
}
