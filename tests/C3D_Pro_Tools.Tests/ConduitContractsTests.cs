using System;
using System.Collections.Generic;
using System.Linq;
using C3DProTools.Features.Networks.Contracts;
using Xunit;

namespace C3DProTools.Tests
{
    public class ConduitContractsTests
    {
        private static readonly DrawingIdentity Dwg = new DrawingIdentity("s1", "F1", @"C:\x.dwg", "m");
        private static DrawingObjectRef R(string h) => DrawingObjectRef.Of(h);
        private static ConduitTemplate T(string key) => ConduitTemplateLibrary.Find(key);

        private static ConduitRequest Request(string key = "ICT-2C-3R", Func<ConduitRequest, ConduitRequest> edit = null)
        {
            ConduitRequest r = new ConduitRequest
            {
                Drawing = Dwg,
                Encasements = new[] { R("1A"), R("1B") },
                Template = T(key),
                Part = new ConduitPartChoice(R("50"), "", "HDPE Duct", "110 mm OD", 0.110),
            };
            return edit == null ? r : edit(r);
        }

        private static string[] Codes(IEnumerable<OperationIssue> issues) => issues.Select(i => i.Code).ToArray();

        // ---------- Template data ----------

        [Fact]
        public void Standard_library_has_the_seven_templates_and_no_issues()
        {
            IReadOnlyList<ConduitTemplate> lib = ConduitTemplateLibrary.Standard();
            Assert.Equal(new[] { "ICT-2C-1R", "ICT-2C-3R", "ICT-4C-6R", "ICT-4C-3R", "ICT-1C-1R-50", "MV-5C-3R", "MV-5C-1R" }, lib.Select(t => t.Key));
            Assert.Empty(ConduitValidation.ValidateLibrary(lib));
            Assert.All(lib, t => Assert.True(t.IsStandard));
        }

        [Theory]
        // Columns and rows are explicit; the drawing label is kept as written and never parsed.
        [InlineData("ICT-2C-1R", "1X2", 2, 1, "RowsByColumns")]
        [InlineData("ICT-2C-3R", "3X2", 2, 3, "RowsByColumns")]
        [InlineData("ICT-4C-6R", "6X4", 4, 6, "RowsByColumns")]
        [InlineData("ICT-4C-3R", "3X4", 4, 3, "RowsByColumns")]
        [InlineData("ICT-1C-1R-50", "1X1", 1, 1, "RowsByColumns")]
        [InlineData("MV-5C-3R", "5 X 3", 5, 3, "ColumnsByRows")]
        [InlineData("MV-5C-1R", "5 X 1", 5, 1, "ColumnsByRows")]
        public void Label_and_grid_follow_each_drawings_convention(string key, string label, int columns, int rows, string order)
        {
            ConduitTemplate t = T(key);
            Assert.Equal(label, t.Source.Label);
            Assert.Equal(order, t.Source.LabelOrder.ToString());
            Assert.Equal(columns, t.Arrangement.Columns);
            Assert.Equal(rows, t.Arrangement.Rows);
            Assert.Equal(columns * rows, t.Arrangement.Count);
        }

        [Theory]
        // The offsets table of the drawing summary, section 5.
        [InlineData("ICT-2C-1R", new[] { -0.080, 0.080 }, new[] { 0.0 })]
        [InlineData("ICT-2C-3R", new[] { -0.080, 0.080 }, new[] { -0.160, 0, 0.160 })]
        [InlineData("ICT-4C-6R", new[] { -0.240, -0.080, 0.080, 0.240 }, new[] { -0.400, -0.240, -0.080, 0.080, 0.240, 0.400 })]
        [InlineData("ICT-4C-3R", new[] { -0.240, -0.080, 0.080, 0.240 }, new[] { -0.160, 0, 0.160 })]
        [InlineData("ICT-1C-1R-50", new[] { 0.0 }, new[] { 0.0 })]
        [InlineData("MV-5C-3R", new[] { -0.600, -0.300, 0, 0.300, 0.600 }, new[] { -0.500, 0, 0.500 })]
        [InlineData("MV-5C-1R", new[] { -0.600, -0.300, 0, 0.300, 0.600 }, new[] { 0.0 })]
        public void Offsets_match_the_summary(string key, double[] across, double[] up)
        {
            ConduitArrangement a = T(key).Arrangement;
            Assert.Equal(across.Select(x => Math.Round(x, 6)), a.HorizontalOffsetsM().Select(x => Math.Round(x, 6)));
            Assert.Equal(up.Select(x => Math.Round(x, 6)), a.VerticalOffsetsM().Select(x => Math.Round(x, 6)));
        }

        [Theory]
        // ICT envelopes: grid + 110 mm OD + 2 × 100 mm edge (the sheet's dimension chains); also the sizes in the live model.
        [InlineData("ICT-2C-1R", 0.470, 0.310)]
        [InlineData("ICT-2C-3R", 0.470, 0.630)]
        [InlineData("ICT-4C-6R", 0.790, 1.110)]
        [InlineData("ICT-4C-3R", 0.790, 0.630)]
        [InlineData("ICT-1C-1R-50", 0.250, 0.250)]
        public void Ict_envelopes_match_the_sheet(string key, double width, double height)
        {
            ConduitDesignReference d = T(key).Design;
            Assert.Equal(width, d.EnvelopeWidthM.Value, 6);
            Assert.Equal(height, d.EnvelopeHeightM.Value, 6);
        }

