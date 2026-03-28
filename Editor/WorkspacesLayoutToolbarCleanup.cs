using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;

namespace WillGoldstone.Workspaces
{
    /// <summary>
    /// Older Workspaces builds serialized Main Toolbar overlay slots (W01–W20, Prefab1–Prefab20, etc.) into .wlt files.
    /// Unity’s Add (+) menu lists those ids even after toolbar registrations change — we strip legacy overlay entries from saved layouts.
    /// </summary>
    internal static class WorkspacesLayoutToolbarCleanup
    {
        private static readonly Regex LegacyW = new Regex(@"^Workspaces/W\d+$", RegexOptions.Compiled);
        private static readonly Regex LegacyPrefabNumbered = new Regex(@"^Workspaces/Prefab\d+$", RegexOptions.Compiled);

        internal static bool IsLegacyMainToolbarOverlayId(string idLine)
        {
            if (string.IsNullOrEmpty(idLine) || !idLine.TrimStart().StartsWith("id: "))
                return false;

            var id = idLine.Substring(idLine.IndexOf("id: ", StringComparison.Ordinal) + 4).Trim();
            if (id == "Workspaces/Home" || id == "Workspaces/Settings")
                return false;
            if (LegacyW.IsMatch(id))
                return true;
            if (LegacyPrefabNumbered.IsMatch(id))
                return true;
            if (id == "Workspaces/PrefabWorkspaces" || id == "Workspaces/Prefab" || id == "Workspaces/Layout")
                return true;
            return false;
        }

        /// <summary>
        /// Removes overlay-toolbar entries whose <c>id</c> is from a previous MainToolbarElement scheme.
        /// </summary>
        internal static int StripLegacyMainToolbarOverlayEntries(string absoluteLayoutPath)
        {
            if (string.IsNullOrEmpty(absoluteLayoutPath) || !File.Exists(absoluteLayoutPath))
                return 0;

            var lines = File.ReadAllLines(absoluteLayoutPath);
            var output = new List<string>(lines.Length);
            var removedLines = 0;

            for (var i = 0; i < lines.Length;)
            {
                if (lines[i].StartsWith("    - dockPosition:", StringComparison.Ordinal))
                {
                    var block = new List<string>();
                    string idLine = null;
                    while (i < lines.Length)
                    {
                        var line = lines[i];
                        block.Add(line);
                        if (line.StartsWith("      id: ", StringComparison.Ordinal))
                            idLine = line;
                        i++;
                        if (line.StartsWith("      sizeOverridden: ", StringComparison.Ordinal))
                            break;
                    }

                    if (IsLegacyMainToolbarOverlayId(idLine))
                    {
                        removedLines += block.Count;
                        continue;
                    }

                    output.AddRange(block);
                    continue;
                }

                output.Add(lines[i]);
                i++;
            }

            if (removedLines == 0)
                return 0;

            File.WriteAllLines(absoluteLayoutPath, output);
            return removedLines;
        }

        internal static void StripLegacyAfterLayoutSave(string layoutPath)
        {
            if (string.IsNullOrEmpty(layoutPath))
                return;

            var absolutePath = Path.IsPathRooted(layoutPath)
                ? layoutPath
                : Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, "..", layoutPath));

            var n = StripLegacyMainToolbarOverlayEntries(absolutePath);
            if (n > 0 && layoutPath.StartsWith("Assets/", StringComparison.Ordinal))
                AssetDatabase.ImportAsset(layoutPath, ImportAssetOptions.ForceUpdate);
        }
    }

    [InitializeOnLoad]
    internal static class WorkspacesLayoutToolbarCleanupBoot
    {
        static WorkspacesLayoutToolbarCleanupBoot()
        {
            EditorApplication.delayCall += MigrateLayoutFilesOnce;
        }

        private static void MigrateLayoutFilesOnce()
        {
            const string sessionKey = "WillGoldstone.Workspaces.LegacyToolbarOverlayStrip_v1";
            if (SessionState.GetBool(sessionKey, false))
                return;

            try
            {
                var dir = Path.Combine(UnityEngine.Application.dataPath, "Workspaces", "Layouts");
                if (!Directory.Exists(dir))
                {
                    SessionState.SetBool(sessionKey, true);
                    return;
                }

                var total = 0;
                foreach (var path in Directory.GetFiles(dir, "*.wlt"))
                {
                    total += WorkspacesLayoutToolbarCleanup.StripLegacyMainToolbarOverlayEntries(path);
                }

                if (total > 0)
                    AssetDatabase.Refresh();
            }
            finally
            {
                SessionState.SetBool(sessionKey, true);
            }
        }
    }
}
