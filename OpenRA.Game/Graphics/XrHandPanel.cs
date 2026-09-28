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
using OpenRA.Primitives;

namespace OpenRA.Graphics
{
	public sealed class XrPanelButton(Rectangle bounds, string label, Action<IInputHandler, Modifiers> activate)
	{
		public readonly Rectangle Bounds = bounds;
		public readonly string Label = label;
		public readonly Action<IInputHandler, Modifiers> Activate = activate;
	}

	/// <summary>
	/// XR-only controls that replace the keyboard: a hotkey menu (control groups and the most important
	/// game hotkeys) and an on-screen keyboard for text fields. They are drawn into a reserved band of the
	/// virtual screen that is never shown on the board, and presented on their own quads.
	/// </summary>
	public sealed class XrHandPanel
	{
		const int Margin = 6;
		const int HotkeyWidth = 768;

		static readonly Color BackgroundColor = Color.FromArgb(230, 20, 24, 28);
		static readonly Color ButtonColor = Color.FromArgb(255, 60, 66, 74);
		static readonly Color ButtonHoverColor = Color.FromArgb(255, 110, 120, 60);
		static readonly Color ButtonActiveColor = Color.FromArgb(255, 70, 110, 150);
		static readonly Color TextColor = Color.FromArgb(255, 235, 235, 235);

		static readonly string[] LetterRows = ["1234567890", "qwertyuiop", "asdfghjkl-", "zxcvbnm.,/"];
		static readonly string[] SymbolRows = ["!@#$%^&*()", "_+=:;\"'?<>", "[]{}|\\~`$%", "@#&*-_.,/?"];

		static readonly (string Label, string Hotkey)[] GameHotkeys =
		[
			("All", "SelectAllUnits"), ("Type", "SelectUnitsByType"), ("Event", "ToLastEvent"), ("Base", "CycleBase"),
			("Harv", "CycleHarvesters"), ("View Sel", "ToSelection"), ("Pause", "Pause"), ("Save", "QuickSave"),
		];

		static readonly (string Label, string Hotkey)[] EditorHotkeys =
		[
			("Undo", "EditorUndo"), ("Redo", "EditorRedo"), ("Copy", "EditorCopy"), ("Paste", "EditorPaste"),
			("Delete", "EditorDeleteSelection"), ("Grid", "EditorToggleGridOverlay"), ("Save", "EditorQuickSave"),
		];

		readonly Func<string, Hotkey> lookupHotkey;
		readonly List<XrPanelButton> gameButtons = [];
		readonly List<XrPanelButton> editorButtons = [];
		readonly List<XrPanelButton> letterKeys = [];
		readonly List<XrPanelButton> symbolKeys = [];

		public Rectangle HotkeyRect { get; }
		public Rectangle KeyboardRect { get; }

		public bool HotkeysOpen { get; set; }
		public bool KeyboardOpen { get; set; }

		/// <summary>Show the map editor shortcuts instead of the game hotkeys.</summary>
		public bool EditorMode { get; set; }

		public bool Shift { get; private set; }
		public bool Symbols { get; private set; }

		/// <summary>Set when the player closes the keyboard, until the text field loses focus.</summary>
		public bool KeyboardDismissed { get; set; }

		/// <summary>The pixel the pointer is hovering over, used for highlighting.</summary>
		public int2? Hover { get; set; }

		public IReadOnlyList<XrPanelButton> HotkeyButtons => EditorMode ? editorButtons : gameButtons;
		public IReadOnlyList<XrPanelButton> KeyboardButtons => Symbols ? symbolKeys : letterKeys;

		/// <summary>Lays out the hotkey menu and keyboard inside the reserved band.</summary>
		/// <param name="band">The reserved region of the virtual screen.</param>
		/// <param name="lookupHotkey">Returns the player's current binding for a named hotkey.</param>
		public XrHandPanel(Rectangle band, Func<string, Hotkey> lookupHotkey)
		{
			this.lookupHotkey = lookupHotkey;

			var hotkeyWidth = Math.Min(HotkeyWidth, band.Width / 2);
			HotkeyRect = new Rectangle(band.X, band.Y, hotkeyWidth, band.Height);
			KeyboardRect = new Rectangle(band.X + hotkeyWidth, band.Y, band.Width - hotkeyWidth, band.Height);

			BuildHotkeyButtons(gameButtons, true, GameHotkeys);
			BuildHotkeyButtons(editorButtons, false, EditorHotkeys);
			BuildKeyboard(letterKeys, LetterRows);
			BuildKeyboard(symbolKeys, SymbolRows);
		}