        [Fact]
        public void Mv_states_no_diameter_or_envelope_and_says_so()
        {
            foreach (string key in new[] { "MV-5C-3R", "MV-5C-1R" })
            {
                ConduitTemplate t = T(key);
                Assert.Null(t.Design.ConduitOutsideDiameterM);
                Assert.Null(t.Design.EnvelopeWidthM);
                Assert.Contains(t.Notes, n => n.Contains("outside diameter"));
                Assert.Contains(t.Notes, n => n.Contains("placement"));
            }
        }

        [Fact]
        public void Variants_are_metadata_with_source_ids()
        {
            Assert.Equal(new[] { "2A", "2B" }, T("ICT-2C-3R").Variants.Select(v => v.Id));
            Assert.Equal(new[] { "1C" }, T("ICT-1C-1R-50").Variants.Select(v => v.Id));
            Assert.Equal(new[] { "Type 01", "Type 02" }, T("MV-5C-3R").Variants.Select(v => v.Id));
            Assert.Equal(ConduitTemplateLibrary.MvRevision, T("MV-5C-1R").Source.Revision);
            Assert.Equal(ConduitTemplateLibrary.IctDrawing, T("ICT-4C-6R").Source.DrawingNumber);
        }

        [Fact]
        public void Ict_capacity_keeps_subducts_and_spare_as_metadata()
        {
            ConduitCapacity c = T("ICT-4C-6R").Capacity;
            Assert.Equal(5, c.SubductsPerConduit);
            Assert.Equal(0.032, c.SubductOutsideDiameterM.Value, 6);
            Assert.Equal(0.5, c.SpareCapacityFraction.Value, 6);
            Assert.Equal(24, T("ICT-4C-6R").Arrangement.Count); // spare capacity never removes ducts
            Assert.Equal(0, T("ICT-1C-1R-50").Capacity.SubductsPerConduit);
        }

        [Fact]
        public void Custom_copy_is_editable_and_traceable()
        {
            ConduitTemplate copy = T("MV-5C-3R").CustomCopy("Custom-MV-wide", "MV 5×3 wide") with { Arrangement = new ConduitArrangement(5, 3, 0.35, 0.5) };
            Assert.False(copy.IsStandard);
            Assert.Equal("MV-5C-3R", copy.DerivedFromKey);
            Assert.Equal(ConduitTemplateLibrary.MvDrawing, copy.Source.DrawingNumber);
            Assert.Empty(ConduitValidation.ValidateTemplate(copy));
            Assert.Equal(0.3, T("MV-5C-3R").Arrangement.HorizontalPitchM); // the standard is untouched
        }

        [Fact]
        public void Template_lists_are_frozen()
        {
            List<ConstructionVariant> list = new List<ConstructionVariant> { new ConstructionVariant("X", "x", ConstructionContext.Unspecified) };
            ConduitTemplate t = T("ICT-2C-1R") with { Variants = list };
            list.Add(new ConstructionVariant("Y", "y", ConstructionContext.Unspecified));
            Assert.Single(t.Variants);
        }

        // ---------- Template validation ----------

        [Fact]
        public void Overlapping_pitch_is_an_error_and_small_gap_a_warning()
        {
            ConduitTemplate ict = T("ICT-2C-3R");
            Assert.Contains(ConduitIssueCodes.TemplateOverlap, Codes(ConduitValidation.ValidateTemplate(ict with { Arrangement = new ConduitArrangement(2, 3, 0.100, 0.160) })));
            List<OperationIssue> tight = ConduitValidation.ValidateTemplate(ict with { Arrangement = new ConduitArrangement(2, 3, 0.150, 0.160) });
            Assert.Contains(tight, i => i.Code == ConduitIssueCodes.TemplateGapBelowReference && i.Severity == IssueSeverity.Warning);
            Assert.Contains(ConduitIssueCodes.TemplateEnvelopeInconsistent, Codes(tight));
        }

        [Theory]
        [InlineData(0, 3, 0.16, 0.16)]
        [InlineData(2, 31, 0.16, 0.16)]
        public void Grid_out_of_range_is_rejected(int columns, int rows, double h, double v) =>
            Assert.Contains(ConduitIssueCodes.TemplateGrid, Codes(ConduitValidation.ValidateTemplate(T("ICT-2C-3R") with { Arrangement = new ConduitArrangement(columns, rows, h, v) })));

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(0)]
        [InlineData(-0.1)]
        [InlineData(6)]
        public void Bad_pitch_is_rejected(double pitch) =>
            Assert.Contains(ConduitIssueCodes.TemplatePitch, Codes(ConduitValidation.ValidateTemplate(T("MV-5C-1R") with { Arrangement = new ConduitArrangement(5, 1, pitch, 0.5) })));

        [Fact]
        public void Single_row_ignores_vertical_pitch()
        {
            Assert.Empty(ConduitValidation.ValidateTemplate(T("MV-5C-1R") with { Arrangement = new ConduitArrangement(5, 1, 0.3, 0) }));
        }

