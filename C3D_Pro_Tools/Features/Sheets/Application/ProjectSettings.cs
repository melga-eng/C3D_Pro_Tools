using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using C3DProTools.Features.Sheets.Contracts;

// The project settings file, C3DPro.project.json next to the drawing (docs/sheet-production-contract.md, section 4): the label
// rule sets the user chose for this job, and the build settings. Plain JSON, read and written without Civil 3D.
namespace C3DProTools.Features.Sheets.Application
{
    internal sealed class ProjectSettings
    {
        public const string FileName = "C3DPro.project.json";

        /// <summary>The label rule sets of the job; the build names one of them.</summary>
        public List<LabelRuleSet> LabelRules { get; set; } = new List<LabelRuleSet>();
        public BuildSettings Build { get; set; } = BuildSettings.Default;

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };

        /// <summary>The settings file beside a drawing.</summary>
        public static string PathFor(string drawingPath) =>
            System.IO.Path.Combine(System.IO.Path.GetDirectoryName(drawingPath ?? "") ?? "", FileName);

        /// <summary>
        /// Reads the settings beside a drawing. A missing file gives empty settings (no rules) and no problem; a file that cannot
        /// be read gives empty settings and the reason, so the build says why it has no rules instead of silently labelling nothing.
        /// </summary>
        public static ProjectSettings Load(string drawingPath, out string problem)
        {
            problem = null;
            string text = Features.Shared.Contracts.SettingsSource.Text(drawingPath, out _);
            try { return FromJson(text); }
            catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException)
            {
                problem = $"The C3D Pro settings could not be read: {ex.Message}";
                return new ProjectSettings();
            }
        }

        public static ProjectSettings FromJson(string json)
        {
            ProjectSettings s = JsonSerializer.Deserialize<ProjectSettings>(json, Json) ?? new ProjectSettings();
            s.LabelRules ??= new List<LabelRuleSet>();
            s.Build ??= BuildSettings.Default;
            return s;
        }

        public string ToJson() => JsonSerializer.Serialize(this, Json);

        /// <summary>The rule set of that name (ignoring case), or the only one when <paramref name="name"/> is empty.</summary>
        public LabelRuleSet RuleSet(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return LabelRules.Count == 1 ? LabelRules[0] : null;
            return LabelRules.Find(r => string.Equals(r?.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        }
    }
}
