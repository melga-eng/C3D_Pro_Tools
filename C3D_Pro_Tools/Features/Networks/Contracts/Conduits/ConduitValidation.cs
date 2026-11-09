using System;
using System.Collections.Generic;
using System.Linq;

// Validation and section rules for Generate Conduits: authoritative checks of templates, requests and apply requests, the
// section math every encasement is checked with, and stale-plan comparison. Plain data only, so it is tested offline.
namespace C3DProTools.Features.Networks.Contracts
{
    /// <summary>Stable issue codes (tests and logs rely on them; never reuse one for another meaning).</summary>
    internal static class ConduitIssueCodes
    {
        public const string DrawingMissing = "conduits.drawing.missing";
        public const string DrawingMismatch = "conduits.drawing.mismatch";
        public const string UnitsNotMetres = "conduits.units.notMetres";
        public const string EncasementsNone = "conduits.encasements.none";
        public const string EncasementInvalid = "conduits.encasements.invalid";
        public const string EncasementDuplicate = "conduits.encasements.duplicate";

        public const string TemplateMissing = "conduits.template.missing";
        public const string TemplateKey = "conduits.template.key";
        public const string TemplateKeyDuplicate = "conduits.template.keyDuplicate";
        public const string TemplateGrid = "conduits.template.grid";
        public const string TemplatePitch = "conduits.template.pitch";
        /// <summary>Error: the pitch is smaller than the stated conduit diameter, so conduits would overlap.</summary>
        public const string TemplateOverlap = "conduits.template.overlap";
        /// <summary>Warning: the pitch leaves less than the stated clear gap.</summary>
        public const string TemplateGapBelowReference = "conduits.template.gap";
        /// <summary>Warning: the stated envelope does not equal the grid plus stated diameter and edge clearances.</summary>
        public const string TemplateEnvelopeInconsistent = "conduits.template.envelope";
        public const string TemplateDesign = "conduits.template.design";
        public const string TemplateVariant = "conduits.template.variant";
        public const string VariantUnknown = "conduits.variant.unknown";

        public const string SectionPlacement = "conduits.section.placement";
        public const string SectionBottom = "conduits.section.bottom";
        public const string SectionShift = "conduits.section.shift";

        public const string PartMissing = "conduits.part.missing";
        public const string PartDiameter = "conduits.part.diameter";
        /// <summary>Error: the part's outside diameter could not be read, so fit cannot be approved (a callout is not a diameter).</summary>
        public const string PartDiameterUnknown = "conduits.part.diameterUnknown";
        /// <summary>Error: the part's outside diameter is larger than a pitch of the template, so neighbouring conduits overlap.</summary>
        public const string PartOverlap = "conduits.part.overlap";
        /// <summary>Warning: the part's diameter differs from the template's stated conduit OD.</summary>
        public const string PartDiameterDiffers = "conduits.part.diameterDiffers";
        /// <summary>Warning: a copied (family-less) part stays in its template's network whatever the destination.</summary>
        public const string PartStaysInTemplateNetwork = "conduits.part.staysInTemplateNetwork";
        /// <summary>Error (item): the part could not be resolved or put in the destination network's parts list.</summary>
        public const string PartUnavailable = "conduits.part.unavailable";

        public const string EndsSearch = "conduits.ends.search";
        public const string EndsOption = "conduits.ends.option";
        public const string InnerBoxInvalid = "conduits.ends.innerBox";
        public const string InnerBoxDuplicate = "conduits.ends.innerBoxDuplicate";
        public const string OverrideInvalid = "conduits.ends.override";
        public const string OverrideDuplicate = "conduits.ends.overrideDuplicate";
        public const string StructureChoiceInvalid = "conduits.ends.structureChoice";
        public const string StructureChoiceDuplicate = "conduits.ends.structureChoiceDuplicate";
        /// <summary>Error: connections asked for, but the conduits go in a new network (Civil 3D connects only within a network).</summary>
        public const string ConnectNeedsSameNetwork = "conduits.ends.connectNeedsSameNetwork";
        public const string NetworkName = "conduits.network.name";

        // Item issues, set by the planner.
        /// <summary>Error or Warning (by policy): a structure's inner footprint is not known and no box was given for its size.</summary>
        public const string FootprintUnknown = "conduits.item.footprintUnknown";
        /// <summary>Info: an unconnected end was matched to a structure by position (inside or within the search distance).</summary>
        public const string StructureMatchedByPosition = "conduits.item.structureByPosition";
        /// <summary>Error: more than one structure qualifies for an unconnected end; the user must pick one.</summary>
        public const string StructureAmbiguous = "conduits.item.structureAmbiguous";
        /// <summary>Error: neighbouring conduits overlap with the resolved part (always blocks, whatever the outside option says).</summary>
        public const string ConduitsOverlap = "conduits.item.overlap";
        /// <summary>Error: wall or centre end treatment on a curved encasement (phase one supports only encasement ends there).</summary>
        public const string CurvedEndUnsupported = "conduits.item.curvedEndUnsupported";
        /// <summary>Info: a joint with a curved encasement on either side is not mitred; both sides keep their encasement ends (phase one).</summary>
        public const string CurvedJointNotMitred = "conduits.item.curvedJointNotMitred";
        /// <summary>Info: more than two chosen encasements meet at one point; that joint is not mitred and each keeps its end.</summary>
        public const string JointNotPaired = "conduits.item.jointNotPaired";
        /// <summary>Warning: a conduit's line misses the structure's face; that conduit keeps the encasement's end point.</summary>
        public const string WallMissed = "conduits.item.wallMissed";
        /// <summary>Error: segmented, flex or otherwise unsupported encasement pipe.</summary>
        public const string EncasementUnsupported = "conduits.item.unsupported";
        /// <summary>Error (or Warning when allowed): conduits cross the encasement's outside edge.</summary>
        public const string ConduitsOutside = "conduits.item.outside";
        /// <summary>Warning: the encasement's size differs from the template's stated envelope (it is never resized).</summary>
        public const string EnvelopeDiffers = "conduits.item.envelopeDiffers";
        /// <summary>Error: a conduit would be shorter than the minimum after end treatment.</summary>
        public const string ConduitTooShort = "conduits.item.tooShort";
        /// <summary>Warning: the conduits will not be in the structure's network, so the asked connection cannot be made.</summary>
        public const string ConnectionImpossible = "conduits.item.connectionImpossible";
        public const string NothingToGenerate = "conduits.plan.nothing";

