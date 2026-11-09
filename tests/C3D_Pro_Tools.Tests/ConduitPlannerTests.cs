using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using C3DProTools.Features.Networks.Application;
using C3DProTools.Features.Networks.Contracts;
using Xunit;

namespace C3DProTools.Tests
{
    public class ConduitPlannerTests
    {
        private static readonly DrawingIdentity Dwg = new DrawingIdentity("s1", "F1", @"C:\x.dwg", "m");
        private static DrawingObjectRef R(string h) => DrawingObjectRef.Of(h);
        private static readonly DrawingObjectRef Net = R("E1");
        private static readonly ConduitDestinationPlan Dest = new ConduitDestinationPlan(Net, "ICT");
        private static readonly ConduitPartSnapshot Duct = new ConduitPartSnapshot("HDPE", "110", 0.110);

        private static EncasementRead Enc(string h, Point3 s, Point3 e, DrawingObjectRef? startStr = null, DrawingObjectRef? endStr = null, double w = 0.47, double ht = 0.31, double bulge = 0) =>
            new EncasementRead(R(h), "Duct " + h, new EncasementSnapshot(s, e, Net, "ICT", startStr, endStr, "ICT 1x2 way", w, ht, bulge != 0, bulge), null, Dest, Duct);

        private static StructureRead Mh(string h, Point3 at, double rotDeg, double width, double length, double wall = 0, string size = "MH 4600x2300", bool round = false) =>
            new StructureRead(R(h), "MH-" + h, "ICT-Manholes", size, Net, at, rotDeg * Math.PI / 180, round, width, length, wall);

        private static ConduitRequest Req(string key, IEnumerable<EncasementRead> encs, Func<ConduitEndRules, ConduitEndRules> ends = null) => new ConduitRequest
        {
            Drawing = Dwg,
            Encasements = encs.Select(e => e.Encasement).ToList(),
            Template = ConduitTemplateLibrary.Find(key),
            Part = new ConduitPartChoice(R("50"), "", "HDPE", "110", 0.110),
            Ends = ends == null ? new ConduitEndRules() : ends(new ConduitEndRules())
        };

        private static List<ConduitSourcePlan> Plan(ConduitRequest r, IEnumerable<EncasementRead> encs, params StructureRead[] structures) =>
            ConduitPlanner.Plan(r, new ConduitDrawingRead(encs.ToList(), structures, null), CancellationToken.None).Items;

        private static (double x, double y) Local(StructureRead s, Point3 p)
        {
            double dx = p.X - s.Location.X, dy = p.Y - s.Location.Y, c = Math.Cos(-s.RotationRad), n = Math.Sin(-s.RotationRad);
            return (dx * c - dy * n, dx * n + dy * c);
        }

        // ---------- Inner-wall trimming ----------

        [Fact]
        public void Conduits_stop_at_a_rotated_manholes_inner_wall()
        {
            // A 4.6 × 2.3 manhole turned 30°, the encasement running along its X axis to its centre (as the ICT model connects them).
            Point3 c = new Point3(100, 200, 50);
            double a = 30 * Math.PI / 180;
            Point3 far = new Point3(c.X - 20 * Math.Cos(a), c.Y - 20 * Math.Sin(a), 51);
            StructureRead mh = Mh("5A1", c, 30, 4.6, 2.3, wall: 0.4);
            EncasementRead e = Enc("1A", far, c, endStr: mh.Structure);
            ConduitSourcePlan item = Assert.Single(Plan(Req("ICT-2C-1R", new[] { e }), new[] { e }, mh));
            Assert.True(item.CanApply, string.Join(" | ", item.Issues.Select(i => i.Message)));
            Assert.Equal(ConduitEndKind.Structure, item.EndEnd.Kind);
            Assert.Equal(StructureEndTreatment.InnerWall, item.EndEnd.Treatment);
            Assert.False(item.EndEnd.Connect);
            Assert.Equal(FootprintSource.PartSize, item.EndEnd.Structure.Source);
            foreach (PlannedConduit p in item.Conduits)
            {
                (double x, double y) = Local(mh, p.End);
                Assert.Equal(-2.3, x, 6);                         // on the inner face, 2.3 m before the centre
                Assert.Equal(p.Column == 1 ? 0.08 : -0.08, y, 6); // its own offset kept (column 1 on the left)
                Assert.Equal(20 - 2.3, p.PlanLengthM, 6);
                // The grade is kept: 51 → 50 over 20 m, so 2.3 m short of the end the level is 50.115.
                Assert.Equal(50 + 2.3 / 20, p.End.Z, 6);
            }
        }

