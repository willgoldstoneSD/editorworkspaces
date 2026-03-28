using System.Collections.Generic;
using System.Text;

namespace WillGoldstone.Workspaces
{
    /// <summary>
    /// Stable toolbar registration paths shared by <see cref="WorkspacesToolbarCodeGenerator"/> and <see cref="WorkspacesToolbar.RefreshToolbar"/>.
    /// Unity’s Main Toolbar Add (+) submenu label is the last path segment — we use the workspace Display Name, with disambiguation only when needed.
    /// </summary>
    internal static class WorkspacesToolbarPaths
    {
        internal static string SanitizeDisplayNameForPathSegment(string displayName)
        {
            if (string.IsNullOrEmpty(displayName))
                return "Workspace";

            var sb = new StringBuilder(displayName.Length);
            foreach (var c in displayName)
            {
                if (char.IsLetterOrDigit(c))
                    sb.Append(c);
                else if (c == ' ' || c == '_' || c == '-')
                    sb.Append('_');
            }

            var s = sb.ToString();
            if (s.Length > 48)
                s = s.Substring(0, 48);
            if (string.IsNullOrEmpty(s))
                return "Workspace";
            return s;
        }

        /// <summary>
        /// Unique last segment for <c>Workspaces/{segment}</c>, matching codegen order in <see cref="WorkspacesSettings.Workspaces"/>.
        /// </summary>
        internal static string AllocateUniquePathSegment(WorkspaceDefinition def, HashSet<string> usedSegments)
        {
            var baseName = SanitizeDisplayNameForPathSegment(def.DisplayName);
            var candidate = baseName;
            if (!usedSegments.Contains(candidate))
            {
                usedSegments.Add(candidate);
                return candidate;
            }

            var idShort = def.Id.Length >= 8 ? def.Id.Substring(0, 8) : def.Id;
            candidate = $"{baseName}_{idShort}";
            if (!usedSegments.Contains(candidate))
            {
                usedSegments.Add(candidate);
                return candidate;
            }

            candidate = $"{baseName}_{def.Id}";
            usedSegments.Add(candidate);
            return candidate;
        }

        /// <summary>
        /// Path must match the generated <c>[MainToolbarElement("…")]</c> for this workspace (regenerate after renames).
        /// </summary>
        internal static string GetPrefabWorkspaceToolbarPath(WorkspaceDefinition def)
        {
            if (def == null || def.Id == "home")
                return null;

            WorkspacesSettings.instance.EnsureDefaults();
            var used = new HashSet<string>();
            foreach (var w in WorkspacesSettings.instance.Workspaces)
            {
                var segment = AllocateUniquePathSegment(w, used);
                if (w.Id == def.Id)
                    return $"Workspaces/{segment}";
            }

            return null;
        }
    }
}
