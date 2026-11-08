using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using C3DProTools.Features.Networks.Contracts;

namespace C3DProTools.Tests
{
    /// <summary>The split contracts: identities and handles, request validation, plan items, stale plans, apply checks and result status.</summary>
    public class SplitContractsTests
    {
        private static readonly DrawingIdentity Drawing = new DrawingIdentity("doc-1", "{6F1C0E7A-0000-0000-0000-000000000001}", @"C:\jobs\plan.dwg");
        private static DrawingObjectRef H(string h) => DrawingObjectRef.Of(h);

        private static SplitRequest Equal(params string[] pipes) =>
            new SplitRequest { Drawing = Drawing, Mode = SplitMode.EqualParts, Parts = 3, Pipes = pipes.Select(H).ToList() };

        private static PipeSnapshot Snap(double x1 = 10, string size = "DN300", string end = "2B") =>
            new PipeSnapshot(new Point3(0, 0, 100), new Point3(x1, 0, 99.5), H("1A"), null, end == null ? null : H(end), H("F1"), H("51"), size, false, 0);

        private static SplitItemPlan Item(string pipe = "3C", params double[] at) =>
            new SplitItemPlan(H(pipe), "P-" + pipe, false, 10, at.Select(x => new Point3(x, 0, 100 - 0.05 * x)).ToList(), Snap());

        private static SplitPreview Plan(params SplitItemPlan[] items) =>
            new SplitPreview { Drawing = Drawing, Request = Equal("3C"), Status = OperationStatus.Succeeded, Items = items };

        private static IEnumerable<string> Codes(IEnumerable<OperationIssue> issues) => issues.Select(i => i.Code);

        // ---------- Drawing identity and handles ----------

        [Fact]
        public void TheSameDrawing_MatchesOnlyBySessionAndFingerprint()
        {
            Assert.True(Drawing.Matches(Drawing with { FilePath = @"D:\copy\renamed.dwg" }));
            Assert.True(Drawing.Matches(Drawing with { FingerprintGuid = Drawing.FingerprintGuid.ToLowerInvariant() }));
            // Reopened (new session key) or another drawing (other fingerprint): not the same.
            Assert.False(Drawing.Matches(Drawing with { SessionKey = "doc-2" }));
            Assert.False(Drawing.Matches(Drawing with { FingerprintGuid = "{00000000-0000-0000-0000-000000000002}" }));
            Assert.False(Drawing.Matches(null));
            Assert.False(new DrawingIdentity("", "", "").Matches(new DrawingIdentity("", "", "")));
        }

        [Fact]
        public void Handles_AreNormalised_AndBadOnesRefused()
        {
            Assert.Equal("2A3F", H("002a3f").Handle);
            Assert.Equal(H("2A3F"), H(" 2a3f "));
            foreach (string bad in new[] { "", "0", "000", "XYZ", "12345678901234567", null })
                Assert.False(DrawingObjectRef.TryCreate(bad, out _));
            Assert.False(default(DrawingObjectRef).IsValid);
            Assert.Throws<ArgumentException>(() => DrawingObjectRef.Of("nope"));
        }

        // ---------- Request validation ----------

        [Fact]
        public void AValidRequest_HasNoIssues_InEachMode()
        {
            Assert.Empty(SplitValidation.Validate(Equal("3C", "4D")));
            Assert.Empty(SplitValidation.Validate(Equal("3C") with { Mode = SplitMode.FixedLength, Every = 20 }));
            // Picked points with no pipes chosen: every gravity pipe in the drawing is a candidate.
            Assert.Empty(SplitValidation.Validate(new SplitRequest { Drawing = Drawing, Mode = SplitMode.AtPoints, Picks = new[] { new Point3(5, 0, 0) }, PickTolerance = 1 }));
        }

        [Fact]
        public void TheDrawing_MustBeNamed()
        {
            Assert.Contains(SplitIssueCodes.DrawingMissing, Codes(SplitValidation.Validate(Equal("3C") with { Drawing = null })));
            Assert.Contains(SplitIssueCodes.DrawingMissing, Codes(SplitValidation.Validate(Equal("3C") with { Drawing = new DrawingIdentity("doc-1", "", null) })));
            Assert.Contains(SplitIssueCodes.DrawingMissing, Codes(SplitValidation.Validate(null)));
        }