        [Fact]
        public void Outer_wall_centre_and_encasement_end_treatments()
        {
            Point3 c = new Point3(0, 0, 0);
            StructureRead mh = Mh("5A1", c, 0, 4.6, 2.3, wall: 0.4);
            EncasementRead e = Enc("1A", new Point3(-20, 0, 0), c, endStr: mh.Structure);
            double EndX(StructureEndTreatment t) =>
                Plan(Req("ICT-1C-1R-50", new[] { e }, r => r with { AtStructure = t }), new[] { e }, mh)[0].Conduits[0].End.X;
            Assert.Equal(-2.7, EndX(StructureEndTreatment.OuterWall), 6);
            Assert.Equal(0, EndX(StructureEndTreatment.StructureCentre), 6);
            Assert.Equal(0, EndX(StructureEndTreatment.EncasementEnd), 6);
        }

        [Fact]
        public void Round_manhole_trims_each_conduit_on_its_own_line()
        {
            StructureRead mh = Mh("5A1", new Point3(0, 0, 0), 0, 2.0, 2.0, round: true);
            EncasementRead e = Enc("1A", new Point3(-20, 0, 0), new Point3(0, 0, 0), endStr: mh.Structure, w: 1.7, ht: 0.5);
            ConduitSourcePlan item = Plan(Req("MV-5C-1R", new[] { e }), new[] { e }, mh)[0];
            foreach (PlannedConduit p in item.Conduits)
                Assert.Equal(1.0, Math.Sqrt(p.End.X * p.End.X + p.End.Y * p.End.Y), 6); // each meets the circle
            Assert.True(item.Conduits.Select(p => Math.Round(p.End.X, 6)).Distinct().Count() > 1); // outer ones nearer
        }

        // ---------- Unknown footprints ----------

        [Fact]
        public void Unknown_footprint_leaves_the_encasement_out_until_a_box_is_given()
        {
            StructureRead mh = Mh("5A1", new Point3(0, 0, 0), 120, 0, 0, wall: 0.4, size: "4,600.00 x 2,300.00 x 3,300.00");
            double a = 120 * Math.PI / 180; // the encasement runs along the manhole's turned X axis
            EncasementRead e = Enc("1A", new Point3(-20 * Math.Cos(a), -20 * Math.Sin(a), 0), new Point3(0, 0, 0), endStr: mh.Structure);
            ConduitSourcePlan left = Plan(Req("ICT-2C-1R", new[] { e }), new[] { e }, mh)[0];
            Assert.False(left.CanApply);
            Assert.Contains(left.Issues, i => i.Code == ConduitIssueCodes.FootprintUnknown && i.Message.Contains("4,600.00 x 2,300.00 x 3,300.00"));
            Assert.Equal(FootprintSource.Unknown, left.EndEnd.Structure.Source);

            ConduitSourcePlan kept = Plan(Req("ICT-2C-1R", new[] { e }, r => r with { WhenFootprintUnknown = UnknownFootprintPolicy.KeepEncasementEnd }), new[] { e }, mh)[0];
            Assert.True(kept.CanApply);
            Assert.Equal(StructureEndTreatment.EncasementEnd, kept.EndEnd.Treatment);
            Assert.Equal(0, Local(mh, kept.Conduits[0].End).x, 6);

            ConduitSourcePlan boxed = Plan(Req("ICT-2C-1R", new[] { e }, r => r with { InnerBoxes = new[] { new StructureInnerBox("4,600.00 x 2,300.00 x 3,300.00", 2.3, 4.6, Project: "NCRD") } }), new[] { e }, mh)[0];
            Assert.True(boxed.CanApply);
            Assert.Equal(FootprintSource.UserBox, boxed.EndEnd.Structure.Source);
            foreach (PlannedConduit p in boxed.Conduits) Assert.Equal(-2.3, Local(mh, p.End).x, 6); // width 4.6 along the turned X axis
        }

