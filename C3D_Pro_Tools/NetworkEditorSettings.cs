using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace C3DProTools
{
    /// <summary>A named set of parallel-copy offsets (e.g. a duct bank's conduit layout).</summary>
    internal sealed class ParallelPreset
    {
        public string Name { get; set; }
        public string Horizontal { get; set; }
        public string Vertical { get; set; }
    }

    /// <summary>
    /// A duct bank's conduit arrangement: a grid of columns × rows at centre-to-centre spacings, centred across the encasement and
    /// either centred on its centreline or measured up from its bottom. Lengths in metres.
    /// </summary>
    internal sealed class ConduitLayout
    {
        public string Name { get; set; }
        public int Columns { get; set; } = 5;
        public int Rows { get; set; } = 1;
        public double HorizontalSpacing { get; set; } = 0.3;
        public double VerticalSpacing { get; set; } = 0.5;
        /// <summary>False: the rows are centred on the encasement's centreline; true: the lowest row's centre is <see cref="BottomToFirstRow"/> above its bottom.</summary>
        public bool FromBottom { get; set; }
        public double BottomToFirstRow { get; set; } = 0.25;
        /// <summary>Moves the whole grid sideways (+ = right of the encasement's direction).</summary>
        public double SideShift { get; set; }

        public ConduitLayout Copy() => (ConduitLayout)MemberwiseClone();

        /// <summary>Plan offsets of the columns, centred on the encasement (+ = right of its direction).</summary>
        public List<double> HorizontalOffsets()
        {
            List<double> list = new List<double>();
            for (int i = 0; i < Columns; i++) list.Add(Math.Round(SideShift + (i - (Columns - 1) / 2.0) * HorizontalSpacing, 6));
            return list;
        }

        /// <summary>Vertical positions of the rows: from the centreline, or from the bottom when <see cref="FromBottom"/>.</summary>
        public List<double> VerticalOffsets()
        {
            List<double> list = new List<double>();
            for (int i = 0; i < Rows; i++)
                list.Add(Math.Round(FromBottom ? BottomToFirstRow + i * VerticalSpacing : (i - (Rows - 1) / 2.0) * VerticalSpacing, 6));
            return list;
        }

        public static List<ConduitLayout> Defaults() => new List<ConduitLayout>
        {
            new ConduitLayout { Name = "MV Type-01 (5 × 3)", Columns = 5, Rows = 3, HorizontalSpacing = 0.3, VerticalSpacing = 0.5 },
            new ConduitLayout { Name = "MV Type-03 (5 × 1)", Columns = 5, Rows = 1, HorizontalSpacing = 0.3, VerticalSpacing = 0.5 },
        };
    }

    /// <summary>Network Editor choices remembered between sessions (JSON in %APPDATA%\C3DProTools). Lengths in metres.</summary>
    internal sealed class NetworkEditorSettings
    {
        public int Action { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }

        public int SplitMode { get; set; }
        public int SplitParts { get; set; } = 2;
        public double SplitEvery { get; set; } = 20.0;
        public double SplitPickTolerance { get; set; } = 1.0;
        public bool SplitAddStructure { get; set; }
        public string SplitStructureList { get; set; }
        public string SplitStructureFamily { get; set; }
        public string SplitStructureSize { get; set; }

        public string ParallelHorizontal { get; set; } = "1.0";
        public string ParallelVertical { get; set; } = "0";
        public bool ParallelSameNetwork { get; set; } = true;
        public string ParallelNetworkName { get; set; } = "Parallel copies";
        public bool ParallelOwnSize { get; set; } = true;
        public string ParallelPartsList { get; set; }
        public string ParallelFamily { get; set; }
        public string ParallelSize { get; set; }
        public bool ParallelFit { get; set; }
        public bool ParallelMitre { get; set; } = true;
        public bool ParallelConnect { get; set; } = true;
        public bool ParallelClearGap { get; set; }
        public double CheckMaxSkew { get; set; } = 5;
        public double CheckMaxSpan { get; set; } = 150;
        public bool CheckSpanWarn { get; set; } = true;
        public List<ParallelPreset> Presets { get; set; } = new List<ParallelPreset>
        {
            new ParallelPreset { Name = "Offset 1.0 m right", Horizontal = "1.0", Vertical = "0" },
            new ParallelPreset { Name = "Both sides 1.0 m", Horizontal = "-1.0, 1.0", Vertical = "0" }
        };

        public List<ConduitLayout> ConduitLayouts { get; set; } = ConduitLayout.Defaults();
        public string ConduitLayoutName { get; set; }
        public string ConduitPartsList { get; set; }
        public string ConduitFamily { get; set; }
        public string ConduitSize { get; set; }
        public bool ConduitSameNetwork { get; set; } = true;
        public string ConduitNetworkName { get; set; } = "Conduits";

        public int ParallelFace { get; set; }
        public double ParallelBoxLength { get; set; } = 6.0;
        public double ParallelBoxWidth { get; set; } = 3.0;

        public int FitFace { get; set; }
        public bool FitConnect { get; set; } = true;
        public double FitBoxLength { get; set; } = 6.0;
        public double FitBoxWidth { get; set; } = 3.0;
        public bool FlowVerify { get; set; } = true;

        public string RimSurface { get; set; }
        public double RimAboveSurface { get; set; }
        public bool RimFollow { get; set; }

        public string GravityPartsList { get; set; }
        public string GravityFamily { get; set; }
        public string GravitySize { get; set; }
        public string GravityNetworkName { get; set; } = "Gravity from pressure";

        public string SwapPartsList { get; set; }
        public string SwapFamily { get; set; }
        public string SwapSize { get; set; }
        public string SwapStructureList { get; set; }
        public string SwapStructureFamily { get; set; }
        public string SwapStructureSize { get; set; }
        public string SwapPressureList { get; set; }
        public bool SwapNearest { get; set; } = true;
        public bool SwapStructures { get; set; } = true;
        public bool SwapFittings { get; set; } = true;
        /// <summary>False: gravity pipes keep their inverts when their size changes; true: their centrelines.</summary>
        public bool SwapHoldCentre { get; set; }
        public List<SwapSet> SwapSets { get; set; } = new List<SwapSet>();

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "C3DProTools", "NetworkEditor.json");

        public static NetworkEditorSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    NetworkEditorSettings s = JsonSerializer.Deserialize<NetworkEditorSettings>(File.ReadAllText(FilePath)) ?? new NetworkEditorSettings();
                    // The old duct bank presets (wrong spacings, and one conduit short: parallel copies skip the source position)
                    // are replaced by the Conduits task.
                    s.Presets?.RemoveAll(p => p.Name != null && p.Name.StartsWith("Duct bank ", StringComparison.Ordinal) && p.Name.Contains("(0.2"));
                    if (s.ConduitLayouts == null || s.ConduitLayouts.Count == 0) s.ConduitLayouts = ConduitLayout.Defaults();
                    return s;
                }
            }
            catch (Exception ex) { C3DProLogger.Warn("NetworkEditorSettings: the settings file could not be read", ex); }
            return new NetworkEditorSettings();
        }

        /// <summary>Best effort: a settings file that cannot be written must never stop the tool.</summary>
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { C3DProLogger.Warn("NetworkEditorSettings: the settings file could not be written", ex); }
        }
    }
}