        // Apply.
        public const string PlanNotApplicable = "conduits.plan.notApplicable";
        public const string ChosenNotInPlan = "conduits.chosen.notInPlan";
        public const string ChosenNotApplicable = "conduits.chosen.notApplicable";
        public const string ChosenDuplicate = "conduits.chosen.duplicate";
        public const string EncasementMissing = "conduits.stale.missing";
        public const string EncasementChanged = "conduits.stale.changed";
        public const string StructureChanged = "conduits.stale.structure";
        public const string NeighbourChanged = "conduits.stale.neighbour";
        /// <summary>The conduit part now resolves to another part, size or diameter for this encasement's destination.</summary>
        public const string PartChanged = "conduits.stale.part";
        /// <summary>The destination network is gone, or a copied part's template now sits in another network.</summary>
        public const string DestinationChanged = "conduits.stale.destination";
        /// <summary>Warning (result): a run's mitred joint has conduits on one side only (the other was not chosen, rejected or rolled back).</summary>
        public const string RunIncomplete = "conduits.run.incomplete";
        public const string ItemFailed = "conduits.item.failed";
        public const string ItemConnectionFailed = "conduits.item.connection";
        public const string PartsListChanged = "conduits.partsList.changed";
        public const string Cancelled = "conduits.cancelled";
        public const string OutcomeUnknown = "conduits.outcomeUnknown";
        public const string SessionNotClosed = "conduits.session.notClosed";
    }

    /// <summary>A conduit's centre in an encasement's section: 1-based column (left to right along the run) and row (bottom up), metres from the centreline.</summary>
    internal readonly record struct SectionCentre(int Column, int Row, double AcrossM, double UpM);

    internal static class ConduitValidation
    {
        public const double PositionTolerance = 1e-6, BulgeTolerance = 1e-9, RotationTolerance = 1e-9, SizeTolerance = 1e-6;

        // ---------- Templates ----------

