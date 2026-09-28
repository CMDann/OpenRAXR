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
using System.Text;
using OpenRA.Primitives;
using Silk.NET.OpenXR;
using Silk.NET.OpenXR.Extensions.KHR;
using XrAction = Silk.NET.OpenXR.Action;

namespace OpenRA.Platforms.Default
{
	/// <summary>
	/// OpenXR session that presents regions of the game's virtual screen as world-locked quad layers.
	/// All methods must be called from the thread that owns the OpenGL context.
	/// </summary>
	sealed unsafe class OpenXrDevice : IXrDevice
	{
		const int GlSrgb8Alpha8 = 0x8C43;
		const int GlRgba8 = 0x8058;
		const int SolidSwapchainSize = 16;

		sealed class QuadSwapchain
		{
			public Swapchain Handle;
			public Size Size;
			public uint[] Images;
			public bool UsedThisFrame;
		}

		sealed class HandActions
		{
			public ulong Path;
			public Space AimSpace;
			public Space GripSpace;
			public XrHandState State;
		}

		readonly XR xr;
		readonly Instance instance;
		readonly ulong systemId;
		readonly XrSettings settings;
		readonly EnvironmentBlendMode environmentBlendMode;

		Session session;
		Space stageSpace;
		Space viewSpace;
		long swapchainFormat;

		readonly Dictionary<string, QuadSwapchain> quadSwapchains = [];
		readonly Dictionary<Color, Swapchain> solidSwapchains = [];
		Swapchain backgroundSwapchain;
		uint copyFramebuffer;

		ActionSet actionSet;
		XrAction aimAction, gripAction, selectAction, orderAction, grabAction, menuAction, buttonAAction, buttonBAction, stickAction, stickClickAction, hapticAction;
		readonly HandActions[] hands = [new(), new()];

		SessionState sessionState = SessionState.Unknown;
		FrameState frameState;
		bool frameBegun;
		XrPose headPose = XrPose.Identity;

		public Size VirtualScreenSize { get; }
		public Size LayoutSize { get; }
		public bool IsSessionRunning { get; private set; }
		public bool IsFocused => sessionState == SessionState.Focused;
		public bool ExitRequested { get; private set; }
		public XrBlendMode BlendMode { get; }
		public bool ChromaKeyEnabled { get; }
		public string RuntimeName { get; }
		public string SystemName { get; }
		public XrPose HeadPose => headPose;
		public bool HeadPoseValid { get; private set; }

		OpenXrDevice(XR xr, Instance instance, ulong systemId, XrSettings settings, EnvironmentBlendMode blendMode, string runtimeName, string systemName)
		{
			this.xr = xr;
			this.instance = instance;
			this.systemId = systemId;
			this.settings = settings;
			environmentBlendMode = blendMode;
			RuntimeName = runtimeName;
			SystemName = systemName;

			VirtualScreenSize = new Size(Math.Max(settings.VirtualScreenSize.X, 640), Math.Max(settings.VirtualScreenSize.Y, 480));
			var band = Math.Clamp(settings.HandPanelHeight, 0, VirtualScreenSize.Height - 480);
			LayoutSize = new Size(VirtualScreenSize.Width, VirtualScreenSize.Height - band);
			BlendMode = blendMode == EnvironmentBlendMode.AlphaBlend ? XrBlendMode.AlphaBlend :
				blendMode == EnvironmentBlendMode.Additive ? XrBlendMode.Additive : XrBlendMode.Opaque;

			ChromaKeyEnabled = BlendMode == XrBlendMode.Opaque &&
				(settings.Passthrough == XrPassthroughMode.ChromaKey || settings.Passthrough == XrPassthroughMode.Auto);
		}

