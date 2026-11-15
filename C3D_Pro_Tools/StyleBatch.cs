using Autodesk.AutoCAD.DatabaseServices;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace C3DProTools
{
    internal static partial class StyleOperations
    {
        /// <summary>
        /// Deletes unused styles, then (when <paramref name="repeat"/> is set) rescans and deletes again, because removing a
        /// style can free the ones it used (a label style's text style, a band set's band styles). Stops when nothing more frees up.
        /// </summary>
        public static OpResult DeepPurge(StyleSession session, Func<StyleItem, bool> scope, HashSet<ObjectId> keep, bool repeat, Action<string> status = null)
        {
            OpResult total = new OpResult("Purge");
            HashSet<ObjectId> tried = new HashSet<ObjectId>();
            for (int pass = 1; pass <= (repeat ? 8 : 1); pass++)
            {
                if (pass > 1)
                {
                    status?.Invoke($"Purge pass {pass}: rescanning...");
                    session.Reload();
                }
                List<StyleItem> candidates = session.Items
                    .Where(i => i.Usage == StyleUsage.Unused && !i.Protected && !keep.Contains(i.Id) && !tried.Contains(i.Id) && scope(i))
                    .ToList();
                if (candidates.Count == 0) break;
                foreach (StyleItem c in candidates) tried.Add(c.Id);

                OpResult pass1 = Delete(session, candidates);
                total.Changed += pass1.Changed;
                total.Failed += pass1.Failed;
                total.Log.AddRange(pass1.Log);
                if (pass1.Changed == 0) break;
                total.Log.Add($"Pass {pass}: {pass1.Changed} style(s) deleted.");
            }
            return total;
        }
    }

    internal sealed class BatchOptions
    {
        public bool ImportMissing, OverwriteFromTemplate, ApplyMapping, MergeDuplicates, Rename, DeleteUnused, RepeatPurge = true;
        public string TemplatePath = "", MappingPath = "";
        public RenameRule Rule;
        /// <summary>True: save over each file (a .bak copy is kept). False: save into <see cref="OutputFolder"/>.</summary>
        public bool Overwrite = true;
        public string OutputFolder = "";
    }

    internal sealed class BatchFileResult
    {
        public string File = "";
        public string Status = "";
        public int Imported, Merged, Renamed, Deleted;
        public string Note = "";
    }

    /// <summary>Runs the style clean-up on drawings that are not open, reading and saving each one in the background.</summary>
    internal static class StyleBatch
    {
        /// <param name="target">Where the copy is saved (from <see cref="BatchTargets.Plan"/>); ignored when <see cref="BatchOptions.Overwrite"/> is set.</param>
        public static BatchFileResult Process(string path, string target, BatchOptions o, StyleTemplate template, List<StyleMapEntry> mapping, Action<string> status)
        {
            BatchFileResult r = new BatchFileResult { File = path };
            List<string> notes = new List<string>();
            List<string> failures = new List<string>();
            bool hasProblems = false;
            void RecordResult(OpResult result)
            {
                notes.Add(result.Summary("changed"));
                notes.AddRange(result.Log);
                if (result.Skipped > 0) hasProblems = true;
                if (result.Failed > 0)
                    failures.Add(result.Title + ": " + (result.Log.Count > 0
                        ? string.Join("; ", result.Log) : $"{result.Failed} failed"));
            }
            StyleTemplate mappingTemplate = null;
            Database previous = HostApplicationServices.WorkingDatabase;
            using (Database db = new Database(false, true))
            {
                try
                {
                    db.ReadDwgFile(path, FileOpenMode.OpenForReadAndWriteNoShare, true, "");
                    db.CloseInput(true);
                    HostApplicationServices.WorkingDatabase = db;

                    StyleSession session = new StyleSession(db);
                    status("Scanning styles...");
                    session.Reload();

                    if (template != null && o.ImportMissing)
                    {
                        List<StyleItem> missing = StyleOperations.CompareStandards(session.Items, template.Items);
                        if (missing.Count > 0)
                        {
                            status($"Importing {missing.Count} missing style(s)...");
                            OpResult imp = StyleOperations.Import(template.Db, db, missing, ConflictMode.Skip);
                            r.Imported += imp.Changed;
                            RecordResult(imp);
                            session.Reload();
                        }
                    }
                    if (template != null && o.OverwriteFromTemplate)
                    {
                        HashSet<string> drawingKeys = new HashSet<string>(session.Items.Select(i => i.Key));
                        List<StyleItem> matching = template.Items.Where(t => !t.IsChild && drawingKeys.Contains(t.Key)).ToList();
                        status($"Updating {matching.Count} style(s) from the template...");
                        OpResult upd = StyleOperations.Import(template.Db, db, matching, ConflictMode.Overwrite);
                        r.Imported += upd.Changed;
                        RecordResult(upd);
                        session.Reload();
                    }
                    if (o.ApplyMapping && mapping != null && mapping.Count > 0)
                    {
                        // Mapping-only runs do not receive a template from the batch form.
                        if (template == null && !string.IsNullOrWhiteSpace(o.TemplatePath))
                            template = mappingTemplate = new StyleTemplate(o.TemplatePath);
                        HashSet<string> present = new HashSet<string>(session.Items.Select(i => i.Key));
                        List<StyleMapEntry> applicable = mapping.Where(e => present.Contains(e.Collection + "|" + e.From.ToUpperInvariant())).ToList();
                        if (template != null)
                        {
                            List<StyleItem> needed = template.Items.Where(t => !present.Contains(t.Key) && applicable.Any(e =>
                                e.Collection == t.CollectionKey && string.Equals(e.To, t.Name, StringComparison.OrdinalIgnoreCase))).ToList();
                            if (needed.Count > 0)
                            {
                                status($"Importing {needed.Count} mapping target style(s)...");
                                OpResult imp = StyleOperations.Import(template.Db, db, needed, ConflictMode.Skip);
                                r.Imported += imp.Changed;
                                RecordResult(imp);
                                session.Reload();
                                present = new HashSet<string>(session.Items.Select(i => i.Key));
                            }
                        }
                        foreach (StyleMapEntry e in applicable)
                        {
                            if (present.Contains(e.Collection + "|" + e.To.ToUpperInvariant())) continue;
                            string reason = $"Mapping {e.Collection}: '{e.From}' -> '{e.To}': target style missing from the drawing";
                            notes.Add(reason);
                            failures.Add(reason);
                        }
                        List<KeyValuePair<StyleItem, StyleItem>> pairs = StyleOperations.ResolveMapping(session.Items, applicable);
                        if (pairs.Count > 0)
                        {
                            status($"Merging {pairs.Count} mapped style(s)...");
                            OpResult m = StyleOperations.Merge(session, pairs);
                            r.Merged += m.Changed;
                            RecordResult(m);
                            session.Reload();
                        }
                    }
                    if (o.MergeDuplicates)
                    {
                        List<KeyValuePair<StyleItem, StyleItem>> pairs = session.Items.Where(i => i.DuplicateOf != null && !i.Protected)
                            .Select(i => new KeyValuePair<StyleItem, StyleItem>(i, i.DuplicateOf)).ToList();
                        if (pairs.Count > 0)
                        {
                            status($"Merging {pairs.Count} duplicate(s)...");
                            OpResult m = StyleOperations.Merge(session, pairs);
                            r.Merged += m.Changed;
                            RecordResult(m);
                            session.Reload();
                        }
                    }
                    if (o.Rename && o.Rule != null && !o.Rule.IsEmpty)
                    {
                        List<PlannedRename> plans = StyleOperations.PlanRenames(session.Items, session.Items.Where(i => !i.Protected), o.Rule);
                        if (plans.Count > 0)
                        {
                            status($"Renaming {plans.Count} style(s)...");
                            OpResult rn = StyleOperations.ApplyRenames(db, plans, o.Rule.TargetDescription);
                            r.Renamed += rn.Changed;
                            RecordResult(rn);
                            int problems = plans.Count(p => p.Problem.Length > 0);
                            if (problems > 0)
                            {
                                hasProblems = true;
                                notes.Add($"{problems} name(s) skipped (conflict or invalid)");
                            }
                            session.Reload();
                        }
                    }
                    if (o.DeleteUnused)
                    {
                        status("Deleting unused styles...");
                        OpResult p = StyleOperations.DeepPurge(session, i => true, new HashSet<ObjectId>(), o.RepeatPurge);
                        r.Deleted += p.Changed;
                        RecordResult(p);
                    }

                    if (failures.Count > 0)
                    {
                        r.Status = "Failed: " + string.Join("; ", failures);
                        notes.Add("Drawing not saved because a required step failed.");
                        return r;
                    }
                    if (o.Overwrite)
                    {
                        target = path;
                        File.Copy(path, Path.ChangeExtension(path, ".bak"), true);
                    }
                    else
                    {
                        // SaveAs replaces an existing file without asking: never let a copy land on its own source.
                        if (string.IsNullOrEmpty(target) || string.Equals(Path.GetFullPath(target), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("The copy would replace the drawing itself; choose another output folder.");
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                    }
                    status("Saving...");
                    db.SaveAs(target, DwgVersion.Current);
                    r.Status = hasProblems ? "Done with problems" : "Done";
                }
                catch (Exception ex)
                {
                    r.Status = "Failed: " + ex.Message;
                    notes.Add(ex.Message);
                }
                finally
                {
                    HostApplicationServices.WorkingDatabase = previous;
                    r.Note = string.Join("; ", notes);
                    mappingTemplate?.Dispose();
                }
            }
            return r;
        }
    }
}
