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
using System.Linq;
using System.Numerics;
using NUnit.Framework;
using OpenRA.Graphics;
using OpenRA.Primitives;
using OpenRA.Test.Fakes;

namespace OpenRA.Test
{
	[TestFixture]
	[NonParallelizable]
	sealed class XrTabletopTest
	{
		FakeXrDevice device;
		XrSettings settings;
		XrTabletop tabletop;
		RecordingInputHandler input;
		FakeViewportControls viewport;
		readonly List<XrQuad> quads = [];
		readonly List<Rectangle> uiPanels = [];
		readonly Dictionary<string, Hotkey> hotkeys = [];
		long time;
		int saves;
		bool exited;

		XrSceneState Playing => new(true, WorldType.Regular, false, false, viewport);
		static XrSceneState InMenu => new(false, WorldType.Shellmap, true, false, null);
		XrSceneState InEditor => new(true, WorldType.Editor, false, false, viewport);

		ref XrHandState Pointer => ref device.Hand(XrHand.Right);
		ref XrHandState OffHand => ref device.Hand(XrHand.Left);

		[SetUp]
		public void SetUp()
		{
			device = new FakeXrDevice(new Size(2048, 1280));
			settings = new XrSettings();
			input = new RecordingInputHandler();
			viewport = new FakeViewportControls();
			quads.Clear();
			uiPanels.Clear();
			hotkeys.Clear();
			time = 1000;
			saves = 0;
			exited = false;
			Game.XrModifiers = Modifiers.None;
			CreateTabletop();
		}

		[TearDown]
		public void TearDown()
		{
			Game.XrModifiers = Modifiers.None;
		}

		void CreateTabletop()
		{
			tabletop = new XrTabletop(device, settings, () => time, () => saves++,
				name => hotkeys.TryGetValue(name, out var hotkey) ? hotkey : Hotkey.Invalid, () => exited = true);
		}

		void Frame(XrSceneState state, int milliseconds = 11)
		{
			time += milliseconds;
			tabletop.Update(state, input, quads, uiPanels);
		}

		XrQuad Panel(string name) => tabletop.Panels.Single(p => p.Name == name);

		static Vector3 Normal(XrQuad quad) => Vector3.Transform(Vector3.UnitZ, quad.Pose.Orientation);

		/// <summary>Returns an aim pose 30cm in front of a panel that points at the given pixel of the virtual screen.</summary>
		XrPose AimAt(string panel, int2 pixel)
		{
			var quad = Panel(panel);
			var u = (pixel.X - quad.Source.X + 0.5f) / quad.Source.Width;
			var v = (pixel.Y - quad.Source.Y + 0.5f) / quad.Source.Height;
			var local = new Vector3((u - 0.5f) * quad.Size.X, (0.5f - v) * quad.Size.Y, 0);
			var target = quad.Pose.TransformPoint(local);
			return new XrPose(target + Normal(quad) * 0.3f, quad.Pose.Orientation);
		}

		void Point(string panel, int2 pixel)
		{
			Pointer.IsActive = true;
			Pointer.Aim = AimAt(panel, pixel);
		}

		List<MouseInput> Events(MouseInputEvent type) => input.Mouse.Where(m => m.Event == type).ToList();

		[Test]
		public void BoardIsPlacedOnATableInFrontOfThePlayer()
		{
			Frame(Playing);
			var board = Panel(XrTabletop.BoardQuad);

			// Below eye level, straight ahead, lying flat with the top of the image away from the player
			Assert.That(tabletop.BoardPose.Position.Y, Is.EqualTo(device.HeadPose.Position.Y - 0.45f).Within(1e-4f));
			Assert.That(tabletop.BoardPose.Position.X, Is.EqualTo(0).Within(1e-4f));
			Assert.That(tabletop.BoardPose.Position.Z, Is.LessThan(-0.25f));
			Assert.That(Vector3.Dot(Normal(board), Vector3.UnitY), Is.GreaterThan(0.999f));
			Assert.That(Vector3.Dot(Vector3.Transform(Vector3.UnitY, board.Pose.Orientation), -Vector3.UnitZ), Is.GreaterThan(0.999f));
			Assert.That(settings.BoardPlaced, Is.True);
		}

