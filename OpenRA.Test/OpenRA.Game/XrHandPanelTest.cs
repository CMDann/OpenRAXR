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

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using OpenRA.Graphics;
using OpenRA.Primitives;
using OpenRA.Test.Fakes;

namespace OpenRA.Test
{
	[TestFixture]
	sealed class XrHandPanelTest
	{
		static readonly Rectangle Band = new(0, 1024, 1792, 256);

		readonly Dictionary<string, Hotkey> hotkeys = [];
		XrHandPanel panel;
		RecordingInputHandler input;

		[SetUp]
		public void SetUp()
		{
			hotkeys.Clear();
			panel = new XrHandPanel(Band, name => hotkeys.TryGetValue(name, out var hotkey) ? hotkey : Hotkey.Invalid)
			{
				HotkeysOpen = true,
				KeyboardOpen = true,
			};

			input = new RecordingInputHandler();
		}

		static int2 CenterOf(XrPanelButton button) => button.Bounds.Location + button.Bounds.Size.ToInt2() / 2;

		void Press(string label, Modifiers modifiers = Modifiers.None) => Click(panel.HotkeyButtons, label, modifiers);

		void Type(string label) => Click(panel.KeyboardButtons, label, Modifiers.None);

		void Click(IReadOnlyList<XrPanelButton> buttons, string label, Modifiers modifiers)
		{
			var button = buttons.First(b => b.Label == label);
			Assert.That(panel.Click(CenterOf(button), input, modifiers), Is.True, $"Clicking {label}");
		}

		IEnumerable<(KeyInputEvent Event, Keycode Key, Modifiers Modifiers)> SentKeys => input.Keys.Select(k => (k.Event, k.Key, k.Modifiers));

		[Test]
		public void PanelsSplitTheReservedBand()
		{
			Assert.That(panel.HotkeyRect.Top, Is.EqualTo(Band.Top));
			Assert.That(panel.KeyboardRect.Top, Is.EqualTo(Band.Top));
			Assert.That(panel.HotkeyRect.Right, Is.EqualTo(panel.KeyboardRect.Left));
			Assert.That(panel.KeyboardRect.Right, Is.EqualTo(Band.Right));
		}

		[Test]
		public void ButtonsFitInsideTheirPanelWithoutOverlapping()
		{
			static void Check(XrHandPanel p)
			{
				foreach (var (area, buttons) in new[] { (p.HotkeyRect, p.HotkeyButtons), (p.KeyboardRect, p.KeyboardButtons) })
				{
					foreach (var b in buttons)
					{
						Assert.That(area.Contains(b.Bounds), Is.True, $"{b.Label} is outside its panel");
						Assert.That(b.Bounds.Width, Is.GreaterThan(20), $"{b.Label} is too small to hit");
						foreach (var other in buttons)
							if (other != b)
								Assert.That(b.Bounds.IntersectsWith(other.Bounds), Is.False, $"{b.Label} overlaps {other.Label}");
					}
				}
			}

			Check(panel);
			panel.EditorMode = true;
			Check(panel);
		}

