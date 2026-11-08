using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using C3DProTools.Features.Networks.Application;
using C3DProTools.Features.Networks.Contracts;

namespace C3DProTools.Tests
{
    /// <summary>The split workflow against a fake drawing: planning rules, drawing checks, stale pipes, per-pipe rollback, cancellation and commit.</summary>
    public class SplitWorkflowTests
    {
        private static readonly DrawingIdentity Drawing = new DrawingIdentity("doc-1", "{6F1C0E7A-0000-0000-0000-000000000001}", @"C:\jobs\plan.dwg", "m");
        private static DrawingObjectRef H(string h) => DrawingObjectRef.Of(h);

        private static PipeSnapshot Snap(string pipe) => new PipeSnapshot(new Point3(0, 0, 100), new Point3(10, 0, 99.5), H("1A"), null, H("2B"), H("F1"), H("51"), "DN300", false, 0);

        private static PlannedPipe Pipe(string handle, params double[] along) =>
            new PlannedPipe(new SplitItemPlan(H(handle), "P-" + handle, false, 10, along.Select(d => new Point3(d, 0, 100 - 0.05 * d)).ToList(), Snap(handle)), along);

        private static SplitRequest Equal(params string[] pipes) => new SplitRequest { Drawing = Drawing, Mode = SplitMode.EqualParts, Parts = 2, Pipes = pipes.Select(H).ToList() };

        private sealed class FakeDrawing : ISplitDrawingAccess
        {
            public DrawingIdentity Active = Drawing;
            public DrawingSplitPlan PlanResult = new DrawingSplitPlan(null, 0, null);
            public readonly Dictionary<DrawingObjectRef, PipeSnapshot> Now = new Dictionary<DrawingObjectRef, PipeSnapshot>();
            public readonly HashSet<DrawingObjectRef> Fails = new HashSet<DrawingObjectRef>();
            public readonly HashSet<DrawingObjectRef> Throws = new HashSet<DrawingObjectRef>();
            public bool CommitThrows, DisposeThrows, Committed, Disposed, Planned;
            public Action<DrawingObjectRef> OnSplit;
            public readonly List<DrawingObjectRef> Split = new List<DrawingObjectRef>();

            public DrawingIdentity ActiveDrawing() => Active;
            public DrawingSplitPlan Plan(SplitRequest request, CancellationToken cancel) { Planned = true; return PlanResult; }
            public ISplitWriteSession BeginWrite(DrawingIdentity drawing) =>
                drawing.Matches(Active) ? new Session(this) : throw new InvalidOperationException("not current");

            private sealed class Session : ISplitWriteSession
            {
                private readonly FakeDrawing _d;
                public Session(FakeDrawing d) { _d = d; }
                public IReadOnlyList<OperationIssue> Notes => new[] { OperationIssue.Info(SplitIssueCodes.PartsListChanged, "Added MH 1200 to Storm.") };
                public PipeSnapshot ReadPipe(DrawingObjectRef pipe) => _d.Now.TryGetValue(pipe, out PipeSnapshot s) ? s : null;
                public SplitItemOutcome SplitPipe(SplitItemPlan item, SplitStructureChoice structure)
                {
                    _d.OnSplit?.Invoke(item.Pipe);
                    if (_d.Throws.Contains(item.Pipe)) throw new InvalidOperationException("engine fault");
                    _d.Split.Add(item.Pipe);
                    return _d.Fails.Contains(item.Pipe)
                        ? new SplitItemOutcome(item.Pipe, item.PipeName, ItemStatus.RolledBack, null, null, OperationIssue.Error(SplitIssueCodes.ItemConnectionFailed, "could not connect", null, item.Pipe))
                        : new SplitItemOutcome(item.Pipe, item.PipeName, ItemStatus.Committed, new[] { H("A" + item.Pipe.Handle) }, null);
                }
                public void Commit() { if (_d.CommitThrows) throw new InvalidOperationException("commit failed"); _d.Committed = true; }
                public void Dispose() { _d.Disposed = true; if (_d.DisposeThrows) throw new InvalidOperationException("lock release failed"); }
            }
        }

        private static (FakeDrawing d, SplitWorkflow w, SplitPreview plan) Planned(params PlannedPipe[] pipes)
        {
            FakeDrawing d = new FakeDrawing { PlanResult = new DrawingSplitPlan(pipes, 0, null) };
            foreach (PlannedPipe p in pipes) d.Now[p.Item.Pipe] = p.Item.Snapshot;
            SplitWorkflow w = new SplitWorkflow(d);
            // The request names the pipes (one, when the drawing plans none, so the request itself is valid).
            string[] handles = pipes.Length == 0 ? new[] { "3C" } : pipes.Select(p => p.Item.Pipe.Handle).ToArray();
            return (d, w, w.Preview(Equal(handles), CancellationToken.None));
        }

