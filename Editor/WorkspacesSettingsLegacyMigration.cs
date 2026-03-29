using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace WillGoldstone.Workspaces
{
    /// <summary>
    /// One-time copy from <c>ProjectWeasel.Workspaces.asset</c> into the current settings singleton,
    /// save to <c>WillGoldstone.Workspaces.asset</c>, then remove the legacy file.
    /// </summary>
    internal static class WorkspacesSettingsLegacyMigration
    {
        private const string LegacyProjectRelativePath = "ProjectSettings/ProjectWeasel.Workspaces.asset";

        private static string MigrationEditorPrefKey =>
            "WillGoldstone.Workspaces.LegacyAssetMigrated_v1." + Directory.GetParent(Application.dataPath)!.FullName;

        internal static void TryMigrateInto(WorkspacesSettings settings)
        {
            if (EditorPrefs.GetBool(MigrationEditorPrefKey, false))
                return;

            var projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
            var legacyFullPath = Path.Combine(projectRoot, "ProjectSettings", "ProjectWeasel.Workspaces.asset");

            if (!File.Exists(legacyFullPath))
            {
                EditorPrefs.SetBool(MigrationEditorPrefKey, true);
                return;
            }

            var legacyObjects = AssetDatabase.LoadAllAssetsAtPath(LegacyProjectRelativePath);
            var fromLegacy = legacyObjects.OfType<WorkspacesSettings>().FirstOrDefault();

            if (fromLegacy == null)
            {
                Debug.LogWarning(
                    "[Workspaces] Found ProjectWeasel.Workspaces.asset but could not load it as WorkspacesSettings (missing or mismatched script reference). " +
                    "Your settings are unchanged; you can remove or fix the legacy asset manually.");
                EditorPrefs.SetBool(MigrationEditorPrefKey, true);
                return;
            }

            EditorUtility.CopySerialized(fromLegacy, settings);
            settings.SaveToDiskAfterMigration();

            if (!AssetDatabase.DeleteAsset(LegacyProjectRelativePath))
            {
                File.Delete(legacyFullPath);
                var meta = legacyFullPath + ".meta";
                if (File.Exists(meta))
                    File.Delete(meta);
            }

            AssetDatabase.Refresh();
            EditorPrefs.SetBool(MigrationEditorPrefKey, true);
            Debug.Log("[Workspaces] Migrated settings from ProjectWeasel.Workspaces.asset into WillGoldstone.Workspaces.asset and removed the legacy file.");
        }
    }
}
