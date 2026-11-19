using System;
using System.IO;
using System.Text.Json;

namespace C3DProTools
{
    /// <summary>Navisworks Import choices and window layout remembered between sessions (JSON in %APPDATA%\C3DProTools).</summary>
    internal sealed class NavisImportSettings
    {
        public int WindowX { get; set; } = -1;
        public int WindowY { get; set; } = -1;
        public int WindowWidth { get; set; } = 1360;
        public int WindowHeight { get; set; } = 820;
        public bool Maximized { get; set; }
        public int DetailsWidth { get; set; } = 400;
        public bool OptionsVisible { get; set; } = true;

        public string PartsListName { get; set; } = "Navis Import";
        public int Grouping { get; set; }
        /// <summary>0 = join matching drawing networks (Auto), 1 = one chosen network, 2 = new networks.</summary>
        public int Destination { get; set; }
        public string ChosenNetwork { get; set; } = "";
        public string Prefix { get; set; } = "NAVIS - ";
        public string SingleNetworkName { get; set; } = "NAVIS - Import";
        public string SurfaceName { get; set; } = "";
        public bool ConnectToStructures { get; set; } = true;
        public int Existing { get; set; }
        public string SolidLayer { get; set; } = "NAVIS-SOLIDS";
        /// <summary>A catalog size further than this from the Navisworks size is not trusted.</summary>
        public int MaxPartErrorMm { get; set; } = 25;
        /// <summary>0 = bring such rows in as exact solids, 1 = use the nearest size anyway.</summary>
        public int NoSizeMode { get; set; }
        public bool SearchDrawingLists { get; set; } = true;
        /// <summary>0 = nearest size, 1 = Method A (any-size families), 2 = Method B (add sizes to the catalog).</summary>
        public int SizeMode { get; set; } = 1;
        public bool AskSizeMode { get; set; } = true;
        /// <summary>Navisworks to drawing coordinates: turned about the origin (degrees, counter-clockwise), then shifted (m).</summary>
        public double TransformDx { get; set; }
        public double TransformDy { get; set; }
        public double TransformDz { get; set; }
        public double TransformRotationDeg { get; set; }

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "C3DProTools", "NavisImport.json");

        public static NavisImportSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<NavisImportSettings>(File.ReadAllText(FilePath)) ?? new NavisImportSettings();
            }
            catch (Exception ex) { C3DProLogger.Warn("NavisImportSettings: the settings file could not be read", ex); }
            return new NavisImportSettings();
        }

        /// <summary>Best effort: a settings file that cannot be written must never stop the tool.</summary>
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { C3DProLogger.Warn("NavisImportSettings: the settings file could not be written", ex); }
        }
    }
}