        [Fact]
        public void Pipes_AreNeeded_ValidAndNotRepeated()
        {
            Assert.Contains(SplitIssueCodes.PipesNone, Codes(SplitValidation.Validate(Equal())));
            List<OperationIssue> twice = SplitValidation.Validate(Equal("3C", "03c"));
            Assert.Contains(SplitIssueCodes.PipeDuplicate, Codes(twice));
            Assert.Equal(H("3C"), twice.Single(i => i.Code == SplitIssueCodes.PipeDuplicate).Object);
            Assert.Contains(SplitIssueCodes.PipeInvalid, Codes(SplitValidation.Validate(Equal("3C") with { Pipes = new[] { H("3C"), default } })));
            Assert.Contains(SplitIssueCodes.ModeUnknown, Codes(SplitValidation.Validate(Equal("3C") with { Mode = (SplitMode)7 })));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(101)]
        public void Parts_OutsideTwoToAHundred_AreRefused(int parts)
        {
            OperationIssue issue = Assert.Single(SplitValidation.Validate(Equal("3C") with { Parts = parts }));
            Assert.Equal(SplitIssueCodes.PartsRange, issue.Code);
            Assert.Equal(nameof(SplitRequest.Parts), issue.Field);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(0.049)]
        [InlineData(-5.0)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void AFixedLength_BelowTheShortestPiece_OrNotFinite_IsRefused(double every)
        {
            Assert.Contains(SplitIssueCodes.EveryRange, Codes(SplitValidation.Validate(Equal("3C") with { Mode = SplitMode.FixedLength, Every = every })));
        }

        [Fact]
        public void PickedPoints_NeedPointsAndASensiblePickDistance()
        {
            SplitRequest r = new SplitRequest { Drawing = Drawing, Mode = SplitMode.AtPoints, Picks = new[] { new Point3(1, 1, 0) }, PickTolerance = 1 };
            Assert.Contains(SplitIssueCodes.PicksNone, Codes(SplitValidation.Validate(r with { Picks = Array.Empty<Point3>() })));
            Assert.Contains(SplitIssueCodes.PickNotFinite, Codes(SplitValidation.Validate(r with { Picks = new[] { new Point3(double.NaN, 0, 0) } })));
            foreach (double tol in new[] { 0, -1, 100.5, double.NaN })
                Assert.Contains(SplitIssueCodes.PickToleranceRange, Codes(SplitValidation.Validate(r with { PickTolerance = tol })));
            // Fields of other modes are ignored.
            Assert.Empty(SplitValidation.Validate(r with { Parts = 0, Every = -1 }));
        }

        [Fact]
        public void AChosenStructure_NeedsItsListFamilyAndSize()
        {
            SplitStructureChoice ok = new SplitStructureChoice(H("5E"), "", "Concentric Cylindrical Structure", "1200 dia");
            Assert.Empty(SplitValidation.Validate(Equal("3C") with { Structure = ok }));
            Assert.Contains(SplitIssueCodes.StructureIncomplete, Codes(SplitValidation.Validate(Equal("3C") with { Structure = ok with { SizeName = " " } })));
            Assert.Contains(SplitIssueCodes.StructureIncomplete, Codes(SplitValidation.Validate(Equal("3C") with { Structure = ok with { FamilyName = "" } })));
            Assert.Contains(SplitIssueCodes.StructureIncomplete, Codes(SplitValidation.Validate(Equal("3C") with { Structure = ok with { PartsList = default } })));
        }

        // ---------- Plan items ----------

        [Fact]
        public void PlanItemPoints_AreOrdered_AwayFromTheEnds()
        {
            Func<Point3, double> along = p => p.X;
            Assert.Null(SplitValidation.CheckItem(Item("3C", 2.5, 5, 7.5), along));
            Assert.Equal(3, Item("3C", 3, 6).PieceCount);
            Assert.Equal(SplitIssueCodes.ItemPointsInvalid, SplitValidation.CheckItem(Item("3C", 5, 2.5), along).Code);       // out of order
            Assert.Equal(SplitIssueCodes.ItemPointsInvalid, SplitValidation.CheckItem(Item("3C", 5, 5), along).Code);         // a zero-length piece
            Assert.Equal(SplitIssueCodes.ItemPointsInvalid, SplitValidation.CheckItem(Item("3C", 0.04), along).Code);         // too near the start
            Assert.Equal(SplitIssueCodes.ItemPointsInvalid, SplitValidation.CheckItem(Item("3C", 9.97), along).Code);         // too near the end
            Assert.Null(SplitValidation.CheckItem(Item("3C", 0.05, 9.95), along));                                           // exactly the shortest piece
            Assert.Equal(SplitIssueCodes.ItemPointsInvalid, SplitValidation.CheckItem(Item("3C", 3, 3.03), along).Code);      // a piece between points too short
            Assert.Null(SplitValidation.CheckItem(Item("3C", 3, 3.05), along));                                              // the shortest piece between points
        }

        [Fact]
        public void Messages_NameTheDrawingsUnit_AndLengthsAreNotConverted()
        {
            SplitRequest feet = Equal("3C") with { Drawing = Drawing with { UnitLabel = "ft" }, Mode = SplitMode.FixedLength, Every = 0.01 };
            Assert.EndsWith("0.05 ft.", SplitValidation.Validate(feet).Single().Message);
            Assert.Contains("drawing units", SplitValidation.Validate(feet with { Drawing = Drawing }).Single().Message);
            // 0.05 is the same number in any drawing: drawing units, never metres converted.
            Assert.Empty(SplitValidation.Validate(feet with { Every = 0.05 }));
            Assert.Equal("drawing units", Drawing.Unit);
            // The unit is for display: it does not change which drawing a plan is for.
            Assert.True(Drawing.Matches(Drawing with { UnitLabel = "m" }));
        }

        // ---------- Frozen collections ----------

        [Fact]
        public void APlan_CannotChangeAfterThePreview()
        {
            List<Point3> points = new List<Point3> { new Point3(5, 0, 99.75) };
            SplitItemPlan item = new SplitItemPlan(H("3C"), "P-3C", false, 10, points, Snap());
            List<SplitItemPlan> items = new List<SplitItemPlan> { item };
            List<DrawingObjectRef> pipes = new List<DrawingObjectRef> { H("3C") };
            Point3[] picks = { new Point3(5, 0, 0) };
            SplitPreview plan = Plan() with { Items = items, Request = Equal() with { Pipes = pipes, Picks = picks } };

            // The caller changes what it passed in: the plan does not.
            points.Add(new Point3(7, 0, 99.65));
            items.Clear();
            pipes.Add(H("99"));
            picks[0] = new Point3(1, 1, 1);
            Assert.Single(plan.Items);
            Assert.Single(plan.Items[0].Points);
            Assert.Equal(new[] { H("3C") }, plan.Request.Pipes);
            Assert.Equal(new Point3(5, 0, 0), plan.Request.Picks[0]);

            // Nor can the plan's own lists be changed through a cast, and a copy made with "with" is frozen too.
            Assert.Throws<NotSupportedException>(() => ((IList<Point3>)plan.Items[0].Points).Add(new Point3(1, 0, 0)));
            List<Point3> more = new List<Point3> { new Point3(4, 0, 99.8) };
            SplitItemPlan copy = item with { Points = more };
            more.Clear();
            Assert.Single(copy.Points);

            List<DrawingObjectRef> chosen = new List<DrawingObjectRef> { H("3C") };
            SplitApplyRequest apply = new SplitApplyRequest(plan, chosen);
            chosen.Add(H("4D"));
            Assert.Single(apply.Chosen);

            List<DrawingObjectRef> made = new List<DrawingObjectRef> { H("A1") };
            SplitItemOutcome outcome = new SplitItemOutcome(H("3C"), "P-3C", ItemStatus.Committed, made, null);
            made.Add(H("A2"));
            Assert.Single(outcome.NewPipes);
            Assert.Empty(outcome.NewStructures);
        }

        [Fact]
        public void APipeSplitIntoTooManyPieces_IsLeftOut()
        {
            SplitItemPlan many = Item("3C") with { Points = Enumerable.Range(1, SplitLimits.MaxPiecesPerPipe).Select(i => new Point3(i * 0.001, 0, 0)).ToList() };
            OperationIssue issue = SplitValidation.CheckItem(many, p => p.X);
            Assert.Equal(SplitIssueCodes.TooManyPieces, issue.Code);
            Assert.Equal(IssueSeverity.Warning, issue.Severity);
        }

        // ---------- Stale plans ----------

        [Fact]
        public void AnUnchangedPipe_IsNotStale()
        {
            Assert.Null(SplitValidation.Compare(Item("3C", 5), Snap()));
            Assert.Null(SplitValidation.Compare(Item("3C", 5), Snap() with { Start = new Point3(0, 0, 100 + 1e-9), PartSizeName = "dn300" }));
        }

        [Fact]
        public void AMissingOrChangedPipe_IsStale_AndSaysWhy()
        {
            SplitItemPlan item = Item("3C", 5);
            Assert.Equal(SplitIssueCodes.PipeMissing, SplitValidation.Compare(item, null).Code);
            foreach (PipeSnapshot changed in new[]
            {
                Snap(x1: 10.01),                                     // moved
                Snap(size: "DN450"),                                 // resized
                Snap(end: null),                                     // end disconnected
                Snap(end: "7F"),                                     // end reconnected elsewhere
                Snap() with { Network = H("9A") },                   // other network
                Snap() with { Curved = true, Bulge = 0.1 },          // now curved
                Snap() with { StartStructure = H("2C") },            // start connected
                Snap() with { PartFamily = H("F2") },                // another family, same size name
                Snap() with { PartFamily = null },                   // family gone
                Snap() with { PartSize = H("52") }                   // another size object, same name
            })
            {
                OperationIssue issue = SplitValidation.Compare(item, changed);
                Assert.Equal(SplitIssueCodes.PipeChanged, issue.Code);
                Assert.Equal(item.Pipe, issue.Object);
            }
            Assert.Contains("moved", SplitValidation.Compare(item, Snap(x1: 11)).Message);
        }

        // ---------- Apply checks ----------

        [Fact]
        public void Apply_NeedsASucceededPlan_ForThisDrawing()
        {
            SplitPreview plan = Plan(Item("3C", 5));
            Assert.Empty(SplitValidation.ValidateApply(new SplitApplyRequest(plan, new[] { H("3C") }), Drawing));
            foreach (SplitPreview notApplicable in new[] { plan with { Status = OperationStatus.Rejected }, plan with { Status = OperationStatus.NoChange }, plan with { Items = Array.Empty<SplitItemPlan>() }, null })
                Assert.Contains(SplitIssueCodes.PlanNotApplicable, Codes(SplitValidation.ValidateApply(new SplitApplyRequest(notApplicable, new[] { H("3C") }), Drawing)));
            Assert.Contains(SplitIssueCodes.DrawingMismatch, Codes(SplitValidation.ValidateApply(new SplitApplyRequest(plan, new[] { H("3C") }), Drawing with { SessionKey = "doc-2" })));
        }

        [Fact]
        public void Apply_TakesOnlyPipesOfThePlan_EachOnce()
        {
            SplitPreview plan = Plan(Item("3C", 5), Item("4D", 5));
            Assert.Contains(SplitIssueCodes.ChosenNotInPlan, Codes(SplitValidation.ValidateApply(new SplitApplyRequest(plan, new[] { H("3C"), H("99") }), Drawing)));
            Assert.Contains(SplitIssueCodes.ChosenDuplicate, Codes(SplitValidation.ValidateApply(new SplitApplyRequest(plan, new[] { H("3C"), H("3c") }), Drawing)));
            // Unticking every row is not an error: it is no change.
            Assert.Empty(SplitValidation.ValidateApply(new SplitApplyRequest(plan, Array.Empty<DrawingObjectRef>()), Drawing));
        }

        // ---------- Result status and counts ----------

        [Fact]
        public void TheStatus_FollowsTheItems()
        {
            OperationStatus S(bool cancelled, params ItemStatus[] items) => OperationOutcome.Summarize(items, cancelled);
            Assert.Equal(OperationStatus.Succeeded, S(false, ItemStatus.Committed, ItemStatus.Committed));
            Assert.Equal(OperationStatus.PartiallySucceeded, S(false, ItemStatus.Committed, ItemStatus.RolledBack));
            Assert.Equal(OperationStatus.PartiallySucceeded, S(false, ItemStatus.Committed, ItemStatus.Rejected));
            Assert.Equal(OperationStatus.RolledBack, S(false, ItemStatus.RolledBack, ItemStatus.Rejected));
            Assert.Equal(OperationStatus.Rejected, S(false, ItemStatus.Rejected, ItemStatus.Rejected));
            Assert.Equal(OperationStatus.NoChange, S(false));
            Assert.Equal(OperationStatus.Cancelled, S(true, ItemStatus.Committed, ItemStatus.NotAttempted));
            Assert.Equal(OperationStatus.Cancelled, S(true));
            // An unknown outcome wins over everything: read the drawing before trying again.
            Assert.Equal(OperationStatus.OutcomeUnknown, S(true, ItemStatus.Committed, ItemStatus.OutcomeUnknown));
        }

        [Fact]
        public void Counts_AreOfCommittedItemsOnly()
        {
            DrawingObjectRef[] two = { H("A1"), H("A2") };
            SplitResult r = SplitResult.From(Plan(Item("3C", 3, 6), Item("4D", 5)), new[]
            {
                new SplitItemOutcome(H("3C"), "P-3C", ItemStatus.Committed, two, new[] { H("B1"), H("B2") }),
                new SplitItemOutcome(H("4D"), "P-4D", ItemStatus.RolledBack, new[] { H("A9") }, new[] { H("B9") },
                    OperationIssue.Error(SplitIssueCodes.ItemConnectionFailed, "P-4D could not be connected; it was left as it was.", null, H("4D")))
            }, cancelled: false);
            Assert.Equal(OperationStatus.PartiallySucceeded, r.Status);
            Assert.Equal(1, r.PipesSplit);
            Assert.Equal(2, r.PipesCreated);
            Assert.Equal(3, r.PiecesTotal);
            Assert.Equal(2, r.StructuresAdded);
            Assert.Equal(Drawing, r.Drawing);
            Assert.Equal(OperationStatus.Rejected, SplitResult.Rejected(Plan(), new[] { OperationIssue.Error(SplitIssueCodes.DrawingMismatch, "x") }).Status);
        }

        // ---------- Boundary ----------

        [Fact]
        public void TheContracts_UseNoAutodeskOrUiTypes()
        {
            Type[] contracts = typeof(SplitRequest).Assembly.GetTypes().Where(t => t.Namespace == typeof(SplitRequest).Namespace).ToArray();
            Assert.NotEmpty(contracts);
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (Type t in contracts)
            {
                IEnumerable<Type> used = t.GetFields(all).Select(f => f.FieldType)
                    .Concat(t.GetProperties(all).Select(p => p.PropertyType))
                    .Concat(t.GetMethods(all).SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType)))
                    .Concat(t.GetConstructors(all).SelectMany(c => c.GetParameters().Select(p => p.ParameterType)));
                foreach (Type u in used.SelectMany(Flatten))
                {
                    string ns = u.Namespace ?? "";
                    Assert.False(ns.StartsWith("Autodesk", StringComparison.Ordinal) || ns.StartsWith("System.Windows", StringComparison.Ordinal),
                        $"{t.Name} uses {u.FullName}");
                }
            }

            static IEnumerable<Type> Flatten(Type t) =>
                new[] { t }.Concat(t.HasElementType ? Flatten(t.GetElementType()) : Enumerable.Empty<Type>())
                           .Concat(t.IsGenericType ? t.GetGenericArguments().SelectMany(Flatten) : Enumerable.Empty<Type>());
        }
    }
}
