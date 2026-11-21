using System;
using System.IO;

namespace C3DProTools.Features.Shared.Contracts
{
    /// <summary>
    /// Where the project settings (profile views, networks, pavement, labels, label rules: one JSON document) come from, so no
    /// file has to sit beside a drawing:
    ///   1. the settings saved in the drawing itself (set from the C3D Pro Run window);
    ///   2. otherwise the settings last saved in any drawing, so a new drawing starts from the last job;
    ///   3. otherwise the built-in project defaults.
    /// Background drawing tests set C3DPRO_SETTINGS_FILE to a file of their own; then only that file is used.
    /// </summary>
    internal static class SettingsSource
    {
        public const string TestFileVariable = "C3DPRO_SETTINGS_FILE";

        /// <summary>Reads the settings saved in an open drawing (by its path); null when it has none. Set by the plugin.</summary>
        public static Func<string, string> FromDrawing { get; set; }

        public static string LastUsedPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "C3DProTools", "LastProjectSettings.json");

        /// <summary>The settings JSON for a drawing, and a few words on where it came from.</summary>
        public static string Text(string drawingPath, out string source)
        {
            string test = Environment.GetEnvironmentVariable(TestFileVariable);
            if (!string.IsNullOrWhiteSpace(test))
            {
                if (File.Exists(test)) { source = test; return File.ReadAllText(test); }
                source = "the built-in defaults (test without a settings file)";
                return Defaults;
            }
            string saved = null;
            try { saved = FromDrawing?.Invoke(drawingPath); }
            catch (Exception ex) { C3DProLogger.Warn("Settings: the settings saved in the drawing could not be read", ex); }
            if (!string.IsNullOrWhiteSpace(saved)) { source = "the settings saved in this drawing"; return saved; }
            try
            {
                if (File.Exists(LastUsedPath)) { source = "the settings you last used (not yet saved in this drawing)"; return File.ReadAllText(LastUsedPath); }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { C3DProLogger.Warn("Settings: the last used settings could not be read", ex); }
            source = "the built-in defaults";
            return Defaults;
        }

        /// <summary>Remembers the settings as the start for drawings that have none. Best effort.</summary>
        public static void RememberLastUsed(string json)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LastUsedPath));
                File.WriteAllText(LastUsedPath, json);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { C3DProLogger.Warn("Settings: the last used settings could not be written", ex); }
        }

        /// <summary>The project rules of the KSIA dry-utility work: cover, pavement, surfaces, label rules.</summary>
        public const string Defaults = """
        {
          "profileViews": {
            "alignments": "*",
            "matchTemplate": "ICT_PROFILE_VIEW_SETTINGS",
            "existingSurface": "COMBINED NGL TILL 21-06-2026 AND NGL NOT SUBMITED",
            "proposedSurface": "PROPOSED_SURFACE"
          },
          "networks": {
            "networks": "*",
            "surface": "CE_PROP-CONT-NCRD",
            "fitFace": "InnerWall",
            "coverMax": 6
          },
          "pavement": {
            "zones": [
              { "surface": "KSIA_TAXIWAY-PAVEMENT", "name": "Taxiway Pavement", "minCover": 1.5, "ductType": "A", "edgeBand": 16.5 },
              { "surface": "KSIA_ROAD-AIRSIDE", "name": "Road Pavement", "minCover": 0.64, "ductType": "B" }
            ],
            "unpaved": { "name": "Unpaved", "minCover": 0.6, "ductType": "B" }
          },
          "labels": {
            "layouts": "*",
            "networks": "ICT-*"
          },
          "labelRules": [
            {
              "name": "KSIA ICT",
              "version": 1,
              "rules": [
                { "id": "ict.plan.manhole", "view": "Plan", "select": { "kind": "Structure", "networkPattern": "ICT-*" },
                  "labelStyles": [ "KSIA_Description Only_Northing_Easting" ], "at": "Insertion", "every": "PerPart", "leader": true, "side": "Alternate", "priority": 2 },
                { "id": "ict.plan.joint", "view": "Plan", "select": { "kind": "Joint", "networkPattern": "ICT-*" },
                  "labelStyles": [ "KSIA_START [NE]" ], "at": "EachJoint", "every": "PerPart", "leader": true, "side": "Alternate", "priority": 3 }
              ]
            }
          ]
        }
        """;
    }
}
