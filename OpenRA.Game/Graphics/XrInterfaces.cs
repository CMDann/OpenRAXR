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

namespace OpenRA
{
	public enum XrPassthroughMode
	{
		[Desc("Use native alpha-blended passthrough if the runtime supports it, otherwise fall back to ChromaKey.")]
		Auto,

		[Desc("Fill the background with KeyColor so that a streaming client (e.g. Virtual Desktop) can replace it with passthrough.")]
		ChromaKey,

		[Desc("Require native alpha-blended passthrough (XR_ENVIRONMENT_BLEND_MODE_ALPHA_BLEND).")]
		AlphaBlend,

		[Desc("Fully immersive VR with a dark background.")]
		Off
	}

	public enum XrBlendMode { Opaque, AlphaBlend, Additive }

	public enum XrHand { Left = 0, Right = 1 }

	/// <summary>A rigid transform in the XR reference (stage) space. Units are meters, right-handed, +Y up, -Z forward.</summary>
	public readonly record struct XrPose(Vector3 Position, Quaternion Orientation)
	{
		public static readonly XrPose Identity = new(Vector3.Zero, Quaternion.Identity);

		public Vector3 Forward => Vector3.Transform(-Vector3.UnitZ, Orientation);
		public Vector3 Up => Vector3.Transform(Vector3.UnitY, Orientation);
		public Vector3 Right => Vector3.Transform(Vector3.UnitX, Orientation);

		/// <summary>Transforms a point from this pose's local frame into the parent frame.</summary>
		public Vector3 TransformPoint(Vector3 local) => Position + Vector3.Transform(local, Orientation);

		/// <summary>Transforms a point from the parent frame into this pose's local frame.</summary>
		public Vector3 InverseTransformPoint(Vector3 world) => Vector3.Transform(world - Position, Quaternion.Inverse(Orientation));

		/// <summary>Composes a child pose (expressed in this pose's frame) into the parent frame.</summary>
		public XrPose Multiply(XrPose child) => new(TransformPoint(child.Position), Quaternion.Normalize(Orientation * child.Orientation));
	}

	public struct XrHandState
	{
		public bool IsActive;

		/// <summary>Pointing ray origin and orientation. The ray points along <see cref="XrPose.Forward"/>.</summary>
		public XrPose Aim;

		/// <summary>Grip (palm) pose, used for grabbing and moving the board.</summary>
		public XrPose Grip;

		public bool Select;
		public bool Order;
		public bool Grab;
		public bool Menu;
		public bool ButtonA;
		public bool ButtonB;
		public Vector2 Stick;
	}

	/// <summary>
	/// A flat panel floating in the room that displays a region of the virtual screen, or a solid color if Fill is set.
	/// The quad faces along its local +Z axis and its image's top edge points along local +Y. Size is in meters.
	/// </summary>
	public readonly record struct XrQuad(string Name, Rectangle Source, XrPose Pose, Vector2 Size, Color? Fill = null);

	/// <summary>
	/// Provided by the platform when running with an OpenXR session.
	/// The game renders a regular 2D "virtual screen" and the device presents regions of it as quads in the room.
	/// </summary>
	public interface IXrDevice : IDisposable
	{
		/// <summary>Size of the offscreen virtual screen that the game UI is laid out on.</summary>
		Size VirtualScreenSize { get; }

		/// <summary>True while the runtime expects frames to be submitted.</summary>
		bool IsSessionRunning { get; }

		/// <summary>True while the application has input focus in the headset.</summary>
		bool IsFocused { get; }

		/// <summary>True once the runtime has asked the application to quit.</summary>
		bool ExitRequested { get; }

		XrBlendMode BlendMode { get; }

		/// <summary>True if the background is filled with a key color for streaming passthrough.</summary>
		bool ChromaKeyEnabled { get; }

		string RuntimeName { get; }
		string SystemName { get; }

		XrPose HeadPose { get; }
		bool HeadPoseValid { get; }

		XrHandState GetHand(XrHand hand);

		/// <summary>
		/// Processes runtime events, waits for the next display frame (this paces rendering to the headset
		/// refresh rate) and samples controller input at the predicted display time.
		/// Must be matched by exactly one <see cref="EndFrame"/> call.
		/// </summary>
		void BeginFrame();

		/// <summary>Copies the finished virtual screen into the headset swapchain and submits the given quads.</summary>
		void EndFrame(IFrameBuffer screen, IReadOnlyList<XrQuad> quads);

		void Vibrate(XrHand hand, float amplitude, float seconds);
	}
}
