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

using NUnit.Framework;

namespace OpenRA.Test
{
	[TestFixture]
	[NonParallelizable]
	sealed class ModifierTest
	{
		Modifiers oldKeyboard;
		Modifiers oldXr;

		[SetUp]
		public void SetUp()
		{
			oldKeyboard = Game.GetModifierKeys() & ~Game.XrModifiers;
			oldXr = Game.XrModifiers;
		}

		[TearDown]
		public void TearDown()
		{
			Game.HandleModifierKeys(oldKeyboard);
			Game.XrModifiers = oldXr;
		}

		[TestCase(Modifiers.None)]
		[TestCase(Modifiers.Shift)]
		[TestCase(Modifiers.Ctrl | Modifiers.Alt)]
		public void KeyboardModifiersAreUnchangedWithoutXr(Modifiers keyboard)
		{
			Game.XrModifiers = Modifiers.None;
			Game.HandleModifierKeys(keyboard);
			Assert.That(Game.GetModifierKeys(), Is.EqualTo(keyboard));
		}

		[Test]
		public void XrModifiersAreCombinedWithTheKeyboard()
		{
			Game.HandleModifierKeys(Modifiers.Ctrl);
			Game.XrModifiers = Modifiers.Shift;
			Assert.That(Game.GetModifierKeys(), Is.EqualTo(Modifiers.Ctrl | Modifiers.Shift));

			Game.HandleModifierKeys(Modifiers.None);
			Assert.That(Game.GetModifierKeys(), Is.EqualTo(Modifiers.Shift));
		}
	}
}