        // ---------- Runs, reversal and mitres ----------

        [Fact]
        public void Reversed_encasement_keeps_columns_on_the_runs_left()
        {
            // A runs east; B is drawn from the far end back to the joint (end to end), so it is reversed in the run.
            EncasementRead a = Enc("1A", new Point3(0, 0, 0), new Point3(10, 0, 0));
            EncasementRead b = Enc("1B", new Point3(20, 0, 0), new Point3(10, 0, 0));
            List<ConduitSourcePlan> items = Plan(Req("ICT-2C-1R", new[] { a, b }), new[] { a, b });
            Assert.Equal(items[0].RunId, items[1].RunId);
            Assert.False(items[0].Reversed);
            Assert.True(items[1].Reversed);
            Assert.Equal(ConduitEndKind.Joint, items[0].EndEnd.Kind);
            Assert.Equal(ConduitEndKind.Joint, items[1].EndEnd.Kind);
            // Column 1 is north (+y, the run's left) in both, though B's own right is north.
            foreach (ConduitSourcePlan i in items)
                Assert.Equal(0.08, i.Conduits.Single(p => p.Column == 1).Start.Y, 6);
        }

        [Fact]
        public void Mitred_columns_meet_at_a_deflection_whatever_the_direction()
        {
            EncasementRead a = Enc("1A", new Point3(0, 0, 0), new Point3(10, 0, 0), w: 0.79, ht: 0.63);
            EncasementRead b = Enc("1B", new Point3(20, 6, 0), new Point3(10, 0, 0), w: 0.79, ht: 0.63); // reversed, deflected
            List<ConduitSourcePlan> items = Plan(Req("ICT-4C-3R", new[] { a, b }), new[] { a, b });
            Assert.All(items, i => Assert.True(i.CanApply));
            foreach (PlannedConduit pa in items[0].Conduits)
            {
                PlannedConduit pb = items[1].Conduits.Single(p => p.Column == pa.Column && p.Row == pa.Row);
                Assert.True(pa.End.PlanDistanceTo(pb.End) < 1e-6, $"column {pa.Column} row {pa.Row}: {pa.End} vs {pb.End}");
            }
            // The side shift follows the run too.
            List<ConduitSourcePlan> shifted = Plan(Req("ICT-2C-1R", new[] { a, b }) with { Section = new ConduitSection(ConduitPlacement.SectionCentred, 0, 0.05) }, new[] { a, b });
            Assert.Equal(0.08 - 0.05, shifted[0].Conduits.Single(p => p.Column == 1).Start.Y, 6);
        }

        [Fact]
        public void Three_ends_at_one_point_are_not_mitred()
        {
            EncasementRead a = Enc("1A", new Point3(0, 0, 0), new Point3(10, 0, 0));
            EncasementRead b = Enc("1B", new Point3(10, 0, 0), new Point3(20, 0, 0));
            EncasementRead c = Enc("1C", new Point3(10, 0, 0), new Point3(10, 10, 0));
            List<ConduitSourcePlan> items = Plan(Req("ICT-2C-1R", new[] { a, b, c }), new[] { a, b, c });
            Assert.All(items, i => Assert.Contains(i.Issues, x => x.Code == ConduitIssueCodes.JointNotPaired));
            Assert.Equal(ConduitEndKind.Free, items[0].EndEnd.Kind);
        }

        // ---------- Curves ----------