		/// <summary>
		/// Creates the OpenXR instance and finds the headset. This does not require an OpenGL context,
		/// so it runs before the game window is created to decide whether to lay the game out for XR.
		/// Returns null (and logs the reason) if no runtime or headset is available.
		/// </summary>
		public static OpenXrDevice TryCreate(XrSettings settings)
		{
			XR xr;
			try
			{
				xr = XR.GetApi();
			}
			catch (Exception e)
			{
				Log.Write("graphics", "Failed to load the OpenXR loader (openxr_loader). XR mode is disabled.");
				Log.Write("graphics", e);
				Console.WriteLine("XR: OpenXR loader not found, falling back to desktop mode.");
				return null;
			}

			var instance = default(Instance);
			try
			{
				if (!HasExtension(xr, KhrOpenglEnable.ExtensionName))
					throw new NotSupportedException($"The active OpenXR runtime does not support {KhrOpenglEnable.ExtensionName}.");

				var appInfo = default(ApplicationInfo);
				WriteString(appInfo.ApplicationName, 128, "OpenRA");
				WriteString(appInfo.EngineName, 128, "OpenRA");
				appInfo.ApplicationVersion = 1;
				appInfo.EngineVersion = 1;

				// XR_MAKE_VERSION(1, 0, 34)
				appInfo.ApiVersion = (1UL << 48) | 34;

				var extensionName = Utf8(KhrOpenglEnable.ExtensionName);
				fixed (byte* extensionNamePtr = extensionName)
				{
					var extensionNames = stackalloc byte*[1] { extensionNamePtr };
					var createInfo = new InstanceCreateInfo
					{
						Type = StructureType.InstanceCreateInfo,
						ApplicationInfo = appInfo,
						EnabledExtensionCount = 1,
						EnabledExtensionNames = extensionNames,
					};

					Check(xr.CreateInstance(in createInfo, ref instance), "xrCreateInstance");
				}

				var instanceProperties = new InstanceProperties { Type = StructureType.InstanceProperties };
				Check(xr.GetInstanceProperties(instance, ref instanceProperties), "xrGetInstanceProperties");
				var runtimeName = ReadString(instanceProperties.RuntimeName, 128);

				var systemInfo = new SystemGetInfo { Type = StructureType.SystemGetInfo, FormFactor = FormFactor.HeadMountedDisplay };
				ulong systemId = 0;
				Check(xr.GetSystem(instance, in systemInfo, ref systemId), "xrGetSystem (is the headset connected?)");

				var systemProperties = new SystemProperties { Type = StructureType.SystemProperties };
				Check(xr.GetSystemProperties(instance, systemId, ref systemProperties), "xrGetSystemProperties");
				var systemName = ReadString(systemProperties.SystemName, 256);

				var blendMode = ChooseBlendMode(xr, instance, systemId, settings.Passthrough);

				Console.WriteLine($"XR: Using runtime {runtimeName} with {systemName}, environment blend mode {blendMode}");
				return new OpenXrDevice(xr, instance, systemId, settings, blendMode, runtimeName, systemName);
			}
			catch (Exception e)
			{
				Log.Write("graphics", "Failed to initialize OpenXR. XR mode is disabled.");
				Log.Write("graphics", e);
				Console.WriteLine($"XR: {e.Message} Falling back to desktop mode.");

				if (instance.Handle != 0)
					xr.DestroyInstance(instance);

				xr.Dispose();
				return null;
			}
		}

		static EnvironmentBlendMode ChooseBlendMode(XR xr, Instance instance, ulong systemId, XrPassthroughMode mode)
		{
			uint count = 0;
			const ViewConfigurationType ViewConfig = ViewConfigurationType.PrimaryStereo;
			Check(xr.EnumerateEnvironmentBlendModes(instance, systemId, ViewConfig, 0, ref count, null), "xrEnumerateEnvironmentBlendModes");
			var modes = new EnvironmentBlendMode[count];
			fixed (EnvironmentBlendMode* modesPtr = modes)
				Check(xr.EnumerateEnvironmentBlendModes(instance, systemId, ViewConfig, count, ref count, modesPtr), "xrEnumerateEnvironmentBlendModes");

			var supportsAlpha = Array.IndexOf(modes, EnvironmentBlendMode.AlphaBlend) >= 0;
			if ((mode == XrPassthroughMode.Auto || mode == XrPassthroughMode.AlphaBlend) && supportsAlpha)
				return EnvironmentBlendMode.AlphaBlend;

			if (mode == XrPassthroughMode.AlphaBlend)
				Console.WriteLine("XR: The runtime does not support alpha-blended passthrough.");

			// The first enumerated mode is the runtime's preferred mode
			if (Array.IndexOf(modes, EnvironmentBlendMode.Opaque) >= 0)
				return EnvironmentBlendMode.Opaque;

			return modes.Length > 0 ? modes[0] : EnvironmentBlendMode.Opaque;
		}

		/// <summary>Creates the session. The game's OpenGL context must be current on the calling thread.</summary>
		public void InitializeSession()
		{
			// Extension functions are not exported by the loader and must be resolved through the instance
			if (!xr.TryGetInstanceExtension<KhrOpenglEnable>(null, instance, out var glExtension))
				throw new NotSupportedException($"Failed to load {KhrOpenglEnable.ExtensionName}.");

			var requirements = new GraphicsRequirementsOpenGLKHR { Type = StructureType.GraphicsRequirementsOpenglKhr };
			Check(glExtension.GetOpenGlgraphicsRequirements(instance, systemId, ref requirements), "xrGetOpenGLGraphicsRequirementsKHR");

			var sessionInfo = new SessionCreateInfo { Type = StructureType.SessionCreateInfo, SystemId = systemId };
			if (Platform.CurrentPlatform == PlatformType.Windows)
			{
				var binding = new GraphicsBindingOpenGLWin32KHR
				{
					Type = StructureType.GraphicsBindingOpenglWin32Khr,
					HDC = NativeGL.WglGetCurrentDC(),
					HGlrc = NativeGL.WglGetCurrentContext(),
				};

				if (binding.HDC == IntPtr.Zero || binding.HGlrc == IntPtr.Zero)
					throw new InvalidOperationException("No current WGL context.");

				sessionInfo.Next = &binding;
				Check(xr.CreateSession(instance, in sessionInfo, ref session), "xrCreateSession");
			}
			else if (Platform.CurrentPlatform == PlatformType.Linux)
			{
				var glx = NativeGL.GetCurrentGlx();
				var binding = new GraphicsBindingOpenGLXlibKHR
				{
					Type = StructureType.GraphicsBindingOpenglXlibKhr,
					XDisplay = (IntPtr*)glx.Display,
					Visualid = glx.VisualId,
					GlxFbconfig = glx.FBConfig,
					GlxDrawable = glx.Drawable,
					GlxContext = glx.Context,
				};

				sessionInfo.Next = &binding;
				Check(xr.CreateSession(instance, in sessionInfo, ref session), "xrCreateSession");
			}
			else
				throw new PlatformNotSupportedException("XR mode is only supported on Windows and Linux.");

			stageSpace = CreateReferenceSpace(ReferenceSpaceType.Stage) ?? CreateReferenceSpace(ReferenceSpaceType.Local)
				?? throw new InvalidOperationException("Failed to create a reference space.");
			viewSpace = CreateReferenceSpace(ReferenceSpaceType.View) ?? default;

			swapchainFormat = ChooseSwapchainFormat();

			OpenGL.glGenFramebuffers(1, out copyFramebuffer);
			OpenGL.CheckGLError();

			CreateActions();
		}