        [Fact]
        public void Duplicate_keys_and_variant_ids_are_rejected()
        {
            ConduitTemplate a = T("ICT-2C-1R");
            Assert.Contains(ConduitIssueCodes.TemplateKeyDuplicate, Codes(ConduitValidation.ValidateLibrary(new[] { a, a.CustomCopy("ict-2c-1r", "Copy") })));
            ConduitTemplate twice = a with { Variants = new[] { new ConstructionVariant("1A", "", ConstructionContext.Unspecified), new ConstructionVariant("1a", "", ConstructionContext.Unspecified) } };
            Assert.Contains(ConduitIssueCodes.TemplateVariant, Codes(ConduitValidation.ValidateTemplate(twice)));
        }

        // ---------- Request validation ----------

        [Fact]
        public void A_complete_request_is_valid() => Assert.Empty(ConduitValidation.Validate(Request()));

        [Fact]
        public void Defaults_are_inner_wall_no_connection_mitre_leave_out()
        {
            ConduitEndRules e = new ConduitRequest().Ends;
            Assert.Equal(StructureEndTreatment.InnerWall, e.AtStructure);
            Assert.False(e.ConnectToStructures);
            Assert.Equal(JointTreatment.Mitre, e.AtJoint);
            Assert.Equal(UnknownFootprintPolicy.LeaveOut, e.WhenFootprintUnknown);
            Assert.Equal(ConduitDestination.EncasementNetwork, new ConduitRequest().Destination);
            Assert.False(new ConduitRequest().AllowConduitsOutsideEncasement);
        }

        [Fact]
        public void Non_metre_drawings_are_rejected() =>
            Assert.Contains(ConduitIssueCodes.UnitsNotMetres, Codes(ConduitValidation.Validate(Request(edit: r => r with { MetresPerDrawingUnit = 0.3048 }))));

        [Fact]
        public void Encasements_none_invalid_or_twice_are_rejected()
        {
            Assert.Contains(ConduitIssueCodes.EncasementsNone, Codes(ConduitValidation.Validate(Request(edit: r => r with { Encasements = Array.Empty<DrawingObjectRef>() }))));
            Assert.Contains(ConduitIssueCodes.EncasementInvalid, Codes(ConduitValidation.Validate(Request(edit: r => r with { Encasements = new[] { default(DrawingObjectRef) } }))));
            Assert.Contains(ConduitIssueCodes.EncasementDuplicate, Codes(ConduitValidation.Validate(Request(edit: r => r with { Encasements = new[] { R("1A"), R("1a") } }))));
        }

        [Fact]
        public void Variant_must_belong_to_the_template()
        {
            Assert.Empty(ConduitValidation.Validate(Request(edit: r => r with { VariantId = "2b" })));
            Assert.Contains(ConduitIssueCodes.VariantUnknown, Codes(ConduitValidation.Validate(Request(edit: r => r with { VariantId = "Type 01" }))));
        }

        [Fact]
        public void Bottom_reference_needs_a_height()
        {
            Assert.Contains(ConduitIssueCodes.SectionBottom, Codes(ConduitValidation.Validate(Request(edit: r => r with { Section = new ConduitSection(ConduitPlacement.BottomReferenced, 0) }))));
            Assert.Empty(ConduitValidation.Validate(Request(edit: r => r with { Section = new ConduitSection(ConduitPlacement.BottomReferenced, 0.155) })));
        }

        [Fact]
        public void Part_checks()
        {
            Assert.Contains(ConduitIssueCodes.PartMissing, Codes(ConduitValidation.Validate(Request(edit: r => r with { Part = null }))));
            Assert.Contains(ConduitIssueCodes.PartDiameter, Codes(ConduitValidation.Validate(Request(edit: r => r with { Part = r.Part with { OutsideDiameterM = double.NaN } }))));
            Assert.Contains(ConduitIssueCodes.PartDiameterUnknown, Codes(ConduitValidation.Validate(Request(edit: r => r with { Part = r.Part with { OutsideDiameterM = 0 } }))));
            Assert.Contains(ConduitIssueCodes.PartDiameterDiffers, Codes(ConduitValidation.Validate(Request(edit: r => r with { Part = r.Part with { OutsideDiameterM = 0.125 } }))));
            // MV states no diameter: any part diameter is accepted without that warning.
            Assert.DoesNotContain(ConduitIssueCodes.PartDiameterDiffers, Codes(ConduitValidation.Validate(Request("MV-5C-3R", r => r with { Part = r.Part with { OutsideDiameterM = 0.225 } }))));
        }

        [Fact]
        public void Copied_part_with_new_network_warns_it_stays()
        {
            ConduitPartChoice copied = new ConduitPartChoice(default, "", "", "ICT Duct 110", 0.110, R("77"));
            List<OperationIssue> issues = ConduitValidation.Validate(Request(edit: r => r with { Part = copied, Destination = ConduitDestination.NewNetwork }));
            Assert.Contains(issues, i => i.Code == ConduitIssueCodes.PartStaysInTemplateNetwork && i.Severity == IssueSeverity.Warning);
        }

        [Fact]
        public void Connection_needs_the_encasement_network()
        {
            Assert.Contains(ConduitIssueCodes.ConnectNeedsSameNetwork, Codes(ConduitValidation.Validate(Request(edit: r => r with { Destination = ConduitDestination.NewNetwork, Ends = new ConduitEndRules { ConnectToStructures = true } }))));
            Assert.Empty(ConduitValidation.Validate(Request(edit: r => r with { Ends = new ConduitEndRules { ConnectToStructures = true } })));
        }