        [Fact]
        public void Curved_encasement_conduits_follow_offset_arcs()
        {
            // Quarter circle, radius 10, counter-clockwise (bulge tan(22.5°)), centre at the origin.
            double b = Math.Tan(Math.PI / 8);
            EncasementRead e = Enc("1A", new Point3(10, 0, 0), new Point3(0, 10, 0), bulge: b, w: 1.7, ht: 0.5);
            ConduitSourcePlan item = Plan(Req("MV-5C-1R", new[] { e }, r => r with { AtStructure = StructureEndTreatment.EncasementEnd }), new[] { e })[0];
            Assert.True(item.CanApply);
            (double cx, double cy, double r) = ConduitGeometry.ArcCentre(e.Snapshot.Start, e.Snapshot.End, b);
            Assert.Equal(0, cx, 6); Assert.Equal(0, cy, 6); Assert.Equal(10, r, 6);
            foreach (PlannedConduit p in item.Conduits)
            {
                Assert.Equal(b, p.Bulge, 9);
                double radius = Math.Sqrt(p.Start.X * p.Start.X + p.Start.Y * p.Start.Y);
                Assert.Equal(10 + p.OffsetAcrossM, radius, 6); // right of a left turn is outward
                Assert.Equal(radius * Math.PI / 2, p.PlanLengthM, 6);
            }
        }

        [Fact]
        public void Curved_end_at_a_structure_needs_the_encasement_end()
        {
            StructureRead mh = Mh("5A1", new Point3(0, 10, 0), 0, 2, 2);
            EncasementRead e = Enc("1A", new Point3(10, 0, 0), new Point3(0, 10, 0), endStr: mh.Structure, bulge: Math.Tan(Math.PI / 8), w: 1.7, ht: 0.5);
            ConduitSourcePlan item = Plan(Req("MV-5C-1R", new[] { e }), new[] { e }, mh)[0];
            Assert.False(item.CanApply);
            Assert.Contains(item.Issues, i => i.Code == ConduitIssueCodes.CurvedEndUnsupported);
        }

        [Fact]
        public void Curved_to_straight_joint_is_not_mitred()
        {
            EncasementRead a = Enc("1A", new Point3(-10, 0, 0), new Point3(10, 0, 0));
            EncasementRead b = Enc("1B", new Point3(10, 0, 0), new Point3(20, 10, 0), bulge: Math.Tan(Math.PI / 8), w: 0.47, ht: 0.31);
            List<ConduitSourcePlan> items = Plan(Req("ICT-2C-1R", new[] { a, b }), new[] { a, b });
            Assert.Equal(JointTreatment.EncasementEnd, items[0].EndEnd.Joint);
            Assert.Equal(JointTreatment.EncasementEnd, items[1].StartEnd.Joint);
            Assert.Contains(items[0].Issues, i => i.Code == ConduitIssueCodes.CurvedJointNotMitred);
            Assert.Equal(10, items[0].Conduits[0].End.X, 6);
        }

        // ---------- Matching, fit, connection ----------

        [Fact]
        public void Unconnected_ends_match_structures_only_when_asked_and_ambiguity_blocks()
        {
            // m1 holds the end; m2 lies across the line just short of it (0.1 m away), so both qualify.
            StructureRead m1 = Mh("5A1", new Point3(0.3, 0, 0), 0, 1, 1), m2 = Mh("5A2", new Point3(-0.6, 0.4, 0), 0, 1, 1);
            EncasementRead e = Enc("1A", new Point3(-20, 0, 0), new Point3(0, 0, 0));
            Assert.Equal(ConduitEndKind.Free, Plan(Req("ICT-1C-1R-50", new[] { e }), new[] { e }, m1)[0].EndEnd.Kind);

            ConduitSourcePlan one = Plan(Req("ICT-1C-1R-50", new[] { e }, r => r with { MatchUnconnectedEnds = true }), new[] { e }, m1)[0];
            Assert.Equal(StructureMatch.InsideOuterFace, one.EndEnd.FoundBy);
            Assert.Contains(one.Issues, i => i.Code == ConduitIssueCodes.StructureMatchedByPosition);
            Assert.Equal(-0.2, one.Conduits[0].End.X, 6);

            ConduitSourcePlan two = Plan(Req("ICT-1C-1R-50", new[] { e }, r => r with { MatchUnconnectedEnds = true }), new[] { e }, m1, m2)[0];
            Assert.False(two.CanApply);
            Assert.Equal(2, two.EndEnd.Candidates.Count);
            Assert.Contains(two.Issues, i => i.Code == ConduitIssueCodes.StructureAmbiguous);

            ConduitSourcePlan picked = Plan(Req("ICT-1C-1R-50", new[] { e }, r => r with { MatchUnconnectedEnds = true, StructureChoices = new[] { new StructureChoice(R("1A"), PipeEnd.End, R("5A2")) } }), new[] { e }, m1, m2)[0];
            Assert.True(picked.CanApply, string.Join(" | ", picked.Issues.Select(i => i.Message)));
            Assert.Equal(R("5A2"), picked.EndEnd.Structure.Structure);
            Assert.Equal(-1.1, picked.Conduits[0].End.X, 6); // m2's inner face
            Assert.Equal(StructureMatch.WithinSearchDistance, picked.EndEnd.FoundBy);
        }

