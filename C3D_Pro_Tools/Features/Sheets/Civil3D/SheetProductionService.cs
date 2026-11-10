using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using C3DProTools.Features.Networks.Civil3D;
using C3DProTools.Features.Sheets.Application;
using C3DProTools.Features.Sheets.Contracts;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

// The real ISheetProductionService behind the workspace (docs/sheet-production-contract.md, section 10). It runs on the
// AutoCAD main thread, as the palette does; Apply runs in the document's command context, so a build is one UNDO step.
// Outcomes are values: nothing here throws OperationCanceledException at the caller.
namespace C3DProTools.Features.Sheets.Civil3D
{
    internal sealed class SheetProductionService : ISheetProductionService
    {
        private readonly ISheetScene _scene;
        private int _busy;

        public SheetProductionService(ISheetScene scene = null)
        {
            _scene = scene ?? new SheetSceneCivil3D();
            AcadApp.DocumentManager.DocumentActivated += (s, e) => ActiveDrawingChanged?.Invoke(this, EventArgs.Empty);
            AcadApp.DocumentManager.DocumentDestroyed += (s, e) => ActiveDrawingChanged?.Invoke(this, EventArgs.Empty);
        }

        public DrawingIdentity ActiveDrawing => DrawingRefs3D.Identity(AcadApp.DocumentManager.MdiActiveDocument);
        public event EventHandler ActiveDrawingChanged;

        /// <summary>
        /// The rule set a build uses: the request's when it has rules, otherwise the project's (C3DPro.project.json beside the
        /// drawing; it must hold exactly one rule set, or the build is rejected with the reason).
        /// </summary>
        private static LabelRuleSet RulesFor(Document doc, LabelRuleSet requested, List<OperationIssue> issues)
        {
            if (requested != null && requested.Rules.Count > 0) return requested;
            ProjectSettings settings = ProjectSettings.Load(doc.Name, out string problem);
            if (problem != null) issues.Add(OperationIssue.Error(SheetCodes.RulesMissing, problem));
            LabelRuleSet set = settings.RuleSet(null);
            if (set == null)
                issues.Add(OperationIssue.Error(settings.LabelRules.Count > 1 ? SheetCodes.RulesAmbiguous : SheetCodes.RulesMissing,
                    settings.LabelRules.Count > 1
                        ? $"{ProjectSettings.FileName} has {settings.LabelRules.Count} rule sets; choose one."
                        : $"No label rules: add a rule set to {ProjectSettings.PathFor(doc.Name)}."));
            return set;
        }

        private bool Enter() => Interlocked.CompareExchange(ref _busy, 1, 0) == 0;
        private void Leave() => Interlocked.Exchange(ref _busy, 0);
        private static OperationIssue Busy() => OperationIssue.Error(SheetCodes.Busy, "Another build or preview is running; wait for it to finish.");

        public Task<SheetOverview> GetOverviewAsync(DrawingIdentity drawing, CancellationToken ct)
        {
            Document doc = DrawingRefs3D.Find(drawing);
            if (doc == null) return Task.FromResult(new SheetOverview(drawing, null, new[] { OperationIssue.Error(SheetCodes.PreviewWrongDrawing, "That drawing is not open.") }));
            try
            {
                List<OperationIssue> issues = new List<OperationIssue>();
                LabelRuleSet rules = RulesFor(doc, null, issues);
                SheetScene scene;
                using (doc.LockDocument()) scene = _scene.Read(new SheetSceneRequest { Drawing = drawing }, ct);
                return Task.FromResult(BuildSheetsPlanner.Overview(scene, rules, issues));
            }
            catch (Exception ex)
            {
                C3DProLogger.Error("Sheets overview failed", ex);
                return Task.FromResult(new SheetOverview(drawing, null, new[] { OperationIssue.Error(SheetCodes.Internal, "The sheets could not be read: " + ex.Message) }));
            }
        }

        public Task<BuildSheetsPreview> PreviewAsync(BuildSheetsRequest request, IProgress<BuildProgress> progress, CancellationToken ct)
        {
            if (!Enter()) return Task.FromResult(BuildSheetsPreview.Empty(OperationStatus.Rejected, request?.Drawing, request, new[] { Busy() }));
            try { return Task.FromResult(Plan(request, progress, ct)); }
            finally { Leave(); }
        }

        private BuildSheetsPreview Plan(BuildSheetsRequest request, IProgress<BuildProgress> progress, CancellationToken ct)
        {
            Document doc = DrawingRefs3D.Find(request?.Drawing);
            if (doc == null)
                return BuildSheetsPreview.Empty(OperationStatus.Rejected, request?.Drawing, request, new[] { OperationIssue.Error(SheetCodes.PreviewWrongDrawing, "That drawing is not open.") });
            if (ct.IsCancellationRequested) return BuildSheetsPreview.Empty(OperationStatus.Cancelled, request.Drawing, request);
            try
            {
                List<OperationIssue> issues = new List<OperationIssue>();
                LabelRuleSet rules = RulesFor(doc, request.Rules, issues);
                if (rules == null) return BuildSheetsPreview.Empty(OperationStatus.Rejected, request.Drawing, request, issues);
                progress?.Report(new BuildProgress(BuildPhase.Reading, 0, 2, null));
                SheetScene scene;
                // Every sheet is read: which sheet owns a part is decided over all of them.
                using (doc.LockDocument()) scene = _scene.Read(new SheetSceneRequest { Drawing = request.Drawing }, ct);
                if (ct.IsCancellationRequested) return BuildSheetsPreview.Empty(OperationStatus.Cancelled, request.Drawing, request);
                progress?.Report(new BuildProgress(BuildPhase.Planning, 1, 2, null));
                BuildSheetsPreview preview = BuildSheetsPlanner.Preview(scene, request, rules);
                progress?.Report(new BuildProgress(BuildPhase.Planning, 2, 2, null));
                return preview with { Issues = issues.Concat(preview.Issues).ToList() };
            }
            catch (OperationCanceledException) { return BuildSheetsPreview.Empty(OperationStatus.Cancelled, request.Drawing, request); }
            catch (Exception ex)
            {
                C3DProLogger.Error("Build preview failed", ex);
                return BuildSheetsPreview.Empty(OperationStatus.Rejected, request.Drawing, request, new[] { OperationIssue.Error(SheetCodes.Internal, "The preview failed: " + ex.Message) });
            }
        }