        [Fact]
        public void New_network_needs_a_name() =>
            Assert.Contains(ConduitIssueCodes.NetworkName, Codes(ConduitValidation.Validate(Request(edit: r => r with { Destination = ConduitDestination.NewNetwork, NewNetworkName = " " }))));

        [Theory]
        [InlineData(-0.1)]
        [InlineData(2.5)]
        [InlineData(double.NaN)]
        public void Search_distance_range(double d) =>
            Assert.Contains(ConduitIssueCodes.EndsSearch, Codes(ConduitValidation.Validate(Request(edit: r => r with { Ends = new ConduitEndRules { StructureSearchM = d } }))));

        [Fact]
        public void Inner_boxes_are_checked_and_unique_by_size_name()
        {
            ConduitEndRules ok = new ConduitEndRules { InnerBoxes = new[] { new StructureInnerBox("4,600.00 x 2,300.00 x 3,300.00", 4.6, 2.3) } };
            Assert.Empty(ConduitValidation.Validate(Request(edit: r => r with { Ends = ok })));
            Assert.Contains(ConduitIssueCodes.InnerBoxInvalid, Codes(ConduitValidation.Validate(Request(edit: r => r with { Ends = new ConduitEndRules { InnerBoxes = new[] { new StructureInnerBox("MH", 0, 2) } } }))));
            ConduitEndRules dup = new ConduitEndRules { InnerBoxes = new[] { new StructureInnerBox("MH", 4, 2), new StructureInnerBox("mh ", 4, 2) } };
            Assert.Contains(ConduitIssueCodes.InnerBoxDuplicate, Codes(ConduitValidation.Validate(Request(edit: r => r with { Ends = dup }))));
            Assert.Equal(4.6, ConduitValidation.InnerBoxFor(ok, null, "4,600.00 X 2,300.00 X 3,300.00").InnerLengthM);
            Assert.Null(ConduitValidation.InnerBoxFor(ok, null, "MV Air Craft Manhole"));
        }

        [Fact]
        public void Overrides_must_name_a_chosen_encasement_once()
        {
            Assert.Contains(ConduitIssueCodes.OverrideInvalid, Codes(ConduitValidation.Validate(Request(edit: r => r with { Ends = new ConduitEndRules { Overrides = new[] { new ConduitEndOverride(R("99"), PipeEnd.Start, StructureEndTreatment.OuterWall) } } }))));
            ConduitEndRules twice = new ConduitEndRules { Overrides = new[] { new ConduitEndOverride(R("1A"), PipeEnd.End, StructureEndTreatment.OuterWall), new ConduitEndOverride(R("1A"), PipeEnd.End, null, true) } };
            Assert.Contains(ConduitIssueCodes.OverrideDuplicate, Codes(ConduitValidation.Validate(Request(edit: r => r with { Ends = twice }))));
        }

        [Fact]
        public void End_treatment_and_connection_resolve_independently()
        {
            ConduitEndRules rules = new ConduitEndRules { Overrides = new[] { new ConduitEndOverride(R("1A"), PipeEnd.End, null, true), new ConduitEndOverride(R("1B"), PipeEnd.Start, StructureEndTreatment.EncasementEnd) } };
            Assert.Equal((StructureEndTreatment.InnerWall, false, false), ConduitValidation.ResolveStructureEnd(rules, R("1A"), PipeEnd.Start));
            Assert.Equal((StructureEndTreatment.InnerWall, true, true), ConduitValidation.ResolveStructureEnd(rules, R("1A"), PipeEnd.End));
            Assert.Equal((StructureEndTreatment.EncasementEnd, false, true), ConduitValidation.ResolveStructureEnd(rules, R("1B"), PipeEnd.Start));
        }

        [Fact]
        public void Request_carries_the_template_by_value()
        {
            ConduitRequest r = Request();
            Assert.Same(T("ICT-2C-3R").DefaultSection, r.EffectiveSection);
            ConduitSection s = new ConduitSection(ConduitPlacement.SectionCentred, 0, 0.05);
            Assert.Same(s, (r with { Section = s }).EffectiveSection);
        }

        // ---------- Section checks (sizes as in the user's live model) ----------

        [Fact]
        public void Ict_6x4_in_its_own_envelope_fits_with_sheet_clearances()
        {
            ConduitFitCheck f = ConduitValidation.CheckSection(T("ICT-4C-6R"), ConduitSection.Centred, 0.79, 1.11, 0.110);
            Assert.Equal(0, f.ConduitsOutside);
            Assert.Equal(0.100, f.MinEdgeClearanceM, 6);
            Assert.Equal(0.050, f.MinClearGapM.Value, 6);
            Assert.True(f.TemplateEnvelopeMatches);
            Assert.True(f.TemplateDiameterMatches);
        }