		Space? CreateReferenceSpace(ReferenceSpaceType type)
		{
			var info = new ReferenceSpaceCreateInfo
			{
				Type = StructureType.ReferenceSpaceCreateInfo,
				ReferenceSpaceType = type,
				PoseInReferenceSpace = new Posef(new Quaternionf(0, 0, 0, 1), new Vector3f(0, 0, 0)),
			};

			var space = default(Space);
			return xr.CreateReferenceSpace(session, in info, ref space) >= 0 ? space : null;
		}

		long ChooseSwapchainFormat()
		{
			uint count = 0;
			Check(xr.EnumerateSwapchainFormats(session, 0, ref count, null), "xrEnumerateSwapchainFormats");
			var formats = new long[count];
			fixed (long* formatsPtr = formats)
				Check(xr.EnumerateSwapchainFormats(session, count, ref count, formatsPtr), "xrEnumerateSwapchainFormats");

			// The game renders sRGB-encoded colors into a linear buffer. Copying these bytes into an sRGB
			// swapchain (with GL_FRAMEBUFFER_SRGB disabled) makes the compositor display them correctly.
			// SteamVR also misinterprets linear swapchains as sRGB, so this is the most reliable choice.
			foreach (var preferred in new long[] { GlSrgb8Alpha8, GlRgba8 })
				if (Array.IndexOf(formats, preferred) >= 0)
					return preferred;

			if (formats.Length == 0)
				throw new InvalidOperationException("The runtime does not support any swapchain formats.");

			return formats[0];
		}

