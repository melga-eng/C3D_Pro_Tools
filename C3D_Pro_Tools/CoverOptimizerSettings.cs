using System;
using System.IO;
using System.Text.Json;

namespace C3DProTools
{
    /// <summary>Cover Optimizer choices remembered between sessions (JSON in %APPDATA%\C3DProTools). Slopes are in percent.</summary>
    internal sealed class CoverOptimizerSettings
    {
        public CoverMode Mode { get; set; } = CoverMode.Dry;
        public string Surface { get; set; }
        public CoverReference Reference { get; set; } = CoverReference.OutsideTop;
        public double MinCover { get; set; } = 0.6;
        public double MaxCover { get; set; } = 1.5;
        public double MinLength { get; set; } = 20.0;
        public double DryMinSlope { get; set; } = 0.0;
        public double GravityMinSlope { get; set; } = 0.3;
        public double MaxSlope { get; set; } = 10.0;
        public double MaxSlopeChange { get; set; } = 5.0;
        public bool PreferGentleBends { get; set; } = true;
        public double MaxDrop { get; set; } = 0.5;
        public int FlowDirection { get; set; }
        public string StructureList { get; set; }
        public string StructureFamily { get; set; }
        public string StructureSize { get; set; }
        public bool StartFixed { get; set; } = true;
        public bool EndFixed { get; set; }
        public double GapLimit { get; set; } = 1.0;
        public bool CreateProfile { get; set; }
        public double MaxLength { get; set; }
        public double CrossingClearance { get; set; }
        public double Spacing { get; set; } = 1.0;

        // Drawing new pipes along paths.
        public bool DrawToExisting { get; set; }
        public string DrawNetwork { get; set; }
        public string DrawNewNetworkName { get; set; } = "New Route";
        public string DrawPartsList { get; set; }
        public string DrawPipeFamily { get; set; }
        public string DrawPipeSize { get; set; }
        public string DrawStructureList { get; set; }
        public string DrawStructureFamily { get; set; }
        public string DrawStructureSize { get; set; }
        public bool StructuresAtBends { get; set; } = true;
        public bool StructuresAtEnds { get; set; } = true;
        public double MaxStructureSpacing { get; set; }
        public bool ReversePaths { get; set; }

        // Window.
        public int Section { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "C3DProTools", "CoverOptimizer.json");

        public static CoverOptimizerSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<CoverOptimizerSettings>(File.ReadAllText(FilePath)) ?? new CoverOptimizerSettings();
            }
            catch (Exception) { }
            return new CoverOptimizerSettings();
        }

        /// <summary>Best effort: a settings file that cannot be written must never stop the tool.</summary>
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception) { }
        }
    }
}