		[Test]
		public void SavedBoardPoseIsRestored()
		{
			settings.BoardPlaced = true;
			settings.BoardX = 0.5f;
			settings.BoardY = 0.7f;
			settings.BoardZ = -0.8f;
			settings.BoardYaw = 90;
			CreateTabletop();

			Frame(Playing);
			Assert.That(Vector3.Distance(tabletop.BoardPose.Position, new Vector3(0.5f, 0.7f, -0.8f)), Is.LessThan(1e-4f));

			// Yawed 90 degrees to the left, so the far edge points along -X
			Assert.That(Vector3.Dot(tabletop.BoardPose.Forward, -Vector3.UnitX), Is.GreaterThan(0.999f));
		}

		[Test]
		public void RecenterButtonMovesTheBoardInFrontOfTheHead()
		{
			Frame(Playing);
			device.HeadPose = new XrPose(new Vector3(2, 1.2f, 1), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2));
			OffHand.ButtonB = true;
			Frame(Playing);

			Assert.That(tabletop.BoardPose.Position.X, Is.LessThan(2 - 0.25f));
			Assert.That(tabletop.BoardPose.Position.Z, Is.EqualTo(1).Within(1e-3f));
			Assert.That(saves, Is.EqualTo(1));
		}

		[Test]
		public void GrabbingMovesTheBoardAndSavesOnRelease()
		{
			Frame(Playing);
			var start = tabletop.BoardPose.Position;

			OffHand.IsActive = true;
			OffHand.Grab = true;
			OffHand.Grip = new XrPose(new Vector3(-0.3f, 0.9f, -0.3f), Quaternion.Identity);
			Frame(Playing);

			OffHand.Grip = new XrPose(new Vector3(-0.2f, 0.85f, -0.3f), Quaternion.Identity);
			Frame(Playing);
			Assert.That(Vector3.Distance(tabletop.BoardPose.Position, start + new Vector3(0.1f, -0.05f, 0)), Is.LessThan(1e-4f));
			Assert.That(saves, Is.Zero);

			OffHand.Grab = false;
			Frame(Playing);
			Assert.That(saves, Is.EqualTo(1));
			Assert.That(settings.BoardX, Is.EqualTo(tabletop.BoardPose.Position.X).Within(1e-5f));
		}

		[Test]
		public void GrabbingRotatesTheBoardWithTheHand()
		{
			Frame(Playing);
			OffHand.IsActive = true;
			OffHand.Grab = true;
			OffHand.Grip = new XrPose(new Vector3(0, 0.9f, -0.3f), Quaternion.Identity);
			Frame(Playing);

			OffHand.Grip = new XrPose(new Vector3(0, 0.9f, -0.3f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4));
			Frame(Playing);
			Assert.That(settings.BoardYaw, Is.EqualTo(45).Within(0.01f));

			// The board stays level
			Assert.That(Vector3.Dot(tabletop.BoardPose.Up, Vector3.UnitY), Is.GreaterThan(0.999f));
		}

		[Test]
		public void PlayingShowsTheBoardAndSidebar()
		{
			Frame(Playing);
			Assert.That(tabletop.Panels.Select(p => p.Name), Is.EqualTo(new[] { XrTabletop.BoardQuad, XrTabletop.SidebarQuad }));

			var board = Panel(XrTabletop.BoardQuad);
			var sidebar = Panel(XrTabletop.SidebarQuad);
			Assert.That(board.Source, Is.EqualTo(new Rectangle(0, 0, 1792, 1024)));
			Assert.That(sidebar.Source, Is.EqualTo(new Rectangle(1792, 0, 256, 1024)));
			Assert.That(board.Size.X, Is.EqualTo(settings.BoardWidth).Within(1e-5f));
			Assert.That(board.Size.Y / board.Size.X, Is.EqualTo(1024f / 1792).Within(1e-4f));

			// The sidebar stands to the right of the board, facing the player
			Assert.That(sidebar.Pose.Position.X, Is.GreaterThan(board.Pose.Position.X + board.Size.X / 2));
			Assert.That(sidebar.Pose.Position.Y, Is.GreaterThan(board.Pose.Position.Y));
			Assert.That(Normal(sidebar).Z, Is.GreaterThan(0.5f));

			Assert.That(uiPanels, Is.EqualTo(new[] { tabletop.BoardRect, tabletop.SidebarRect }));
		}