		void CreateActions()
		{
			var setInfo = new ActionSetCreateInfo { Type = StructureType.ActionSetCreateInfo, Priority = 0 };
			WriteString(setInfo.ActionSetName, 64, "gameplay");
			WriteString(setInfo.LocalizedActionSetName, 128, "Gameplay");
			Check(xr.CreateActionSet(instance, in setInfo, ref actionSet), "xrCreateActionSet");

			hands[0].Path = StringToPath("/user/hand/left");
			hands[1].Path = StringToPath("/user/hand/right");

			aimAction = CreateAction("aim", "Aim Pointer", ActionType.PoseInput);
			gripAction = CreateAction("grip", "Grip", ActionType.PoseInput);
			selectAction = CreateAction("select", "Select / Left Click", ActionType.BooleanInput);
			orderAction = CreateAction("order", "Order / Right Click", ActionType.BooleanInput);
			grabAction = CreateAction("grab", "Grab Board", ActionType.BooleanInput);
			menuAction = CreateAction("menu", "Game Menu", ActionType.BooleanInput);
			buttonAAction = CreateAction("button_a", "Primary Button", ActionType.BooleanInput);
			buttonBAction = CreateAction("button_b", "Secondary Button", ActionType.BooleanInput);
			stickAction = CreateAction("stick", "Pan / Zoom", ActionType.Vector2fInput);
			stickClickAction = CreateAction("stick_click", "Hotkey Menu", ActionType.BooleanInput);
			hapticAction = CreateAction("haptic", "Vibration", ActionType.VibrationOutput);

			// Oculus Touch is what Virtual Desktop, Steam Link and ALVR present to SteamVR for Quest headsets
			SuggestBindings("/interaction_profiles/oculus/touch_controller", [
				(aimAction, "/user/hand/left/input/aim/pose"), (aimAction, "/user/hand/right/input/aim/pose"),
				(gripAction, "/user/hand/left/input/grip/pose"), (gripAction, "/user/hand/right/input/grip/pose"),
				(selectAction, "/user/hand/left/input/trigger/value"), (selectAction, "/user/hand/right/input/trigger/value"),
				(grabAction, "/user/hand/left/input/squeeze/value"), (grabAction, "/user/hand/right/input/squeeze/value"),
				(orderAction, "/user/hand/left/input/x/click"), (orderAction, "/user/hand/right/input/a/click"),
				(buttonAAction, "/user/hand/left/input/x/click"), (buttonAAction, "/user/hand/right/input/a/click"),
				(buttonBAction, "/user/hand/left/input/y/click"), (buttonBAction, "/user/hand/right/input/b/click"),
				(menuAction, "/user/hand/left/input/menu/click"),
				(stickAction, "/user/hand/left/input/thumbstick"), (stickAction, "/user/hand/right/input/thumbstick"),
				(stickClickAction, "/user/hand/left/input/thumbstick/click"), (stickClickAction, "/user/hand/right/input/thumbstick/click"),
				(hapticAction, "/user/hand/left/output/haptic"), (hapticAction, "/user/hand/right/output/haptic"),
			]);

			SuggestBindings("/interaction_profiles/valve/index_controller", [
				(aimAction, "/user/hand/left/input/aim/pose"), (aimAction, "/user/hand/right/input/aim/pose"),
				(gripAction, "/user/hand/left/input/grip/pose"), (gripAction, "/user/hand/right/input/grip/pose"),
				(selectAction, "/user/hand/left/input/trigger/click"), (selectAction, "/user/hand/right/input/trigger/click"),
				(grabAction, "/user/hand/left/input/squeeze/value"), (grabAction, "/user/hand/right/input/squeeze/value"),
				(orderAction, "/user/hand/left/input/a/click"), (orderAction, "/user/hand/right/input/a/click"),
				(buttonAAction, "/user/hand/left/input/a/click"), (buttonAAction, "/user/hand/right/input/a/click"),
				(buttonBAction, "/user/hand/left/input/b/click"), (buttonBAction, "/user/hand/right/input/b/click"),
				(menuAction, "/user/hand/left/input/system/click"),
				(stickAction, "/user/hand/left/input/thumbstick"), (stickAction, "/user/hand/right/input/thumbstick"),
				(stickClickAction, "/user/hand/left/input/thumbstick/click"), (stickClickAction, "/user/hand/right/input/thumbstick/click"),
				(hapticAction, "/user/hand/left/output/haptic"), (hapticAction, "/user/hand/right/output/haptic"),
			]);

			SuggestBindings("/interaction_profiles/htc/vive_controller", [
				(aimAction, "/user/hand/left/input/aim/pose"), (aimAction, "/user/hand/right/input/aim/pose"),
				(gripAction, "/user/hand/left/input/grip/pose"), (gripAction, "/user/hand/right/input/grip/pose"),
				(selectAction, "/user/hand/left/input/trigger/click"), (selectAction, "/user/hand/right/input/trigger/click"),
				(grabAction, "/user/hand/left/input/squeeze/click"), (grabAction, "/user/hand/right/input/squeeze/click"),
				(orderAction, "/user/hand/right/input/trackpad/click"), (stickClickAction, "/user/hand/left/input/trackpad/click"),
				(menuAction, "/user/hand/left/input/menu/click"),
				(stickAction, "/user/hand/left/input/trackpad"), (stickAction, "/user/hand/right/input/trackpad"),
				(hapticAction, "/user/hand/left/output/haptic"), (hapticAction, "/user/hand/right/output/haptic"),
			]);

			SuggestBindings("/interaction_profiles/khr/simple_controller", [
				(aimAction, "/user/hand/left/input/aim/pose"), (aimAction, "/user/hand/right/input/aim/pose"),
				(gripAction, "/user/hand/left/input/grip/pose"), (gripAction, "/user/hand/right/input/grip/pose"),
				(selectAction, "/user/hand/left/input/select/click"), (selectAction, "/user/hand/right/input/select/click"),
				(menuAction, "/user/hand/left/input/menu/click"), (orderAction, "/user/hand/right/input/menu/click"),
				(hapticAction, "/user/hand/left/output/haptic"), (hapticAction, "/user/hand/right/output/haptic"),
			]);

			foreach (var hand in hands)
			{
				hand.AimSpace = CreateActionSpace(aimAction, hand.Path);
				hand.GripSpace = CreateActionSpace(gripAction, hand.Path);
			}

			var set = actionSet;
			var attachInfo = new SessionActionSetsAttachInfo { Type = StructureType.SessionActionSetsAttachInfo, CountActionSets = 1, ActionSets = &set };
			Check(xr.AttachSessionActionSets(session, in attachInfo), "xrAttachSessionActionSets");
		}

		XrAction CreateAction(string name, string localizedName, ActionType type)
		{
			var subactionPaths = stackalloc ulong[2] { hands[0].Path, hands[1].Path };
			var info = new ActionCreateInfo
			{
				Type = StructureType.ActionCreateInfo,
				ActionType = type,
				CountSubactionPaths = 2,
				SubactionPaths = subactionPaths,
			};

			WriteString(info.ActionName, 64, name);
			WriteString(info.LocalizedActionName, 128, localizedName);

			var action = default(XrAction);
			Check(xr.CreateAction(actionSet, in info, ref action), $"xrCreateAction({name})");
			return action;
		}

		void SuggestBindings(string profile, (XrAction Action, string Path)[] bindings)
		{
			var suggested = new ActionSuggestedBinding[bindings.Length];
			for (var i = 0; i < bindings.Length; i++)
				suggested[i] = new ActionSuggestedBinding(bindings[i].Action, StringToPath(bindings[i].Path));

			fixed (ActionSuggestedBinding* suggestedPtr = suggested)
			{
				var info = new InteractionProfileSuggestedBinding
				{
					Type = StructureType.InteractionProfileSuggestedBinding,
					InteractionProfile = StringToPath(profile),
					CountSuggestedBindings = (uint)suggested.Length,
					SuggestedBindings = suggestedPtr,
				};

				// Unsupported profiles are not fatal: the runtime will pick one of the others
				var result = xr.SuggestInteractionProfileBinding(instance, in info);
				if (result < 0)
					Log.Write("graphics", $"XR: Binding suggestion for {profile} was rejected ({result}).");
			}
		}