        [Fact]
        public void Each_encasement_is_fitted_at_its_own_size()
        {
            EncasementRead big = Enc("1A", new Point3(0, 0, 0), new Point3(10, 0, 0), w: 0.47, ht: 0.63);
            EncasementRead small = Enc("1B", new Point3(0, 5, 0), new Point3(10, 5, 0), w: 0.47, ht: 0.31);
            List<ConduitSourcePlan> items = Plan(Req("ICT-2C-3R", new[] { big, small }), new[] { big, small });
            Assert.True(items[0].CanApply);
            Assert.False(items[1].CanApply);
            Assert.Contains(items[1].Issues, i => i.Code == ConduitIssueCodes.ConduitsOutside);
            Assert.Empty(items[1].Conduits);
        }

        [Fact]
        public void Connection_only_in_the_structures_network()
        {
            StructureRead mh = Mh("5A1", new Point3(0, 0, 0), 0, 2, 2) with { Network = R("E2") };
            EncasementRead e = Enc("1A", new Point3(-20, 0, 0), new Point3(0, 0, 0), endStr: mh.Structure);
            // A required connection that cannot be made blocks the encasement; it is never dropped to a warning.
            ConduitSourcePlan item = Plan(Req("ICT-1C-1R-50", new[] { e }, r => r with { ConnectToStructures = true }), new[] { e }, mh)[0];
            Assert.False(item.CanApply);
            Assert.Empty(item.Conduits);
            Assert.Contains(item.Issues, i => i.Code == ConduitIssueCodes.ConnectionImpossible && i.Severity == IssueSeverity.Error);
            // Turning the connection off for that end lets it through.
            ConduitSourcePlan off = Plan(Req("ICT-1C-1R-50", new[] { e }, r => r with { ConnectToStructures = true, Overrides = new[] { new ConduitEndOverride(R("1A"), PipeEnd.End, null, false) } }), new[] { e }, mh)[0];
            Assert.True(off.CanApply);
            Assert.False(off.EndEnd.Connect);
            ConduitSourcePlan same = Plan(Req("ICT-1C-1R-50", new[] { e }, r => r with { ConnectToStructures = true }), new[] { e }, mh with { Network = Net })[0];
            Assert.True(same.CanApply);
            Assert.True(same.EndEnd.Connect);
        }

        [Fact]
        public void A_conduit_missing_the_wall_blocks_the_encasement_and_is_named()
        {
            // A narrow 0.3 m manhole: the outer MV columns (±0.6) pass beside it.
            StructureRead mh = Mh("5A1", new Point3(0, 0, 0), 0, 0.3, 0.3);
            EncasementRead e = Enc("1A", new Point3(-20, 0, 0), new Point3(0, 0, 0), endStr: mh.Structure, w: 1.7, ht: 0.5);
            ConduitSourcePlan item = Plan(Req("MV-5C-1R", new[] { e }), new[] { e }, mh)[0];
            Assert.False(item.CanApply);
            OperationIssue miss = Assert.Single(item.Issues, i => i.Code == ConduitIssueCodes.WallMissed);
            Assert.Equal(IssueSeverity.Error, miss.Severity);
            Assert.Contains("conduit 1-1", miss.Message);
            // Explicitly keeping the encasement end at that end lets it through.
            ConduitSourcePlan kept = Plan(Req("MV-5C-1R", new[] { e }, r => r with { Overrides = new[] { new ConduitEndOverride(R("1A"), PipeEnd.End, StructureEndTreatment.EncasementEnd) } }), new[] { e }, mh)[0];
            Assert.True(kept.CanApply);
        }