		[Test]
		public void MenusAreShownOnAnUprightScreen()
		{
			Frame(InMenu);
			Assert.That(tabletop.Panels.Select(p => p.Name), Is.EqualTo(new[] { XrTabletop.ScreenQuad }));

			var screen = Panel(XrTabletop.ScreenQuad);
			Assert.That(screen.Source, Is.EqualTo(new Rectangle(0, 0, 2048, 1024)));
			Assert.That(Normal(screen).Z, Is.GreaterThan(0.9f));
			Assert.That(screen.Pose.Position.Y, Is.GreaterThan(tabletop.BoardPose.Position.Y));
			Assert.That(uiPanels, Is.EqualTo(new[] { tabletop.MainRect }));
		}

		[Test]
		public void OpeningAMenuDuringAGameSwitchesToTheScreen()
		{
			Frame(Playing);
			Frame(Playing with { MenuOpen = true });
			Assert.That(tabletop.Panels.Select(p => p.Name), Is.EqualTo(new[] { XrTabletop.ScreenQuad }));
		}

		[Test]
		public void WorldRectDependsOnTheWorldType()
		{
			Assert.That(tabletop.WorldRectFor(WorldType.Regular), Is.EqualTo(new Rectangle(0, 0, 1792, 1024)));
			Assert.That(tabletop.WorldRectFor(WorldType.Editor), Is.EqualTo(new Rectangle(0, 0, 2048, 1024)));
			Assert.That(tabletop.WorldRectFor(WorldType.Shellmap), Is.EqualTo(new Rectangle(0, 0, 2048, 1024)));
		}

		[Test]
		public void SidebarWidthIsConfigurable()
		{
			settings.SidebarWidth = 300;
			CreateTabletop();
			Assert.That(tabletop.BoardRect, Is.EqualTo(new Rectangle(0, 0, 1748, 1024)));
			Assert.That(tabletop.SidebarRect, Is.EqualTo(new Rectangle(1748, 0, 300, 1024)));
		}

		[Test]
		public void BeamIsSubmittedButIsNotAPanel()
		{
			Frame(Playing);
			Point(XrTabletop.BoardQuad, new int2(800, 500));
			Frame(Playing);

			var beam = quads[^1];
			Assert.That(beam.Name, Is.EqualTo(XrTabletop.BeamQuad));
			Assert.That(beam.Fill, Is.Not.Null);
			Assert.That(beam.Size.Y, Is.EqualTo(0.3f).Within(1e-3f), "The beam ends where it hits the board");
			Assert.That(tabletop.Panels.Any(p => p.Name == XrTabletop.BeamQuad), Is.False);
		}

		[TestCase(XrTabletop.BoardQuad, 500, 300)]
		[TestCase(XrTabletop.BoardQuad, 0, 0)]
		[TestCase(XrTabletop.BoardQuad, 1791, 1023)]
		[TestCase(XrTabletop.SidebarQuad, 1900, 400)]
		public void PointingMovesTheMouseToThatPixel(string panel, int x, int y)
		{
			Frame(Playing);
			Point(panel, new int2(x, y));
			Frame(Playing);

			var move = Events(MouseInputEvent.Move).Single();
			Assert.That(move.Location, Is.EqualTo(new int2(x, y)));
		}

		[Test]
		public void PointingAtTheMenuScreenMovesTheMouse()
		{
			Frame(InMenu);
			Point(XrTabletop.ScreenQuad, new int2(1024, 512));
			Frame(InMenu);
			Assert.That(Events(MouseInputEvent.Move).Single().Location, Is.EqualTo(new int2(1024, 512)));
		}

		[Test]
		public void MouseOnlyMovesWhenThePointerMoves()
		{
			Frame(Playing);
			Point(XrTabletop.BoardQuad, new int2(400, 400));
			Frame(Playing);
			Frame(Playing);
			Assert.That(Events(MouseInputEvent.Move), Has.Count.EqualTo(1));

			Point(XrTabletop.BoardQuad, new int2(410, 400));
			Frame(Playing);
			var moves = Events(MouseInputEvent.Move);
			Assert.That(moves, Has.Count.EqualTo(2));
			Assert.That(moves[1].Delta, Is.EqualTo(new int2(10, 0)));
		}

		[Test]
		public void TriggerIsALeftClick()
		{
			Frame(Playing);
			Point(XrTabletop.BoardQuad, new int2(300, 200));
			Pointer.Select = true;
			Frame(Playing);
			Pointer.Select = false;
			Frame(Playing);

			var down = Events(MouseInputEvent.Down).Single();
			var up = Events(MouseInputEvent.Up).Single();
			Assert.That(down.Button, Is.EqualTo(MouseButton.Left));
			Assert.That(down.Location, Is.EqualTo(new int2(300, 200)));
			Assert.That(down.MultiTapCount, Is.EqualTo(1));
			Assert.That(up.Button, Is.EqualTo(MouseButton.Left));
		}