		Space CreateActionSpace(XrAction action, ulong subactionPath)
		{
			var info = new ActionSpaceCreateInfo
			{
				Type = StructureType.ActionSpaceCreateInfo,
				Action = action,
				SubactionPath = subactionPath,
				PoseInActionSpace = new Posef(new Quaternionf(0, 0, 0, 1), new Vector3f(0, 0, 0)),
			};

			var space = default(Space);
			Check(xr.CreateActionSpace(session, in info, ref space), "xrCreateActionSpace");
			return space;
		}

		public XrHandState GetHand(XrHand hand) => hands[(int)hand].State;

		public void BeginFrame()
		{
			if (frameBegun)
				throw new InvalidOperationException("XR BeginFrame called twice without EndFrame.");

			PollEvents();
			if (!IsSessionRunning)
				return;

			var waitInfo = new FrameWaitInfo { Type = StructureType.FrameWaitInfo };
			frameState = new FrameState { Type = StructureType.FrameState };
			Check(xr.WaitFrame(session, in waitInfo, ref frameState), "xrWaitFrame");

			var beginInfo = new FrameBeginInfo { Type = StructureType.FrameBeginInfo };
			Check(xr.BeginFrame(session, in beginInfo), "xrBeginFrame");
			frameBegun = true;

			UpdateInput(frameState.PredictedDisplayTime);
		}

		void PollEvents()
		{
			while (true)
			{
				var buffer = new EventDataBuffer { Type = StructureType.EventDataBuffer };
				var result = xr.PollEvent(instance, ref buffer);
				if (result == Result.EventUnavailable)
					break;

				Check(result, "xrPollEvent");

				switch (buffer.Type)
				{
					case StructureType.EventDataInstanceLossPending:
						ExitRequested = true;
						break;

					case StructureType.EventDataSessionStateChanged:
					{
						var changed = *(EventDataSessionStateChanged*)&buffer;
						sessionState = changed.State;
						Console.WriteLine($"XR: Session state changed to {sessionState}");

						switch (sessionState)
						{
							case SessionState.Ready:
							{
								var beginInfo = new SessionBeginInfo
								{
									Type = StructureType.SessionBeginInfo,
									PrimaryViewConfigurationType = ViewConfigurationType.PrimaryStereo
								};

								Check(xr.BeginSession(session, in beginInfo), "xrBeginSession");
								IsSessionRunning = true;
								break;
							}

							case SessionState.Stopping:
								IsSessionRunning = false;
								Check(xr.EndSession(session), "xrEndSession");
								break;

							case SessionState.Exiting:
							case SessionState.LossPending:
								IsSessionRunning = false;
								ExitRequested = true;
								break;
						}

						break;
					}
				}
			}
		}

		void UpdateInput(long time)
		{
			if (viewSpace.Handle != 0)
				HeadPoseValid = TryLocate(viewSpace, time, out headPose);

			var activeSet = new ActiveActionSet(actionSet, 0);
			var syncInfo = new ActionsSyncInfo { Type = StructureType.ActionsSyncInfo, CountActiveActionSets = 1, ActiveActionSets = &activeSet };
			var syncResult = xr.SyncAction(session, in syncInfo);

			foreach (var hand in hands)
			{
				ref var s = ref hand.State;
				if (syncResult != Result.Success)
				{
					// Not focused (e.g. the SteamVR dashboard is open): release everything
					s = default;
					continue;
				}

				s.IsActive = TryLocate(hand.AimSpace, time, out s.Aim);
				TryLocate(hand.GripSpace, time, out s.Grip);
				s.Select = GetBool(selectAction, hand.Path);
				s.Order = GetBool(orderAction, hand.Path);
				s.Grab = GetBool(grabAction, hand.Path);
				s.Menu = GetBool(menuAction, hand.Path);
				s.ButtonA = GetBool(buttonAAction, hand.Path);
				s.ButtonB = GetBool(buttonBAction, hand.Path);
				s.StickClick = GetBool(stickClickAction, hand.Path);
				s.Stick = GetVector2(stickAction, hand.Path);
			}
		}

		bool TryLocate(Space space, long time, out XrPose pose)
		{
			var location = new SpaceLocation { Type = StructureType.SpaceLocation };
			if (xr.LocateSpace(space, stageSpace, time, ref location) >= 0)
			{
				const SpaceLocationFlags Valid = SpaceLocationFlags.PositionValidBit | SpaceLocationFlags.OrientationValidBit;
				if ((location.LocationFlags & Valid) == Valid)
				{
					pose = ToPose(location.Pose);
					return true;
				}
			}

			pose = XrPose.Identity;
			return false;
		}

		bool GetBool(XrAction action, ulong subactionPath)
		{
			var info = new ActionStateGetInfo { Type = StructureType.ActionStateGetInfo, Action = action, SubactionPath = subactionPath };
			var state = new ActionStateBoolean { Type = StructureType.ActionStateBoolean };
			return xr.GetActionStateBoolean(session, in info, ref state) >= 0 && state.IsActive != 0 && state.CurrentState != 0;
		}