		void BuildHotkeyButtons(List<XrPanelButton> buttons, bool controlGroups, (string Label, string Hotkey)[] hotkeys)
		{
			var rows = controlGroups ? 2 : 1;
			var rowHeight = (HotkeyRect.Height - Margin) / rows;
			var y = HotkeyRect.Y + Margin;

			if (controlGroups)
			{
				// Control groups 1-9 and 0, laid out like the number row of a keyboard
				var width = (HotkeyRect.Width - Margin) / 10;
				for (var i = 0; i < 10; i++)
				{
					var group = i + 1;
					var label = (group % 10).ToString(NumberFormat);
					var bounds = new Rectangle(HotkeyRect.X + Margin + i * width, y, width - Margin, rowHeight - Margin);
					buttons.Add(new XrPanelButton(bounds, label, (input, mods) => SendHotkey(input, ControlGroupHotkey(group, mods))));
				}

				y += rowHeight;
			}

			var hotkeyWidth = (HotkeyRect.Width - Margin) / hotkeys.Length;
			for (var i = 0; i < hotkeys.Length; i++)
			{
				var name = hotkeys[i].Hotkey;
				var bounds = new Rectangle(HotkeyRect.X + Margin + i * hotkeyWidth, y, hotkeyWidth - Margin, rowHeight - Margin);
				buttons.Add(new XrPanelButton(bounds, hotkeys[i].Label, (input, _) => SendHotkey(input, name)));
			}
		}

		static readonly System.Globalization.NumberFormatInfo NumberFormat = System.Globalization.NumberFormatInfo.InvariantInfo;

		/// <summary>Picks the control group action that the held modifiers select, matching the keyboard shortcuts.</summary>
		public static string ControlGroupHotkey(int group, Modifiers mods)
		{
			var action =
				mods.HasModifier(Modifiers.Ctrl) && mods.HasModifier(Modifiers.Shift) ? "AddTo" :
				mods.HasModifier(Modifiers.Ctrl) ? "Create" :
				mods.HasModifier(Modifiers.Shift) ? "CombineWith" :
				mods.HasModifier(Modifiers.Alt) ? "JumpTo" : "Select";

			return $"ControlGroup{action}{group:D2}";
		}

		void BuildKeyboard(List<XrPanelButton> keys, string[] rows)
		{
			// Four rows of ten character keys with a column of special keys on the right, and a space bar row
			var rowCount = rows.Length + 1;
			var rowHeight = (KeyboardRect.Height - Margin) / rowCount;
			var keyWidth = (KeyboardRect.Width - Margin) / 12;

			for (var r = 0; r < rows.Length; r++)
			{
				var y = KeyboardRect.Y + Margin + r * rowHeight;
				for (var c = 0; c < rows[r].Length; c++)
				{
					var character = rows[r][c];
					var bounds = new Rectangle(KeyboardRect.X + Margin + c * keyWidth, y, keyWidth - Margin, rowHeight - Margin);
					keys.Add(new XrPanelButton(bounds, character.ToString(), (input, _) => TypeCharacter(input, character)));
				}

				var specialBounds = new Rectangle(KeyboardRect.X + Margin + 10 * keyWidth, y, 2 * keyWidth - Margin, rowHeight - Margin);
				keys.Add(r switch
				{
					0 => new XrPanelButton(specialBounds, "Bksp", (input, _) => SendKey(input, Keycode.BACKSPACE, Modifiers.None)),
					1 => new XrPanelButton(specialBounds, "Enter", (input, _) => SendKey(input, Keycode.RETURN, Modifiers.None)),
					2 => new XrPanelButton(specialBounds, "Shift", (_, _) => Shift = !Shift),
					_ => new XrPanelButton(specialBounds, "Sym", (_, _) => Symbols = !Symbols),
				});
			}

			var bottom = KeyboardRect.Y + Margin + rows.Length * rowHeight;
			keys.Add(new XrPanelButton(new Rectangle(KeyboardRect.X + Margin, bottom, 2 * keyWidth - Margin, rowHeight - Margin),
				"Esc", (input, _) => SendKey(input, Keycode.ESCAPE, Modifiers.None)));
			keys.Add(new XrPanelButton(new Rectangle(KeyboardRect.X + Margin + 2 * keyWidth, bottom, 8 * keyWidth - Margin, rowHeight - Margin),
				"Space", (input, _) => TypeCharacter(input, ' ')));
			keys.Add(new XrPanelButton(new Rectangle(KeyboardRect.X + Margin + 10 * keyWidth, bottom, 2 * keyWidth - Margin, rowHeight - Margin),
				"Close", (_, _) =>
				{
					KeyboardOpen = false;
					KeyboardDismissed = true;
				}));
		}

