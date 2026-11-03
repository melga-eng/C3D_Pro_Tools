using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace C3DProTools
{
    /// <summary>
    /// Parts to Profile choices remembered between sessions, saved as JSON in %APPDATA%\C3DProTools.
    /// Styles are stored by name, so the same choices carry over to any drawing that has styles of those names.
    /// </summary>
    internal sealed class PartsToProfileSettings
    {
        public bool CreateMode { get; set; }
        public double Tolerance { get; set; } = 0.5;
        public bool IncludeCrossings { get; set; }
        public RunStartMode StartMode { get; set; } = RunStartMode.FirstPipePicked;
        public double GapLimit { get; set; } = 1.0;
        public double CenterTolerance { get; set; } = 0.05;
        public bool CheckFlow { get; set; } = true;

        // Top-of-pipe profiles.
        public string TopPrefix { get; set; } = "TOP - ";
        public bool TopHighest { get; set; }
        public string TopStyle { get; set; }
        public string TopLabelSet { get; set; }

        public string AlignmentStyle { get; set; }
        public string AlignmentLabelSet { get; set; }
        public string Layer { get; set; }
        public double StartStation { get; set; }
        public List<string> Surfaces { get; set; } = new List<string>();
        public string ProfileStyle { get; set; }
        public string ProfileLabelSet { get; set; }
        public string ProfileViewStyle { get; set; }
        public string BandSet { get; set; }
        public double ElevationMargin { get; set; } = 1.0;

        public int PipeLabelCount { get; set; } = 1;
        public List<string> PipeLabels { get; set; } = new List<string>();
        public string StructureLabel { get; set; }
        public int PressurePipeLabelCount { get; set; } = 1;
        public List<string> PressurePipeLabels { get; set; } = new List<string>();
        public string FittingLabel { get; set; }
        public string AppurtenanceLabel { get; set; }

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "C3DProTools", "PartsToProfile.json");

        public static PartsToProfileSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<PartsToProfileSettings>(File.ReadAllText(FilePath)) ?? new PartsToProfileSettings();
            }
            catch (Exception ex) { C3DProLogger.Warn("PartsToProfileSettings: the settings file could not be read", ex); }
            return new PartsToProfileSettings();
        }

        /// <summary>Best effort: a settings file that cannot be written must never stop the tool.</summary>
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { C3DProLogger.Warn("PartsToProfileSettings: the settings file could not be written", ex); }
        }
    }
}