        private static SplitApplyRequest All(SplitPreview plan) => new SplitApplyRequest(plan, plan.Items.Select(i => i.Pipe).ToList());

        // ---------- Preview ----------

        [Fact]
        public void AnInvalidRequest_IsRejected_WithoutReadingTheDrawing()
        {
            FakeDrawing d = new FakeDrawing();
            SplitPreview p = new SplitWorkflow(d).Preview(Equal("3C") with { Parts = 1 }, CancellationToken.None);
            Assert.Equal(OperationStatus.Rejected, p.Status);
            Assert.Contains(p.Issues, i => i.Code == SplitIssueCodes.PartsRange);
            Assert.False(d.Planned);
        }

        [Fact]
        public void ARequestForAnotherDrawing_IsRejected()
        {
            FakeDrawing d = new FakeDrawing { Active = Drawing with { SessionKey = "doc-2" } };
            SplitPreview p = new SplitWorkflow(d).Preview(Equal("3C"), CancellationToken.None);
            Assert.Equal(OperationStatus.Rejected, p.Status);
            Assert.Contains(p.Issues, i => i.Code == SplitIssueCodes.DrawingMismatch);
            Assert.False(d.Planned);
        }

        [Fact]
        public void APreview_ListsThePipes_AndChangesNothing()
        {
            (FakeDrawing d, _, SplitPreview p) = Planned(Pipe("3C", 5), Pipe("4D", 2.5, 5, 7.5));
            Assert.Equal(OperationStatus.Succeeded, p.Status);
            Assert.Equal(new[] { 2, 4 }, p.Items.Select(i => i.PieceCount));
            Assert.Equal(Atomicity.PerItem, p.Atomicity);
            Assert.Empty(d.Split);
            Assert.False(d.Committed);
        }

        [Fact]
        public void PickedPointsTooCloseOnOnePipe_DropTheSecond_AndCountAsMissed()
        {
            FakeDrawing d = new FakeDrawing { PlanResult = new DrawingSplitPlan(new[] { Pipe("3C", 3, 3.03, 6) }, 1, null) };
            SplitPreview p = new SplitWorkflow(d).Preview(new SplitRequest { Drawing = Drawing, Mode = SplitMode.AtPoints, Picks = new[] { new Point3(3, 0, 0) } }, CancellationToken.None);
            Assert.Equal(OperationStatus.Succeeded, p.Status);
            Assert.Equal(new[] { 3.0, 6.0 }, p.Items.Single().Points.Select(x => x.X));
            Assert.Equal(2, p.MissedPicks);
            Assert.Contains(p.Issues, i => i.Code == SplitIssueCodes.PicksMissed);
        }

        [Fact]
        public void APipeTooShortForTheParts_IsLeftOut_AndNamed()
        {
            // 0.08 long into 2 parts would give 0.04 pieces: left out with a warning; the other pipe is planned.
            PlannedPipe tiny = new PlannedPipe(new SplitItemPlan(H("9A"), "P-9A", false, 0.08, new[] { new Point3(0.04, 0, 0) }, Snap("9A")), new[] { 0.04 });
            (_, _, SplitPreview p) = Planned(Pipe("3C", 5), tiny);
            Assert.Equal(new[] { H("3C") }, p.Items.Select(i => i.Pipe));
            OperationIssue issue = p.Issues.Single(i => i.Object == H("9A"));
            Assert.Equal(IssueSeverity.Warning, issue.Severity);
            Assert.Contains("0.05 m", issue.Message);
        }

        [Fact]
        public void NothingToSplit_IsNoChange()
        {
            (_, _, SplitPreview p) = Planned();
            Assert.Equal(OperationStatus.NoChange, p.Status);
            Assert.Contains(p.Issues, i => i.Code == SplitIssueCodes.NothingToSplit);
        }

        [Fact]
        public void ACancelledPreview_HasNothingToApply()
        {
            FakeDrawing d = new FakeDrawing();
            SplitPreview p = new SplitWorkflow(d).Preview(Equal("3C"), new CancellationToken(true));
            Assert.Equal(OperationStatus.Cancelled, p.Status);
            Assert.Empty(p.Items);
            Assert.False(d.Planned);
        }