        [Fact]
        public void Sloped_reversed_bend_mitres_meet_in_xyz()
        {
            // A falls 2% east to the joint; B is drawn from its far end back to the joint (reversed), deflected 31° and falling away.
            EncasementRead a = Enc("1A", new Point3(0, 0, 10.0), new Point3(10, 0, 9.8), w: 0.79, ht: 0.63);
            EncasementRead b = Enc("1B", new Point3(20, 6, 9.6), new Point3(10, 0, 9.8), w: 0.79, ht: 0.63);
            List<ConduitSourcePlan> items = Plan(Req("ICT-4C-3R", new[] { a, b }), new[] { a, b });
            Assert.True(items[1].Reversed);
            Assert.All(items, i => Assert.True(i.CanApply, string.Join(" | ", i.Issues.Select(x => x.Message))));
            foreach (PlannedConduit pa in items[0].Conduits)
            {
                PlannedConduit pb = items[1].Conduits.Single(p => p.Column == pa.Column && p.Row == pa.Row);
                Assert.True(pa.End.DistanceTo(pb.End) < 1e-9, $"conduit {pa.Column}-{pa.Row}: {pa.End} vs {pb.End}");
                // The shared level stays within the tolerance of A's own grade at the mitre.
                double t = pa.End.X / 10; // A runs along x from 0 to 10
                Assert.True(Math.Abs(pa.End.Z - (10.0 - 0.2 * t + pa.OffsetUpM)) <= ConduitLimits.MitreLevelToleranceM);
            }
        }

        /// <summary>Every matching conduit pair of two planned banks meets at one point (XYZ) at their joint.</summary>
        private static void AssertJointClosed(ConduitSourcePlan a, ConduitSourcePlan b, Func<PlannedConduit, Point3> aEnd, Func<PlannedConduit, Point3> bEnd)
        {
            Assert.True(a.CanApply && b.CanApply, string.Join(" | ", a.Issues.Concat(b.Issues).Select(i => i.Message)));
            Assert.Equal(a.Conduits.Count, b.Conduits.Count);
            foreach (PlannedConduit pa in a.Conduits)
            {
                PlannedConduit pb = b.Conduits.Single(p => p.Column == pa.Column && p.Row == pa.Row);
                Assert.True(aEnd(pa).DistanceTo(bEnd(pb)) < 1e-9, $"conduit {pa.Column}-{pa.Row}: {aEnd(pa)} vs {bEnd(pb)}");
            }
        }

        [Fact]
        public void Collinear_banks_5mm_apart_share_one_endpoint()
        {
            // Paired (5 mm is within the 10 mm joint tolerance) and collinear: the conduits must not keep the 5 mm gap.
            EncasementRead a = Enc("1A", new Point3(0, 0, 0), new Point3(10, 0, 0));
            EncasementRead b = Enc("1B", new Point3(10.005, 0, 0), new Point3(20, 0, 0));
            List<ConduitSourcePlan> items = Plan(Req("ICT-2C-1R", new[] { a, b }), new[] { a, b });
            Assert.Equal(ConduitEndKind.Joint, items[0].EndEnd.Kind);
            Assert.Equal(JointTreatment.Mitre, items[0].EndEnd.Joint);
            AssertJointClosed(items[0], items[1], p => p.End, p => p.Start);
            Assert.All(items[0].Conduits, p => Assert.Equal(10.0025, p.End.X, 9)); // the midpoint of the two ends

            // Reversed, and 5 mm apart sideways instead of along.
            EncasementRead c = Enc("1C", new Point3(20, 0.005, 0), new Point3(10, 0.005, 0));
            List<ConduitSourcePlan> side = Plan(Req("ICT-2C-1R", new[] { a, c }), new[] { a, c });
            Assert.True(side[1].Reversed);
            AssertJointClosed(side[0], side[1], p => p.End, p => p.End);
            // Column 1 stays on the run's left (north) on both sides of the joint.
            Assert.True(side[0].Conduits.Single(p => p.Column == 1).End.Y > 0.07);
        }

