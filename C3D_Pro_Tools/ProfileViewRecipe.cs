using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace C3DProTools
{
    /// <summary>What a profile shown in a view is for. Every view of the workflow has one of each.</summary>
    internal enum ProfileRole { None = 0, Existing = 1, Proposed = 2, Top = 3 }

    internal enum OtherProfileAction { Leave = 0, Hide = 1, Delete = 2 }

    /// <summary>
    /// How a view's profiles are told apart: the existing and proposed ground by the surface they were sampled from (checked
    /// against the surfaces themselves, so the profile's name does not matter), the top of pipe / duct by its name.
    /// </summary>
    internal sealed class ProfileRoleRules
    {
        public string ExistingSurface { get; set; } = "";
        public string ProposedSurface { get; set; } = "";
        /// <summary>Words that mark a top-of-pipe / duct profile, matched as a whole word in its name (TOP, TOD).</summary>
        public List<string> TopWords { get; set; } = new List<string> { "TOP", "TOD" };

        public ProfileRoleRules Copy() => new ProfileRoleRules { ExistingSurface = ExistingSurface, ProposedSurface = ProposedSurface, TopWords = new List<string>(TopWords ?? new List<string>()) };
    }

    internal sealed class BandRecipe
    {
        public bool Top { get; set; }
        public string BandType { get; set; } = "ProfileData";
        public string Style { get; set; } = "";
        public double Gap { get; set; }
        public double Weeding { get; set; }
        public bool ShowLabels { get; set; } = true;
        public bool LabelAtStart { get; set; } = true;
        public bool LabelAtEnd { get; set; } = true;
        public double MajorInterval { get; set; }
        public double MinorInterval { get; set; }
        public ProfileRole Profile1 { get; set; }
        public ProfileRole Profile2 { get; set; }
    }

    internal sealed class LabelGroupRecipe
    {
        /// <summary>Line, PVI, Sag, Crest, MajorStation, MinorStation or Geometry.</summary>
        public string Kind { get; set; } = "";
        public string Style { get; set; } = "";
        public double Increment { get; set; }
    }

    internal sealed class RoleRecipe
    {
        public ProfileRole Role { get; set; }
        public string Style { get; set; } = "";
        /// <summary>True: the style is set as the view's override (only this view); false: on the profile itself.</summary>
        public bool InView { get; set; }
        public string Layer { get; set; } = "";
        /// <summary>The source profile's name, to tell a layer named after its profile.</summary>
        public string ProfileName { get; set; } = "";
        public List<LabelGroupRecipe> Labels { get; set; } = new List<LabelGroupRecipe>();
    }

    /// <summary>A finished profile view, recorded by role so it can be applied to views of any other alignment.</summary>
    internal sealed class ProfileViewRecipe
    {
        public string Name { get; set; } = "";
        public string SourceView { get; set; } = "";
        public DateTime Captured { get; set; } = DateTime.Now;
        public string ViewStyle { get; set; } = "";
        public List<BandRecipe> Bands { get; set; } = new List<BandRecipe>();
        public List<RoleRecipe> Roles { get; set; } = new List<RoleRecipe>();
        public ProfileRoleRules Rules { get; set; } = new ProfileRoleRules();

        public RoleRecipe RoleOf(ProfileRole r) => Roles.FirstOrDefault(x => x.Role == r);

        [JsonIgnore]
        public string Summary =>
            $"View style \"{ViewStyle}\"; {Bands.Count} band(s); " +
            string.Join(", ", Roles.Select(r => $"{ProfileViewMatch.RoleName(r.Role)} \"{r.Style}\"{(r.Labels.Count > 0 ? $" + {r.Labels.Count} label group(s)" : "")}"));
    }

    /// <summary>Profile View Tools choices and saved templates (JSON in %APPDATA%\C3DProTools).</summary>
    internal sealed class ProfileViewToolsSettings
    {
        public int Page { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public ProfileRoleRules Rules { get; set; } = new ProfileRoleRules();
        public List<ProfileViewRecipe> Templates { get; set; } = new List<ProfileViewRecipe>();

        // Match
        public bool MatchViewStyle { get; set; } = true;
        public bool MatchBands { get; set; } = true;
        public bool MatchProfileStyles { get; set; } = true;
        public bool MatchProfileLabels { get; set; } = true;
        public bool MatchElevation { get; set; } = true;
        public bool StylesInView { get; set; }
        public bool CreateMissing { get; set; } = true;
        public OtherProfileAction Others { get; set; } = OtherProfileAction.Hide;
        public double Margin { get; set; } = 1.0;
        /// <summary>Elevation ranges round outward to whole steps of this (m).</summary>
        public double DatumIncrement { get; set; } = 1.0;
        public bool MatchZones { get; set; }
        public string TopPrefix { get; set; } = "TOP - ";
        public bool LinkFollowers { get; set; } = true;

        // Split
        public double SplitLength { get; set; } = 450;
        public bool SplitFitViewport { get; set; }
        public string SplitViewportLayout { get; set; } = "";
        public double SplitRound { get; set; } = 10;
        public double SplitOverlap { get; set; }
        public double SplitMinLast { get; set; } = 100;
        public bool SplitBelow { get; set; } = true;
        public double SplitGap { get; set; } = 30;
        public string SplitName { get; set; } = "{view} - P{n}";
        public bool SplitTrimParts { get; set; } = true;
        public bool SplitLabels { get; set; } = true;
        public bool SplitFit { get; set; } = true;
        public bool SplitDeleteSource { get; set; }
        public bool SplitSheets { get; set; }
        public string SplitSheetTemplate { get; set; } = "";
        public string SplitSheetFirstName { get; set; } = "";
        public bool SplitSheetPartTitle { get; set; } = true;
        public int SplitFirstSheetNo { get; set; } = 1;
        public bool SplitZones { get; set; }
        public string ZoneLayers { get; set; } = "";
        public string ZoneLabel { get; set; } = "Paved";

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "C3DProTools", "ProfileViewTools.json");

        public static ProfileViewToolsSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    ProfileViewToolsSettings s = JsonSerializer.Deserialize<ProfileViewToolsSettings>(File.ReadAllText(FilePath)) ?? new ProfileViewToolsSettings();
                    s.Rules ??= new ProfileRoleRules();
                    s.Templates ??= new List<ProfileViewRecipe>();
                    return s;
                }
            }
            catch (Exception ex) { C3DProLogger.Warn("ProfileViewRecipe: the settings file could not be read", ex); }
            return new ProfileViewToolsSettings();
        }

        /// <summary>Best effort: a settings file that cannot be written must never stop the tool.</summary>
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { C3DProLogger.Warn("ProfileViewRecipe: the settings file could not be written", ex); }
        }
    }
}
