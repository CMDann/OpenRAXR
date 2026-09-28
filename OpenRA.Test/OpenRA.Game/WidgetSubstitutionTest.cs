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
using System.Collections.ObjectModel;
using System.Linq;
using NUnit.Framework;
using OpenRA.Primitives;
using OpenRA.Support;
using OpenRA.Widgets;

namespace OpenRA.Test
{
	[TestFixture]
	sealed class WidgetSubstitutionTest
	{
		static readonly Size Desktop = new(1920, 1080);
		static readonly Rectangle DesktopWorld = new(0, 0, 1920, 1080);

		static readonly Size XrLayout = new(2048, 1024);
		static readonly Rectangle XrBoard = new(0, 0, 1792, 1024);

		static Dictionary<string, int> Substitutions(Size resolution, Rectangle world)
			=> Widget.CreateSubstitutions(resolution, world, new WidgetBounds(0, 0, resolution.Width, resolution.Height), null);

		/// <summary>Evaluates a widget's bounds the same way Widget.Initialize does.</summary>
		static Rectangle Evaluate(MiniYaml widget, Dictionary<string, int> substitutions)
		{
			int Eval(string field, IReadOnlyDictionary<string, int> symbols)
			{
				var node = widget.Nodes.FirstOrDefault(n => n.Key == field);
				return node == null ? 0 : new IntegerExpression(node.Value.Value).Evaluate(symbols);
			}

			var symbols = new ReadOnlyDictionary<string, int>(substitutions);
			var width = Eval("Width", symbols);
			var height = Eval("Height", symbols);
			substitutions.Add("WIDTH", width);
			substitutions.Add("HEIGHT", height);
			return new Rectangle(Eval("X", symbols), Eval("Y", symbols), width, height);
		}

		static IEnumerable<MiniYamlNode> Descendants(IEnumerable<MiniYamlNode> nodes)
		{
			foreach (var n in nodes)
			{
				yield return n;
				foreach (var child in Descendants(n.Value.Nodes))
					yield return child;
			}
		}

		[Test]
		public void DesktopWorldVariablesMatchTheWindow()
		{
			var s = Substitutions(Desktop, DesktopWorld);
			Assert.That(s["WINDOW_WIDTH"], Is.EqualTo(1920));
			Assert.That(s["WINDOW_HEIGHT"], Is.EqualTo(1080));
			Assert.That(s["WORLD_LEFT"], Is.Zero);
			Assert.That(s["WORLD_TOP"], Is.Zero);
			Assert.That(s["WORLD_WIDTH"], Is.EqualTo(s["WINDOW_WIDTH"]));
			Assert.That(s["WORLD_HEIGHT"], Is.EqualTo(s["WINDOW_HEIGHT"]));
		}

		[Test]
		public void XrWorldVariablesDescribeTheBoard()
		{
			var s = Substitutions(XrLayout, XrBoard);
			Assert.That(s["WINDOW_WIDTH"], Is.EqualTo(2048));
			Assert.That(s["WORLD_WIDTH"], Is.EqualTo(1792));
			Assert.That(s["WORLD_HEIGHT"], Is.EqualTo(1024));
		}

		[Test]
		public void ExtraSubstitutionsArePreserved()
		{
			var s = Widget.CreateSubstitutions(Desktop, DesktopWorld, new WidgetBounds(0, 0, 300, 200), new Dictionary<string, int> { { "FOO", 7 } });
			Assert.That(s["FOO"], Is.EqualTo(7));
			Assert.That(s["PARENT_WIDTH"], Is.EqualTo(300));
			Assert.That(s["PARENT_HEIGHT"], Is.EqualTo(200));
		}

		// Every mod's in-game layout must keep the world controllers on the world viewport:
		// the whole window on the desktop, and only the board in XR mode
		[TestCase("common", "ingame.yaml")]
		[TestCase("cnc", "ingame.yaml")]
		public void WorldControllersFollowTheWorldViewport(string mod, string file)
		{
			var nodes = Descendants(MiniYaml.FromFile(TestPaths.ModFile(mod, "chrome", file)))
				.Where(n => n.Key.StartsWith("WorldInteractionController@", System.StringComparison.Ordinal)
					|| n.Key == "ViewportController" || n.Key.StartsWith("ViewportController@", System.StringComparison.Ordinal))
				.ToList();

			Assert.That(nodes, Is.Not.Empty);
			foreach (var node in nodes)
			{
				Assert.That(Evaluate(node.Value, Substitutions(Desktop, DesktopWorld)), Is.EqualTo(DesktopWorld), $"{node.Location} on the desktop");
				Assert.That(Evaluate(node.Value, Substitutions(XrLayout, XrBoard)), Is.EqualTo(XrBoard), $"{node.Location} in XR");
			}
		}

		// The sidebar of each mod must fit inside the XR sidebar strip (the right 256 pixels of the 2048 wide layout)
		[TestCase("ra", "ingame-player.yaml", "SIDEBAR_BACKGROUND_TOP")]
		[TestCase("ra", "ingame-player.yaml", "SIDEBAR_PRODUCTION")]
		[TestCase("cnc", "ingame.yaml", "SIDEBAR_BACKGROUND")]
		[TestCase("d2k", "ingame-player.yaml", "SIDEBAR_BACKGROUND_TOP")]
		[TestCase("d2k", "ingame-player.yaml", "SIDEBAR_PRODUCTION")]
		[TestCase("ts", "ingame-player.yaml", "SIDEBAR_BACKGROUND_TOP")]
		[TestCase("ts", "ingame-player.yaml", "SIDEBAR_PRODUCTION")]
		public void SidebarFitsInTheXrStrip(string mod, string file, string id)
		{
			var node = Descendants(MiniYaml.FromFile(TestPaths.ModFile(mod, "chrome", file)))
				.FirstOrDefault(n => n.Key.EndsWith("@" + id, System.StringComparison.Ordinal));

			Assert.That(node, Is.Not.Null, $"{id} is not defined in {mod}/{file}");

			var bounds = Evaluate(node.Value, Substitutions(XrLayout, XrBoard));
			Assert.That(bounds.Left, Is.GreaterThanOrEqualTo(XrBoard.Right), $"{node.Location} starts on the board");
			Assert.That(bounds.Right, Is.LessThanOrEqualTo(XrLayout.Width), $"{node.Location} runs off the screen");
		}

		[Test]
		public void ChatIsCentredOnTheBoard()
		{
			var node = Descendants(MiniYaml.FromFile(TestPaths.ModFile("common", "chrome", "ingame-chat.yaml")))
				.First(n => n.Key == "Container@CHAT_PANEL");

			var desktop = Evaluate(node.Value, Substitutions(Desktop, DesktopWorld));
			Assert.That(desktop.X, Is.EqualTo((1920 - desktop.Width) / 2));

			var xr = Evaluate(node.Value, Substitutions(XrLayout, XrBoard));
			Assert.That(xr.X + xr.Width / 2, Is.EqualTo(XrBoard.Width / 2).Within(1));
		}
	}
}
