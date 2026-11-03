using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

// Design rules of the Profile & Cover Designer beyond the basic limits: junction matching, drops and backdrops, slopes by
// size, cover margins and zones, dry utility bends and spans, and crossing clearances. No AutoCAD / Civil 3D types, so the
// solver and its checks are tested without Civil 3D. Saved with the designer's settings (JSON).
namespace C3DProTools
{
    /// <summary>How the outgoing pipe's level is limited by the incoming one at a manhole (gravity networks).</summary>
    internal enum JunctionMatchRule
    {
        /// <summary>No limit: levels are free at structures (dry utilities: cables can rise or fall through a pit).</summary>
        None,
        /// <summary>Soffit matching: the outgoing crown is not above the incoming crown.</summary>
        CrownToCrown,
        /// <summary>0.8 depth matching: out invert ≤ in invert + 0.8 × (D in − D out).</summary>
        Match08Depth,
        /// <summary>The outgoing invert is not above the incoming invert.</summary>
        InvertToInvert,
        /// <summary>Both the crowns and the inverts.</summary>
        StrictCrownAndInvert
    }

    /// <summary>What happens when the levels need a manhole drop above the standard maximum.</summary>
    internal enum BackdropMode
    {
        /// <summary>Drops are limited to the standard maximum.</summary>
        Disallow,
        /// <summary>A backdrop (from the minimum backdrop height up to its maximum) is used only when no layout fits without one, and is flagged.</summary>
        CostedBackdropWithFlag,
        /// <summary>Any drop, never flagged.</summary>
        UnrestrictedDrop
    }

    /// <summary>Where each section's slope limits come from.</summary>
    internal enum SlopeSource
    {
        /// <summary>The single minimum / maximum slope of the job.</summary>
        Flat,
        /// <summary>The <see cref="CoverRules.SlopeTable"/>: the row of the largest diameter not above the pipe's (the smallest row for smaller pipes).</summary>
        Table,
        /// <summary>Minimum slope 1 in DN (DN in millimetres, e.g. DN300 → 1:300); maximum from the job.</summary>
        OneInDiameter
    }

    /// <summary>Utility classes for the crossing clearance matrix.</summary>
    internal enum UtilityClass { Unknown, DryIct, DryPower, WetGravity, WetPressure }

    /// <summary>One row of the slope-by-diameter table (inner diameter in millimetres, slopes in percent; 0 = use the job's).</summary>
    internal sealed class SlopeRow
    {
        public double DiameterMm { get; set; }
        public double MinSlopePercent { get; set; }
        public double MaxSlopePercent { get; set; }
    }

    /// <summary>A stretch of route with its own minimum cover (a paved road, taxiway or pavement crossing).</summary>
    internal sealed class CoverZone
    {
        public double S0, S1, MinCover;
        public string Label = "";
    }

    internal sealed class CoverRules
    {
        // ---------------- Junctions, drops, backdrops ----------------
        public JunctionMatchRule GravityJunction { get; set; } = JunctionMatchRule.CrownToCrown;
        /// <summary>Dry utilities: free by default (an invert rule would force every pit to step down).</summary>
        public JunctionMatchRule DryJunction { get; set; } = JunctionMatchRule.None;
        /// <summary>
        /// Dry utilities with a free junction: the most the invert may step up or down through a pit (m; 0 = flush entry and
        /// exit), so a conduit cannot jump through a single pull pit.
        /// </summary>
        public double DryMaxStep { get; set; } = 0.30;
        /// <summary>Minimum drop across a gravity manhole by the turn there (m): up to <see cref="StraightMaxDeg"/>, up to <see cref="MediumMaxDeg"/>, sharper.</summary>
        public double MinDropStraight { get; set; } = 0.02;
        public double MinDropMedium { get; set; } = 0.03;
        public double MinDropSharp { get; set; } = 0.05;
        public double StraightMaxDeg { get; set; } = 15;
        public double MediumMaxDeg { get; set; } = 45;
        public BackdropMode Backdrop { get; set; } = BackdropMode.CostedBackdropWithFlag;
        public double MinBackdropHeight { get; set; } = 1.0;
        public double MaxBackdropDrop { get; set; } = 6.0;

        // ---------------- Slopes ----------------
        public SlopeSource Slopes { get; set; } = SlopeSource.Flat;
        public List<SlopeRow> SlopeTable { get; set; } = new List<SlopeRow>();

        // ---------------- Cover ----------------
        /// <summary>Kept inside the cover limits (m), so 0.60 m minimum cover is solved to at least 0.602 m.</summary>
        public double CoverSafetyEpsilon { get; set; } = 0.002;
        /// <summary>Closed polylines on these layers (comma-separated, * allowed) mark zones with <see cref="ZoneMinCover"/>.</summary>
        public string ZoneLayers { get; set; } = "";
        public double ZoneMinCover { get; set; } = 0.64;
        public string ZoneLabel { get; set; } = "Paved";

