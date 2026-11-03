using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace C3DProTools
{
    /// <summary>Annotation Studio choices remembered between sessions (JSON in %APPDATA%\C3DProTools). Paper distances in mm.</summary>
    internal sealed class AnnotationSettings
    {
        public int Section { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }

        // Add labels: the label table as last used, and saved label sets.
        public List<LabelSetItem> LabelRows { get; set; } = new List<LabelSetItem>();
        public List<LabelSet> LabelSets { get; set; } = new List<LabelSet>();
        public string LabelSet { get; set; }
        public int LabelTarget { get; set; }
        public int ExistingMode { get; set; }
        public bool ArrangeAfterLabelling { get; set; } = true;

        // Arrange
        public double MaxDistance { get; set; } = 60;
        public double Gap { get; set; } = 1.5;
        public double Margin { get; set; } = 3;
        public bool Plan { get; set; } = true;
        public bool Profile { get; set; } = true;
        public bool ArrangeEveryLabel { get; set; } = true;
        public bool SlideOnPipe { get; set; }
        public int ArrangeTarget { get; set; } = 1;
        public bool AvoidModelText { get; set; } = true;
        public bool AvoidXrefs { get; set; } = true;
        public bool MaskArranged { get; set; }

        // Names
        public NamingOptions Naming { get; set; } = new NamingOptions();
        public int RenameTarget { get; set; } = 1;
        public double RouteGap { get; set; } = 1.0;
        public double RouteCenterTolerance { get; set; } = 0.05;

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "C3DProTools", "AnnotationStudio.json");

        public static AnnotationSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    AnnotationSettings s = JsonSerializer.Deserialize<AnnotationSettings>(File.ReadAllText(FilePath)) ?? new AnnotationSettings();
                    s.Naming = s.Naming ?? new NamingOptions();
                    // Type codes are looked up without regard to case.
                    s.Naming.TypeCodes = new Dictionary<string, string>(s.Naming.TypeCodes ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
                    s.LabelSets = s.LabelSets ?? new List<LabelSet>();
                    s.LabelRows = s.LabelRows ?? new List<LabelSetItem>();
                    return s;
                }
            }
            catch (Exception ex) { C3DProLogger.Warn("AnnotationSettings: the settings file could not be read", ex); }
            return new AnnotationSettings();
        }

        /// <summary>Best effort: a settings file that cannot be written must never stop the tool.</summary>
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { C3DProLogger.Warn("AnnotationSettings: the settings file could not be written", ex); }
        }
    }
}