        [Fact]
        public void Each_encasement_is_checked_with_its_own_size()
        {
            // An ICT 3X2 arrangement in the model's 0.47 × 0.79 "4x2 way" encasement: fits, but not the template's envelope.
            ConduitFitCheck taller = ConduitValidation.CheckSection(T("ICT-2C-3R"), ConduitSection.Centred, 0.47, 0.79, 0.110);
            Assert.Equal(0, taller.ConduitsOutside);
            Assert.False(taller.TemplateEnvelopeMatches);
            // The same arrangement in a 1X2 encasement (0.47 × 0.31): the top and bottom rows cross the edge.
            ConduitFitCheck small = ConduitValidation.CheckSection(T("ICT-2C-3R"), ConduitSection.Centred, 0.47, 0.31, 0.110);
            Assert.Equal(4, small.ConduitsOutside);
            Assert.True(small.MinEdgeClearanceM < 0);
        }

        [Fact]
        public void Mv_5x3_in_the_model_encasement()
        {
            ConduitFitCheck f = ConduitValidation.CheckSection(T("MV-5C-3R"), ConduitSection.Centred, 1.7, 1.5, 0.200);
            Assert.Equal(0, f.ConduitsOutside);
            Assert.Equal(0.150, f.MinEdgeClearanceM, 6);
            Assert.Equal(0.100, f.MinClearGapM.Value, 6);
            Assert.Null(f.TemplateEnvelopeMatches);
            Assert.Null(f.TemplateDiameterMatches);
        }

        [Fact]
        public void Bottom_referenced_and_shifted_centres()
        {
            IReadOnlyList<SectionCentre> c = ConduitValidation.SectionCentres(T("ICT-2C-3R").Arrangement, new ConduitSection(ConduitPlacement.BottomReferenced, 0.155, 0.02), 0.63);
            Assert.Equal(6, c.Count);
            Assert.Equal((1, 1), (c[0].Column, c[0].Row));
            Assert.Equal(-0.060, c[0].AcrossM, 6);           // -0.080 + 0.02 shift
            Assert.Equal(-0.315 + 0.155, c[0].UpM, 6);      // lowest centre 0.155 above the bottom
            Assert.Equal((2, 3), (c[5].Column, c[5].Row));
            Assert.Equal(-0.315 + 0.155 + 0.32, c[5].UpM, 6);
        }

        [Fact]
        public void Without_a_diameter_only_centres_are_checked()
        {
            ConduitFitCheck f = ConduitValidation.CheckSection(T("MV-5C-1R"), ConduitSection.Centred, 1.2, 0.5, 0);
            Assert.Equal(2, f.ConduitsOutside); // the outer columns sit exactly on the 1.2 m edge
            Assert.Null(f.TemplateDiameterMatches);
        }

        // ---------- Apply and stale plans ----------

        private static EncasementSnapshot Snap(double x = 0) => new EncasementSnapshot(new Point3(x, 0, 10), new Point3(x + 20, 0, 9.8), R("E1"), "ICT", R("5A1"), null, "ICT 3x2 way", 0.47, 0.63, false, 0);
        private static StructureSnapshot Mh(double x = 0) => new StructureSnapshot(R("5A1"), "MH-1", "2,300 x 4,600 Rect", new Point3(x, 0, 12), 0.5, FootprintSource.PartSize, false, 4.6, 2.3, 0);

        private static readonly ConduitDestinationPlan IctNet = new ConduitDestinationPlan(R("E1"), "ICT");
        private static readonly ConduitPartSnapshot Duct110 = new ConduitPartSnapshot("HDPE Duct", "110 mm OD", 0.110);

        private static ConduitSourcePlan Item(string h = "1A", ConduitEndPlan startEnd = null, ConduitEndPlan endEnd = null, IReadOnlyList<OperationIssue> issues = null) =>
            new ConduitSourcePlan(R(h), "Duct " + h, Snap(), 1, false,
                startEnd ?? new ConduitEndPlan(PipeEnd.Start, ConduitEndKind.Structure, Mh(), StructureMatch.Connected, StructureEndTreatment.InnerWall),
                endEnd ?? new ConduitEndPlan(PipeEnd.End, ConduitEndKind.Free), ConduitValidation.CheckSection(T("ICT-2C-3R"), ConduitSection.Centred, 0.47, 0.63, 0.11), IctNet, Duct110,
                new[] { new PlannedConduit(1, 1, -0.08, -0.16, new Point3(2.3, -0.08, 9.6), new Point3(20, -0.08, 9.64), 0, 17.7) })
            { Issues = issues };

        /// <summary>Compare with the destination and part resolving as planned unless given.</summary>
        private static OperationIssue Cmp(ConduitSourcePlan item, Func<DrawingObjectRef, EncasementSnapshot> enc, Func<DrawingObjectRef, StructureSnapshot> str,
            IReadOnlyDictionary<DrawingObjectRef, EncasementSnapshot> neighbours = null, ConduitDestinationPlan dest = null, ConduitPartSnapshot part = null, bool partGone = false) =>
            ConduitValidation.Compare(item, enc, str, neighbours ?? new Dictionary<DrawingObjectRef, EncasementSnapshot>(), _ => dest ?? IctNet, _ => partGone ? null : part ?? Duct110);

        private static ConduitPreview Plan(params ConduitSourcePlan[] items) => new ConduitPreview { Drawing = Dwg, Request = Request(), Status = OperationStatus.Succeeded, Items = items };