		[Test]
		public void HotkeyMenuHasControlGroupsAndGameHotkeys()
		{
			var labels = panel.HotkeyButtons.Select(b => b.Label).ToList();
			Assert.That(labels.Take(10), Is.EqualTo(new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" }));
			Assert.That(labels, Is.SupersetOf((string[])["All", "Type", "Event", "Base", "Harv", "Pause", "Save"]));
		}

		[Test]
		public void EditorModeShowsEditorShortcuts()
		{
			panel.EditorMode = true;
			Assert.That(panel.HotkeyButtons.Select(b => b.Label), Is.SupersetOf((string[])["Undo", "Redo", "Copy", "Paste", "Delete"]));
		}

		[TestCase(1, Modifiers.None, "ControlGroupSelect01")]
		[TestCase(1, Modifiers.Ctrl, "ControlGroupCreate01")]
		[TestCase(2, Modifiers.Shift, "ControlGroupCombineWith02")]
		[TestCase(3, Modifiers.Ctrl | Modifiers.Shift, "ControlGroupAddTo03")]
		[TestCase(10, Modifiers.Alt, "ControlGroupJumpTo10")]
		public void ControlGroupActionFollowsTheModifiers(int group, Modifiers modifiers, string expected)
		{
			Assert.That(XrHandPanel.ControlGroupHotkey(group, modifiers), Is.EqualTo(expected));
		}

		[Test]
		public void ControlGroupButtonsSendTheBoundKey()
		{
			hotkeys["ControlGroupSelect05"] = new Hotkey(Keycode.NUMBER_5, Modifiers.None);
			hotkeys["ControlGroupCreate05"] = new Hotkey(Keycode.NUMBER_5, Modifiers.Ctrl);

			Press("5");
			Press("5", Modifiers.Ctrl);
			Assert.That(SentKeys, Is.EqualTo(new[]
			{
				(KeyInputEvent.Down, Keycode.NUMBER_5, Modifiers.None), (KeyInputEvent.Up, Keycode.NUMBER_5, Modifiers.None),
				(KeyInputEvent.Down, Keycode.NUMBER_5, Modifiers.Ctrl), (KeyInputEvent.Up, Keycode.NUMBER_5, Modifiers.Ctrl),
			}));
		}

		[Test]
		public void RebindingIsRespected()
		{
			hotkeys["SelectAllUnits"] = new Hotkey(Keycode.F5, Modifiers.Alt);
			Press("All");
			Assert.That(SentKeys.First(), Is.EqualTo((KeyInputEvent.Down, Keycode.F5, Modifiers.Alt)));
		}

		[Test]
		public void UnboundHotkeysSendNothing()
		{
			Press("Base");
			Assert.That(input.Keys, Is.Empty);
		}

		[Test]
		public void KeyboardTypesCharacters()
		{
			Type("h");
			Type("i");
			Type("Space");
			Type("7");
			Assert.That(string.Concat(input.Text), Is.EqualTo("hi 7"));
		}

		[Test]
		public void ShiftCapitalisesTheNextLetter()
		{
			Type("Shift");
			Assert.That(panel.Shift, Is.True);
			Type("a");
			Type("b");
			Assert.That(string.Concat(input.Text), Is.EqualTo("Ab"));
			Assert.That(panel.Shift, Is.False);
		}

		[Test]
		public void SymbolsPageTypesSymbols()
		{
			Type("Sym");
			Assert.That(panel.Symbols, Is.True);
			Type("@");
			Type("Sym");
			Type("q");
			Assert.That(string.Concat(input.Text), Is.EqualTo("@q"));
		}

		[TestCase("Bksp", Keycode.BACKSPACE)]
		[TestCase("Enter", Keycode.RETURN)]
		[TestCase("Esc", Keycode.ESCAPE)]
		public void SpecialKeysSendKeyEvents(string label, Keycode key)
		{
			Type(label);
			Assert.That(SentKeys, Is.EqualTo(new[] { (KeyInputEvent.Down, key, Modifiers.None), (KeyInputEvent.Up, key, Modifiers.None) }));
			Assert.That(input.Text, Is.Empty);
		}

		[Test]
		public void CloseHidesTheKeyboard()
		{
			Type("Close");
			Assert.That(panel.KeyboardOpen, Is.False);
			Assert.That(panel.KeyboardDismissed, Is.True);
		}

		[Test]
		public void ClosedPanelsIgnoreClicks()
		{
			panel.HotkeysOpen = false;
			panel.KeyboardOpen = false;
			var button = panel.HotkeyButtons[0];
			Assert.That(panel.ButtonAt(CenterOf(button)), Is.Null);
			Assert.That(panel.Click(CenterOf(button), input, Modifiers.None), Is.False);
		}

		[Test]
		public void ClickingBetweenButtonsDoesNothing()
		{
			Assert.That(panel.Click(new int2(panel.HotkeyRect.X + 1, panel.HotkeyRect.Y + 1), input, Modifiers.None), Is.False);
			Assert.That(input.Keys, Is.Empty);
		}
	}
}