		[Test]
		public void AButtonIsARightClick()
		{
			Frame(Playing);
			Point(XrTabletop.BoardQuad, new int2(300, 200));
			Pointer.Order = true;
			Frame(Playing);
			Pointer.Order = false;
			Frame(Playing);

			Assert.That(Events(MouseInputEvent.Down).Single().Button, Is.EqualTo(MouseButton.Right));
			Assert.That(Events(MouseInputEvent.Up).Single().Button, Is.EqualTo(MouseButton.Right));
		}

		[Test]
		public void QuickTapsAreDoubleClicks()
		{
			Frame(Playing);
			Point(XrTabletop.BoardQuad, new int2(300, 200));
			foreach (var pressed in new[] { true, false, true, false })
			{
				Pointer.Select = pressed;
				Frame(Playing, 50);
			}

			Assert.That(Events(MouseInputEvent.Down).Select(d => d.MultiTapCount), Is.EqualTo(new[] { 1, 2 }));

			// A slow second tap is a new single click
			Frame(Playing, 1000);
			Pointer.Select = true;
			Frame(Playing);
			Assert.That(Events(MouseInputEvent.Down).Last().MultiTapCount, Is.EqualTo(1));
		}

		[Test]
		public void HeldButtonsCaptureThePanelForDragSelection()
		{
			Frame(Playing);
			Point(XrTabletop.BoardQuad, new int2(1700, 500));
			Pointer.Select = true;
			Frame(Playing);

			// Sweep the pointer past the right edge of the board and over the sidebar
			Pointer.Aim = AimAt(XrTabletop.SidebarQuad, new int2(1950, 500));
			Frame(Playing);

			var drag = Events(MouseInputEvent.Move).Last();
			Assert.That(drag.Button, Is.EqualTo(MouseButton.Left));
			Assert.That(drag.Location.X, Is.LessThan(1792), "The drag stays on the board");

			Pointer.Select = false;
			Frame(Playing);
			Pointer.Aim = AimAt(XrTabletop.SidebarQuad, new int2(1950, 500));
			Frame(Playing);
			Assert.That(Events(MouseInputEvent.Move).Last().Location, Is.EqualTo(new int2(1950, 500)), "Released: the sidebar is reachable again");
		}

		[Test]
		public void LosingTrackingReleasesHeldButtons()
		{
			Frame(Playing);
			Point(XrTabletop.BoardQuad, new int2(300, 200));
			Pointer.Select = true;
			Frame(Playing);

			Pointer.IsActive = false;
			Frame(Playing);
			Assert.That(Events(MouseInputEvent.Up).Single().Button, Is.EqualTo(MouseButton.Left));
		}

		[Test]
		public void ReleasingWhilePointingAwayStillReleases()
		{
			Frame(Playing);
			Point(XrTabletop.BoardQuad, new int2(300, 200));
			Pointer.Order = true;
			Frame(Playing);

			// The right button does not capture the board beyond its edges forever: point at the ceiling
			Pointer.Aim = new XrPose(new Vector3(0, 1, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2));
			Pointer.Order = false;
			Frame(Playing);
			Assert.That(Events(MouseInputEvent.Up).Single().Button, Is.EqualTo(MouseButton.Right));
		}

		[Test]
		public void MenuButtonSendsEscape()
		{
			Frame(Playing);
			OffHand.Menu = true;
			Frame(Playing);
			Frame(Playing);

			Assert.That(input.Keys.Select(k => (k.Event, k.Key)),
				Is.EqualTo(new[] { (KeyInputEvent.Down, Keycode.ESCAPE), (KeyInputEvent.Up, Keycode.ESCAPE) }));
		}

		[Test]
		public void ExitRequestExitsTheGame()
		{
			device.ExitRequested = true;
			Frame(Playing);
			Assert.That(exited, Is.True);
		}