        // ---------------- Dry utilities (cables, ducts) ----------------
        /// <summary>Minimum vertical bend radius as a multiple of the inner diameter (0 = off), unless <see cref="MinBendRadius"/> is set.</summary>
        public double MinBendRadiusMultiple { get; set; } = 10;
        /// <summary>Minimum vertical bend radius in metres (0 = use the multiple).</summary>
        public double MinBendRadius { get; set; }
        /// <summary>Most total bending (plan + profile, degrees) between two structures, for cable pulling (0 = off).</summary>
        public double BendBudgetDeg { get; set; } = 270;
        /// <summary>Longest structure-to-structure pull (m, 0 = off).</summary>
        public double MaxStructureSpan { get; set; } = 150;

        // ---------------- Crossings ----------------
        public bool UseClearanceMatrix { get; set; } = true;
        public double DryToDryClearance { get; set; } = 0.30;
        /// <summary>Wet to dry and wet to wet.</summary>
        public double WetClearance { get; set; } = 0.50;
        /// <summary>Closest clearance accepted with a concrete protection slab / HDD (m, 0 = never: the full clearance is required).</summary>
        public double ProtectionMinClearance { get; set; }
        /// <summary>Added to every side of dry utilities' outsides (concrete encasement not drawn in the model), m.</summary>
        public double EncasementPadding { get; set; }
        /// <summary>Edge-to-edge distance in plan below which a pipe alongside is reported (m).</summary>
        public double HorizontalClearance { get; set; } = 0.30;
        /// <summary>Pipes alongside within the horizontal clearance are cleared vertically too, instead of only reported.</summary>
        public bool EnforceVerticalOnParallel { get; set; }
        /// <summary>Around the route's own structures pipes meeting them are branches, not crossings (m beyond the structure).</summary>
        public double StructureCollar { get; set; } = 0.5;
        public string IctPattern { get; set; } = @"\b(ICT|TEL|TELECOM|COMM|FIBRE|FIBER|FO|DATA|ELV)\b";
        public string PowerPattern { get; set; } = @"\b(POWER|MV|LV|HV|ELEC|ELECTRICAL|CABLE|STREETLIGHT|LIGHTING)\b";
        public string GravityPattern { get; set; } = @"\b(SEWER|FOUL|STORM|SW|FW|DRAIN|DRAINAGE|SS|SD)\b";
        public string PressurePattern { get; set; } = @"\b(WATER|POTABLE|PW|FIRE|FH|IRRIGATION|IRR|TSE|RISING)\b";

        // =====================================================================================

        /// <summary>
        /// The "Standard Gravity Preset (v ≥ 0.75 m/s)" slope table (DN mm, min %, max %), as given by the tool's owner for
        /// self-cleansing velocity; loaded on request, never applied by default.
        /// </summary>
        public static List<SlopeRow> GravityPresetV075() => new List<SlopeRow>
        {
            Row(150, 0.65, 10.0), Row(200, 0.45, 8.0), Row(250, 0.35, 8.0), Row(300, 0.28, 6.0), Row(400, 0.20, 5.0),
            Row(500, 0.15, 4.0), Row(600, 0.12, 3.5), Row(800, 0.09, 3.0), Row(1000, 0.07, 2.5), Row(1200, 0.06, 2.0)
        };

        private static SlopeRow Row(double dn, double min, double max) => new SlopeRow { DiameterMm = dn, MinSlopePercent = min, MaxSlopePercent = max };

        public JunctionMatchRule JunctionFor(CoverMode mode) => mode == CoverMode.Gravity ? GravityJunction : DryJunction;

        /// <summary>The most the invert may step either way through a structure with a free junction (+∞ when not limited).</summary>
        public double FreeStepFor(CoverMode mode) => mode == CoverMode.Dry ? Math.Max(0, DryMaxStep) : double.PositiveInfinity;

        /// <summary>
        /// The step the solver and the checks accept: the rule's, but never less than half the solver's level grid, so a flush
        /// (0 m) step between two sizes whose half-difference is not a whole number of grid steps still finds the nearest level.
        /// </summary>
        public double FreeStepTolerance(CoverMode mode) => Math.Max(FreeStepFor(mode), CoverSolver.ElevationStep * 0.5 + 1e-6);

        /// <summary>The minimum drop at a gravity manhole turning by <paramref name="deflectionDeg"/>; none for dry utilities.</summary>
        public double MinDropFor(CoverMode mode, double deflectionDeg)
        {
            if (mode != CoverMode.Gravity) return 0;
            double a = Math.Abs(deflectionDeg);
            return a <= StraightMaxDeg + 1e-9 ? MinDropStraight : a <= MediumMaxDeg + 1e-9 ? MinDropMedium : MinDropSharp;
        }