		void TypeCharacter(IInputHandler input, char c)
		{
			if (Shift && char.IsLetter(c))
			{
				c = char.ToUpperInvariant(c);

				// Shift applies to one letter, like on a phone keyboard
				Shift = false;
			}

			input.OnTextInput(c.ToString());
		}

		void SendHotkey(IInputHandler input, string name)
		{
			var hotkey = lookupHotkey(name);
			if (hotkey.IsValid())
				SendKey(input, hotkey.Key, hotkey.Modifiers);
		}

		static void SendKey(IInputHandler input, Keycode key, Modifiers modifiers)
		{
			input.OnKeyInput(new KeyInput { Event = KeyInputEvent.Down, Key = key, Modifiers = modifiers, MultiTapCount = 1 });
			input.OnKeyInput(new KeyInput { Event = KeyInputEvent.Up, Key = key, Modifiers = modifiers, MultiTapCount = 1 });
		}

		public XrPanelButton ButtonAt(int2 pixel)
		{
			if (HotkeysOpen && HotkeyRect.Contains(pixel))
				return HotkeyButtons.FirstOrDefault(b => b.Bounds.Contains(pixel));

			if (KeyboardOpen && KeyboardRect.Contains(pixel))
				return KeyboardButtons.FirstOrDefault(b => b.Bounds.Contains(pixel));

			return null;
		}

		/// <summary>Activates the button under the pointer. Returns false if there is no button there.</summary>
		public bool Click(int2 pixel, IInputHandler input, Modifiers modifiers)
		{
			var button = ButtonAt(pixel);
			if (button == null)
				return false;

			button.Activate(input, modifiers);
			return true;
		}

		public void Draw(Renderer renderer)
		{
			if (!HotkeysOpen && !KeyboardOpen)
				return;

			var font = PickFont(renderer);
			if (HotkeysOpen)
				DrawButtons(renderer, font, HotkeyRect, HotkeyButtons);

			if (KeyboardOpen)
				DrawButtons(renderer, font, KeyboardRect, KeyboardButtons);
		}

		void DrawButtons(Renderer renderer, SpriteFont font, Rectangle area, IReadOnlyList<XrPanelButton> buttons)
		{
			var cr = renderer.RgbaColorRenderer;
			cr.FillRect(new Vector3(area.Left, area.Top, 0), new Vector3(area.Right, area.Bottom, 0), BackgroundColor);

			var hover = Hover != null ? ButtonAt(Hover.Value) : null;
			foreach (var b in buttons)
			{
				var active = (b.Label == "Shift" && Shift) || (b.Label == "Sym" && Symbols);
				var color = b == hover ? ButtonHoverColor : active ? ButtonActiveColor : ButtonColor;
				cr.FillRect(new Vector3(b.Bounds.Left, b.Bounds.Top, 0), new Vector3(b.Bounds.Right, b.Bounds.Bottom, 0), color);

				if (font == null)
					continue;

				var label = Shift && b.Label.Length == 1 ? b.Label.ToUpperInvariant() : b.Label;
				var size = font.Measure(label);
				var location = new Vector2(b.Bounds.X + (b.Bounds.Width - size.X) / 2, b.Bounds.Y + (b.Bounds.Height - size.Y) / 2);
				font.DrawText(label, location, TextColor);
			}
		}

		static SpriteFont PickFont(Renderer renderer)
		{
			if (renderer.Fonts == null)
				return null;

			foreach (var name in new[] { "BigBold", "MediumBold", "Bold", "Regular" })
				if (renderer.Fonts.TryGetValue(name, out var font))
					return font;

			return renderer.Fonts.Values.FirstOrDefault();
		}
	}
}