		[TestCase(nameof(XrHandState.Select), false, Modifiers.Shift)]
		[TestCase(nameof(XrHandState.ButtonA), false, Modifiers.Ctrl)]
		[TestCase(nameof(XrHandState.ButtonB), true, Modifiers.Alt)]
		public void ControllerButtonsActAsModifiers(string button, bool onPointer, Modifiers expected)
		{
			Frame(Playing);
			Point(XrTabletop.BoardQuad, new int2(300, 200));
			OffHand.IsActive = true;

			ref var hand = ref onPointer ? ref Pointer : ref OffHand;
			switch (button)
			{
				case nameof(XrHandState.Select): hand.Select = true; break;
				case nameof(XrHandState.ButtonA): hand.ButtonA = true; break;
				case nameof(XrHandState.ButtonB): hand.ButtonB = true; break;
			}

			Frame(Playing);
			Assert.That(Game.XrModifiers, Is.EqualTo(expected));
			Assert.That(Game.GetModifierKeys().HasModifier(expected), Is.True);

			// Clicks carry the modifier, so Shift-click adds to the selection and so on
			Pointer.Select = true;
			Frame(Playing);
			Assert.That(Events(MouseInputEvent.Down).Single().Modifiers, Is.EqualTo(expected));
		}

		[Test]
		public void ModifiersAreReleasedWithTheButton()
		{
			Frame(Playing);
			OffHand.IsActive = true;
			OffHand.Select = true;
			Frame(Playing);
			OffHand.Select = false;
			Frame(Playing);
			Assert.That(Game.XrModifiers, Is.EqualTo(Modifiers.None));
		}

		[Test]
		public void LeftHandedSwapsTheHands()
		{
			settings.LeftHanded = true;
			CreateTabletop();
			Frame(Playing);

			device.Hand(XrHand.Left).IsActive = true;
			device.Hand(XrHand.Left).Aim = AimAt(XrTabletop.BoardQuad, new int2(640, 480));
			device.Hand(XrHand.Right).IsActive = true;
			device.Hand(XrHand.Right).Select = true;
			Frame(Playing);

			Assert.That(Events(MouseInputEvent.Move).Single().Location, Is.EqualTo(new int2(640, 480)));
			Assert.That(Game.XrModifiers, Is.EqualTo(Modifiers.Shift));
		}

		[Test]
		public void PointerStickZoomsTheBoard()
		{
			Frame(Playing);
			Point(XrTabletop.BoardQuad, new int2(300, 200));
			Pointer.Stick = new Vector2(0, 1);
			Frame(Playing, 100);

			Assert.That(viewport.Zoomed, Is.GreaterThan(0));
			Assert.That(Events(MouseInputEvent.Scroll), Is.Empty);
		}

		[Test]
		public void PointerStickIsAMouseWheelOnTheSidebar()
		{
			Frame(Playing);
			Point(XrTabletop.SidebarQuad, new int2(1900, 700));
			Pointer.Stick = new Vector2(0, 1);
			for (var i = 0; i < 5; i++)
				Frame(Playing, 100);

			var scrolls = Events(MouseInputEvent.Scroll);
			Assert.That(scrolls, Is.Not.Empty);
			Assert.That(scrolls.All(s => s.Delta == new int2(0, 1) && s.Location == new int2(1900, 700)), Is.True);
			Assert.That(viewport.Zoomed, Is.Zero);

			input.Clear();
			Pointer.Stick = new Vector2(0, -1);
			for (var i = 0; i < 5; i++)
				Frame(Playing, 100);

			Assert.That(Events(MouseInputEvent.Scroll).All(s => s.Delta == new int2(0, -1)), Is.True);
		}

		[Test]
		public void PointerStickScrollsMenuLists()
		{
			Frame(InMenu);
			Point(XrTabletop.ScreenQuad, new int2(1000, 500));
			Pointer.Stick = new Vector2(0, -1);
			for (var i = 0; i < 5; i++)
				Frame(InMenu, 100);

			Assert.That(Events(MouseInputEvent.Scroll), Is.Not.Empty);
		}

		[Test]
		public void SmallStickMovementsAreIgnored()
		{
			Frame(Playing);
			Point(XrTabletop.SidebarQuad, new int2(1900, 700));
			Pointer.Stick = new Vector2(0, 0.1f);
			OffHand.Stick = new Vector2(0.1f, 0.1f);
			for (var i = 0; i < 5; i++)
				Frame(Playing, 100);

			Assert.That(Events(MouseInputEvent.Scroll), Is.Empty);
			Assert.That(viewport.Scrolled, Is.EqualTo(Vector2.Zero));
		}