        /// <summary>Checks a template's own values. Errors make it unusable; warnings are review notes (standard templates have none).</summary>
        public static List<OperationIssue> ValidateTemplate(ConduitTemplate t)
        {
            List<OperationIssue> issues = new List<OperationIssue>();
            if (t == null) { issues.Add(OperationIssue.Error(ConduitIssueCodes.TemplateMissing, "Choose a conduit template.")); return issues; }
            string who = string.IsNullOrWhiteSpace(t.Name) ? t.Key ?? "The template" : t.Name;
            if (string.IsNullOrWhiteSpace(t.Key) || string.IsNullOrWhiteSpace(t.Name))
                issues.Add(OperationIssue.Error(ConduitIssueCodes.TemplateKey, "Give the template a key and a name."));
            ConduitArrangement a = t.Arrangement;
            if (a == null || a.Columns < 1 || a.Columns > ConduitLimits.MaxColumns || a.Rows < 1 || a.Rows > ConduitLimits.MaxRows)
            {
                issues.Add(OperationIssue.Error(ConduitIssueCodes.TemplateGrid, $"{who}: columns and rows must each be 1 to {ConduitLimits.MaxColumns}.", nameof(ConduitTemplate.Arrangement)));
                return issues;
            }
            foreach ((string field, int n, double pitch, string axis) in new[] { (nameof(a.HorizontalPitchM), a.Columns, a.HorizontalPitchM, "across"), (nameof(a.VerticalPitchM), a.Rows, a.VerticalPitchM, "up") })
                if (n > 1 && !(double.IsFinite(pitch) && pitch > 0 && pitch <= ConduitLimits.MaxPitchM))
                    issues.Add(OperationIssue.Error(ConduitIssueCodes.TemplatePitch, $"{who}: the spacing {axis} must be more than 0 and at most {ConduitLimits.MaxPitchM} m.", field));
            issues.AddRange(ValidateSection(t.DefaultSection ?? ConduitSection.Centred, who));

            ConduitDesignReference d = t.Design;
            if (d != null)
            {
                foreach ((string name, double? v) in new[] { ("conduit diameter", d.ConduitOutsideDiameterM), ("clear gap", d.MinClearGapM), ("edge clearance", d.EdgeClearanceM), ("envelope width", d.EnvelopeWidthM), ("envelope height", d.EnvelopeHeightM) })
                    if (v.HasValue && !(double.IsFinite(v.Value) && v.Value >= 0 && v.Value <= ConduitLimits.MaxPitchM))
                        issues.Add(OperationIssue.Error(ConduitIssueCodes.TemplateDesign, $"{who}: the stated {name} is not a valid length."));
                if (issues.Any(i => i.Severity == IssueSeverity.Error)) return issues;
                if (d.ConduitOutsideDiameterM is double od && od > 0)
                {
                    foreach ((int n, double pitch, string axis) in new[] { (a.Columns, a.HorizontalPitchM, "across"), (a.Rows, a.VerticalPitchM, "up") })
                    {
                        if (n < 2) continue;
                        if (pitch < od - SizeTolerance)
                            issues.Add(OperationIssue.Error(ConduitIssueCodes.TemplateOverlap, $"{who}: the spacing {axis} ({Mm(pitch)}) is less than the conduit diameter ({Mm(od)}), so conduits would overlap."));
                        else if (d.MinClearGapM is double gap && pitch - od < gap - SizeTolerance)
                            issues.Add(OperationIssue.Warning(ConduitIssueCodes.TemplateGapBelowReference, $"{who}: the spacing {axis} leaves a {Mm(pitch - od)} gap, less than the stated {Mm(gap)}."));
                    }
                    if (d.EdgeClearanceM is double edge)
                    {
                        double w = (a.Columns - 1) * a.HorizontalPitchM + od + 2 * edge, h = (a.Rows - 1) * a.VerticalPitchM + od + 2 * edge;
                        if ((d.EnvelopeWidthM is double ew && Math.Abs(ew - w) > ConduitLimits.SizeMatchToleranceM)
                            || (d.EnvelopeHeightM is double eh && Math.Abs(eh - h) > ConduitLimits.SizeMatchToleranceM))
                            issues.Add(OperationIssue.Warning(ConduitIssueCodes.TemplateEnvelopeInconsistent, $"{who}: the stated envelope does not equal the grid plus the conduit diameter and edge clearances ({Mm(w)} × {Mm(h)})."));
                    }
                }
            }
            IReadOnlyList<ConstructionVariant> variants = t.Variants;
            if (variants.Any(v => v == null || string.IsNullOrWhiteSpace(v.Id)) || variants.GroupBy(v => v?.Id?.Trim() ?? "", StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
                issues.Add(OperationIssue.Error(ConduitIssueCodes.TemplateVariant, $"{who}: every construction variant needs its own ID."));
            return issues;
        }

        /// <summary>Checks a library: each template, and keys unique (ignoring case).</summary>
        public static List<OperationIssue> ValidateLibrary(IEnumerable<ConduitTemplate> templates)
        {
            List<ConduitTemplate> list = (templates ?? Enumerable.Empty<ConduitTemplate>()).ToList();
            List<OperationIssue> issues = list.SelectMany(ValidateTemplate).ToList();
            foreach (IGrouping<string, ConduitTemplate> dup in list.Where(t => !string.IsNullOrWhiteSpace(t?.Key)).GroupBy(t => t.Key.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                issues.Add(OperationIssue.Error(ConduitIssueCodes.TemplateKeyDuplicate, $"More than one template has the key \"{dup.Key}\"."));
            return issues;
        }

        private static IEnumerable<OperationIssue> ValidateSection(ConduitSection s, string who)
        {
            if (!Enum.IsDefined(typeof(ConduitPlacement), s.Placement))
                yield return OperationIssue.Error(ConduitIssueCodes.SectionPlacement, $"{who}: choose centred or bottom-referenced placement.", nameof(ConduitSection.Placement));
            if (s.Placement == ConduitPlacement.BottomReferenced && !(double.IsFinite(s.BottomToLowestCentreM) && s.BottomToLowestCentreM > 0 && s.BottomToLowestCentreM <= ConduitLimits.MaxBottomToLowestCentreM))
                yield return OperationIssue.Error(ConduitIssueCodes.SectionBottom, $"{who}: the lowest row's height above the bottom must be more than 0 and at most {ConduitLimits.MaxBottomToLowestCentreM} m.", nameof(ConduitSection.BottomToLowestCentreM));
            if (!(double.IsFinite(s.SideShiftM) && Math.Abs(s.SideShiftM) <= ConduitLimits.MaxSideShiftM))
                yield return OperationIssue.Error(ConduitIssueCodes.SectionShift, $"{who}: the side shift must be between -{ConduitLimits.MaxSideShiftM} and {ConduitLimits.MaxSideShiftM} m.", nameof(ConduitSection.SideShiftM));
        }

        // ---------- Requests ----------

        /// <summary>The request's own checks (no drawing needed). Errors reject it; warnings go to the preview.</summary>
        public static List<OperationIssue> Validate(ConduitRequest r)
        {
            List<OperationIssue> issues = new List<OperationIssue>();
            if (r == null) { issues.Add(OperationIssue.Error(ConduitIssueCodes.DrawingMissing, "There is no request.")); return issues; }
            if (r.Drawing == null || !r.Drawing.IsComplete)
                issues.Add(OperationIssue.Error(ConduitIssueCodes.DrawingMissing, "The drawing is not known. Open the drawing and try again.", nameof(r.Drawing)));
            if (!(double.IsFinite(r.MetresPerDrawingUnit) && Math.Abs(r.MetresPerDrawingUnit - 1.0) <= 1e-9))
                issues.Add(OperationIssue.Error(ConduitIssueCodes.UnitsNotMetres, "Generate Conduits works in metre drawings only. This drawing's units are not metres; its settings are left as they are.", nameof(r.MetresPerDrawingUnit)));

            if (r.Encasements.Count == 0)
                issues.Add(OperationIssue.Error(ConduitIssueCodes.EncasementsNone, "Choose the duct bank (encasement) pipes to fill.", nameof(r.Encasements)));
            foreach (DrawingObjectRef bad in r.Encasements.Where(e => !e.IsValid))
                issues.Add(OperationIssue.Error(ConduitIssueCodes.EncasementInvalid, "A chosen encasement is not a drawing object.", nameof(r.Encasements)));
            foreach (DrawingObjectRef dup in r.Encasements.Where(e => e.IsValid).GroupBy(e => e).Where(g => g.Count() > 1).Select(g => g.Key))
                issues.Add(OperationIssue.Error(ConduitIssueCodes.EncasementDuplicate, $"Encasement {dup.Handle} is chosen more than once.", nameof(r.Encasements), dup));

            issues.AddRange(ValidateTemplate(r.Template).Select(i => i with { Field = i.Field ?? nameof(r.Template) }));
            if (r.Template != null && r.VariantId != null && !r.Template.Variants.Any(v => string.Equals(v.Id, r.VariantId, StringComparison.OrdinalIgnoreCase)))
                issues.Add(OperationIssue.Error(ConduitIssueCodes.VariantUnknown, $"\"{r.VariantId}\" is not a construction variant of {r.Template.Name}.", nameof(r.VariantId)));
            if (r.Section != null) issues.AddRange(ValidateSection(r.Section, "Section"));

            ConduitPartChoice part = r.Part;
            if (part == null || !part.IsComplete)
                issues.Add(OperationIssue.Error(ConduitIssueCodes.PartMissing, "Choose the conduit part (parts list, family and size, or From drawing…).", nameof(r.Part)));
            else if (!(double.IsFinite(part.OutsideDiameterM) && part.OutsideDiameterM >= 0 && part.OutsideDiameterM <= ConduitLimits.MaxPitchM))
                issues.Add(OperationIssue.Error(ConduitIssueCodes.PartDiameter, "The conduit part's outside diameter could not be read as a length.", nameof(r.Part)));
            else
            {
                if (part.OutsideDiameterM == 0)
                    issues.Add(OperationIssue.Error(ConduitIssueCodes.PartDiameterUnknown, $"{part.Label}: its outside diameter could not be read from the part, so the fit cannot be checked. Choose a part whose size can be read, or measure it.", nameof(r.Part)));
                else
                {
                    if (r.Template?.Design?.ConduitOutsideDiameterM is double od && Math.Abs(od - part.OutsideDiameterM) > ConduitLimits.SizeMatchToleranceM)
                        issues.Add(OperationIssue.Warning(ConduitIssueCodes.PartDiameterDiffers, $"{part.Label} is {Mm(part.OutsideDiameterM)} outside; {r.Template.Name} states {Mm(od)}.", nameof(r.Part)));
                    // Overlap depends only on the part and the pitches, so it is refused here for every encasement at once.
                    if (r.Template?.Arrangement is ConduitArrangement a)
                        foreach ((int n, double pitch, string axis) in new[] { (a.Columns, a.HorizontalPitchM, "across"), (a.Rows, a.VerticalPitchM, "up") })
                            if (n > 1 && double.IsFinite(pitch) && part.OutsideDiameterM > pitch + SizeTolerance)
                                issues.Add(OperationIssue.Error(ConduitIssueCodes.PartOverlap, $"{part.Label} is {Mm(part.OutsideDiameterM)} outside, more than the {Mm(pitch)} spacing {axis} of {r.Template.Name}: neighbouring conduits would overlap by {Mm(part.OutsideDiameterM - pitch)}.", nameof(r.Part)));
                }
                if (part.Template.HasValue && !part.PartsList.IsValid && r.Destination == ConduitDestination.NewNetwork)
                    issues.Add(OperationIssue.Warning(ConduitIssueCodes.PartStaysInTemplateNetwork, $"{part.Label} has no part family, so its conduits stay in the network of the conduit it is copied from, not in a new network.", nameof(r.Destination)));
            }

            if (!Enum.IsDefined(typeof(ConduitDestination), r.Destination))
                issues.Add(OperationIssue.Error(ConduitIssueCodes.NetworkName, "Choose where the conduits go.", nameof(r.Destination)));
            else if (r.Destination == ConduitDestination.NewNetwork && string.IsNullOrWhiteSpace(r.NewNetworkName))
                issues.Add(OperationIssue.Error(ConduitIssueCodes.NetworkName, "Name the new network.", nameof(r.NewNetworkName)));

            ConduitEndRules ends = r.Ends ?? new ConduitEndRules();
            if (!Enum.IsDefined(typeof(StructureEndTreatment), ends.AtStructure) || !Enum.IsDefined(typeof(JointTreatment), ends.AtJoint) || !Enum.IsDefined(typeof(UnknownFootprintPolicy), ends.WhenFootprintUnknown))
                issues.Add(OperationIssue.Error(ConduitIssueCodes.EndsOption, "Choose how conduit ends are made.", nameof(r.Ends)));
            if (!(double.IsFinite(ends.StructureSearchM) && ends.StructureSearchM >= 0 && ends.StructureSearchM <= ConduitLimits.MaxStructureSearchM))
                issues.Add(OperationIssue.Error(ConduitIssueCodes.EndsSearch, $"The structure search distance must be 0 to {ConduitLimits.MaxStructureSearchM} m.", nameof(ends.StructureSearchM)));
            if (ends.ConnectToStructures && r.Destination == ConduitDestination.NewNetwork)
                issues.Add(OperationIssue.Error(ConduitIssueCodes.ConnectNeedsSameNetwork, "Conduits can be connected to structures only in the encasement's own network. Turn connection off, or put the conduits in the encasement's network.", nameof(ends.ConnectToStructures)));
            foreach (StructureInnerBox box in ends.InnerBoxes)
                if (box == null || string.IsNullOrWhiteSpace(box.PartSizeName) || !InRange(box.InnerWidthM, ConduitLimits.MaxInnerBoxM) || (!box.Round && !InRange(box.InnerLengthM, ConduitLimits.MaxInnerBoxM)))
                    issues.Add(OperationIssue.Error(ConduitIssueCodes.InnerBoxInvalid, $"The inner box for \"{box?.PartSizeName}\" needs a structure size name and sides more than 0 and at most {ConduitLimits.MaxInnerBoxM} m.", nameof(ends.InnerBoxes)));
            foreach (var dup in ends.InnerBoxes.Where(b => !string.IsNullOrWhiteSpace(b?.PartSizeName))
                         .GroupBy(b => ((b.PartFamilyName ?? "").Trim().ToUpperInvariant(), b.PartSizeName.Trim().ToUpperInvariant())).Where(g => g.Count() > 1))
                issues.Add(OperationIssue.Error(ConduitIssueCodes.InnerBoxDuplicate, $"More than one inner box is given for \"{dup.First().PartSizeName.Trim()}\"{(dup.Key.Item1.Length > 0 ? $" ({dup.First().PartFamilyName.Trim()})" : "")}.", nameof(ends.InnerBoxes)));
            HashSet<DrawingObjectRef> chosen = new HashSet<DrawingObjectRef>(r.Encasements);
            foreach (ConduitEndOverride o in ends.Overrides)
                if (o == null || !o.Encasement.IsValid || !chosen.Contains(o.Encasement) || !Enum.IsDefined(typeof(PipeEnd), o.End)
                    || (o.Treatment.HasValue && !Enum.IsDefined(typeof(StructureEndTreatment), o.Treatment.Value)))
                    issues.Add(OperationIssue.Error(ConduitIssueCodes.OverrideInvalid, "An end exception names an encasement that is not chosen, or an unknown treatment.", nameof(ends.Overrides), o?.Encasement));
                else if (o.Connect == true && r.Destination == ConduitDestination.NewNetwork)
                    issues.Add(OperationIssue.Error(ConduitIssueCodes.ConnectNeedsSameNetwork, $"Encasement {o.Encasement.Handle}: a conduit can be connected only in the encasement's own network.", nameof(ends.Overrides), o.Encasement));
            foreach (var dup in ends.Overrides.Where(o => o != null).GroupBy(o => (o.Encasement, o.End)).Where(g => g.Count() > 1))
                issues.Add(OperationIssue.Error(ConduitIssueCodes.OverrideDuplicate, $"Encasement {dup.Key.Encasement.Handle} has more than one exception for its {dup.Key.End.ToString().ToLowerInvariant()}.", nameof(ends.Overrides), dup.Key.Encasement));
            foreach (StructureChoice c in ends.StructureChoices)
                if (c == null || !c.Encasement.IsValid || !chosen.Contains(c.Encasement) || !c.Structure.IsValid || !Enum.IsDefined(typeof(PipeEnd), c.End))
                    issues.Add(OperationIssue.Error(ConduitIssueCodes.StructureChoiceInvalid, "A structure pick names an encasement that is not chosen, or no structure.", nameof(ends.StructureChoices), c?.Encasement));
                else if (!ends.MatchUnconnectedEnds)
                    issues.Add(OperationIssue.Error(ConduitIssueCodes.StructureChoiceInvalid, $"Encasement {c.Encasement.Handle}: structure picks apply only when unconnected ends are matched to nearby structures. Turn that on, or remove the pick.", nameof(ends.StructureChoices), c.Encasement));
            foreach (var dup in ends.StructureChoices.Where(c => c != null).GroupBy(c => (c.Encasement, c.End)).Where(g => g.Count() > 1))
                issues.Add(OperationIssue.Error(ConduitIssueCodes.StructureChoiceDuplicate, $"Encasement {dup.Key.Encasement.Handle} has more than one structure picked for its {dup.Key.End.ToString().ToLowerInvariant()}.", nameof(ends.StructureChoices), dup.Key.Encasement));
            return issues;
        }

        /// <summary>
        /// The section's item issues for one encasement. Overlapping conduits always block it (Error), whatever
        /// <paramref name="allowOutside"/> says; conduits crossing its edge block it unless <paramref name="allowOutside"/>
        /// (then a warning); a size different from the template's stated envelope is a warning (it is never resized).
        /// </summary>
        public static List<OperationIssue> FitIssues(ConduitFitCheck fit, bool allowOutside, string name, DrawingObjectRef encasement)
        {
            List<OperationIssue> issues = new List<OperationIssue>();
            if (fit.ConduitsOverlap)
                issues.Add(OperationIssue.Error(ConduitIssueCodes.ConduitsOverlap, $"{name}: neighbouring {Mm(fit.ConduitOutsideDiameterM)} conduits overlap by {Mm(-fit.MinClearGapM.Value)}; it is left out.", null, encasement));
            if (fit.ConduitsOutside > 0)
                issues.Add(allowOutside
                    ? OperationIssue.Warning(ConduitIssueCodes.ConduitsOutside, $"{name}: {fit.ConduitsOutside} conduit(s) cross its outside edge ({Mm(fit.EncasementWidthM)} × {Mm(fit.EncasementHeightM)}); filled anyway, as asked.", null, encasement)
                    : OperationIssue.Error(ConduitIssueCodes.ConduitsOutside, $"{name}: {fit.ConduitsOutside} conduit(s) would cross its outside edge ({Mm(fit.EncasementWidthM)} × {Mm(fit.EncasementHeightM)}); it is left out.", null, encasement));
            if (fit.TemplateEnvelopeMatches == false)
                issues.Add(OperationIssue.Warning(ConduitIssueCodes.EnvelopeDiffers, $"{name} is {Mm(fit.EncasementWidthM)} × {Mm(fit.EncasementHeightM)}, not the template's stated section; it is not resized.", null, encasement));
            return issues;
        }

        /// <summary>
        /// After Apply: mitred joints whose other side did not commit. One warning per joint; the conduits made are kept (each
        /// encasement is its own unit). Filling the other encasement is suggested only when it is confirmed to have no new
        /// conduits: not chosen, not attempted, rejected or rolled back. When its outcome is unknown it may already hold
        /// conduits, so a retry could duplicate them: the joint is reported for inspection instead.
        /// </summary>
        public static List<OperationIssue> IncompleteRuns(ConduitPreview plan, IReadOnlyList<ConduitItemOutcome> outcomes)
        {
            List<OperationIssue> issues = new List<OperationIssue>();
            if (plan == null || outcomes == null) return issues;
            Dictionary<DrawingObjectRef, ItemStatus> status = outcomes.GroupBy(o => o.Encasement).ToDictionary(g => g.Key, g => g.First().Status);
            Dictionary<DrawingObjectRef, ConduitSourcePlan> items = plan.Items.GroupBy(i => i.Encasement).ToDictionary(g => g.Key, g => g.First());
            HashSet<(DrawingObjectRef, DrawingObjectRef)> said = new HashSet<(DrawingObjectRef, DrawingObjectRef)>();
            foreach (ConduitSourcePlan item in plan.Items.Where(i => status.TryGetValue(i.Encasement, out ItemStatus s) && s == ItemStatus.Committed))
                foreach (ConduitEndPlan end in new[] { item.StartEnd, item.EndEnd })
                {
                    if (end?.Kind != ConduitEndKind.Joint || end.Joint != JointTreatment.Mitre || !(end.Neighbour is DrawingObjectRef n)) continue;
                    // Not in the outcomes at all: it was not chosen, so it is confirmed untouched.
                    bool known = status.TryGetValue(n, out ItemStatus other);
                    if (known && other == ItemStatus.Committed) continue;
                    if (!said.Add((item.Encasement, n))) continue;
                    string name = items.TryGetValue(n, out ConduitSourcePlan o) ? o.Name : n.Handle;
                    issues.Add(known && other == ItemStatus.OutcomeUnknown
                        ? OperationIssue.Warning(ConduitIssueCodes.RunIncomplete, $"Run {item.RunId}: {item.Name} has conduits mitred to {name}, whose outcome is not known. Inspect {name} and the joint in the drawing before filling it again: it may already hold conduits.", null, item.Encasement)
                        : OperationIssue.Warning(ConduitIssueCodes.RunIncomplete, $"Run {item.RunId} is incomplete: {item.Name} has conduits mitred to {name}, which has none. Fill {name} (preview again) to complete the joint.", null, item.Encasement));
                }
            return issues;
        }

        /// <summary>The treatment and connection for one structure end: the request's rule, then that end's override if any.</summary>
        public static (StructureEndTreatment Treatment, bool Connect, bool Overridden) ResolveStructureEnd(ConduitEndRules rules, DrawingObjectRef encasement, PipeEnd end)
        {
            rules ??= new ConduitEndRules();
            ConduitEndOverride o = rules.Overrides.FirstOrDefault(x => x != null && x.Encasement == encasement && x.End == end);
            return (o?.Treatment ?? rules.AtStructure, o?.Connect ?? rules.ConnectToStructures, o != null && (o.Treatment.HasValue || o.Connect.HasValue));
        }

        /// <summary>
        /// The user's inner box for a structure's part (names ignoring case): one naming its family and size first, then one
        /// naming the size only; null when none.
        /// </summary>
        public static StructureInnerBox InnerBoxFor(ConduitEndRules rules, string partFamilyName, string partSizeName)
        {
            if (string.IsNullOrWhiteSpace(partSizeName)) return null;
            static bool Same(string a, string b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
            List<StructureInnerBox> sized = (rules ?? new ConduitEndRules()).InnerBoxes.Where(b => b != null && Same(b.PartSizeName, partSizeName)).ToList();
            return sized.FirstOrDefault(b => !string.IsNullOrWhiteSpace(b.PartFamilyName) && Same(b.PartFamilyName, partFamilyName))
                ?? sized.FirstOrDefault(b => string.IsNullOrWhiteSpace(b.PartFamilyName));
        }

        // ---------- Section ----------

        /// <summary>
        /// Every conduit centre in an encasement's section (outside height <paramref name="encasementHeightM"/>), row by row from
        /// the bottom, left to right along the run. Centred: the grid's middle on the centreline. Bottom-referenced: the lowest
        /// row's centre the given height above the outside bottom. The side shift moves every column.
        /// </summary>
        public static IReadOnlyList<SectionCentre> SectionCentres(ConduitArrangement a, ConduitSection s, double encasementHeightM)
        {
            s ??= ConduitSection.Centred;
            IReadOnlyList<double> across = a.HorizontalOffsetsM(), up = a.VerticalOffsetsM();
            List<SectionCentre> list = new List<SectionCentre>(a.Count);
            for (int row = 0; row < up.Count; row++)
            {
                double y = s.Placement == ConduitPlacement.BottomReferenced
                    ? -encasementHeightM / 2.0 + s.BottomToLowestCentreM + row * a.VerticalPitchM
                    : up[row];
                for (int col = 0; col < across.Count; col++)
                    list.Add(new SectionCentre(col + 1, row + 1, Math.Round(across[col] + s.SideShiftM, 9), Math.Round(y, 9)));
            }
            return Frozen.List(list);
        }

        /// <summary>
        /// Checks one encasement's section with the chosen part: conduits crossing the outside edge, least edge clearance, least
        /// clear gap between neighbours, and whether the encasement and part match the template's stated sizes. With a 0
        /// diameter only centres are checked (a centre on or past the edge counts as outside).
        /// </summary>
        public static ConduitFitCheck CheckSection(ConduitTemplate template, ConduitSection section, double encasementWidthM, double encasementHeightM, double conduitOutsideDiameterM)
        {
            ConduitArrangement a = template.Arrangement;
            double r = Math.Max(0, conduitOutsideDiameterM) / 2.0;
            IReadOnlyList<SectionCentre> centres = SectionCentres(a, section, encasementHeightM);
            int outside = 0;
            double minEdge = double.PositiveInfinity;
            foreach (SectionCentre c in centres)
            {
                double edge = Math.Min(encasementWidthM / 2.0 - Math.Abs(c.AcrossM), encasementHeightM / 2.0 - Math.Abs(c.UpM)) - r;
                minEdge = Math.Min(minEdge, edge);
                if (edge < -PositionTolerance || (r == 0 && edge <= PositionTolerance)) outside++;
            }
            double? gap = null;
            if (a.Columns > 1) gap = a.HorizontalPitchM - 2 * r;
            if (a.Rows > 1) gap = Math.Min(gap ?? double.PositiveInfinity, a.VerticalPitchM - 2 * r);
            ConduitDesignReference d = template.Design;
            bool? envelope = d?.EnvelopeWidthM is double ew && d.EnvelopeHeightM is double eh
                ? Math.Abs(ew - encasementWidthM) <= ConduitLimits.SizeMatchToleranceM && Math.Abs(eh - encasementHeightM) <= ConduitLimits.SizeMatchToleranceM
                : null;
            bool? diameter = d?.ConduitOutsideDiameterM is double od && conduitOutsideDiameterM > 0
                ? Math.Abs(od - conduitOutsideDiameterM) <= ConduitLimits.SizeMatchToleranceM
                : null;
            return new ConduitFitCheck(encasementWidthM, encasementHeightM, Math.Max(0, conduitOutsideDiameterM), outside,
                Math.Round(minEdge, 9), gap.HasValue ? Math.Round(gap.Value, 9) : null, envelope, diameter);
        }

        // ---------- Apply ----------

        /// <summary>Checks an apply request against the current drawing: a succeeded plan for this drawing, each chosen encasement an applicable item, chosen once.</summary>
        public static List<OperationIssue> ValidateApply(ConduitApplyRequest request, DrawingIdentity current)
        {
            List<OperationIssue> issues = new List<OperationIssue>();
            ConduitPreview plan = request?.Preview;
            if (plan == null || plan.Status != OperationStatus.Succeeded || plan.Items.Count == 0)
            {
                issues.Add(OperationIssue.Error(ConduitIssueCodes.PlanNotApplicable, "There is no conduit plan to apply. Preview first."));
                return issues;
            }
            if (plan.Drawing == null || !plan.Drawing.Matches(current))
                issues.Add(OperationIssue.Error(ConduitIssueCodes.DrawingMismatch, "The plan was made for another drawing (or before this drawing was reopened). Preview again in this drawing."));
            Dictionary<DrawingObjectRef, ConduitSourcePlan> items = plan.Items.GroupBy(i => i.Encasement).ToDictionary(g => g.Key, g => g.First());
            IReadOnlyList<DrawingObjectRef> chosen = request.Chosen;
            foreach (DrawingObjectRef c in chosen.Distinct())
                if (!items.TryGetValue(c, out ConduitSourcePlan item))
                    issues.Add(OperationIssue.Error(ConduitIssueCodes.ChosenNotInPlan, $"Encasement {c.Handle} is not in the plan. Preview again.", null, c));
                else if (!item.CanApply)
                    issues.Add(OperationIssue.Error(ConduitIssueCodes.ChosenNotApplicable, $"{item.Name} cannot be filled (see its notes); untick it or fix the cause and preview again.", null, c));
            foreach (DrawingObjectRef dup in chosen.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key))
                issues.Add(OperationIssue.Error(ConduitIssueCodes.ChosenDuplicate, $"Encasement {dup.Handle} is chosen more than once.", null, dup));
            return issues;
        }

        /// <summary>
        /// Whether an encasement's plan still holds just before it is filled: null when it does; otherwise why it is stale. Reads
        /// the encasement, the structures at its ends and its joint neighbours again through the given functions (null = gone).
        /// A neighbour is compared by its snapshot in <paramref name="plannedNeighbours"/>. The part and destination the fit
        /// and connection checks relied on are resolved again: <paramref name="resolveDestination"/> says where the conduits
        /// would go now, and <paramref name="resolvePart"/> which part they would be for that destination (null = cannot be
        /// resolved). A different network, part identity or diameter makes the item stale.
        /// </summary>
        public static OperationIssue Compare(ConduitSourcePlan item, Func<DrawingObjectRef, EncasementSnapshot> readEncasement,
            Func<DrawingObjectRef, StructureSnapshot> readStructure, IReadOnlyDictionary<DrawingObjectRef, EncasementSnapshot> plannedNeighbours,
            Func<ConduitSourcePlan, ConduitDestinationPlan> resolveDestination, Func<ConduitSourcePlan, ConduitPartSnapshot> resolvePart)
        {
            EncasementSnapshot now = readEncasement(item.Encasement);
            if (now == null)
                return OperationIssue.Error(ConduitIssueCodes.EncasementMissing, $"{item.Name} is no longer in the drawing; no conduits were made in it.", null, item.Encasement);
            List<string> what = EncasementChanges(item.Snapshot, now);
            if (what.Count > 0)
                return OperationIssue.Error(ConduitIssueCodes.EncasementChanged, $"{item.Name} changed since the preview ({string.Join(", ", what)}); no conduits were made in it. Preview again.", null, item.Encasement);
            foreach (ConduitEndPlan end in new[] { item.StartEnd, item.EndEnd })
            {
                if (end?.Structure is StructureSnapshot planned && !SameStructure(planned, readStructure(planned.Structure)))
                    return OperationIssue.Error(ConduitIssueCodes.StructureChanged, $"{item.Name}: structure {planned.Name} moved, changed size or was removed since the preview; no conduits were made in it. Preview again.", null, item.Encasement);
                if (end?.Neighbour is DrawingObjectRef n && end.Joint == JointTreatment.Mitre)
                {
                    EncasementSnapshot nowN = readEncasement(n);
                    if (nowN == null || !plannedNeighbours.TryGetValue(n, out EncasementSnapshot wasN) || EncasementChanges(wasN, nowN).Count > 0)
                        return OperationIssue.Error(ConduitIssueCodes.NeighbourChanged, $"{item.Name}: the encasement joined to its {end.End.ToString().ToLowerInvariant()} changed since the preview, so the mitre no longer holds; no conduits were made in it. Preview again.", null, item.Encasement);
                }
            }
            ConduitDestinationPlan dest = resolveDestination(item);
            if (dest == null || item.Destination == null || dest.CreatedAtApply != item.Destination.CreatedAtApply || dest.Network != item.Destination.Network)
                return OperationIssue.Error(ConduitIssueCodes.DestinationChanged, $"{item.Name}: the network its conduits go in changed or is gone since the preview; no conduits were made in it. Preview again.", null, item.Encasement);
            ConduitPartSnapshot part = resolvePart(item);
            if (part == null || item.Part == null || !SamePart(item.Part, part))
                return OperationIssue.Error(ConduitIssueCodes.PartChanged, $"{item.Name}: the conduit part{(part == null ? " can no longer be found" : " changed")} since the preview, so its fit check no longer holds; no conduits were made in it. Preview again.", null, item.Encasement);
            return null;
        }

        private static bool SamePart(ConduitPartSnapshot was, ConduitPartSnapshot now) =>
            string.Equals(was.FamilyKey ?? "", now.FamilyKey ?? "", StringComparison.OrdinalIgnoreCase)
            && string.Equals(was.SizeName ?? "", now.SizeName ?? "", StringComparison.OrdinalIgnoreCase)
            && Math.Abs(was.OutsideDiameterM - now.OutsideDiameterM) <= SizeTolerance
            && was.Template == now.Template;

        private static List<string> EncasementChanges(EncasementSnapshot was, EncasementSnapshot now)
        {
            List<string> what = new List<string>();
            if (was.Start.DistanceTo(now.Start) > PositionTolerance || was.End.DistanceTo(now.End) > PositionTolerance) what.Add("moved");
            bool sameBulge = double.IsNaN(was.Bulge) || double.IsNaN(now.Bulge) ? double.IsNaN(was.Bulge) && double.IsNaN(now.Bulge) : Math.Abs(was.Bulge - now.Bulge) <= BulgeTolerance;
            if (was.Curved != now.Curved || !sameBulge) what.Add("its curve changed");
            if (!string.Equals(was.PartSizeName ?? "", now.PartSizeName ?? "", StringComparison.OrdinalIgnoreCase)
                || Math.Abs(was.OuterWidthM - now.OuterWidthM) > SizeTolerance || Math.Abs(was.OuterHeightM - now.OuterHeightM) > SizeTolerance) what.Add("its size changed");
            if (was.Network != now.Network) what.Add("it moved to another network");
            if (was.StartStructure != now.StartStructure || was.EndStructure != now.EndStructure) what.Add("its connections changed");
            return what;
        }

        private static bool SameStructure(StructureSnapshot was, StructureSnapshot now) =>
            now != null && was.Location.DistanceTo(now.Location) <= PositionTolerance && Math.Abs(was.RotationRad - now.RotationRad) <= RotationTolerance
            && was.Source == now.Source && was.Round == now.Round && Math.Abs(was.InnerWidthM - now.InnerWidthM) <= SizeTolerance
            && Math.Abs(was.InnerLengthM - now.InnerLengthM) <= SizeTolerance && Math.Abs(was.WallM - now.WallM) <= SizeTolerance;

        private static bool InRange(double v, double max) => double.IsFinite(v) && v > 0 && v <= max;

        private static string Mm(double m) => $"{Math.Round(m * 1000, 1):0.#} mm";
    }
}