        public async Task<BuildSheetsResult> ApplyAsync(BuildSheetsApplyRequest request, IProgress<BuildProgress> progress, CancellationToken ct)
        {
            string operation = Guid.NewGuid().ToString("N");
            IReadOnlyList<OperationIssue> invalid = BuildValidation.ValidateApply(request);
            if (invalid.Count > 0) return new BuildSheetsResult(OperationStatus.Rejected, operation, request?.Preview?.PreviewId, null, invalid);
            if (!Enter()) return new BuildSheetsResult(OperationStatus.Rejected, operation, request.Preview.PreviewId, null, new[] { Busy() });
            try
            {
                Document doc = DrawingRefs3D.Find(request.Preview.Drawing);
                if (doc == null)
                    return new BuildSheetsResult(OperationStatus.Rejected, operation, request.Preview.PreviewId, null,
                        new[] { OperationIssue.Error(SheetCodes.PreviewWrongDrawing, "The previewed drawing is not open any more; preview again.") });

                // Plan again: only the exact plan the user previewed is written.
                BuildSheetsPreview again = Plan(request.Preview.Request, null, ct);
                if (again.Status != OperationStatus.Succeeded || again.SceneStamp != request.Preview.SceneStamp)
                    return new BuildSheetsResult(OperationStatus.Rejected, operation, request.Preview.PreviewId, null,
                        new[] { OperationIssue.Error(SheetCodes.PreviewStale, "The drawing, the rules or a style changed after the preview, so nothing was written. Preview again.") });

                HashSet<string> chosen = new HashSet<string>(request.ItemKeys, StringComparer.Ordinal);
                List<BuildItem> items = again.Items.Where(i => chosen.Count == 0 || chosen.Contains(i.ItemKey)).ToList();
                LabelRuleSet rules = again.Request.Rules;
                List<BuildItemOutcome> outcomes = new List<BuildItemOutcome>();
                bool cancelled = false;

                void Run()
                {
                    using (doc.LockDocument())
                        for (int i = 0; i < items.Count; i++)
                        {
                            if (ct.IsCancellationRequested)
                            {
                                cancelled = true;
                                outcomes.AddRange(items.Skip(i).Select(it => new BuildItemOutcome(it.ItemKey, ItemStatus.NotAttempted, 0, 0, 0, null, null)));
                                break;
                            }
                            progress?.Report(new BuildProgress(BuildPhase.Applying, i, items.Count, items[i].ItemKey));
                            outcomes.Add(BuildSheetsCivil3D.Apply(doc, items[i], rules, again.SceneStamp));
                        }
                }

                // From the palette (application context) the build runs as a command, so it is one UNDO step.
                if (AcadApp.DocumentManager.IsApplicationContext)
                    await AcadApp.DocumentManager.ExecuteInCommandContextAsync(_ => { Run(); return Task.CompletedTask; }, null);
                else Run();

                progress?.Report(new BuildProgress(BuildPhase.Applying, items.Count, items.Count, null));
                OperationStatus status = OperationOutcome.Summarize(outcomes.Select(o => o.Status).ToList(), cancelled);
                return new BuildSheetsResult(status, operation, request.Preview.PreviewId, outcomes, null);
            }
            catch (Exception ex)
            {
                C3DProLogger.Error("Build apply failed", ex);
                return new BuildSheetsResult(OperationStatus.OutcomeUnknown, operation, request.Preview.PreviewId, null,
                    new[] { OperationIssue.Error(SheetCodes.Internal, "The build stopped unexpectedly; read the drawing again before trying again: " + ex.Message) });
            }
            finally { Leave(); }
        }

        public Task<BuildSheetsPreview> PreviewPinAsync(DrawingIdentity drawing, IReadOnlyList<DrawingObjectRef> labels, bool pin, CancellationToken ct) =>
            Task.FromResult(BuildSheetsPreview.Empty(OperationStatus.Rejected, drawing, null,
                new[] { OperationIssue.Info(SheetCodes.StepNotBuilt, "Pin and Release are not built yet.") }));

        public Task ZoomToAsync(DrawingIdentity drawing, string layout, DrawingObjectRef? target, CancellationToken ct)
        {
            Document doc = DrawingRefs3D.Find(drawing);
            if (doc == null || string.IsNullOrWhiteSpace(layout)) return Task.CompletedTask;
            try
            {
                using (doc.LockDocument())
                    Autodesk.AutoCAD.DatabaseServices.LayoutManager.Current.CurrentLayout = layout;
            }
            catch (Exception ex) { C3DProLogger.Warn($"Zoom to sheet {layout} failed", ex); }
            return Task.CompletedTask;
        }
    }
}