        [Fact]
        public void Apply_rejects_wrong_drawing_strays_blocked_items_and_duplicates()
        {
            ConduitSourcePlan blocked = Item("1B", issues: new[] { OperationIssue.Error(ConduitIssueCodes.FootprintUnknown, "x") });
            ConduitPreview plan = Plan(Item(), blocked);
            Assert.False(blocked.CanApply);
            Assert.Empty(ConduitValidation.ValidateApply(new ConduitApplyRequest(plan, new[] { R("1A") }), Dwg));
            Assert.Contains(ConduitIssueCodes.DrawingMismatch, Codes(ConduitValidation.ValidateApply(new ConduitApplyRequest(plan, new[] { R("1A") }), Dwg with { SessionKey = "s2" })));
            Assert.Contains(ConduitIssueCodes.ChosenNotInPlan, Codes(ConduitValidation.ValidateApply(new ConduitApplyRequest(plan, new[] { R("99") }), Dwg)));
            Assert.Contains(ConduitIssueCodes.ChosenNotApplicable, Codes(ConduitValidation.ValidateApply(new ConduitApplyRequest(plan, new[] { R("1B") }), Dwg)));
            Assert.Contains(ConduitIssueCodes.ChosenDuplicate, Codes(ConduitValidation.ValidateApply(new ConduitApplyRequest(plan, new[] { R("1A"), R("1A") }), Dwg)));
            Assert.Contains(ConduitIssueCodes.PlanNotApplicable, Codes(ConduitValidation.ValidateApply(new ConduitApplyRequest(plan with { Status = OperationStatus.NoChange }, new[] { R("1A") }), Dwg)));
        }

        [Fact]
        public void Stale_encasement_structure_or_neighbour_is_detected()
        {
            ConduitSourcePlan item = Item();
            Assert.Null(Cmp(item, _ => Snap(), _ => Mh()));
            Assert.Equal(ConduitIssueCodes.EncasementMissing, Cmp(item, _ => null, _ => Mh()).Code);
            Assert.Equal(ConduitIssueCodes.EncasementChanged, Cmp(item, _ => Snap(0.01), _ => Mh()).Code);
            Assert.Equal(ConduitIssueCodes.EncasementChanged, Cmp(item, _ => Snap() with { OuterHeightM = 0.79 }, _ => Mh()).Code);
            Assert.Equal(ConduitIssueCodes.StructureChanged, Cmp(item, _ => Snap(), _ => Mh(0.2)).Code);
            Assert.Equal(ConduitIssueCodes.StructureChanged, Cmp(item, _ => Snap(), _ => null).Code);

            ConduitSourcePlan joined = Item(endEnd: new ConduitEndPlan(PipeEnd.End, ConduitEndKind.Joint, Neighbour: R("1B"), Joint: JointTreatment.Mitre));
            var planned = new Dictionary<DrawingObjectRef, EncasementSnapshot> { [R("1B")] = Snap(20) };
            Assert.Null(Cmp(joined, h => h == R("1B") ? Snap(20) : Snap(), _ => Mh(), planned));
            Assert.Equal(ConduitIssueCodes.NeighbourChanged, Cmp(joined, h => h == R("1B") ? Snap(21) : Snap(), _ => Mh(), planned).Code);
        }

        [Fact]
        public void Stale_part_or_destination_is_detected()
        {
            ConduitSourcePlan item = Item();
            // The size being added to the network's parts list during Apply is not a change: identity is family, size, diameter, template.
            Assert.Null(Cmp(item, _ => Snap(), _ => Mh(), part: new ConduitPartSnapshot("hdpe duct", "110 MM OD", 0.110)));
            Assert.Equal(ConduitIssueCodes.PartChanged, Cmp(item, _ => Snap(), _ => Mh(), part: Duct110 with { SizeName = "125 mm OD" }).Code);
            Assert.Equal(ConduitIssueCodes.PartChanged, Cmp(item, _ => Snap(), _ => Mh(), part: Duct110 with { OutsideDiameterM = 0.125 }).Code);
            Assert.Equal(ConduitIssueCodes.PartChanged, Cmp(item, _ => Snap(), _ => Mh(), part: Duct110 with { Template = R("77") }).Code);
            Assert.Equal(ConduitIssueCodes.PartChanged, Cmp(item, _ => Snap(), _ => Mh(), partGone: true).Code);
            Assert.Equal(ConduitIssueCodes.DestinationChanged, Cmp(item, _ => Snap(), _ => Mh(), dest: new ConduitDestinationPlan(R("E2"), "Other")).Code);
            Assert.Equal(ConduitIssueCodes.DestinationChanged, Cmp(item, _ => Snap(), _ => Mh(), dest: new ConduitDestinationPlan(null, "Conduits", true)).Code);
            // A new network keeps matching even if its final name gets a suffix.
            ConduitSourcePlan toNew = item with { Destination = new ConduitDestinationPlan(null, "Conduits", true) };
            Assert.Null(Cmp(toNew, _ => Snap(), _ => Mh(), dest: new ConduitDestinationPlan(null, "Conduits (2)", true)));
        }

        // ---------- Codex review fixes ----------