        [Fact]
        public void Bent_banks_5mm_apart_meet_near_the_true_mitre()
        {
            // A 31° bend whose two ends are 5 mm apart: one shared end, close to the mitre of a perfectly closed joint.
            EncasementRead a = Enc("1A", new Point3(0, 0, 0), new Point3(10, 0, 0), w: 0.79, ht: 0.63);
            EncasementRead b = Enc("1B", new Point3(20, 6, 0), new Point3(10.004, 0.003, 0), w: 0.79, ht: 0.63);
            List<ConduitSourcePlan> items = Plan(Req("ICT-4C-3R", new[] { a, b }), new[] { a, b });
            AssertJointClosed(items[0], items[1], p => p.End, p => p.End);
            EncasementRead closed = Enc("1B", new Point3(20, 6, 0), new Point3(10, 0, 0), w: 0.79, ht: 0.63);
            List<ConduitSourcePlan> exact = Plan(Req("ICT-4C-3R", new[] { a, closed }), new[] { a, closed });
            foreach (PlannedConduit p in items[0].Conduits)
                Assert.True(p.End.PlanDistanceTo(exact[0].Conduits.Single(q => q.Column == p.Column && q.Row == p.Row).End) < 0.01);
        }

        [Fact]
        public void Mitre_that_cannot_meet_in_level_is_flagged()
        {
            // Bottom-referenced rows in encasements of different heights sit at different levels about the centreline.
            EncasementRead a = Enc("1A", new Point3(0, 0, 10), new Point3(10, 0, 10), w: 0.47, ht: 0.63);
            EncasementRead b = Enc("1B", new Point3(10, 0, 10), new Point3(20, 5, 10), w: 0.47, ht: 0.79);
            ConduitRequest req = Req("ICT-2C-3R", new[] { a, b }) with { Section = new ConduitSection(ConduitPlacement.BottomReferenced, 0.155) };
            List<ConduitSourcePlan> items = Plan(req, new[] { a, b });
            Assert.All(items, i =>
            {
                Assert.False(i.CanApply);
                Assert.Contains(i.Issues, x => x.Code == ConduitIssueCodes.MitreLevelMismatch && x.Severity == IssueSeverity.Error);
            });
            // Keeping encasement ends at joints avoids the shared end.
            Assert.All(Plan(req with { Ends = new ConduitEndRules { AtJoint = JointTreatment.EncasementEnd } }, new[] { a, b }), i => Assert.True(i.CanApply));
        }

        // ---------- Workflow ----------

        private sealed class FakeDrawing : IConduitDrawingAccess
        {
            public ConduitDrawingRead ReadResult;
            public FakeSession Session = new FakeSession();
            public double Units = 1.0;
            public DrawingIdentity ActiveDrawing() => Dwg;
            public double MetresPerDrawingUnit() => Units;
            public ConduitDrawingRead Read(ConduitRequest request, CancellationToken cancel) => ReadResult;
            public IConduitWriteSession BeginWrite(DrawingIdentity drawing) => Session;
        }

        private sealed class FakeSession : IConduitWriteSession
        {
            public Dictionary<DrawingObjectRef, EncasementSnapshot> Encasements = new Dictionary<DrawingObjectRef, EncasementSnapshot>();
            public HashSet<DrawingObjectRef> FailFill = new HashSet<DrawingObjectRef>();
            public ConduitPartSnapshot Part = Duct;
            public List<DrawingObjectRef> Filled = new List<DrawingObjectRef>();
            public bool Committed, Disposed;
            public EncasementSnapshot ReadEncasement(DrawingObjectRef e) => Encasements.TryGetValue(e, out var s) ? s : null;
            public StructureSnapshot ReadStructure(DrawingObjectRef s, ConduitRequest r) => null;
            public ConduitDestinationPlan ResolveDestination(ConduitSourcePlan item, ConduitRequest r) => Dest;
            public ConduitPartSnapshot ResolvePart(ConduitSourcePlan item, ConduitRequest r) => Part;
            public ConduitItemOutcome Fill(ConduitSourcePlan item, ConduitRequest r)
            {
                if (FailFill.Contains(item.Encasement))
                    return new ConduitItemOutcome(item.Encasement, item.Name, ItemStatus.RolledBack, null, 0, OperationIssue.Error(ConduitIssueCodes.ItemFailed, "boom"));
                Filled.Add(item.Encasement);
                return new ConduitItemOutcome(item.Encasement, item.Name, ItemStatus.Committed, item.Conduits.Select((c, i) => R("F" + (Filled.Count * 100 + i + 1).ToString("X"))).ToList());
            }
            public IReadOnlyList<OperationIssue> Notes => Array.Empty<OperationIssue>();
            public void Commit() => Committed = true;
            public void Dispose() => Disposed = true;
        }

