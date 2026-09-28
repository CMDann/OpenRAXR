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
	/// <summary>
	/// Lays the game out as a board on a real table with floating HUD panels, and turns controller
	/// rays into regular mouse input on the virtual screen.
	/// </summary>
	public sealed class XrTabletop
	{
		const float StickDeadzone = 0.2f;
		const float DoubleClickSeconds = 0.35f;
		const int DoubleClickDistance = 6;
		const float BeamWidth = 0.004f;
		const float BeamMissLength = 0.6f;

		// Panel placement relative to the board, in meters
		const float SidebarGap = 0.03f;
		const float SidebarScale = 1.2f;
		const float SidebarTilt = -15f;
		const float SidebarYaw = -25f;
		const float MenuScreenWidth = 1.2f;
		const float MenuScreenHeight = 0.45f;
		const float MenuScreenDistance = 0.2f;
		const float MenuScreenTilt = -10f;

		static readonly Color BeamColor = Color.FromArgb(255, 255, 220, 64);
		static readonly Color BeamPressedColor = Color.FromArgb(255, 64, 255, 96);

		readonly IXrDevice device;
		readonly XrSettings settings;
		readonly Size screenSize;
		readonly Rectangle sidebarRect;

		XrPose boardPose;
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
		readonly long[] lastDownTime = new long[3];
		readonly int2[] lastDownPos = new int2[3];
		readonly int[] tapCount = new int[3];
		XrHandState lastPointer;
		XrHandState lastOffHand;
		Widget lastHoverWidget;

		// Pointer-grip drag panning
		bool dragPanning;
		int2 dragPanLast;

		long lastUpdateTime;

		readonly List<XrQuad> panels = [];

		public XrTabletop(IXrDevice device, XrSettings settings)
		{
			this.device = device;
			this.settings = settings;
			screenSize = device.VirtualScreenSize;

			var sidebarWidth = Math.Clamp(settings.SidebarWidth, 0, screenSize.Width / 2);
			WorldRect = new Rectangle(0, 0, screenSize.Width - sidebarWidth, screenSize.Height);
			sidebarRect = new Rectangle(WorldRect.Right, 0, sidebarWidth, screenSize.Height);

			boardPlaced = settings.BoardPlaced;
			boardPose = new XrPose(new Vector3(settings.BoardX, settings.BoardY, settings.BoardZ), YawRotationDegrees(settings.BoardYaw));
		}

		/// <summary>The part of the virtual screen that the world is rendered into (the board).</summary>
		public Rectangle WorldRect { get; }

		XrHand PointerHand => settings.LeftHanded ? XrHand.Left : XrHand.Right;
		XrHand OffHand => settings.LeftHanded ? XrHand.Right : XrHand.Left;

		float BoardWidth => Math.Max(settings.BoardWidth, 0.2f);
		float BoardDepth => BoardWidth * WorldRect.Height / WorldRect.Width;

		/// <summary>
		/// Updates the panel layout and feeds controller input into the game. Call once per render frame,
		/// after <see cref="IXrDevice.BeginFrame"/> and before the UI is drawn.
		/// </summary>
		public void Update(WorldRenderer worldRenderer, World inputWorld, List<XrQuad> quads)
		{
			var now = Game.RunTime;
			var dt = lastUpdateTime == 0 ? 0 : Math.Clamp((now - lastUpdateTime) / 1000f, 0, 0.1f);
			lastUpdateTime = now;

			if (device.ExitRequested)
			{
				Game.Exit();
				return;
			}

			if (!boardPlaced && device.HeadPoseValid)
				PlaceBoardInFrontOfHead();

			var pointer = device.GetHand(PointerHand);
			var offHand = device.GetHand(OffHand);

			UpdateBoardGrab(offHand);

			// Recenter the board with the off-hand secondary button (Y on Touch controllers)
			if (offHand.ButtonB && !lastOffHand.ButtonB && device.HeadPoseValid)
			{
				PlaceBoardInFrontOfHead();
				SaveBoardPose();
			}

			var tabletop = worldRenderer != null && worldRenderer.World.Type == WorldType.Regular
				&& !worldRenderer.World.IsLoadingGameSave && !IsMenuOpen();

			BuildPanels(tabletop);

			var inputHandler = new DefaultInputHandler(inputWorld);

			// The off-hand menu button opens the game menu (or closes dialogs) like Escape on the keyboard
			if (offHand.Menu && !lastOffHand.Menu)
				SendKey(inputHandler, Keycode.ESCAPE);

			if (tabletop && worldRenderer != null && dt > 0)
				UpdateViewportControls(worldRenderer, pointer, offHand, dt);

			UpdatePointer(inputHandler, worldRenderer, tabletop, pointer, quads);

			lastPointer = pointer;
			lastOffHand = offHand;
		}

		static bool IsMenuOpen()
		{
			if (Ui.CurrentWindow() != null)
				return true;

			var menuRoot = Ui.Root.GetOrNull("MENU_ROOT");
			return menuRoot != null && menuRoot.Children.Count > 0;
		}

		void BuildPanels(bool tabletop)
		{
			panels.Clear();
			if (tabletop)
			{
				// Lay the world flat on the table: rotate the quad's +Z normal to point up and the top of the image away from the player
				var boardOrientation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2);
				panels.Add(new XrQuad("board", WorldRect, boardPose.Multiply(new XrPose(Vector3.Zero, boardOrientation)),
					new Vector2(BoardWidth, BoardDepth)));

				if (sidebarRect.Width > 0)
				{
					var pixelsPerMeter = WorldRect.Width / BoardWidth / SidebarScale;
					var size = new Vector2(sidebarRect.Width / pixelsPerMeter, sidebarRect.Height / pixelsPerMeter);
					var tilt = Quaternion.CreateFromAxisAngle(Vector3.UnitX, SidebarTilt * MathF.PI / 180);
					var yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, SidebarYaw * MathF.PI / 180);
					var center = new Vector3(BoardWidth / 2 + SidebarGap + size.X / 2, size.Y / 2 * MathF.Cos(-SidebarTilt * MathF.PI / 180), -BoardDepth / 4);
					panels.Add(new XrQuad("sidebar", sidebarRect, boardPose.Multiply(new XrPose(center, yaw * tilt)), size));
				}
			}
			else
			{
				// Menus, the lobby and dialogs are shown on an upright screen behind the board
				var tilt = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MenuScreenTilt * MathF.PI / 180);
				var height = MenuScreenWidth * screenSize.Height / screenSize.Width;
				var center = new Vector3(0, MenuScreenHeight, -BoardDepth / 2 - MenuScreenDistance);
				panels.Add(new XrQuad("screen", new Rectangle(int2.Zero, screenSize), boardPose.Multiply(new XrPose(center, tilt)),
					new Vector2(MenuScreenWidth, height)));
			}
		}

		void UpdatePointer(DefaultInputHandler inputHandler, WorldRenderer worldRenderer, bool tabletop, XrHandState pointer, List<XrQuad> quads)
		{
			quads.Clear();
			quads.AddRange(panels);

			if (!pointer.IsActive)
			{
				ReleaseButtons(inputHandler);
				return;
			}

			var hit = Raycast(pointer.Aim, captureQuad);
			var modifiers = Game.GetModifierKeys();

			if (hit != null)
			{
				var pos = hit.Value.Pixel;
				if (pos != lastMousePos)
				{
					var delta = lastMousePos.X < 0 ? int2.Zero : pos - lastMousePos;
					inputHandler.OnMouseInput(new MouseInput(MouseInputEvent.Move, heldButtons, pos, delta, modifiers, 0));
					lastMousePos = pos;
				}

				UpdateButton(inputHandler, MouseButton.Left, pointer.Select, lastPointer.Select, hit.Value.Quad, modifiers);
				UpdateButton(inputHandler, MouseButton.Right, pointer.Order, lastPointer.Order, hit.Value.Quad, modifiers);

				// Squeezing the pointer grip over the board grabs the map and drags it like a sheet of paper
				if (tabletop && worldRenderer != null && hit.Value.Quad == "board")
				{
					if (pointer.Grab && !dragPanning)
					{
						dragPanning = true;
						dragPanLast = pos;
					}
					else if (pointer.Grab && dragPanning)
					{
						var d = dragPanLast - pos;
						if (d != int2.Zero)
							worldRenderer.Viewport.Scroll(d.ToVector2(), false);

						// The map moved under the pointer, so the same pixel now shows the dragged spot
						dragPanLast = pos;
					}
				}
			}
			else
			{
				// Releasing a button while pointing away from every panel must still release it in the game
				if (!pointer.Select && lastPointer.Select)
					UpdateButton(inputHandler, MouseButton.Left, false, true, null, modifiers);

				if (!pointer.Order && lastPointer.Order)
					UpdateButton(inputHandler, MouseButton.Right, false, true, null, modifiers);
			}

			if (!pointer.Grab)
				dragPanning = false;

			// Gentle feedback when the pointer moves onto a new interactive widget
			var hover = Ui.MouseOverWidget;
			if (hover != lastHoverWidget)
			{
				if (hover != null && hover.Parent?.Id != "WORLD_ROOT")
					device.Vibrate(PointerHand, 0.15f, 0.01f);

				lastHoverWidget = hover;
			}

			quads.Add(BuildBeam(pointer.Aim, hit?.Distance ?? BeamMissLength, pointer.Select || pointer.Order));
		}

		void UpdateButton(DefaultInputHandler inputHandler, MouseButton button, bool down, bool wasDown, string quad, Modifiers modifiers)
		{
			if (down == wasDown)
				return;

			var index = button == MouseButton.Left ? 0 : button == MouseButton.Right ? 1 : 2;
			if (down)
			{
				var now = Game.RunTime;
				var isRepeat = (now - lastDownTime[index]) / 1000f < DoubleClickSeconds
					&& (lastMousePos - lastDownPos[index]).LengthSquared <= DoubleClickDistance * DoubleClickDistance;

				tapCount[index] = isRepeat ? tapCount[index] + 1 : 1;
				lastDownTime[index] = now;
				lastDownPos[index] = lastMousePos;

				heldButtons |= button;
				captureQuad = quad;
				inputHandler.OnMouseInput(new MouseInput(MouseInputEvent.Down, button, lastMousePos, int2.Zero, modifiers, tapCount[index]));
				device.Vibrate(PointerHand, 0.35f, 0.02f);
			}
			else
			{
				heldButtons &= ~button;
				if (heldButtons == MouseButton.None)
					captureQuad = null;

				inputHandler.OnMouseInput(new MouseInput(MouseInputEvent.Up, button, lastMousePos, int2.Zero, modifiers, tapCount[index]));
			}
		}

		void ReleaseButtons(DefaultInputHandler inputHandler)
		{
			var modifiers = Game.GetModifierKeys();
			if (heldButtons.HasFlag(MouseButton.Left))
				UpdateButton(inputHandler, MouseButton.Left, false, true, null, modifiers);

			if (heldButtons.HasFlag(MouseButton.Right))
				UpdateButton(inputHandler, MouseButton.Right, false, true, null, modifiers);

			dragPanning = false;
		}

		static void SendKey(DefaultInputHandler inputHandler, Keycode key)
		{
			inputHandler.OnKeyInput(new KeyInput { Event = KeyInputEvent.Down, Key = key, MultiTapCount = 1 });
			inputHandler.OnKeyInput(new KeyInput { Event = KeyInputEvent.Up, Key = key, MultiTapCount = 1 });
		}

		void UpdateViewportControls(WorldRenderer worldRenderer, XrHandState pointer, XrHandState offHand, float dt)
		{
			var viewport = worldRenderer.Viewport;

			// Off-hand thumbstick pans the map (stick up scrolls towards the far edge of the board)
			var pan = ApplyDeadzone(offHand.Stick);
			if (pan != Vector2.Zero)
			{
				var speed = 1200f * settings.StickPanSpeed * dt;
				viewport.Scroll(new Vector2(pan.X, -pan.Y) * speed, false);
			}

			// Pointer thumbstick zooms in and out
			var zoom = ApplyDeadzone(pointer.Stick).Y;
			if (zoom != 0)
				viewport.AdjustZoom(zoom * 1.5f * dt);
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
					grabOffset = Vector3.Transform(boardPose.Position - offHand.Grip.Position, Quaternion.Inverse(YawRotation(yaw)));
					grabYawOffset = HeadingOf(boardPose.Forward, 0) - yaw;
					device.Vibrate(OffHand, 0.4f, 0.03f);
				}

				var boardYaw = yaw + grabYawOffset;
				var position = offHand.Grip.Position + Vector3.Transform(grabOffset, YawRotation(yaw));
				boardPose = new XrPose(position, YawRotation(boardYaw));
				boardPlaced = true;
			}
			else if (grabbing)
			{
				grabbing = false;
				SaveBoardPose();
			}
		}

		void PlaceBoardInFrontOfHead()
		{
			var head = device.HeadPose;
			var yaw = HeadingOf(head.Forward, 0);
			var forward = Vector3.Transform(-Vector3.UnitZ, YawRotation(yaw));

			// A comfortable seated tabletop: the near edge a short reach in front of the player, below eye level
			var position = head.Position + forward * (0.25f + BoardDepth / 2) - new Vector3(0, 0.45f, 0);
			boardPose = new XrPose(position, YawRotation(yaw));
			boardPlaced = true;
		}

		void SaveBoardPose()
		{
			settings.BoardPlaced = true;
			settings.BoardX = boardPose.Position.X;
			settings.BoardY = boardPose.Position.Y;
			settings.BoardZ = boardPose.Position.Z;
			settings.BoardYaw = HeadingOf(boardPose.Forward, 0) * 180 / MathF.PI;
			settings.Save();
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
			return new XrQuad("beam", Rectangle.Empty, new XrPose(center, orientation), new Vector2(BeamWidth, length),
				pressed ? BeamPressedColor : BeamColor);
		}
	}
}
