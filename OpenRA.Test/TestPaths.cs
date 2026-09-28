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
using System.IO;

namespace OpenRA.Test
{
	static class TestPaths
	{
		/// <summary>Returns the path of a settings file in a fresh temporary directory. The file itself does not exist yet.</summary>
		public static string TempSettingsFile()
		{
			var dir = Path.Combine(Path.GetTempPath(), "openra-test-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(dir);
			return Path.Combine(dir, "settings.yaml");
		}

		/// <summary>The repository root, found by walking up from the test binaries until the mods directory appears.</summary>
		public static string RepositoryRoot
		{
			get
			{
				var dir = new DirectoryInfo(AppContext.BaseDirectory);
				while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "mods")))
					dir = dir.Parent;

				return dir?.FullName ?? throw new DirectoryNotFoundException("Could not find the mods directory.");
			}
		}

		public static string ModFile(params string[] parts)
		{
			return Path.Combine([RepositoryRoot, "mods", .. parts]);
		}
	}
}
