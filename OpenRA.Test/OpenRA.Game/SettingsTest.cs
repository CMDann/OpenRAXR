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

using System.IO;
using NUnit.Framework;
using OpenRA.Primitives;

namespace OpenRA.Test
{
	[TestFixture]
	sealed class SettingsTest
	{
		[Test]
		public void XrIsDisabledByDefault()
		{
			var settings = new Settings(TestPaths.TempSettingsFile(), new Arguments());
			Assert.That(settings.Xr.Enabled, Is.False);
			Assert.That(settings.Xr.Passthrough, Is.EqualTo(XrPassthroughMode.Auto));
			Assert.That(settings.Xr.VirtualScreenSize, Is.EqualTo(new int2(2048, 1280)));
			Assert.That(settings.Xr.HandPanelHeight, Is.EqualTo(256));
			Assert.That(settings.Xr.SidebarWidth, Is.EqualTo(256));
			Assert.That(settings.Xr.Haptics, Is.True);
		}

		[Test]
		public void CommandLineEnablesXr()
		{
			var settings = new Settings(TestPaths.TempSettingsFile(),
				new Arguments("Xr.Enabled=true", "Xr.SidebarWidth=300", "Xr.Passthrough=Off", "Xr.LeftHanded=true"));

			Assert.That(settings.Xr.Enabled, Is.True);
			Assert.That(settings.Xr.SidebarWidth, Is.EqualTo(300));
			Assert.That(settings.Xr.Passthrough, Is.EqualTo(XrPassthroughMode.Off));
			Assert.That(settings.Xr.LeftHanded, Is.True);
		}

		[Test]
		public void DefaultsAreNotWrittenToTheSettingsFile()
		{
			var file = TestPaths.TempSettingsFile();
			new Settings(file, new Arguments()).Save();

			var text = File.Exists(file) ? File.ReadAllText(file) : "";
			Assert.That(text, Does.Not.Contain("Xr:"));
		}

		[Test]
		public void XrSettingsSurviveASaveAndReload()
		{
			var file = TestPaths.TempSettingsFile();
			var settings = new Settings(file, new Arguments());
			settings.Xr.Enabled = true;
			settings.Xr.KeyColor = Color.FromArgb(255, 12, 200, 34);
			settings.Xr.VirtualScreenSize = new int2(2560, 1536);
			settings.Xr.BoardPlaced = true;
			settings.Xr.BoardX = 0.25f;
			settings.Xr.BoardYaw = -30f;
			settings.Save();

			var reloaded = new Settings(file, new Arguments());
			Assert.That(reloaded.Xr.Enabled, Is.True);
			Assert.That(reloaded.Xr.KeyColor, Is.EqualTo(Color.FromArgb(255, 12, 200, 34)));
			Assert.That(reloaded.Xr.VirtualScreenSize, Is.EqualTo(new int2(2560, 1536)));
			Assert.That(reloaded.Xr.BoardPlaced, Is.True);
			Assert.That(reloaded.Xr.BoardX, Is.EqualTo(0.25f));
			Assert.That(reloaded.Xr.BoardYaw, Is.EqualTo(-30f));
		}

		[Test]
		public void DesktopSettingsAreUntouchedByXrSettings()
		{
			var file = TestPaths.TempSettingsFile();
			var settings = new Settings(file, new Arguments("Xr.Enabled=true"));
			settings.Graphics.UIScale = 1.5f;
			settings.Game.ViewportEdgeScroll = true;
			settings.Save();

			var reloaded = new Settings(file, new Arguments());
			Assert.That(reloaded.Graphics.UIScale, Is.EqualTo(1.5f));
			Assert.That(reloaded.Graphics.DisableHardwareCursors, Is.False);
			Assert.That(reloaded.Game.ViewportEdgeScroll, Is.True);
		}
	}
}