        // ---------- Apply ----------

        [Fact]
        public void Apply_SplitsEachChosenPipe_AndCommitsOnce()
        {
            (FakeDrawing d, SplitWorkflow w, SplitPreview plan) = Planned(Pipe("3C", 5), Pipe("4D", 5), Pipe("5E", 5));
            List<SplitProgress> progress = new List<SplitProgress>();
            SplitResult r = w.Apply(new SplitApplyRequest(plan, new[] { H("3C"), H("5E") }), new Progress(progress), CancellationToken.None);
            Assert.Equal(OperationStatus.Succeeded, r.Status);
            Assert.Equal(new[] { H("3C"), H("5E") }, d.Split);
            Assert.True(d.Committed);
            Assert.True(d.Disposed);
            Assert.Equal(2, r.PipesSplit);
            Assert.Equal(4, r.PiecesTotal);
            Assert.Contains(r.Issues, i => i.Code == SplitIssueCodes.PartsListChanged);
            Assert.Equal(new[] { 0, 1, 2 }, progress.Select(x => x.Completed));
        }

        [Fact]
        public void AStalePipe_IsNotSplit_AndTheOthersGoOn()
        {
            (FakeDrawing d, SplitWorkflow w, SplitPreview plan) = Planned(Pipe("3C", 5), Pipe("4D", 5), Pipe("5E", 5));
            d.Now[H("3C")] = d.Now[H("3C")] with { End = new Point3(12, 0, 99) };
            d.Now.Remove(H("4D"));
            SplitResult r = w.Apply(All(plan), null, CancellationToken.None);
            Assert.Equal(OperationStatus.PartiallySucceeded, r.Status);
            Assert.Equal(new[] { H("5E") }, d.Split);
            Assert.Equal(SplitIssueCodes.PipeChanged, r.Items.Single(i => i.Pipe == H("3C")).Issue.Code);
            Assert.Equal(SplitIssueCodes.PipeMissing, r.Items.Single(i => i.Pipe == H("4D")).Issue.Code);
            Assert.All(r.Items.Where(i => i.Pipe != H("5E")), i => Assert.Equal(ItemStatus.Rejected, i.Status));
        }

        [Fact]
        public void AFailedPipe_IsUndoneOnItsOwn_AndCountsOnlyWhatWasKept()
        {
            (FakeDrawing d, SplitWorkflow w, SplitPreview plan) = Planned(Pipe("3C", 5), Pipe("4D", 5));
            d.Fails.Add(H("3C"));
            SplitResult r = w.Apply(All(plan), null, CancellationToken.None);
            Assert.Equal(OperationStatus.PartiallySucceeded, r.Status);
            Assert.Equal(1, r.PipesSplit);
            Assert.Equal(2, r.PiecesTotal);
            Assert.Equal(ItemStatus.RolledBack, r.Items.Single(i => i.Pipe == H("3C")).Status);
            Assert.True(d.Committed);
        }

        [Fact]
        public void WhenEveryPipeFails_NothingIsCommitted_AndPartsListNotesAreNotClaimed()
        {
            (FakeDrawing d, SplitWorkflow w, SplitPreview plan) = Planned(Pipe("3C", 5), Pipe("4D", 5));
            d.Fails.UnionWith(new[] { H("3C"), H("4D") });
            SplitResult r = w.Apply(All(plan), null, CancellationToken.None);
            Assert.Equal(OperationStatus.RolledBack, r.Status);
            Assert.False(d.Committed);
            Assert.True(d.Disposed);
            Assert.DoesNotContain(r.Issues, i => i.Code == SplitIssueCodes.PartsListChanged);
        }

        [Fact]
        public void CancellingBetweenPipes_KeepsWhatWasSplit_AndSaysWhatWasNotAttempted()
        {
            (FakeDrawing d, SplitWorkflow w, SplitPreview plan) = Planned(Pipe("3C", 5), Pipe("4D", 5), Pipe("5E", 5));
            CancellationTokenSource cts = new CancellationTokenSource();
            d.OnSplit = pipe => { if (pipe == H("3C")) cts.Cancel(); };
            SplitResult r = w.Apply(All(plan), null, cts.Token);
            Assert.Equal(OperationStatus.Cancelled, r.Status);
            Assert.Equal(1, r.PipesSplit);
            Assert.True(d.Committed);
            Assert.Equal(new[] { ItemStatus.Committed, ItemStatus.NotAttempted, ItemStatus.NotAttempted }, r.Items.Select(i => i.Status));
            Assert.Contains(r.Issues, i => i.Code == SplitIssueCodes.Cancelled);
        }

