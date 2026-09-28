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

using System.Numerics;
using NUnit.Framework;
using OpenRA.Graphics;
using OpenRA.Primitives;

namespace OpenRA.Test
{
	[TestFixture]
	sealed class ViewportMathTest
	{
		// The conversions as they were before the world viewport origin was introduced
		static int2 OriginalViewToWorldPx(int2 view, float uiScale, float zoom, Vector2 center, Size viewportSize)
			=> int2.FromVector(uiScale / zoom * view.ToVector2() + center - (viewportSize.ToInt2() / 2).ToVector2());

		static int2 OriginalWorldToViewPx(int2 world, float uiScale, float zoom, Vector2 center, Size viewportSize)
			=> int2.FromVector(zoom / uiScale * (world.ToVector2() - center + (viewportSize.ToInt2() / 2).ToVector2()));

		static readonly Vector2 Center = new(3000.5f, 1234.25f);
		static readonly Size ViewportSize = new(1601, 901);

		[TestCase(1f, 1f)]
		[TestCase(1f, 2f)]
		[TestCase(1.5f, 1f)]
		[TestCase(2f, 0.75f)]
		[TestCase(1.25f, 1.33f)]
		public void DesktopConversionsAreUnchanged(float uiScale, float zoom)
		{
			foreach (var view in new[] { int2.Zero, new int2(1, 1), new int2(640, 360), new int2(1919, 1079), new int2(-5, 7) })
			{
				Assert.That(Viewport.ViewToWorldPx(view, uiScale, zoom, Vector2.Zero, Center, ViewportSize),
					Is.EqualTo(OriginalViewToWorldPx(view, uiScale, zoom, Center, ViewportSize)), $"ViewToWorldPx {view}");

				var world = new int2(2500 + view.X, 1000 + view.Y);
				Assert.That(Viewport.WorldToViewPx(world, uiScale, zoom, Vector2.Zero, Center, ViewportSize),
					Is.EqualTo(OriginalWorldToViewPx(world, uiScale, zoom, Center, ViewportSize)), $"WorldToViewPx {world}");
			}
		}

		[TestCase(1f)]
		[TestCase(2f)]
		[TestCase(0.5f)]
		public void OffsetWorldViewportRoundTrips(float zoom)
		{
			// The XR board starts to the right of a sidebar or other panel
			var origin = new Vector2(100, 40);
			foreach (var view in new[] { new int2(100, 40), new int2(500, 300), new int2(1891, 1063) })
			{
				var world = Viewport.ViewToWorldPx(view, 1f, zoom, origin, Center, ViewportSize);
				var back = Viewport.WorldToViewPx(world, 1f, zoom, origin, Center, ViewportSize);

				// Rounding to whole world pixels loses up to one world pixel, which is `zoom` view pixels
				Assert.That((back - view).Length, Is.LessThanOrEqualTo(System.MathF.Ceiling(zoom) + 1), $"{view} -> {world} -> {back}");
			}
		}

		[Test]
		public void WorldViewportOriginMapsToTheTopLeftOfTheView()
		{
			var origin = new Vector2(100, 40);
			var topLeft = Viewport.ViewToWorldPx(new int2(100, 40), 1f, 1f, origin, Center, ViewportSize);
			Assert.That(topLeft, Is.EqualTo(int2.FromVector(Center) - ViewportSize.ToInt2() / 2));
		}

		[Test]
		public void Vector3OverloadMatchesInt2Overload()
		{
			var world = new int2(2800, 1100);
			var origin = new Vector2(10, 20);
			var a = Viewport.WorldToViewPx(world, 1f, 1f, origin, Center, new Size(1600, 900));
			var b = Viewport.WorldToViewPx(new Vector3(world.X, world.Y, 0), 1f, 1f, origin, Center, new Size(1600, 900));
			Assert.That(b, Is.EqualTo(a));
		}
	}
}