		[Test]
		public void OffHandStickScrollsTheMap()
		{
			Frame(Playing);
			OffHand.Stick = new Vector2(1, 1);
			Frame(Playing, 100);

			// Stick right scrolls right, stick forward scrolls towards the far (top) edge
			Assert.That(viewport.Scrolled.X, Is.GreaterThan(0));
			Assert.That(viewport.Scrolled.Y, Is.LessThan(0));
		}

		[Test]
		public void StickPanSpeedIsConfigurable()
		{
			Frame(Playing);
			OffHand.Stick = new Vector2(1, 0);
			Frame(Playing, 100);
			var normal = viewport.Scrolled.X;

			settings.StickPanSpeed = 2;
			viewport.Scrolled = Vector2.Zero;
			Frame(Playing, 100);
			Assert.That(viewport.Scrolled.X, Is.EqualTo(2 * normal).Within(1e-3f));
		}

		[Test]
		public void MapDoesNotScrollInMenus()
		{
			Frame(Playing with { MenuOpen = true });
			OffHand.Stick = new Vector2(1, 0);
			Frame(Playing with { MenuOpen = true }, 100);
			Assert.That(viewport.Scrolled, Is.EqualTo(Vector2.Zero));
		}

		[Test]
		public void EditorCanBeScrolledOnTheScreen()
		{
			Frame(InEditor);
			Assert.That(tabletop.Panels.Single().Name, Is.EqualTo(XrTabletop.ScreenQuad));

			OffHand.Stick = new Vector2(-1, 0);
			Frame(InEditor, 100);
			Assert.That(viewport.Scrolled.X, Is.LessThan(0));
		}

		[Test]
		public void GripDragsTheMap()
		{
			Frame(Playing);
			Point(XrTabletop.BoardQuad, new int2(500, 500));
			Pointer.Grab = true;
			Frame(Playing);

			Pointer.Aim = AimAt(XrTabletop.BoardQuad, new int2(540, 470));
			Frame(Playing);

			// Pulling the map right and up moves the view left and down
			Assert.That(viewport.Scrolled, Is.EqualTo(new Vector2(-40, 30)));
		}

		[Test]
		public void ClicksVibrate()
		{
			Frame(Playing);
			Point(XrTabletop.BoardQuad, new int2(300, 200));
			Pointer.Select = true;
			Frame(Playing);
			Assert.That(device.Vibrations.Select(v => v.Hand), Does.Contain(XrHand.Right));
		}

		[Test]
		public void HapticsCanBeDisabled()
		{
			settings.Haptics = false;
			Frame(Playing);
			Point(XrTabletop.BoardQuad, new int2(300, 200));
			Pointer.Select = true;
			Frame(Playing);
			Assert.That(device.Vibrations, Is.Empty);
		}

		[Test]
		public void StickClickTogglesTheHotkeyMenu()
		{
			Frame(Playing);
			OffHand.IsActive = true;
			OffHand.Grip = new XrPose(new Vector3(-0.2f, 0.9f, -0.3f), Quaternion.Identity);
			OffHand.StickClick = true;
			Frame(Playing);
			Assert.That(tabletop.Panels.Any(p => p.Name == XrTabletop.HotkeysQuad), Is.True);
			Assert.That(Panel(XrTabletop.HotkeysQuad).Source, Is.EqualTo(tabletop.HandPanel.HotkeyRect));

			// The hotkey menu is rendered below the game's layout, so it never shows up on the board
			Assert.That(tabletop.HandPanel.HotkeyRect.Top, Is.GreaterThanOrEqualTo(tabletop.BoardRect.Bottom));

			OffHand.StickClick = false;
			Frame(Playing);
			OffHand.StickClick = true;
			Frame(Playing);
			Assert.That(tabletop.Panels.Any(p => p.Name == XrTabletop.HotkeysQuad), Is.False);
		}

		[Test]
		public void HotkeyMenuSendsTheBoundHotkeyInsteadOfClicking()
		{
			hotkeys["ControlGroupSelect01"] = new Hotkey(Keycode.NUMBER_1, Modifiers.None);
			Frame(Playing);
			OffHand.IsActive = true;
			OffHand.Grip = new XrPose(new Vector3(-0.2f, 0.9f, -0.3f), Quaternion.Identity);
			OffHand.StickClick = true;
			Frame(Playing);

			var button = tabletop.HandPanel.HotkeyButtons.First(b => b.Label == "1");
			Point(XrTabletop.HotkeysQuad, button.Bounds.Location + button.Bounds.Size.ToInt2() / 2);
			Pointer.Select = true;
			Frame(Playing);

			Assert.That(input.Keys.Select(k => (k.Event, k.Key)),
				Is.EqualTo(new[] { (KeyInputEvent.Down, Keycode.NUMBER_1), (KeyInputEvent.Up, Keycode.NUMBER_1) }));
			Assert.That(input.Mouse, Is.Empty, "The game must not receive a click");
		}