        /// <summary>
        /// The highest outgoing centreline the junction rule allows, from the incoming centreline and the two inner heights
        /// (+∞ for <see cref="JunctionMatchRule.None"/>).
        /// </summary>
        public static double MaxOutletCentre(JunctionMatchRule rule, double zIn, double dIn, double dOut)
        {
            double invertIn = zIn - dIn / 2.0;
            double invertRule = invertIn + dOut / 2.0;                       // out invert = in invert
            double crownRule = zIn + dIn / 2.0 - dOut / 2.0;                 // out crown = in crown
            switch (rule)
            {
                case JunctionMatchRule.CrownToCrown: return crownRule;
                case JunctionMatchRule.InvertToInvert: return invertRule;
                case JunctionMatchRule.StrictCrownAndInvert: return Math.Min(crownRule, invertRule);
                case JunctionMatchRule.Match08Depth: return invertIn + 0.8 * (dIn - dOut) + dOut / 2.0;
                default: return double.PositiveInfinity;
            }
        }

        /// <summary>
        /// The lowest centreline an inlet (a branch arriving at a main line's manhole) may have for the outlet at
        /// <paramref name="zOut"/> to meet the rule and the minimum drop; NaN for <see cref="JunctionMatchRule.None"/>.
        /// </summary>
        public static double MinInletCentre(JunctionMatchRule rule, double zOut, double dIn, double dOut, double minDrop)
        {
            if (rule == JunctionMatchRule.None) return double.NaN;
            // The rule's highest outlet rises one for one with the inlet, so solve MaxOutletCentre(zIn) − minDrop = zOut.
            return zOut + minDrop - MaxOutletCentre(rule, 0.0, dIn, dOut);
        }

        /// <summary>Slope limits (fractions) for a section of this inner height, from the source chosen.</summary>
        public (double min, double max) SlopesFor(double innerHeight, double jobMin, double jobMax)
        {
            double dMm = innerHeight * 1000.0;
            switch (Slopes)
            {
                case SlopeSource.OneInDiameter:
                    return dMm > 1 ? (Math.Max(jobMin, 1.0 / dMm), jobMax) : (jobMin, jobMax);
                case SlopeSource.Table:
                    List<SlopeRow> rows = (SlopeTable ?? new List<SlopeRow>()).Where(r => r.DiameterMm > 0).OrderBy(r => r.DiameterMm).ToList();
                    if (rows.Count == 0) return (jobMin, jobMax);
                    SlopeRow row = rows.LastOrDefault(r => r.DiameterMm <= dMm + 0.5) ?? rows[0];
                    double min = row.MinSlopePercent > 0 ? row.MinSlopePercent / 100.0 : jobMin;
                    double max = row.MaxSlopePercent > 0 ? row.MaxSlopePercent / 100.0 : jobMax;
                    return (min, Math.Max(min, max));
                default:
                    return (jobMin, jobMax);
            }
        }

        /// <summary>Minimum vertical bend radius (m) for a pipe of this inner height in this mode (0 = no limit).</summary>
        public double BendRadiusFor(CoverMode mode, double innerHeight)
        {
            if (mode != CoverMode.Dry) return 0;
            if (MinBendRadius > 0) return MinBendRadius;
            return MinBendRadiusMultiple > 0 ? MinBendRadiusMultiple * innerHeight : 0;
        }

        public double BendBudgetFor(CoverMode mode) => mode == CoverMode.Dry ? BendBudgetDeg : 0;

        public double MaxSpanFor(CoverMode mode) => mode == CoverMode.Dry ? MaxStructureSpan : 0;

        /// <summary>The utility class of a network from its name (or layer), the patterns in order; gravity / pressure by network type otherwise.</summary>
        public UtilityClass Classify(string name, bool pressureNetwork)
        {
            string n = name ?? "";
            if (Matches(IctPattern, n)) return UtilityClass.DryIct;
            if (Matches(PowerPattern, n)) return UtilityClass.DryPower;
            if (Matches(PressurePattern, n)) return UtilityClass.WetPressure;
            if (Matches(GravityPattern, n)) return UtilityClass.WetGravity;
            return pressureNetwork ? UtilityClass.WetPressure : UtilityClass.Unknown;
        }

        private static bool Matches(string pattern, string text)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return false;
            try { return Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase); }
            catch (ArgumentException) { return false; }
        }

        public static bool IsDry(UtilityClass c) => c == UtilityClass.DryIct || c == UtilityClass.DryPower;

        /// <summary>Vertical clearance between two utilities: dry to dry, or with a wet one (unknown counts as wet: the larger).</summary>
        public double ClearanceBetween(UtilityClass a, UtilityClass b, double flat) =>
            !UseClearanceMatrix ? flat : IsDry(a) && IsDry(b) ? DryToDryClearance : WetClearance;

        /// <summary>Encasement added to each side of a utility of this class.</summary>
        public double PaddingFor(UtilityClass c) => IsDry(c) ? EncasementPadding : 0;
    }
}