		Vector2 GetVector2(XrAction action, ulong subactionPath)
		{
			var info = new ActionStateGetInfo { Type = StructureType.ActionStateGetInfo, Action = action, SubactionPath = subactionPath };
			var state = new ActionStateVector2f { Type = StructureType.ActionStateVector2f };
			if (xr.GetActionStateVector2(session, in info, ref state) < 0 || state.IsActive == 0)
				return Vector2.Zero;

			return new Vector2(state.CurrentState.X, state.CurrentState.Y);
		}

		public void Vibrate(XrHand hand, float amplitude, float seconds)
		{
			if (!IsFocused || !settings.Haptics)
				return;

			var info = new HapticActionInfo { Type = StructureType.HapticActionInfo, Action = hapticAction, SubactionPath = hands[(int)hand].Path };
			var vibration = new HapticVibration
			{
				Type = StructureType.HapticVibration,
				Amplitude = Math.Clamp(amplitude, 0, 1),
				Duration = (long)(seconds * 1e9),
				Frequency = 0,
			};

			xr.ApplyHapticFeedback(session, in info, (HapticBaseHeader*)&vibration);
		}

		public void EndFrame(IFrameBuffer screen, IReadOnlyList<XrQuad> quads)
		{
			if (!frameBegun)
				return;

			frameBegun = false;

			var quadLayers = stackalloc CompositionLayerQuad[Math.Max(quads.Count, 1)];
			var layers = stackalloc CompositionLayerBaseHeader*[quads.Count + 1];
			var projectionViews = stackalloc CompositionLayerProjectionView[2];
			var projectionLayer = new CompositionLayerProjection { Type = StructureType.CompositionLayerProjection };
			uint layerCount = 0;

			if (frameState.ShouldRender != 0)
			{
				if (ChromaKeyEnabled && TryBuildBackgroundLayer(projectionViews, ref projectionLayer))
					layers[layerCount++] = (CompositionLayerBaseHeader*)&projectionLayer;

				var source = (FrameBuffer)screen;
				foreach (var q in quadSwapchains.Values)
					q.UsedThisFrame = false;

				for (var i = 0; i < quads.Count; i++)
				{
					var quad = quads[i];
					Swapchain handle;
					Size imageSize;
					if (quad.Fill is Color fill)
					{
						handle = GetSolidSwapchain(fill);
						imageSize = new Size(SolidSwapchainSize, SolidSwapchainSize);
					}
					else
					{
						if (quad.Source.Width <= 0 || quad.Source.Height <= 0)
							continue;

						var swapchain = GetQuadSwapchain(quad.Name, quad.Source.Size);
						swapchain.UsedThisFrame = true;
						CopyToSwapchain(source, quad.Source, swapchain);
						handle = swapchain.Handle;
						imageSize = swapchain.Size;
					}

					quadLayers[i] = new CompositionLayerQuad
					{
						Type = StructureType.CompositionLayerQuad,
						LayerFlags = CompositionLayerFlags.None,
						Space = stageSpace,
						EyeVisibility = EyeVisibility.Both,
						SubImage = new SwapchainSubImage(handle, new Rect2Di(new Offset2Di(0, 0), new Extent2Di(imageSize.Width, imageSize.Height)), 0),
						Pose = ToPosef(quad.Pose),
						Size = new Extent2Df(quad.Size.X, quad.Size.Y),
					};

					layers[layerCount++] = (CompositionLayerBaseHeader*)&quadLayers[i];
				}

				// Remove swapchains for panels that are no longer shown so they don't use memory
				List<string> unused = null;
				foreach (var kv in quadSwapchains)
					if (!kv.Value.UsedThisFrame)
						(unused ??= []).Add(kv.Key);

				if (unused != null)
				{
					foreach (var name in unused)
					{
						xr.DestroySwapchain(quadSwapchains[name].Handle);
						quadSwapchains.Remove(name);
					}
				}
			}

			var endInfo = new FrameEndInfo
			{
				Type = StructureType.FrameEndInfo,
				DisplayTime = frameState.PredictedDisplayTime,
				EnvironmentBlendMode = environmentBlendMode,
				LayerCount = layerCount,
				Layers = layers,
			};

			Check(xr.EndFrame(session, in endInfo), "xrEndFrame");
		}

		Swapchain GetSolidSwapchain(Color color)
		{
			if (!solidSwapchains.TryGetValue(color, out var swapchain))
				solidSwapchains[color] = swapchain = CreateSolidSwapchain(color);

			return swapchain;
		}