        private static (ConduitWorkflow wf, FakeDrawing d, ConduitRequest req) Setup()
        {
            EncasementRead a = Enc("1A", new Point3(0, 0, 0), new Point3(10, 0, 0));
            EncasementRead b = Enc("1B", new Point3(20, 0, 0), new Point3(10, 0, 0));
            FakeDrawing d = new FakeDrawing { ReadResult = new ConduitDrawingRead(new[] { a, b }, null, null) };
            d.Session.Encasements[a.Encasement] = a.Snapshot;
            d.Session.Encasements[b.Encasement] = b.Snapshot;
            return (new ConduitWorkflow(d), d, Req("ICT-2C-1R", new[] { a, b }));
        }

        [Fact]
        public void Preview_then_apply_fills_every_chosen_encasement_once()
        {
            (ConduitWorkflow wf, FakeDrawing d, ConduitRequest req) = Setup();
            ConduitPreview p = wf.Preview(req, CancellationToken.None);
            Assert.Equal(OperationStatus.Succeeded, p.Status);
            Assert.Equal(4, p.ConduitsToCreate);
            Assert.Equal(ConduitTemplateLibrary.Find("ICT-2C-1R").Notes, p.TemplateNotes);
            ConduitResult res = wf.Apply(new ConduitApplyRequest(p, new[] { R("1A"), R("1B") }), null, CancellationToken.None);
            Assert.Equal(OperationStatus.Succeeded, res.Status);
            Assert.Equal(4, res.ConduitsCreated);
            Assert.True(d.Session.Committed && d.Session.Disposed);
            Assert.DoesNotContain(res.Issues, i => i.Code == ConduitIssueCodes.RunIncomplete);
        }

        [Fact]
        public void A_rolled_back_side_of_a_mitre_is_reported_as_an_incomplete_run()
        {
            (ConduitWorkflow wf, FakeDrawing d, ConduitRequest req) = Setup();
            d.Session.FailFill.Add(R("1B"));
            ConduitPreview p = wf.Preview(req, CancellationToken.None);
            ConduitResult res = wf.Apply(new ConduitApplyRequest(p, new[] { R("1A"), R("1B") }), null, CancellationToken.None);
            Assert.Equal(OperationStatus.PartiallySucceeded, res.Status);
            Assert.Equal(1, res.EncasementsFilled);
            Assert.Contains(res.Issues, i => i.Code == ConduitIssueCodes.RunIncomplete && i.Message.Contains("Fill Duct 1B"));
        }

        [Fact]
        public void A_part_changed_since_preview_rejects_the_encasement()
        {
            (ConduitWorkflow wf, FakeDrawing d, ConduitRequest req) = Setup();
            ConduitPreview p = wf.Preview(req, CancellationToken.None);
            d.Session.Part = Duct with { OutsideDiameterM = 0.125 };
            ConduitResult res = wf.Apply(new ConduitApplyRequest(p, new[] { R("1A") }), null, CancellationToken.None);
            Assert.Equal(OperationStatus.Rejected, res.Status);
            Assert.Equal(ConduitIssueCodes.PartChanged, res.Items[0].Issue.Code);
            Assert.False(d.Session.Committed); // nothing committed, so the session is left uncommitted
        }

        [Fact]
        public void Non_metre_drawing_is_rejected_from_its_own_units()
        {
            (ConduitWorkflow wf, FakeDrawing d, ConduitRequest req) = Setup();
            d.Units = 0.3048;
            ConduitPreview p = wf.Preview(req, CancellationToken.None); // the caller's MetresPerDrawingUnit = 1 is not trusted
            Assert.Equal(OperationStatus.Rejected, p.Status);
            Assert.Contains(p.Issues, i => i.Code == ConduitIssueCodes.UnitsNotMetres);
        }
    }
}
