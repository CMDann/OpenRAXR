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
using System.Runtime.InteropServices;

namespace OpenRA.Platforms.Default
{
	/// <summary>Queries the native handles of the current OpenGL context, which OpenXR needs to share it.</summary>
	static class NativeGL
	{
		[DllImport("opengl32.dll", EntryPoint = "wglGetCurrentDC")]
		public static extern IntPtr WglGetCurrentDC();

		[DllImport("opengl32.dll", EntryPoint = "wglGetCurrentContext")]
		public static extern IntPtr WglGetCurrentContext();

		const string LibGL = "libGL.so.1";
		const int GlxScreen = 0x800C;
		const int GlxFBConfigId = 0x8013;

		[DllImport(LibGL)]
		static extern IntPtr glXGetCurrentDisplay();

		[DllImport(LibGL)]
		static extern IntPtr glXGetCurrentDrawable();

		[DllImport(LibGL)]
		static extern IntPtr glXGetCurrentContext();

		[DllImport(LibGL)]
		static extern int glXQueryContext(IntPtr display, IntPtr context, int attribute, out int value);

		[DllImport(LibGL)]
		static extern IntPtr glXChooseFBConfig(IntPtr display, int screen, int[] attribs, out int count);

		[DllImport(LibGL)]
		static extern IntPtr glXGetVisualFromFBConfig(IntPtr display, IntPtr config);

		[DllImport("libX11.so.6")]
		static extern int XFree(IntPtr data);

		public readonly struct GlxHandles(IntPtr display, uint visualId, IntPtr fbConfig, IntPtr drawable, IntPtr context)
		{
			public readonly IntPtr Display = display;
			public readonly uint VisualId = visualId;
			public readonly IntPtr FBConfig = fbConfig;
			public readonly IntPtr Drawable = drawable;
			public readonly IntPtr Context = context;
		}

		public static GlxHandles GetCurrentGlx()
		{
			var display = glXGetCurrentDisplay();
			var context = glXGetCurrentContext();
			var drawable = glXGetCurrentDrawable();
			if (display == IntPtr.Zero || context == IntPtr.Zero)
				throw new InvalidOperationException("No current GLX context. XR mode requires X11 (SDL_VIDEODRIVER=x11) with GLX.");

			glXQueryContext(display, context, GlxScreen, out var screen);
			glXQueryContext(display, context, GlxFBConfigId, out var fbConfigId);

			var configs = glXChooseFBConfig(display, screen, [GlxFBConfigId, fbConfigId, 0], out var count);
			if (configs == IntPtr.Zero || count < 1)
				throw new InvalidOperationException("Failed to find the GLX framebuffer configuration of the current context.");

			var fbConfig = Marshal.ReadIntPtr(configs);
			XFree(configs);

			// XVisualInfo { Visual* visual; VisualID visualid; ... }
			uint visualId = 0;
			var visualInfo = glXGetVisualFromFBConfig(display, fbConfig);
			if (visualInfo != IntPtr.Zero)
			{
				visualId = (uint)Marshal.ReadIntPtr(visualInfo, IntPtr.Size);
				XFree(visualInfo);
			}

			return new GlxHandles(display, visualId, fbConfig, drawable, context);
		}
	}
}