		/// <summary>Creates a small static swapchain filled with a solid color, for backgrounds and pointer beams.</summary>
		Swapchain CreateSolidSwapchain(Color color)
		{
			var swapchain = CreateSwapchain(new Size(SolidSwapchainSize, SolidSwapchainSize), SwapchainCreateFlags.StaticImageBit, out var images);
			var index = AcquireImage(swapchain);

			// Save the viewport so the game's own rendering state is left untouched
			var viewport = new int[4];
			OpenGL.glGetIntegerv(OpenGL.GL_VIEWPORT, out viewport[0]);

			OpenGL.glBindFramebuffer(OpenGL.GL_FRAMEBUFFER, copyFramebuffer);
			OpenGL.glFramebufferTexture2D(OpenGL.GL_FRAMEBUFFER, OpenGL.GL_COLOR_ATTACHMENT0, OpenGL.GL_TEXTURE_2D, images[index], 0);
			OpenGL.glDisable(OpenGL.GL_SCISSOR_TEST);
			OpenGL.glViewport(0, 0, SolidSwapchainSize, SolidSwapchainSize);
			OpenGL.glClearColor(color.R / 255f, color.G / 255f, color.B / 255f, 1f);
			OpenGL.glClear(OpenGL.GL_COLOR_BUFFER_BIT);
			OpenGL.glFramebufferTexture2D(OpenGL.GL_FRAMEBUFFER, OpenGL.GL_COLOR_ATTACHMENT0, OpenGL.GL_TEXTURE_2D, 0, 0);
			OpenGL.glBindFramebuffer(OpenGL.GL_FRAMEBUFFER, 0);
			OpenGL.glViewport(viewport[0], viewport[1], viewport[2], viewport[3]);
			OpenGL.CheckGLError();

			ReleaseImage(swapchain);
			return swapchain;
		}

		QuadSwapchain GetQuadSwapchain(string name, Size size)
		{
			if (quadSwapchains.TryGetValue(name, out var existing))
			{
				if (existing.Size == size)
					return existing;

				xr.DestroySwapchain(existing.Handle);
				quadSwapchains.Remove(name);
			}

			var swapchain = new QuadSwapchain
			{
				Handle = CreateSwapchain(size, SwapchainCreateFlags.None, out var images),
				Size = size,
				Images = images,
			};

			quadSwapchains[name] = swapchain;
			return swapchain;
		}

		Swapchain CreateSwapchain(Size size, SwapchainCreateFlags flags, out uint[] images)
		{
			var info = new SwapchainCreateInfo
			{
				Type = StructureType.SwapchainCreateInfo,
				CreateFlags = flags,
				UsageFlags = SwapchainUsageFlags.ColorAttachmentBit | SwapchainUsageFlags.TransferDstBit | SwapchainUsageFlags.SampledBit,
				Format = swapchainFormat,
				SampleCount = 1,
				Width = (uint)size.Width,
				Height = (uint)size.Height,
				FaceCount = 1,
				ArraySize = 1,
				MipCount = 1,
			};

			var swapchain = default(Swapchain);
			Check(xr.CreateSwapchain(session, in info, ref swapchain), "xrCreateSwapchain");

			uint count = 0;
			Check(xr.EnumerateSwapchainImages(swapchain, 0, ref count, null), "xrEnumerateSwapchainImages");
			var glImages = new SwapchainImageOpenGLKHR[count];
			for (var i = 0; i < count; i++)
				glImages[i].Type = StructureType.SwapchainImageOpenglKhr;

			fixed (SwapchainImageOpenGLKHR* glImagesPtr = glImages)
				Check(xr.EnumerateSwapchainImages(swapchain, count, ref count, (SwapchainImageBaseHeader*)glImagesPtr), "xrEnumerateSwapchainImages");

			images = new uint[count];
			for (var i = 0; i < count; i++)
				images[i] = glImages[i].Image;

			return swapchain;
		}

		uint AcquireImage(Swapchain swapchain)
		{
			var acquireInfo = new SwapchainImageAcquireInfo { Type = StructureType.SwapchainImageAcquireInfo };
			uint index = 0;
			Check(xr.AcquireSwapchainImage(swapchain, in acquireInfo, ref index), "xrAcquireSwapchainImage");

			var waitInfo = new SwapchainImageWaitInfo { Type = StructureType.SwapchainImageWaitInfo, Timeout = long.MaxValue };
			Check(xr.WaitSwapchainImage(swapchain, in waitInfo), "xrWaitSwapchainImage");
			return index;
		}

		void ReleaseImage(Swapchain swapchain)
		{
			var releaseInfo = new SwapchainImageReleaseInfo { Type = StructureType.SwapchainImageReleaseInfo };
			Check(xr.ReleaseSwapchainImage(swapchain, in releaseInfo), "xrReleaseSwapchainImage");
		}

		void CopyToSwapchain(FrameBuffer source, Rectangle sourceRect, QuadSwapchain target)
		{
			var index = AcquireImage(target.Handle);

			OpenGL.glDisable(OpenGL.GL_SCISSOR_TEST);
			OpenGL.glBindFramebuffer(OpenGL.GL_READ_FRAMEBUFFER, source.ID);
			OpenGL.glBindFramebuffer(OpenGL.GL_DRAW_FRAMEBUFFER, copyFramebuffer);
			OpenGL.glFramebufferTexture2D(OpenGL.GL_DRAW_FRAMEBUFFER, OpenGL.GL_COLOR_ATTACHMENT0, OpenGL.GL_TEXTURE_2D, target.Images[index], 0);

			// The game's screen buffer stores the top row of the screen at texture row 0,
			// but OpenXR expects OpenGL swapchain images to have a bottom-left origin, so flip while copying.
			var w = sourceRect.Width;
			var h = sourceRect.Height;
			OpenGL.glBlitFramebuffer(
				sourceRect.Left, sourceRect.Top, sourceRect.Right, sourceRect.Bottom,
				0, h, w, 0,
				OpenGL.GL_COLOR_BUFFER_BIT, OpenGL.GL_NEAREST);

			OpenGL.glFramebufferTexture2D(OpenGL.GL_DRAW_FRAMEBUFFER, OpenGL.GL_COLOR_ATTACHMENT0, OpenGL.GL_TEXTURE_2D, 0, 0);
			OpenGL.glBindFramebuffer(OpenGL.GL_FRAMEBUFFER, 0);
			OpenGL.CheckGLError();

			ReleaseImage(target.Handle);
		}

