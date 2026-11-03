using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace C3DProTools
{
    /// <summary>
    /// How a project's sheets carry their identity: the title block and its attribute tags, the sheet ID and part-letter texts,
    /// the key plan and the scale note. Templates use {tile}, {part}, {number} and, for revision rows, {i}.
    /// </summary>
    internal sealed class SheetProfile
    {
        /// <summary>The title block's block name; empty = the block with the most attributes on each sheet.</summary>
        public string TitleBlock { get; set; } = "KSIAATTRIBUTES _A1";
        /// <summary>The attribute holding the drawing number (kept equal to the layout name).</summary>
        public string NumberTag { get; set; } = "DRAWING_NUMBER";
        public string SheetTitleTag { get; set; } = "SHEET_TITLE_NAME_3";
        public string SheetTitleTemplate { get; set; } = "SHEET {tile} PART - {part}";
        public string RevisionTag { get; set; } = "REV_NO";
        public string StageTag { get; set; } = "STAGE";
        public string DateTag { get; set; } = "DD.MM.YY";
        public string ScaleTag { get; set; } = "SCALE";

        /// <summary>Revision rows in the title block, row 1 the first issue; tags with {i} for the row.</summary>
        public int RevisionRows { get; set; } = 4;
        public string RevDateTag { get; set; } = "REV{i}_DATE";
        public string RevCodeTag { get; set; } = "REVS{i}";
        public string RevDescTag { get; set; } = "REV{i}_DESC";
        public string RevNoTag { get; set; } = "REV{i}_NO";
        public string RevChkTag { get; set; } = "REV{i}_CHK";
        public string RevAppTag { get; set; } = "REV{i}_APPD";
        public string DrawnTag { get; set; } = "DRW";
        public string CheckedTag { get; set; } = "CHK";
        public string ApprovedTag { get; set; } = "APP";

        public string MatchRightTag { get; set; } = "MATCHLINERIGHT";
        public string MatchLeftTag { get; set; } = "MATCHLINELEFT";
        public string MatchUpTag { get; set; } = "MATCHLINEUP";
        public string MatchDownTag { get; set; } = "MATCHLINEDOWN";
        public string MatchTemplate { get; set; } = "FOR CONTINUATION SEE DRAWING {number}";

        /// <summary>The sheet ID text on the sheet; groups "tile" and "part".</summary>
        public string SheetIdPattern { get; set; } = @"^SHEET-(?<tile>\d+)-(?<part>[A-Z])$";
        public string SheetIdTemplate { get; set; } = "SHEET-{tile}-{part}";
        /// <summary>The part letter shown on its own (in the title circle).</summary>
        public string PartTextPattern { get; set; } = @"^[A-D]$";
        /// <summary>The scale note; its "1:N" is rewritten from the viewport.</summary>
        public string ScaleTextPattern { get; set; } = @"^SCALE\b";
        /// <summary>The key plan's quadrant labels; groups "tile" and "part".</summary>
        public string KeyPlanPattern { get; set; } = @"^(?<tile>\d+)-(?<part>[A-Z])$";
        /// <summary>The parts of a tile, rows from the top separated by "/": A and B on top, C and D below.</summary>
        public string PartGrid { get; set; } = "AB/CD";
        /// <summary>The running number in a layout name (renumbering and next free numbers).</summary>
        public string NumberPattern { get; set; } = @"(\d+)(?!.*\d)";

        public List<string> RequiredTags { get; set; } = new List<string> { "DRAWING_NUMBER", "REV_NO", "STAGE", "DD.MM.YY", "DRW", "CHK", "SCALE" };
        public List<string> TableTags { get; set; } = new List<string> { "DRAWING_NUMBER", "SHEET_TITLE_NAME_3", "REV_NO", "STAGE", "DD.MM.YY", "DRW", "CHK", "APP", "SCALE" };

        public SheetProfile Copy() => JsonSerializer.Deserialize<SheetProfile>(JsonSerializer.Serialize(this));
    }

    internal sealed class SheetManagerSettings
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public int Page { get; set; }
        public SheetProfile Profile { get; set; } = new SheetProfile();
        public string PdfFolder { get; set; } = "";
        public string PdfName { get; set; } = "{layout}";
        public bool PdfOneFile { get; set; }
        public bool PdfForceDevice { get; set; }
        public bool PdfOpenFolder { get; set; } = true;
        public string PageSetup { get; set; } = "";
        public string TemplateFile { get; set; } = "";
        public string RevisionDescription { get; set; } = "Issue For Construction";
        public string RevisionBy { get; set; } = "";
        public string RevisionChecked { get; set; } = "";
        public string RevisionApproved { get; set; } = "";

        private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "C3DProTools", "SheetManager.json");

        public static SheetManagerSettings Load()
        {
            try
            {
                if (File.Exists(FilePath)) return JsonSerializer.Deserialize<SheetManagerSettings>(File.ReadAllText(FilePath)) ?? new SheetManagerSettings();
            }
            catch (Exception) { }
            return new SheetManagerSettings();
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
