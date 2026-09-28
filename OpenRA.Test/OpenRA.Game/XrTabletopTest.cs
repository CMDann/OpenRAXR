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
using System.Numerics;
using NUnit.Framework;
using OpenRA.Graphics;
using OpenRA.Primitives;

namespace OpenRA.Test
{
	[TestFixture]
	sealed class XrTabletopTest
	{
		static readonly Quaternion PointDown = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2);

		// A 0.8m x 0.4m board lying flat on a 0.75m high table, laid out the same way XrTabletop does it
		static readonly XrQuad Board = new("board", new Rectangle(0, 0, 1600, 800),
			new XrPose(new Vector3(0, 0.75f, -0.5f), Quaternion.Identity).Multiply(new XrPose(Vector3.Zero, PointDown)),
			new Vector2(0.8f, 0.4f));

		static XrPose DownwardRay(float x, float z) => new(new Vector3(x, 1.5f, z), PointDown);

		[Test]
		public void RayThroughCenterHitsMiddleOfImage()
		{
			Assert.That(XrTabletop.IntersectQuad(DownwardRay(0, -0.5f), Board, false, out var uv, out var distance), Is.True);
			Assert.That(uv.X, Is.EqualTo(0.5f).Within(1e-4f));
			Assert.That(uv.Y, Is.EqualTo(0.5f).Within(1e-4f));
			Assert.That(distance, Is.EqualTo(0.75f).Within(1e-4f));
		}

		[Test]
		public void FarLeftCornerIsTopLeftOfImage()
		{
			// The top of the screen image lies on the far side of the table
			Assert.That(XrTabletop.IntersectQuad(DownwardRay(-0.39f, -0.69f), Board, false, out var uv, out _), Is.True);
			Assert.That(uv.X, Is.LessThan(0.02f));
			Assert.That(uv.Y, Is.LessThan(0.03f));
		}

		[Test]
		public void NearRightCornerIsBottomRightOfImage()
		{
			Assert.That(XrTabletop.IntersectQuad(DownwardRay(0.39f, -0.31f), Board, false, out var uv, out _), Is.True);
			Assert.That(uv.X, Is.GreaterThan(0.98f));
			Assert.That(uv.Y, Is.GreaterThan(0.97f));
		}

		[Test]
		public void RayOutsideBoardMissesUnlessCaptured()
		{
			var ray = DownwardRay(0.6f, -0.5f);
			Assert.That(XrTabletop.IntersectQuad(ray, Board, false, out _, out _), Is.False);

			// While dragging, hits past the edge are clamped to the edge so a selection box can reach it
			Assert.That(XrTabletop.IntersectQuad(ray, Board, true, out var uv, out _), Is.True);
			Assert.That(uv.X, Is.GreaterThan(0.99f));
			Assert.That(uv.Y, Is.EqualTo(0.5f).Within(1e-4f));
		}

		[Test]
		public void RayPointingAwayMisses()
		{
			var up = new XrPose(new Vector3(0, 1.0f, -0.5f), Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2));
			Assert.That(XrTabletop.IntersectQuad(up, Board, false, out _, out _), Is.False);
		}

		[Test]
		public void PoseRoundTrip()
		{
			var pose = new XrPose(new Vector3(1, 2, 3), Quaternion.CreateFromYawPitchRoll(0.3f, -0.2f, 0.1f));
			var point = new Vector3(-0.4f, 0.25f, 1.5f);
			var result = pose.InverseTransformPoint(pose.TransformPoint(point));
			Assert.That(Vector3.Distance(point, result), Is.LessThan(1e-5f));
		}
	}
}
