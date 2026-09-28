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
using System.Numerics;
using OpenRA.Graphics;
using OpenRA.Primitives;

namespace OpenRA.Test.Fakes
{
	/// <summary>A headset whose poses and buttons are set by the test.</summary>
	sealed class FakeXrDevice : IXrDevice
	{
		public readonly XrHandState[] Hands = new XrHandState[2];
		public readonly List<(XrHand Hand, float Amplitude, float Seconds)> Vibrations = [];
		public readonly List<XrQuad> SubmittedQuads = [];
		public int FrameCount;

		public FakeXrDevice(Size virtualScreenSize, int handPanelHeight = 256)
		{
			VirtualScreenSize = virtualScreenSize;
			LayoutSize = new Size(virtualScreenSize.Width, virtualScreenSize.Height - handPanelHeight);
		}

		public Size VirtualScreenSize { get; }
		public Size LayoutSize { get; }
		public bool IsSessionRunning { get; set; } = true;
		public bool IsFocused { get; set; } = true;
		public bool ExitRequested { get; set; }
		public XrBlendMode BlendMode { get; set; } = XrBlendMode.Opaque;
		public bool ChromaKeyEnabled { get; set; } = true;
		public string RuntimeName => "Fake Runtime";
		public string SystemName => "Fake Headset";

		/// <summary>Seated at the origin of the stage at eye height, looking towards -Z.</summary>
		public XrPose HeadPose { get; set; } = new(new Vector3(0, 1.2f, 0), Quaternion.Identity);
		public bool HeadPoseValid { get; set; } = true;

		public XrHandState GetHand(XrHand hand) => Hands[(int)hand];

		public ref XrHandState Hand(XrHand hand) => ref Hands[(int)hand];

		public void BeginFrame() => FrameCount++;

		public void EndFrame(IFrameBuffer screen, IReadOnlyList<XrQuad> quads)
		{
			SubmittedQuads.Clear();
			SubmittedQuads.AddRange(quads);
		}

		public void Vibrate(XrHand hand, float amplitude, float seconds) => Vibrations.Add((hand, amplitude, seconds));

		public void Dispose() { }
	}

	/// <summary>Records everything the XR layer sends to the game.</summary>
	sealed class RecordingInputHandler : IInputHandler
	{
		public readonly List<MouseInput> Mouse = [];
		public readonly List<KeyInput> Keys = [];
		public readonly List<string> Text = [];

		public void ModifierKeys(Modifiers mods) { }
		public void OnKeyInput(KeyInput input) => Keys.Add(input);
		public void OnMouseInput(MouseInput input) => Mouse.Add(input);
		public void OnTextInput(string text) => Text.Add(text);

		public void Clear()
		{
			Mouse.Clear();
			Keys.Clear();
			Text.Clear();
		}
	}

	sealed class FakeViewportControls : IXrViewportControls
	{
		public Vector2 Scrolled;
		public float Zoomed;

		public void Scroll(Vector2 screenPixels) => Scrolled += screenPixels;
		public void Zoom(float amount) => Zoomed += amount;
	}
}
