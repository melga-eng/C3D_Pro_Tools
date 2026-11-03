using System;
using System.IO;
using System.Text.Json;

namespace C3DProTools
{
    /// <summary>Networks from Feature Lines choices remembered between sessions (JSON in %APPDATA%\C3DProTools). Lengths in metres.</summary>
    internal sealed class NetworkFromLinesSettings
    {
        public int Width { get; set; }
        public int Height { get; set; }

        public bool FromSurface { get; set; }
        public string Surface { get; set; }
        public double Depth { get; set; } = 1.0;
        public int Reference { get; set; }

        public bool MergeStraight { get; set; } = true;
        public double MergeTolerance { get; set; } = 0.005;
        public double MinLength { get; set; } = 0.05;
        public double MaxLength { get; set; }
        public bool Downhill { get; set; }

        public string PartsList { get; set; }
        public string Family { get; set; }
        public string Size { get; set; }

        public bool AddJoints { get; set; }
        public string JointList { get; set; }
        public string JointFamily { get; set; }
        public string JointSize { get; set; }
        public bool AddEnds { get; set; }
        public string EndList { get; set; }
        public string EndFamily { get; set; }
        public string EndSize { get; set; }
        public bool ConnectExisting { get; set; } = true;

        public bool OneNetwork { get; set; }
        public string NetworkName { get; set; } = "Feature line network";
        public string Prefix { get; set; } = "NET - ";
        public bool EraseSource { get; set; }

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "C3DProTools", "NetworkFromLines.json");

        public static NetworkFromLinesSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<NetworkFromLinesSettings>(File.ReadAllText(FilePath)) ?? new NetworkFromLinesSettings();
            }
            catch (Exception) { }
            return new NetworkFromLinesSettings();
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