        [Fact]
        public void Oversized_part_overlap_is_refused_at_request()
        {
            // 200 mm OD on the ICT 160 mm pitch: 40 mm overlap, refused even though a wide encasement would contain it.
            List<OperationIssue> issues = ConduitValidation.Validate(Request(edit: r => r with { Part = r.Part with { OutsideDiameterM = 0.200 }, AllowConduitsOutsideEncasement = true }));
            List<OperationIssue> overlaps = issues.Where(i => i.Code == ConduitIssueCodes.PartOverlap).ToList();
            Assert.Equal(2, overlaps.Count); // across and up: both pitches are 160 mm
            Assert.All(overlaps, o => { Assert.Equal(IssueSeverity.Error, o.Severity); Assert.Contains("40 mm", o.Message); });
            // A single conduit has no neighbour to overlap.
            Assert.DoesNotContain(ConduitIssueCodes.PartOverlap, Codes(ConduitValidation.Validate(Request("ICT-1C-1R-50", r => r with { Part = r.Part with { OutsideDiameterM = 0.200 } }))));
        }

        [Fact]
        public void Overlap_blocks_an_item_even_when_outside_is_allowed()
        {
            ConduitFitCheck wide = ConduitValidation.CheckSection(T("ICT-2C-3R"), ConduitSection.Centred, 2.0, 2.0, 0.200);
            Assert.Equal(0, wide.ConduitsOutside);
            Assert.True(wide.ConduitsOverlap);
            Assert.Equal(-0.040, wide.MinClearGapM.Value, 6);
            List<OperationIssue> issues = ConduitValidation.FitIssues(wide, allowOutside: true, "Duct 1A", R("1A"));
            Assert.Contains(issues, i => i.Code == ConduitIssueCodes.ConduitsOverlap && i.Severity == IssueSeverity.Error);

            ConduitFitCheck small = ConduitValidation.CheckSection(T("ICT-2C-3R"), ConduitSection.Centred, 0.47, 0.31, 0.110);
            Assert.False(small.ConduitsOverlap);
            Assert.Contains(ConduitValidation.FitIssues(small, false, "d", R("1A")), i => i.Code == ConduitIssueCodes.ConduitsOutside && i.Severity == IssueSeverity.Error);
            Assert.Contains(ConduitValidation.FitIssues(small, true, "d", R("1A")), i => i.Code == ConduitIssueCodes.ConduitsOutside && i.Severity == IssueSeverity.Warning);
            Assert.Contains(ConduitValidation.FitIssues(small, true, "d", R("1A")), i => i.Code == ConduitIssueCodes.EnvelopeDiffers);
            Assert.Empty(ConduitValidation.FitIssues(ConduitValidation.CheckSection(T("ICT-2C-3R"), ConduitSection.Centred, 0.47, 0.63, 0.110), false, "d", R("1A")));
        }

        [Fact]
        public void Planned_arc_is_fixed_by_signed_bulge()
        {
            Point3 a = new Point3(0, 0, 10), b = new Point3(10, 0, 10);
            PlannedConduit left = new PlannedConduit(1, 1, 0, 0, a, b, 0.25, 10.3), right = left with { Bulge = -0.25 };
            Assert.True(left.Curved);
            Assert.NotEqual(left, right); // same ends and length, mirrored bends
            Assert.False((left with { Bulge = 0 }).Curved);
        }

        [Fact]
        public void Unknown_part_diameter_blocks_the_request() =>
            Assert.Contains(ConduitValidation.Validate(Request(edit: r => r with { Part = r.Part with { OutsideDiameterM = 0 } })),
                i => i.Code == ConduitIssueCodes.PartDiameterUnknown && i.Severity == IssueSeverity.Error);

        [Fact]
        public void Run_with_one_side_of_a_mitre_missing_is_reported()
        {
            ConduitSourcePlan a = Item("1A", endEnd: new ConduitEndPlan(PipeEnd.End, ConduitEndKind.Joint, Neighbour: R("1B"), Joint: JointTreatment.Mitre));
            ConduitSourcePlan b = Item("1B", startEnd: new ConduitEndPlan(PipeEnd.Start, ConduitEndKind.Joint, Neighbour: R("1A"), Joint: JointTreatment.Mitre));
            ConduitPreview plan = Plan(a, b);
            ConduitItemOutcome Ok(string h) => new ConduitItemOutcome(R(h), "Duct " + h, ItemStatus.Committed, new[] { R("A" + h) });
            ConduitItemOutcome Back(string h) => new ConduitItemOutcome(R(h), "Duct " + h, ItemStatus.RolledBack, null);

            Assert.Empty(ConduitValidation.IncompleteRuns(plan, new[] { Ok("1A"), Ok("1B") }));
            OperationIssue w = Assert.Single(ConduitValidation.IncompleteRuns(plan, new[] { Ok("1A"), Back("1B") }));
            Assert.Equal(ConduitIssueCodes.RunIncomplete, w.Code);
            Assert.Equal(IssueSeverity.Warning, w.Severity);
            Assert.Contains("Duct 1B", w.Message);
            // 1B not chosen at all (only 1A applied): still incomplete, and filling it is suggested.
            Assert.Contains("Fill Duct 1B", Assert.Single(ConduitValidation.IncompleteRuns(plan, new[] { Ok("1A") })).Message);
            // 1B's outcome unknown: it may already hold conduits, so inspection is asked for, never a fill (duplicates).
            OperationIssue unknown = Assert.Single(ConduitValidation.IncompleteRuns(plan, new[] { Ok("1A"), new ConduitItemOutcome(R("1B"), "Duct 1B", ItemStatus.OutcomeUnknown, null) }));
            Assert.Contains("Inspect Duct 1B", unknown.Message);
            Assert.DoesNotContain("Fill", unknown.Message);
            // Joints kept at encasement ends are not mitres: nothing to report.
            ConduitSourcePlan plain = a with { EndEnd = a.EndEnd with { Joint = JointTreatment.EncasementEnd } };
            Assert.Empty(ConduitValidation.IncompleteRuns(Plan(plain, b with { StartEnd = b.StartEnd with { Joint = JointTreatment.EncasementEnd } }), new[] { Ok("1A") }));
        }