        [Fact]
        public void CancelledBeforeStarting_ChangesNothing()
        {
            (FakeDrawing d, SplitWorkflow w, SplitPreview plan) = Planned(Pipe("3C", 5));
            SplitResult r = w.Apply(All(plan), null, new CancellationToken(true));
            Assert.Equal(OperationStatus.Cancelled, r.Status);
            Assert.Empty(d.Split);
            Assert.False(d.Disposed);
        }

        [Fact]
        public void AFailedCommit_OrAnUnexpectedError_IsReportedAsUnknown()
        {
            (FakeDrawing d, SplitWorkflow w, SplitPreview plan) = Planned(Pipe("3C", 5), Pipe("4D", 5));
            d.CommitThrows = true;
            SplitResult r = w.Apply(All(plan), null, CancellationToken.None);
            Assert.Equal(OperationStatus.OutcomeUnknown, r.Status);
            Assert.Equal(0, r.PipesSplit);

            (FakeDrawing d2, SplitWorkflow w2, SplitPreview plan2) = Planned(Pipe("3C", 5), Pipe("4D", 5), Pipe("5E", 5));
            d2.Throws.Add(H("4D"));
            SplitResult r2 = w2.Apply(All(plan2), null, CancellationToken.None);
            Assert.Equal(OperationStatus.OutcomeUnknown, r2.Status);
            Assert.False(d2.Committed);
            Assert.True(d2.Disposed);
            // The pipe split before and the pipe being split are both in doubt; the last was not attempted.
            Assert.Equal(new[] { ItemStatus.OutcomeUnknown, ItemStatus.OutcomeUnknown, ItemStatus.NotAttempted }, r2.Items.Select(i => i.Status));
            Assert.Contains(r2.Issues, i => i.Code == SplitIssueCodes.OutcomeUnknown);
        }

        [Fact]
        public void AnErrorOnTheFirstPipe_IsUnknown_NotAClaimedRollback()
        {
            (FakeDrawing d, SplitWorkflow w, SplitPreview plan) = Planned(Pipe("3C", 5), Pipe("4D", 5));
            d.Throws.Add(H("3C"));
            SplitResult r = w.Apply(All(plan), null, CancellationToken.None);
            Assert.Equal(OperationStatus.OutcomeUnknown, r.Status);
            Assert.Equal(new[] { ItemStatus.OutcomeUnknown, ItemStatus.NotAttempted }, r.Items.Select(i => i.Status));
            Assert.Equal(H("3C"), r.Items[0].Issue.Object);
        }

        [Fact]
        public void AnErrorAfterARolledBackPipe_IsStillUnknown()
        {
            (FakeDrawing d, SplitWorkflow w, SplitPreview plan) = Planned(Pipe("3C", 5), Pipe("4D", 5), Pipe("5E", 5));
            d.Fails.Add(H("3C"));
            d.Throws.Add(H("4D"));
            SplitResult r = w.Apply(All(plan), null, CancellationToken.None);
            // Not "RolledBack": 3C was undone on its own, but 4D's state is not known.
            Assert.Equal(OperationStatus.OutcomeUnknown, r.Status);
            Assert.Equal(new[] { ItemStatus.RolledBack, ItemStatus.OutcomeUnknown, ItemStatus.NotAttempted }, r.Items.Select(i => i.Status));
        }

        [Fact]
        public void AProblemReleasingTheDrawing_AfterTheCommit_KeepsTheResult_WithAWarning()
        {
            (FakeDrawing d, SplitWorkflow w, SplitPreview plan) = Planned(Pipe("3C", 5));
            d.DisposeThrows = true;
            SplitResult r = w.Apply(All(plan), null, CancellationToken.None);
            Assert.True(d.Committed);
            Assert.Equal(OperationStatus.Succeeded, r.Status);
            Assert.Equal(1, r.PipesSplit);
            Assert.Contains(r.Issues, i => i.Code == SplitIssueCodes.SessionNotClosed);

            // With nothing split, releasing badly changes nothing that was claimed: still a rollback per pipe.
            (FakeDrawing d2, SplitWorkflow w2, SplitPreview plan2) = Planned(Pipe("3C", 5));
            d2.Fails.Add(H("3C"));
            d2.DisposeThrows = true;
            Assert.Equal(OperationStatus.RolledBack, w2.Apply(All(plan2), null, CancellationToken.None).Status);
        }