		[Test]
		public void KeyboardAppearsForTextFields()
		{
			var typing = InMenu with { TextInputFocused = true };
			Frame(typing);
			Assert.That(tabletop.HandPanel.KeyboardOpen, Is.True);
			Assert.That(Panel(XrTabletop.KeyboardQuad).Source, Is.EqualTo(tabletop.HandPanel.KeyboardRect));

			var key = tabletop.HandPanel.KeyboardButtons.First(b => b.Label == "q");
			Point(XrTabletop.KeyboardQuad, key.Bounds.Location + key.Bounds.Size.ToInt2() / 2);
			Pointer.Select = true;
			Frame(typing);
			Assert.That(input.Text, Is.EqualTo(new[] { "q" }));

			Frame(InMenu);
			Assert.That(tabletop.HandPanel.KeyboardOpen, Is.False);
			Assert.That(tabletop.Panels.Any(p => p.Name == XrTabletop.KeyboardQuad), Is.False);
		}

		[Test]
		public void ClosedKeyboardStaysClosedUntilTheNextTextField()
		{
			var typing = InMenu with { TextInputFocused = true };
			Frame(typing);

			var close = tabletop.HandPanel.KeyboardButtons.First(b => b.Label == "Close");
			Point(XrTabletop.KeyboardQuad, close.Bounds.Location + close.Bounds.Size.ToInt2() / 2);
			Pointer.Select = true;
			Frame(typing);
			Pointer.Select = false;
			Frame(typing);
			Assert.That(tabletop.HandPanel.KeyboardOpen, Is.False);

			Frame(InMenu);
			Frame(typing);
			Assert.That(tabletop.HandPanel.KeyboardOpen, Is.True);
		}

		[Test]
		public void EditorShowsEditorShortcuts()
		{
			Frame(InEditor);
			Assert.That(tabletop.HandPanel.EditorMode, Is.True);
			Frame(Playing);
			Assert.That(tabletop.HandPanel.EditorMode, Is.False);
		}

		[Test]
		public void RayThroughTheBoardCenterHitsTheMiddleOfTheImage()
		{
			Frame(Playing);
			var board = Panel(XrTabletop.BoardQuad);
			var down = new XrPose(board.Pose.Position + new Vector3(0, 0.5f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2));

			Assert.That(XrTabletop.IntersectQuad(down, board, false, out var uv, out var distance), Is.True);
			Assert.That(uv.X, Is.EqualTo(0.5f).Within(1e-4f));
			Assert.That(uv.Y, Is.EqualTo(0.5f).Within(1e-4f));
			Assert.That(distance, Is.EqualTo(0.5f).Within(1e-4f));
		}

		[Test]
		public void RayOutsideTheBoardMissesUnlessCaptured()
		{
			Frame(Playing);
			var board = Panel(XrTabletop.BoardQuad);
			var down = new XrPose(board.Pose.Position + new Vector3(board.Size.X, 0.5f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2));

			Assert.That(XrTabletop.IntersectQuad(down, board, false, out _, out _), Is.False);
			Assert.That(XrTabletop.IntersectQuad(down, board, true, out var uv, out _), Is.True);
			Assert.That(uv.X, Is.GreaterThan(0.99f));
		}

		[Test]
		public void RayPointingAwayMisses()
		{
			Frame(Playing);
			var board = Panel(XrTabletop.BoardQuad);
			var up = new XrPose(board.Pose.Position + new Vector3(0, 0.5f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2));
			Assert.That(XrTabletop.IntersectQuad(up, board, false, out _, out _), Is.False);
		}

		[Test]
		public void PoseRoundTrip()
		{
			var pose = new XrPose(new Vector3(1, 2, 3), Quaternion.CreateFromYawPitchRoll(0.3f, -0.2f, 0.1f));
			var point = new Vector3(-0.4f, 0.25f, 1.5f);
			Assert.That(Vector3.Distance(point, pose.InverseTransformPoint(pose.TransformPoint(point))), Is.LessThan(1e-5f));
		}
	}
}
