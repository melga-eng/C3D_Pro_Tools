using System;
using System.IO;
using System.Text.Json;

namespace C3DProTools
{
    /// <summary>Style Manager choices and window layout remembered between sessions (JSON in %APPDATA%\C3DProTools).</summary>
    internal sealed class StyleManagerSettings
    {
        public int WindowX { get; set; } = -1;
        public int WindowY { get; set; } = -1;
        public int WindowWidth { get; set; } = 1320;
        public int WindowHeight { get; set; } = 820;
        public bool Maximized { get; set; }
        public int TreeWidth { get; set; } = 270;
        public int DetailsWidth { get; set; } = 340;
        public int DeckHeight { get; set; } = 230;
        public int LastTab { get; set; }

        public RenameRule Rule { get; set; } = new RenameRule();
        public bool RepeatPurge { get; set; } = true;
        public int PurgeScope { get; set; }
        public int ConflictMode { get; set; }
        public string TemplatePath { get; set; } = "";
        public string MappingPath { get; set; } = "";
        public string PullPath { get; set; } = "";

        public string BatchFolder { get; set; } = "";
        public bool BatchSubfolders { get; set; }
        public bool BatchImportMissing { get; set; }
        public bool BatchOverwriteFromTemplate { get; set; }
        public bool BatchApplyMapping { get; set; }
        public bool BatchMergeDuplicates { get; set; } = true;
        public bool BatchRename { get; set; }
        public bool BatchDeleteUnused { get; set; } = true;
        public bool BatchOverwrite { get; set; } = true;
        public string BatchOutputFolder { get; set; } = "";

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "C3DProTools", "StyleManager.json");

        public static StyleManagerSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<StyleManagerSettings>(File.ReadAllText(FilePath)) ?? new StyleManagerSettings();
            }
            catch (Exception ex) { C3DProLogger.Warn("StyleManagerSettings: the settings file could not be read", ex); }
            return new StyleManagerSettings();
        }

        /// <summary>Best effort: a settings file that cannot be written must never stop the tool.</summary>
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { C3DProLogger.Warn("StyleManagerSettings: the settings file could not be written", ex); }
        }
    }
}