        [Theory]
        [InlineData(10.0, 2.5, 4)]      // 2.5, 5, 7.5: four pieces
        [InlineData(10.0, 3.0, 4)]      // 3, 6, 9: the last piece 1 long
        [InlineData(10.0, 9.96, 1)]     // the point would leave a 0.04 piece: not split
        [InlineData(10.0, 9.9, 2)]
        [InlineData(10.0, 20.0, 1)]
        [InlineData(1000.0, 0.3, 3334)]  // a long pipe and a short length: counted, never generated
        public void PiecesForAFixedLength_AreCountedBeforeAnyPointIsMade(double length, double every, int pieces)
        {
            Assert.Equal((double)pieces, SplitLimits.PiecesForFixedLength(length, every));
        }

        [Fact]
        public void ATemplateOnlyStructure_IsRefused_NotSilentlyDropped()
        {
            SplitStructureChoice copy = new SplitStructureChoice(default, "", "(family not in the drawing)", "MH-A", H("77"));
            FakeDrawing d = new FakeDrawing();
            SplitPreview p = new SplitWorkflow(d).Preview(Equal("3C") with { Structure = copy }, CancellationToken.None);
            Assert.Equal(OperationStatus.Rejected, p.Status);
            Assert.Contains(p.Issues, i => i.Code == SplitIssueCodes.StructureTemplateOnly);
            Assert.False(d.Planned);
        }

        [Fact]
        public void APlanForAnotherDrawing_OrNotApplicable_IsRejected_BeforeAnyWrite()
        {
            (FakeDrawing d, SplitWorkflow w, SplitPreview plan) = Planned(Pipe("3C", 5));
            d.Active = Drawing with { SessionKey = "doc-2" };
            SplitResult r = w.Apply(All(plan), null, CancellationToken.None);
            Assert.Equal(OperationStatus.Rejected, r.Status);
            Assert.Contains(r.Issues, i => i.Code == SplitIssueCodes.DrawingMismatch);
            Assert.Empty(d.Split);
            Assert.False(d.Disposed);

            Assert.Equal(OperationStatus.Rejected, w.Apply(new SplitApplyRequest(plan with { Status = OperationStatus.NoChange }, new[] { H("3C") }), null, CancellationToken.None).Status);
        }

        [Fact]
        public void NothingChosen_IsNoChange()
        {
            (FakeDrawing d, SplitWorkflow w, SplitPreview plan) = Planned(Pipe("3C", 5));
            SplitResult r = w.Apply(new SplitApplyRequest(plan, Array.Empty<DrawingObjectRef>()), null, CancellationToken.None);
            Assert.Equal(OperationStatus.NoChange, r.Status);
            Assert.False(d.Disposed);
        }

        [Fact]
        public void AnUnreadBulge_IsNeverTakenAsUnchanged()
        {
            SplitItemPlan item = Pipe("3C", 5).Item with { Snapshot = Snap("3C") with { Curved = true, Bulge = 0.2 } };
            Assert.NotNull(SplitValidation.Compare(item, item.Snapshot with { Bulge = double.NaN }));
            SplitItemPlan unread = item with { Snapshot = item.Snapshot with { Bulge = double.NaN } };
            Assert.Null(SplitValidation.Compare(unread, unread.Snapshot));
        }

        [Fact]
        public void AStructureChoice_IsAPartsListPartOrATemplate()
        {
            Assert.True(new SplitStructureChoice(H("5E"), "", "Manhole", "1200").IsComplete);
            // A structure copied from the drawing (its family in no parts list): no parts list, only the template.
            Assert.True(new SplitStructureChoice(default, "", "(family not in the drawing)", "MH-A", H("77")).IsComplete);
            Assert.False(new SplitStructureChoice(default, "", "Manhole", "1200").IsComplete);
            SplitStructureChoice bad = new SplitStructureChoice(H("5E"), "", "Manhole", "1200") { Dimensions = new Dictionary<string, double> { ["StructInnerDiameter"] = double.NaN } };
            Assert.Contains(SplitValidation.Validate(Equal("3C") with { Structure = bad }), i => i.Code == SplitIssueCodes.StructureIncomplete);
        }

        private sealed class Progress : IProgress<SplitProgress>
        {
            private readonly List<SplitProgress> _seen;
            public Progress(List<SplitProgress> seen) { _seen = seen; }
            public void Report(SplitProgress value) => _seen.Add(value);
        }
    }
}
