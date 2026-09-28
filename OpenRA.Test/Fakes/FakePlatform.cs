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
using OpenRA.Primitives;

namespace OpenRA.Test.Fakes
{
	/// <summary>A platform without a GPU, so that the Renderer can be constructed in unit tests.</summary>
	sealed class FakePlatform(Size windowSize, float windowScale = 1f, IXrDevice xr = null) : IPlatform
	{
		public FakeWindow Window { get; private set; }

		public IPlatformWindow CreateWindow(Size size, WindowMode windowMode, float scaleModifier,
			int vertexBatchSize, int indexBatchSize, int videoDisplay, GLProfile profile)
		{
			return Window = new FakeWindow(windowSize, windowScale, xr);
		}

		public ISoundEngine CreateSound(string device) => throw new NotSupportedException();
		public IFont CreateFont(byte[] data) => throw new NotSupportedException();
	}

	sealed class FakeWindow(Size effectiveSize, float scale, IXrDevice xr) : IPlatformWindow
	{
		public IGraphicsContext Context { get; } = new FakeGraphicsContext();
		public Size NativeWindowSize => new((int)(effectiveSize.Width * scale), (int)(effectiveSize.Height * scale));
		public Size EffectiveWindowSize => effectiveSize;
		public float NativeWindowScale => scale;
		public float EffectiveWindowScale => scale;
		public Size SurfaceSize => xr?.VirtualScreenSize ?? NativeWindowSize;
		public int DisplayCount => 1;
		public int CurrentDisplay => 0;
		public bool HasInputFocus => true;
		public bool IsSuspended => false;
		public GLProfile GLProfile => GLProfile.Modern;
		public GLProfile[] SupportedGLProfiles => [GLProfile.Modern];
		public IXrDevice Xr => xr;

		public event Action<float, float, float, float> OnWindowScaleChanged = (_, _, _, _) => { };

		public void PumpInput(IInputHandler inputHandler) { }
		public string GetClipboardText() => "";
		public bool SetClipboardText(string text) => true;
		public bool TryOpenUrl(string url) => false;
		public void GrabWindowMouseFocus() { }
		public void ReleaseWindowMouseFocus() { }
		public IHardwareCursor CreateHardwareCursor(string name, Size size, byte[] data, int2 hotspot, bool pixelDouble) => null;
		public void SetHardwareCursor(IHardwareCursor cursor) { }
		public void SetWindowTitle(string title) { }
		public void SetRelativeMouseMode(bool mode) { }
		public void SetScaleModifier(float scale) => OnWindowScaleChanged(scale, scale, scale, scale);
		public void Dispose() { }
	}

	sealed class FakeGraphicsContext : IGraphicsContext
	{
		public int PresentCount { get; private set; }

		public IVertexBuffer<T> CreateEmptyVertexBuffer<T>(int size) where T : struct => new FakeVertexBuffer<T>();
		public IVertexBuffer<T> CreateVertexBuffer<T>(T[] data, bool dynamic = true) where T : struct => new FakeVertexBuffer<T>();
		public T[] CreateVertices<T>(int size) where T : struct => new T[size];
		public IIndexBuffer CreateIndexBuffer(uint[] indices) => new FakeIndexBuffer();
		public ITexture CreateTexture() => new FakeTexture();
		public IFrameBuffer CreateFrameBuffer(Size s) => new FakeFrameBuffer(s);
		public IFrameBuffer CreateFrameBuffer(Size s, Color clearColor) => new FakeFrameBuffer(s);
		public IShader CreateShader(IShaderBindings shaderBindings) => new FakeShader();
		public void EnableScissor(int x, int y, int width, int height) { }
		public void DisableScissor() { }
		public void Present() => PresentCount++;
		public void DrawPrimitives(PrimitiveType pt, int firstVertex, int numVertices) { }
		public void DrawElements(int numIndices, int offset) { }
		public void Clear() { }
		public void EnableDepthBuffer() { }
		public void DisableDepthBuffer() { }
		public void ClearDepthBuffer() { }
		public void SetBlendMode(BlendMode mode) { }
		public void SetVSyncEnabled(bool enabled) { }
		public string GLVersion => "Fake";
		public void Dispose() { }
	}

	sealed class FakeVertexBuffer<T> : IVertexBuffer<T> where T : struct
	{
		public void Bind() { }
		public void SetData(T[] vertices, int length) { }
		public void SetData(ref T[] vertices, int length) { }
		public void SetData(T[] vertices, int offset, int start, int length) { }
		public void Dispose() { }
	}

	sealed class FakeIndexBuffer : IIndexBuffer
	{
		public void Bind() { }
		public void Dispose() { }
	}

	sealed class FakeShader : IShader
	{
		public void SetBool(string name, bool value) { }
		public void SetVec(string name, float x) { }
		public void SetVec(string name, float x, float y) { }
		public void SetVec(string name, float x, float y, float z) { }
		public void SetVec(string name, ReadOnlyMemory<float> vec, int length) { }
		public void SetTexture(string param, ITexture texture) { }
		public void SetMatrix(string param, float[] mtx) { }
		public void PrepareRender() { }
		public void Bind() { }
	}

	sealed class FakeTexture : ITexture
	{
		public Size Size { get; private set; }
		public TextureScaleFilter ScaleFilter { get; set; }
		public void SetData(byte[] colors, int width, int height) => Size = new Size(width, height);
		public void SetSubData(byte[] colors, int xoffset, int yoffset, int width, int height) { }
		public void SetFloatData(float[] data, int width, int height) => Size = new Size(width, height);
		public void SetDataFromReadBuffer(Rectangle rect) { }
		public byte[] GetData() => new byte[4 * Size.Width * Size.Height];
		public void Dispose() { }
	}

	sealed class FakeFrameBuffer(Size size) : IFrameBuffer
	{
		public ITexture Texture { get; } = new FakeTexture();
		public Size Size => size;
		public void Bind() { }
		public void Unbind() { }
		public void EnableScissor(Rectangle rect) { }
		public void DisableScissor() { }
		public void Dispose() { }
	}
}