		bool TryBuildBackgroundLayer(CompositionLayerProjectionView* views, ref CompositionLayerProjection layer)
		{
			// A tiny static swapchain filled with the key color, stretched over both eyes behind every other layer
			const int BackgroundSize = SolidSwapchainSize;
			if (backgroundSwapchain.Handle == 0)
				backgroundSwapchain = CreateSolidSwapchain(settings.KeyColor);

			var locateInfo = new ViewLocateInfo
			{
				Type = StructureType.ViewLocateInfo,
				ViewConfigurationType = ViewConfigurationType.PrimaryStereo,
				DisplayTime = frameState.PredictedDisplayTime,
				Space = stageSpace,
			};

			var viewState = new ViewState { Type = StructureType.ViewState };
			var located = stackalloc View[2];
			located[0].Type = located[1].Type = StructureType.View;
			uint count = 0;
			if (xr.LocateView(session, in locateInfo, ref viewState, 2, ref count, located) < 0 || count != 2)
				return false;

			for (var i = 0; i < 2; i++)
			{
				views[i] = new CompositionLayerProjectionView
				{
					Type = StructureType.CompositionLayerProjectionView,
					Pose = located[i].Pose,
					Fov = located[i].Fov,
					SubImage = new SwapchainSubImage(backgroundSwapchain, new Rect2Di(new Offset2Di(0, 0), new Extent2Di(BackgroundSize, BackgroundSize)), 0),
				};
			}

			layer.Space = stageSpace;
			layer.ViewCount = 2;
			layer.Views = views;
			return true;
		}

		ulong StringToPath(string path)
		{
			ulong result = 0;
			var bytes = Utf8(path);
			fixed (byte* bytesPtr = bytes)
				Check(xr.StringToPath(instance, bytesPtr, ref result), $"xrStringToPath({path})");

			return result;
		}

		static bool HasExtension(XR xr, string name)
		{
			uint count = 0;
			Check(xr.EnumerateInstanceExtensionProperties((byte*)null, 0, ref count, null), "xrEnumerateInstanceExtensionProperties");
			var properties = new ExtensionProperties[count];
			for (var i = 0; i < count; i++)
				properties[i].Type = StructureType.ExtensionProperties;

			fixed (ExtensionProperties* propertiesPtr = properties)
			{
				Check(xr.EnumerateInstanceExtensionProperties((byte*)null, count, ref count, propertiesPtr), "xrEnumerateInstanceExtensionProperties");
				for (var i = 0; i < count; i++)
					if (ReadString(propertiesPtr[i].ExtensionName, 128) == name)
						return true;
			}

			return false;
		}

		static XrPose ToPose(Posef p) => new(
			new Vector3(p.Position.X, p.Position.Y, p.Position.Z),
			new Quaternion(p.Orientation.X, p.Orientation.Y, p.Orientation.Z, p.Orientation.W));

		static Posef ToPosef(XrPose p)
		{
			var q = Quaternion.Normalize(p.Orientation);
			return new Posef(new Quaternionf(q.X, q.Y, q.Z, q.W), new Vector3f(p.Position.X, p.Position.Y, p.Position.Z));
		}

		static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s + '\0');

		static void WriteString(byte* dest, int capacity, string value)
		{
			var bytes = Encoding.UTF8.GetBytes(value);
			var length = Math.Min(bytes.Length, capacity - 1);
			for (var i = 0; i < length; i++)
				dest[i] = bytes[i];

			dest[length] = 0;
		}

		static string ReadString(byte* src, int capacity)
		{
			var length = 0;
			while (length < capacity && src[length] != 0)
				length++;

			return Encoding.UTF8.GetString(src, length);
		}

		static void Check(Result result, string what)
		{
			if (result < 0)
				throw new InvalidOperationException($"{what} failed: {result}.");
		}

		public void Dispose()
		{
			foreach (var q in quadSwapchains.Values)
				xr.DestroySwapchain(q.Handle);

			quadSwapchains.Clear();

			foreach (var swapchain in solidSwapchains.Values)
				xr.DestroySwapchain(swapchain);

			solidSwapchains.Clear();

			if (backgroundSwapchain.Handle != 0)
				xr.DestroySwapchain(backgroundSwapchain);

			if (copyFramebuffer != 0)
				OpenGL.glDeleteFramebuffers(1, ref copyFramebuffer);

			if (session.Handle != 0)
				xr.DestroySession(session);

			if (instance.Handle != 0)
				xr.DestroyInstance(instance);

			xr.Dispose();
		}
	}
}
