using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using System;
using System.Collections.Generic;
using System.Linq;

namespace C3DProTools
{
    internal sealed class OrphanPipe
    {
        public ObjectId Id, NetworkId;
        public string Name = "", Network = "", Size = "";
        public bool Curved;
    }

    /// <summary>
    /// Finds and repairs pipes that name a network but are not in its list of pipes: copies made by cloning (earlier
    /// versions of the copy-from-drawing fallback did this). Prospector does not select them, routes cannot pass through
    /// them and they cannot be moved. Each is rebuilt as a real member of its network in the same place, then erased.
    /// </summary>
    internal static class NetworkRepair
    {
        public static List<OrphanPipe> Find(Transaction tr, Database db)
        {
            Dictionary<ObjectId, HashSet<ObjectId>> members = new Dictionary<ObjectId, HashSet<ObjectId>>();
            List<OrphanPipe> list = new List<OrphanPipe>();
            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
            Autodesk.AutoCAD.Runtime.RXClass pipeClass = Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(Pipe));
            foreach (ObjectId id in ms)
            {
                if (id.IsErased || !id.ObjectClass.IsDerivedFrom(pipeClass) || !(tr.GetObject(id, OpenMode.ForRead) is Pipe p)) continue;
                if (p.NetworkId.IsNull) continue;
                if (!members.TryGetValue(p.NetworkId, out HashSet<ObjectId> set))
                {
                    set = new HashSet<ObjectId>();
                    try { if (tr.GetObject(p.NetworkId, OpenMode.ForRead) is Network n) foreach (ObjectId m in n.GetPipeIds()) set.Add(m); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                    members[p.NetworkId] = set;
                }
                if (set.Contains(id)) continue;
                list.Add(new OrphanPipe { Id = id, NetworkId = p.NetworkId, Name = p.Name, Network = Safe(() => p.NetworkName), Size = Safe(() => p.PartSizeName), Curved = p.SubEntityType == PipeSubEntityType.Curved });
            }
            return list;
        }

        /// <summary>Rebuilds each pipe as a member of its network. Returns the number repaired; problems go to <paramref name="log"/>.</summary>
        public static int Repair(Transaction tr, List<OrphanPipe> orphans, List<string> log)
        {
            int fixedCount = 0;
            foreach (OrphanPipe o in orphans)
            {
                if (o.Curved) { log.Add($"{o.Name}: curved pipes cannot be rebuilt this way; redraw it, or copy a straight pipe of {o.Size} onto it."); continue; }
                try
                {
                    Pipe old = (Pipe)tr.GetObject(o.Id, OpenMode.ForWrite);
                    Network net = (Network)tr.GetObject(o.NetworkId, OpenMode.ForWrite);
                    Point3d s = old.StartPoint, e = old.EndPoint;
                    ObjectId ss = old.StartStructureId, es = old.EndStructureId;
                    List<ObjectId> views = new List<ObjectId>();
                    try { foreach (ObjectId v in old.GetProfileViewsDisplayingMe()) views.Add(v); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }

                    ObjectId newId = PartCopy.CopyPipe(tr, net, old, s, e);
                    Pipe copy = (Pipe)tr.GetObject(newId, OpenMode.ForWrite);
                    if (!PartCopy.IsMember(tr, copy))
                    {
                        copy.Erase();
                        log.Add($"{o.Name}: network {o.Network} has no member pipe of {o.Size} (or no structure) to rebuild it from; left as it is.");
                        continue;
                    }
                    copy.StyleId = old.StyleId;
                    copy.Layer = old.Layer;
                    try { copy.Description = old.Description; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                    // Reconnected to both its structures, or it is not counted as repaired (and the report says so).
                    bool reconnected = true;
                    foreach ((ObjectId sid, ConnectorPositionType end) in new[] { (ss, ConnectorPositionType.Start), (es, ConnectorPositionType.End) })
                    {
                        if (sid.IsNull || sid.IsErased) continue;
                        try { copy.ConnectToStructure(end, sid, false); }
                        catch (Exception ex)
                        {
                            reconnected = false;
                            C3DProLogger.Warn($"Repair: pipe {o.Name} could not be reconnected at its {end.ToString().ToLowerInvariant()}", ex);
                        }
                    }
                    if (!copy.StartPoint.IsEqualTo(s)) copy.StartPoint = s;
                    if (!copy.EndPoint.IsEqualTo(e)) copy.EndPoint = e;
                    // The erased pipe keeps its name until the end of the transaction, so it gives it up first.
                    string name = old.Name;
                    try { old.Name = name + " (replaced " + old.Handle + ")"; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                    old.Erase();
                    try { copy.Name = name; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                    foreach (ObjectId v in views) try { if (!v.IsErased) copy.AddToProfileView(v); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                    if (reconnected) fixedCount++;
                    else log.Add($"{o.Name}: rebuilt, but it could not be reconnected to its structure(s); connect it by hand (details in {C3DProLogger.LogPath}).");
                }
                catch (Exception ex) { log.Add($"{o.Name}: {ex.Message}"); }
            }
            return fixedCount;
        }

        /// <summary>Finds the pipes to repair in the active drawing, asks, repairs them in one UNDO and reports.</summary>
        public static void Run(System.Windows.Forms.IWin32Window owner)
        {
            Autodesk.AutoCAD.ApplicationServices.Document doc = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            const string title = "C3D Pro Tools - Repair network pipes";
            List<OrphanPipe> orphans;
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                orphans = Find(tr, doc.Database);
                tr.Commit();
            }
            if (orphans.Count == 0)
            {
                System.Windows.Forms.MessageBox.Show(owner, "Every pipe is in its network's list: nothing to repair.", title);
                return;
            }
            string networks = string.Join("\n", orphans.GroupBy(o => o.Network).Select(g => $"• {g.Key}: {g.Count()} ({string.Join(", ", g.Select(o => o.Size).Distinct())})"));
            if (System.Windows.Forms.MessageBox.Show(owner,
                    $"{orphans.Count} pipe(s) name a network but are not in its list, so Prospector does not select them and routes cannot pass through them " +
                    $"(copies made by cloning):\n\n{networks}\n\nRebuild each as a real member of its network, in the same place with the same name, style, " +
                    "layer, connections and profile views? Their labels are not kept. One UNDO reverses it.", title,
                    System.Windows.Forms.MessageBoxButtons.OKCancel, System.Windows.Forms.MessageBoxIcon.Question) != System.Windows.Forms.DialogResult.OK) return;
            List<string> log = new List<string>();
            int repaired;
            using (doc.LockDocument())
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                repaired = Repair(tr, orphans, log);
                tr.Commit();
            }
            ViewHelper.Regen(doc);
            foreach (string l in log) doc.Editor.WriteMessage("\n" + l);
            System.Windows.Forms.MessageBox.Show(owner, $"Repaired {repaired} of {orphans.Count} pipe(s)." + (log.Count > 0 ? "\n\n" + string.Join("\n", log.Take(15)) : ""), title,
                System.Windows.Forms.MessageBoxButtons.OK, log.Count > 0 ? System.Windows.Forms.MessageBoxIcon.Warning : System.Windows.Forms.MessageBoxIcon.Information);
        }

        private static string Safe(Func<string> get)
        {
            try { return get() ?? ""; } catch (Exception) { return ""; }
        }
    }
}
