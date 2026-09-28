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
using OpenRA.Primitives;
using OpenRA.Test.Fakes;

namespace OpenRA.Test
{
	[TestFixture]
	[NonParallelizable]
	sealed class RendererTest
	{
		Settings oldSettings;

		[SetUp]
		public void SetUp()
		{
			oldSettings = Game.Settings;
			Game.Settings = new Settings(TestPaths.TempSettingsFile(), new Arguments());
		}

		[TearDown]
		public void TearDown()
		{
			Game.Settings = oldSettings;
		}

		static Renderer CreateRenderer(Size size, float scale = 1f, IXrDevice xr = null)
		{
			return new Renderer(new FakePlatform(size, scale, xr), Game.Settings.Graphics, 8192);
		}

		[Test]
		public void DesktopWorldViewportCoversTheWholeWindow()
		{
			using var renderer = CreateRenderer(new Size(1280, 720));
			Assert.That(renderer.Xr, Is.Null);
			Assert.That(renderer.WorldViewport, Is.EqualTo(new Rectangle(0, 0, 1280, 720)));
			Assert.That(renderer.WorldViewportNativeSize, Is.EqualTo(renderer.NativeResolution));
			Assert.That(renderer.UIPanels, Is.Empty);
		}

		[Test]
		public void DesktopWorldViewportFollowsHiDpiScale()
		{
			using var renderer = CreateRenderer(new Size(1280, 720), 2f);
			Assert.That(renderer.Resolution, Is.EqualTo(new Size(1280, 720)));
			Assert.That(renderer.WorldViewportNativeSize, Is.EqualTo(new Size(2560, 1440)));
		}

		[Test]
		public void DesktopPanelBoundsAreTheWholeScreen()
		{
			using var renderer = CreateRenderer(new Size(1280, 720));
			Assert.That(renderer.GetPanelBounds(new int2(1270, 700)), Is.EqualTo(new Rectangle(0, 0, 1280, 720)));
		}

		[Test]
		public void ShrunkWorldViewportScalesNativeSize()
		{
			using var renderer = CreateRenderer(new Size(1024, 768), 2f);
			renderer.WorldViewport = new Rectangle(0, 0, 512, 768);
			Assert.That(renderer.WorldViewportNativeSize, Is.EqualTo(new Size(1024, 1536)));
		}

		[Test]
		public void XrDeviceIsExposedThroughTheRenderer()
		{
			var xr = new FakeXrDevice(new Size(2048, 1280));
			using var renderer = CreateRenderer(new Size(2048, 1024), 1f, xr);
			Assert.That(renderer.Xr, Is.SameAs(xr));
		}

		[Test]
		public void PanelBoundsPickThePanelUnderTheCursor()
		{
			using var renderer = CreateRenderer(new Size(2048, 1024));
			var board = new Rectangle(0, 0, 1792, 1024);
			var sidebar = new Rectangle(1792, 0, 256, 1024);
			renderer.UIPanels.Add(board);
			renderer.UIPanels.Add(sidebar);

			Assert.That(renderer.GetPanelBounds(new int2(100, 100)), Is.EqualTo(board));
			Assert.That(renderer.GetPanelBounds(new int2(1900, 500)), Is.EqualTo(sidebar));

			// Points outside every panel fall back to the whole screen
			Assert.That(renderer.GetPanelBounds(new int2(3000, 500)), Is.EqualTo(new Rectangle(0, 0, 2048, 1024)));
		}
	}
}
