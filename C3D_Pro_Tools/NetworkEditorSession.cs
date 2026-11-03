using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.DatabaseServices;
using System;
using System.Collections.Generic;
using System.Linq;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using DBObject = Autodesk.AutoCAD.DatabaseServices.DBObject;

namespace C3DProTools
{
    /// <summary>
    /// The Network Editor's working set apart from its controls: the chosen pipes and other parts, and the reads made on them
    /// (counts, gravity / pressure split, outside sizes, chamber and span audits). Always against the active drawing.
    /// </summary>
    internal sealed class NetworkEditorSession
    {
        /// <summary>The chosen gravity and pressure pipes.</summary>
        public List<ObjectId> Pipes { get; } = new List<ObjectId>();
        /// <summary>Structures, fittings and appurtenances chosen too (used by Swap parts).</summary>
        public List<ObjectId> Parts { get; } = new List<ObjectId>();

        private static Database Db => AcadApp.DocumentManager.MdiActiveDocument.Database;

        private IEnumerable<ObjectId> LivePipes => Pipes.Where(id => !id.IsErased);

        /// <summary>Drops chosen objects that were erased (or belong to a closed drawing).</summary>
        public void Prune()
        {
            Pipes.RemoveAll(id => id.IsErased || !id.IsValid);
            Parts.RemoveAll(id => id.IsErased || !id.IsValid);
        }

        public (int gravity, int pressure) CountPipes()
        {
            int g = 0, p = 0;
            using (Transaction tr = Db.TransactionManager.StartOpenCloseTransaction())
                foreach (ObjectId id in LivePipes)
                {
                    DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                    if (obj is Pipe) g++; else if (obj is PressurePipe) p++;
                }
            return (g, p);
        }

        public List<ObjectId> GravityPipes()
        {
            using (Transaction tr = Db.TransactionManager.StartOpenCloseTransaction())
                return LivePipes.Where(id => tr.GetObject(id, OpenMode.ForRead) is Pipe).ToList();
        }

        public List<ObjectId> PressurePipes()
        {
            using (Transaction tr = Db.TransactionManager.StartOpenCloseTransaction())
                return LivePipes.Where(id => tr.GetObject(id, OpenMode.ForRead) is PressurePipe).ToList();
        }

        /// <summary>The copies' outside width and height: the part given, else the first chosen pipe's. False when neither is known.</summary>
        public bool CopyOutside(PartPick part, out double width, out double height)
        {
            width = height = 0;
            if (part != null && part.InnerHeight > 0)
            {
                width = height = part.InnerHeight + 2 * Math.Max(0, part.Wall);
                return true;
            }
            using (Transaction tr = Db.TransactionManager.StartOpenCloseTransaction())
                foreach (ObjectId id in LivePipes)
                {
                    DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                    if (obj is Pipe p) { width = CoverRoute.OuterWidthOf(p); height = CoverRoute.OuterHeightOf(p); return width > 0; }
                    if (obj is PressurePipe pp) { width = height = pp.OuterDiameter; return width > 0; }
                }
            return false;
        }

        /// <summary>The chamber entry problems around the chosen pipes, and the long-span warning ("" when none, or no span limit).</summary>
        public (List<ChamberEntryIssue> issues, string spans) Audit(double maxSkewDeg, double maxSpan)
        {
            List<ObjectId> pipes = LivePipes.ToList();
            using (Transaction tr = Db.TransactionManager.StartOpenCloseTransaction())
                return (NetworkChecks.CheckEntries(tr, pipes, maxSkewDeg), maxSpan > 0 ? NetworkChecks.SpanWarning(tr, pipes, maxSpan) : "");
        }

        /// <summary>Long spans around these pipes (after an edit); "" when all are within <paramref name="maxSpan"/>.</summary>
        public static string SpanWarning(IEnumerable<ObjectId> pipes, double maxSpan)
        {
            try
            {
                using (Transaction tr = Db.TransactionManager.StartOpenCloseTransaction())
                    return NetworkChecks.SpanWarning(tr, pipes.Where(id => !id.IsErased).ToList(), maxSpan);
            }
            catch (Exception ex)
            {
                C3DProLogger.Warn("Network Editor: the spans could not be checked", ex);
                return "The spans could not be checked: " + ex.Message;
            }
        }
    }
}