        // ---------- Agreed phase-one decisions ----------

        [Fact]
        public void Nearby_matching_is_opt_in_with_a_half_metre_limit()
        {
            ConduitEndRules e = new ConduitEndRules();
            Assert.False(e.MatchUnconnectedEnds);
            Assert.Equal(0.5, e.StructureSearchM);
        }

        [Fact]
        public void Structure_choices_need_matching_on_and_one_per_end()
        {
            StructureChoice pick = new StructureChoice(R("1A"), PipeEnd.End, R("5A1"));
            Assert.Contains(ConduitIssueCodes.StructureChoiceInvalid, Codes(ConduitValidation.Validate(Request(edit: r => r with { Ends = new ConduitEndRules { StructureChoices = new[] { pick } } }))));
            Assert.Empty(ConduitValidation.Validate(Request(edit: r => r with { Ends = new ConduitEndRules { MatchUnconnectedEnds = true, StructureChoices = new[] { pick } } })));
            Assert.Contains(ConduitIssueCodes.StructureChoiceInvalid, Codes(ConduitValidation.Validate(Request(edit: r => r with { Ends = new ConduitEndRules { MatchUnconnectedEnds = true, StructureChoices = new[] { pick with { Encasement = R("99") } } } }))));
            Assert.Contains(ConduitIssueCodes.StructureChoiceDuplicate, Codes(ConduitValidation.Validate(Request(edit: r => r with { Ends = new ConduitEndRules { MatchUnconnectedEnds = true, StructureChoices = new[] { pick, pick with { Structure = R("5A2") } } } }))));
        }

        [Fact]
        public void Ambiguous_end_lists_candidates_and_blocks_the_item()
        {
            ConduitEndPlan end = new ConduitEndPlan(PipeEnd.End, ConduitEndKind.Structure) { Candidates = new[] { Mh(), Mh(3) with { Structure = R("5A2") } } };
            ConduitSourcePlan item = Item(endEnd: end, issues: new[] { OperationIssue.Error(ConduitIssueCodes.StructureAmbiguous, "pick one", null, R("1A")) });
            Assert.Null(item.EndEnd.Structure);
            Assert.Equal(2, item.EndEnd.Candidates.Count);
            Assert.False(item.CanApply);
        }

        [Fact]
        public void Inner_boxes_are_scoped_to_part_identity()
        {
            ConduitEndRules rules = new ConduitEndRules
            {
                InnerBoxes = new[]
                {
                    new StructureInnerBox("4,600.00 x 2,300.00 x 3,300.00", 4.6, 2.3, Project: "NCRD"),
                    new StructureInnerBox("4,600.00 x 2,300.00 x 3,300.00", 4.5, 2.2, Project: "NCRD", PartFamilyName: "ICT-Manholes"),
                }
            };
            Assert.Empty(ConduitValidation.Validate(Request(edit: r => r with { Ends = rules })));
            Assert.Equal(4.5, ConduitValidation.InnerBoxFor(rules, "ict-manholes", "4,600.00 x 2,300.00 x 3,300.00").InnerLengthM);
            Assert.Equal(4.6, ConduitValidation.InnerBoxFor(rules, "Other family", "4,600.00 x 2,300.00 x 3,300.00").InnerLengthM);
            ConduitEndRules dup = rules with { InnerBoxes = rules.InnerBoxes.Append(new StructureInnerBox("4,600.00 X 2,300.00 X 3,300.00", 4, 2, PartFamilyName: "ict-manholes ")).ToList() };
            Assert.Contains(ConduitIssueCodes.InnerBoxDuplicate, Codes(ConduitValidation.Validate(Request(edit: r => r with { Ends = dup }))));
        }

        [Fact]
        public void Result_counts_committed_items_only()
        {
            ConduitPreview plan = Plan(Item(), Item("1B"));
            ConduitResult res = ConduitResult.From(plan, new[]
            {
                new ConduitItemOutcome(R("1A"), "Duct 1A", ItemStatus.Committed, new[] { R("A1"), R("A2") }, 0),
                new ConduitItemOutcome(R("1B"), "Duct 1B", ItemStatus.RolledBack, new[] { R("B1") }, 2),
            }, cancelled: false);
            Assert.Equal(OperationStatus.PartiallySucceeded, res.Status);
            Assert.Equal(1, res.EncasementsFilled);
            Assert.Equal(2, res.ConduitsCreated);
            Assert.Equal(0, res.ConnectionsMade);
            Assert.Equal(2, plan.ConduitsToCreate); // each test item plans one conduit; both applicable
        }
    }
}
