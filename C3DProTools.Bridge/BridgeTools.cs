using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using C3D = Autodesk.Civil.DatabaseServices;

namespace C3DProTools.Bridge
{
    /// <summary>The MCP tools: definitions (JSON schema) and implementations, all run on the main thread.</summary>
    internal sealed class BridgeTools
    {
        public const string Instructions =
            "Live connection to the user's running Civil 3D 2026 session. Read the drawing first (c3d_status, " +
            "c3d_list_civil, c3d_list_objects, c3d_get_object). The drawing is the user's real work: before a call that " +
            "modifies it (an editing c3d_run_command, a writing c3d_execute_csharp), say what will change and get the " +
            "user's go-ahead unless they asked for exactly that. Handles are hex strings such as \"2A3F\".";

        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
        private readonly MainThread _main;

        public BridgeTools(MainThread main) => _main = main;

        // ---------- definitions ----------

        public JsonArray Definitions() => new()
        {
            Tool("c3d_status",
                "Civil 3D session overview: product version, open drawings, the active drawing's path, units, " +
                "coordinate system, object counts, and whether a command is running.",
                Props(), readOnly: true),
            Tool("c3d_list_civil",
                "List Civil 3D objects in the active drawing with their handles: alignments (with profiles), " +
                "surfaces, pipe networks, corridors, sites and point groups.",
                Props(), readOnly: true),
            Tool("c3d_list_objects",
                "Count and list entities in model space (or a named layout's paper space). Without filters it returns " +
                "counts by type; with type/layer filters it also lists matching entities with handles.",
                Props(
                    ("type", "string", "Filter: class name contains this text, e.g. 'Polyline', 'Pipe', 'MText', 'BlockReference'."),
                    ("layer", "string", "Filter: exact layer name (case-insensitive)."),
                    ("layout", "string", "Layout name to read instead of model space."),
                    ("limit", "integer", "Max entities to list (default 100, max 2000).")),
                readOnly: true),
            Tool("c3d_get_object",
                "Read one object's properties by handle: type, layer, and its readable public properties " +
                "(numbers, text, points, enums, linked object handles).",
                Props(("handle", "string", "Object handle (hex), e.g. '2A3F'.")),
                required: new[] { "handle" }, readOnly: true),
            Tool("c3d_zoom_to",
                "Zoom the active view to one or more objects and select them so the user can see them.",
                Props(("handles", "array", "Object handles (hex) to zoom to.")),
                required: new[] { "handles" }, readOnly: true),
            Tool("c3d_run_command",
                "Run a command-line string in the active drawing exactly as typed, e.g. '_.ZOOM _E ' or a C3D Pro Tools " +
                "command. A space or \\n ends each input. Waits until Civil 3D is idle again (or wait_seconds) and " +
                "reports which commands ended, were cancelled or failed. Use '\\u001b\\u001b' to cancel a stuck command. " +
                "May modify the drawing: confirm with the user first when it does.",
                Props(
                    ("drawing", "string", DrawingArg),
                    ("command", "string", "Command string to send. End with a space or \\n so it executes."),
                    ("wait_seconds", "integer", "How long to wait for Civil 3D to become idle (default 20, max 300).")),
                required: new[] { "drawing", "command" }),
            Tool("c3d_execute_csharp",
                "Compile and run a C# snippet inside Civil 3D, on the main thread, with the active document already " +
                "locked. In scope: doc (Document), db (Database), ed (Editor), civil (CivilDocument), output " +
                "(StringBuilder: write results here). The snippet is a method body: start your own " +
                "`using (var tr = db.TransactionManager.StartTransaction()) { ...; tr.Commit(); }` and optionally `return` a value. " +
                "Imported: System, System.Linq, System.Collections.Generic, System.Text, Autodesk.AutoCAD.ApplicationServices/" +
                "DatabaseServices/EditorInput/Geometry, Autodesk.Civil, Autodesk.Civil.ApplicationServices, " +
                "Autodesk.Civil.DatabaseServices. Aliases: Entity = AutoCAD Entity, Surface = Civil Surface, " +
                "Application = AutoCAD Application. No UI or modal dialogs. Modifying the drawing needs the user's go-ahead.",
                Props(
                    ("drawing", "string", DrawingArg),
                    ("code", "string", "C# method body."),
                    ("timeout_seconds", "integer", "Max time to wait for the snippet (default 60, max 600).")),
                required: new[] { "drawing", "code" }),
        };

        private const string DrawingArg =
            "The drawing this is meant for, exactly as c3d_status lists it (full path, or the file name such as " +
            "'Drawing1.dwg'). Nothing runs if a different drawing is active when Civil 3D gets to the request.";

        private static JsonObject Tool(string name, string description, JsonObject properties,
            string[] required = null, bool readOnly = false)
        {
            var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
            if (required != null) schema["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray());
            return new JsonObject
            {
                ["name"] = name,
                ["description"] = description,
                ["inputSchema"] = schema,
                ["annotations"] = new JsonObject { ["readOnlyHint"] = readOnly, ["openWorldHint"] = false },
            };
        }

        private static JsonObject Props(params (string name, string type, string description)[] props)
        {
            var o = new JsonObject();
            foreach (var (name, type, description) in props)
            {
                var p = new JsonObject { ["type"] = type, ["description"] = description };
                if (type == "array") p["items"] = new JsonObject { ["type"] = "string" };
                o[name] = p;
            }
            return o;
        }

        // ---------- dispatch ----------

        public JsonObject Call(string name, JsonObject args)
        {
            try
            {
                string text = name switch
                {
                    "c3d_status" => Status(),
                    "c3d_list_civil" => _main.RunLocked(ListCivil, Timeout),
                    "c3d_list_objects" => _main.RunLocked(doc => ListObjects(doc, args), Timeout),
                    "c3d_get_object" => _main.RunLocked(doc => GetObject(doc, Str(args, "handle")), Timeout),
                    "c3d_zoom_to" => _main.RunLocked(doc => ZoomTo(doc, args["handles"] as JsonArray), Timeout),
                    "c3d_run_command" => RunCommand(Str(args, "drawing"), Str(args, "command"), Int(args, "wait_seconds", 20, 1, 300)),
                    "c3d_execute_csharp" => ExecuteCSharp(Str(args, "drawing"), Str(args, "code"), Int(args, "timeout_seconds", 60, 1, 600)),
                    _ => throw new ArgumentException($"Unknown tool: {name}"),
                };
                return Content(text, false);
            }
            catch (Exception ex)
            {
                var inner = ex is AggregateException agg && agg.InnerException != null ? agg.InnerException : ex;
                return Content($"Error: {inner.GetType().Name}: {inner.Message}", true);
            }
        }

        private static JsonObject Content(string text, bool isError) => new()
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["isError"] = isError,
        };

        private static string Str(JsonObject a, string key)
        {
            string v = a[key] is JsonValue jv && jv.TryGetValue(out string s) ? s : null;
            return string.IsNullOrWhiteSpace(v) ? throw new ArgumentException($"'{key}' is required.") : v;
        }

        private static string OptStr(JsonObject a, string key) =>
            a[key] is JsonValue jv && jv.TryGetValue(out string s) && !string.IsNullOrWhiteSpace(s) ? s : null;

        private static int Int(JsonObject a, string key, int def, int min, int max)
        {
            int v = def;
            if (a[key] is JsonValue jv)
            {
                if (jv.TryGetValue(out int i)) v = i;
                else if (jv.TryGetValue(out double d)) v = (int)d;
                else if (jv.TryGetValue(out string s) && int.TryParse(s, out int p)) v = p;
            }
            return Math.Clamp(v, min, max);
        }

        // ---------- c3d_status ----------

        private string Status() => _main.Run(() =>
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Civil 3D / AutoCAD version: {AcApp.Version}");
            sb.AppendLine($"Bridge: {BridgeApp.Server?.Url}");
            var dm = AcApp.DocumentManager;
            sb.AppendLine($"Open drawings: {dm.Count}");
            foreach (Document d in dm)
                sb.AppendLine($"  {(d == dm.MdiActiveDocument ? "*" : " ")} {d.Name}{(d.IsReadOnly ? " (read-only)" : "")}");

            Document doc = dm.MdiActiveDocument;
            if (doc == null) { sb.AppendLine("No active drawing."); return sb.ToString(); }

            Database db = doc.Database;
            sb.AppendLine($"Active drawing: {doc.Name}");
            sb.AppendLine($"Command in progress: {(string.IsNullOrEmpty(doc.CommandInProgress) ? "none" : doc.CommandInProgress)}; idle: {doc.Editor.IsQuiescent}");
            sb.AppendLine($"Insertion units: {db.Insunits}; LUNITS={db.Lunits}; LUPREC={db.Luprec}");
            sb.AppendLine($"Current layer: {Name(db.Clayer)}; tile mode (model space): {db.TileMode}");
            try
            {
                CivilDocument civil = CivilApplication.ActiveDocument;
                var uz = civil.Settings.DrawingSettings.UnitZoneSettings;
                sb.AppendLine($"Coordinate system: {(string.IsNullOrEmpty(uz.CoordinateSystemCode) ? "(none)" : uz.CoordinateSystemCode)}");
                sb.AppendLine($"Civil objects: {civil.GetAlignmentIds().Count} alignments, {civil.GetSurfaceIds().Count} surfaces, " +
                              $"{civil.GetPipeNetworkIds().Count} pipe networks, {civil.CorridorCollection.Count} corridors, " +
                              $"{civil.GetSiteIds().Count} sites, {civil.CogoPoints.Count} COGO points");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"Civil 3D document not available: {ex.Message}");
            }
            return sb.ToString();
        }, Timeout);

        // ---------- c3d_list_civil ----------

        private static string ListCivil(Document doc)
        {
            CivilDocument civil = CivilApplication.ActiveDocument;
            var sb = new StringBuilder();
            using var tr = doc.Database.TransactionManager.StartTransaction();

            sb.AppendLine("Alignments:");
            foreach (ObjectId id in civil.GetAlignmentIds())
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not C3D.Alignment a) continue;
                sb.AppendLine($"  {a.Handle} \"{a.Name}\" length={a.Length:0.###} stations {a.StartingStation:0.###}-{a.EndingStation:0.###} type={a.AlignmentType}");
                foreach (ObjectId pid in a.GetProfileIds())
                    if (tr.GetObject(pid, OpenMode.ForRead) is C3D.Profile p)
                        sb.AppendLine($"      profile {p.Handle} \"{p.Name}\" ({p.ProfileType})");
            }

            sb.AppendLine("Surfaces:");
            foreach (ObjectId id in civil.GetSurfaceIds())
                if (tr.GetObject(id, OpenMode.ForRead) is C3D.Surface s)
                    sb.AppendLine($"  {s.Handle} \"{s.Name}\" ({s.GetType().Name})");

            sb.AppendLine("Pipe networks:");
            foreach (ObjectId id in civil.GetPipeNetworkIds())
                if (tr.GetObject(id, OpenMode.ForRead) is C3D.Network n)
                    sb.AppendLine($"  {n.Handle} \"{n.Name}\" pipes={n.GetPipeIds().Count} structures={n.GetStructureIds().Count}");

            sb.AppendLine("Corridors:");
            foreach (ObjectId id in civil.CorridorCollection)
                if (tr.GetObject(id, OpenMode.ForRead) is C3D.Corridor c)
                    sb.AppendLine($"  {c.Handle} \"{c.Name}\" baselines={c.Baselines.Count}");

            sb.AppendLine("Sites:");
            foreach (ObjectId id in civil.GetSiteIds())
                if (tr.GetObject(id, OpenMode.ForRead) is C3D.Site site)
                    sb.AppendLine($"  {site.Handle} \"{site.Name}\"");

            sb.AppendLine("Point groups:");
            foreach (ObjectId id in civil.PointGroups)
                if (tr.GetObject(id, OpenMode.ForRead) is C3D.PointGroup g)
                    sb.AppendLine($"  {g.Handle} \"{g.Name}\" points={g.PointsCount}");

            tr.Commit();
            return sb.ToString();
        }

        // ---------- c3d_list_objects ----------

        private static string ListObjects(Document doc, JsonObject args)
        {
            string type = OptStr(args, "type");
            string layer = OptStr(args, "layer");
            string layout = OptStr(args, "layout");
            int limit = Int(args, "limit", 100, 1, 2000);
            Database db = doc.Database;
            var sb = new StringBuilder();
            using var tr = db.TransactionManager.StartTransaction();

            ObjectId spaceId;
            if (layout == null) spaceId = SymbolUtilityServices.GetBlockModelSpaceId(db);
            else
            {
                var layouts = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                if (!layouts.Contains(layout)) throw new ArgumentException($"No layout named '{layout}'.");
                spaceId = ((Layout)tr.GetObject(layouts.GetAt(layout), OpenMode.ForRead)).BlockTableRecordId;
            }
            var space = (BlockTableRecord)tr.GetObject(spaceId, OpenMode.ForRead);

            var counts = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int listed = 0, matched = 0;
            bool filtered = type != null || layer != null;
            foreach (ObjectId id in space)
            {
                string cls = id.ObjectClass.Name;
                if (type != null && cls.IndexOf(type, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (!filtered)
                {
                    counts[cls] = counts.TryGetValue(cls, out int c) ? c + 1 : 1;
                    continue;
                }
                if (tr.GetObject(id, OpenMode.ForRead) is not Entity e) continue;
                if (layer != null && !string.Equals(e.Layer, layer, StringComparison.OrdinalIgnoreCase)) continue;
                matched++;
                counts[cls] = counts.TryGetValue(cls, out int n) ? n + 1 : 1;
                if (listed < limit)
                {
                    listed++;
                    sb.AppendLine($"  {e.Handle} {cls} layer=\"{e.Layer}\"{Describe(e)}");
                }
            }
            tr.Commit();

            var head = new StringBuilder();
            head.AppendLine($"Space: {(layout ?? "Model")}; filters: type={type ?? "any"}, layer={layer ?? "any"}");
            head.AppendLine("Counts by class:");
            foreach (var kv in counts) head.AppendLine($"  {kv.Key}: {kv.Value}");
            if (filtered)
            {
                head.AppendLine($"Matching entities: {matched}{(matched > listed ? $" (showing first {listed}; raise 'limit' to see more)" : "")}");
                head.Append(sb);
            }
            return head.ToString();
        }

        private static string Describe(Entity e)
        {
            try
            {
                return e switch
                {
                    C3D.Pipe p => $" name=\"{p.Name}\" length2d={p.Length2D:0.###}",
                    C3D.Structure s => $" name=\"{s.Name}\"",
                    C3D.Entity ce when TryName(ce, out string nm) => $" name=\"{nm}\"",
                    Curve c => $" length={c.GetDistanceAtParameter(c.EndParam) - c.GetDistanceAtParameter(c.StartParam):0.###}",
                    BlockReference br => $" block=\"{Name(br.DynamicBlockTableRecord)}\"",
                    MText mt => $" text=\"{Trim(mt.Text)}\"",
                    DBText t => $" text=\"{Trim(t.TextString)}\"",
                    _ => "",
                };
            }
            catch { return ""; }
        }

        private static bool TryName(C3D.Entity e, out string name)
        {
            try { name = e.Name; return !string.IsNullOrEmpty(name); }
            catch { name = null; return false; }
        }

        private static string Trim(string s) => s == null ? "" : (s.Length > 60 ? s.Substring(0, 60) + "…" : s).Replace("\"", "'");

        // ---------- c3d_get_object ----------

        private static string GetObject(Document doc, string handle)
        {
            Database db = doc.Database;
            ObjectId id = ParseHandle(db, handle);
            using var tr = db.TransactionManager.StartTransaction();
            DBObject obj = tr.GetObject(id, OpenMode.ForRead);
            var sb = new StringBuilder();
            sb.AppendLine($"Handle: {obj.Handle}");
            sb.AppendLine($"Class: {obj.GetType().FullName} (DXF {id.ObjectClass.DxfName})");
            if (obj is Entity e)
            {
                sb.AppendLine($"Layer: {e.Layer}");
                try { var ext = e.GeometricExtents; sb.AppendLine($"Extents: {Fmt(ext.MinPoint)} to {Fmt(ext.MaxPoint)}"); } catch { }
            }
            sb.AppendLine("Properties:");

            int shown = 0;
            foreach (var prop in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                         .OrderBy(p => p.Name))
            {
                if (shown >= 250) { sb.AppendLine("  …"); break; }
                if (!IsSimple(prop.PropertyType) || Skip.Contains(prop.Name)) continue;
                string value;
                try { value = FmtValue(prop.GetValue(obj)); }
                catch (TargetInvocationException tie) { value = $"<{tie.InnerException?.GetType().Name}>"; }
                catch (Exception ex) { value = $"<{ex.GetType().Name}>"; }
                sb.AppendLine($"  {prop.Name} = {value}");
                shown++;
            }
            tr.Commit();
            return sb.ToString();
        }

        private static readonly HashSet<string> Skip = new()
        {
            "UnmanagedObject", "AutoDelete", "IsDisposed", "IsTransactionResident", "IsReallyClosing",
            "Drawable", "Bounds", "IsPersistent", "AcadObject",
        };

        private static bool IsSimple(Type t)
        {
            t = Nullable.GetUnderlyingType(t) ?? t;
            return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(DateTime)
                   || t == typeof(Point3d) || t == typeof(Point2d) || t == typeof(Vector3d) || t == typeof(ObjectId)
                   || t == typeof(Handle) || t == typeof(Autodesk.AutoCAD.Colors.Color);
        }

        private static string FmtValue(object v) => v switch
        {
            null => "null",
            string s => $"\"{s}\"",
            double d => d.ToString("0.######"),
            Point3d p => Fmt(p),
            Point2d p => $"({p.X:0.###}, {p.Y:0.###})",
            Vector3d vec => $"<{vec.X:0.###}, {vec.Y:0.###}, {vec.Z:0.###}>",
            ObjectId oid => oid.IsNull ? "null" : $"{oid.Handle} ({oid.ObjectClass?.Name})",
            _ => v.ToString(),
        };

        private static string Fmt(Point3d p) => $"({p.X:0.###}, {p.Y:0.###}, {p.Z:0.###})";

        private static ObjectId ParseHandle(Database db, string handle)
        {
            string h = handle.Trim().TrimStart('#');
            if (h.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) h = h.Substring(2);
            long value;
            try { value = Convert.ToInt64(h, 16); }
            catch { throw new ArgumentException($"'{handle}' is not a hex handle."); }
            if (!db.TryGetObjectId(new Handle(value), out ObjectId id) || id.IsNull || id.IsErased)
                throw new ArgumentException($"No object with handle {handle} in {Path.GetFileName(db.Filename)}.");
            return id;
        }

        private static string Name(ObjectId id)
        {
            try
            {
                using var tr = id.Database.TransactionManager.StartOpenCloseTransaction();
                string name = tr.GetObject(id, OpenMode.ForRead) is SymbolTableRecord r ? r.Name : id.Handle.ToString();
                tr.Commit();
                return name;
            }
            catch { return id.Handle.ToString(); }
        }

        // ---------- c3d_zoom_to ----------

        private static string ZoomTo(Document doc, JsonArray handles)
        {
            if (handles == null || handles.Count == 0) throw new ArgumentException("'handles' is required.");
            Database db = doc.Database;
            Editor ed = doc.Editor;
            var ids = new List<ObjectId>();
            var ext = new Extents3d();
            bool any = false;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                foreach (var h in handles)
                {
                    ObjectId id = ParseHandle(db, (string)h);
                    ids.Add(id);
                    if (tr.GetObject(id, OpenMode.ForRead) is Entity e)
                    {
                        try
                        {
                            var ge = e.GeometricExtents;
                            if (any) ext.AddExtents(ge); else { ext = ge; any = true; }
                        }
                        catch { }
                    }
                }
                tr.Commit();
            }
            if (!any) throw new InvalidOperationException("None of the objects has geometric extents.");
            if (db.TileMode == false) throw new InvalidOperationException("Switch to the Model tab first (zoom works in model space).");

            using (ViewTableRecord view = ed.GetCurrentView())
            {
                Matrix3d wcs2dcs = (Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target)
                                    * Matrix3d.Displacement(view.Target - Point3d.Origin)
                                    * Matrix3d.PlaneToWorld(view.ViewDirection)).Inverse();
                ext.TransformBy(wcs2dcs);
                double w = Math.Max(ext.MaxPoint.X - ext.MinPoint.X, 1.0);
                double hgt = Math.Max(ext.MaxPoint.Y - ext.MinPoint.Y, 1.0);
                double ratio = view.Width / view.Height;
                if (w / hgt > ratio) hgt = w / ratio; else w = hgt * ratio;
                view.CenterPoint = new Point2d((ext.MinPoint.X + ext.MaxPoint.X) / 2, (ext.MinPoint.Y + ext.MaxPoint.Y) / 2);
                view.Width = w * 1.25;
                view.Height = hgt * 1.25;
                ed.SetCurrentView(view);
            }
            try { ed.SetImpliedSelection(ids.ToArray()); } catch { }
            ed.UpdateScreen();
            return $"Zoomed to {ids.Count} object(s) and selected them.";
        }

        // ---------- c3d_run_command ----------

        private string RunCommand(string drawing, string command, int waitSeconds)
        {
            var events = new ConcurrentQueue<string>();
            CommandEventHandler ended = (s, e) => events.Enqueue($"ended: {e.GlobalCommandName}");
            CommandEventHandler cancelled = (s, e) => events.Enqueue($"cancelled: {e.GlobalCommandName}");
            CommandEventHandler failed = (s, e) => events.Enqueue($"failed: {e.GlobalCommandName}");
            Document doc = null;

            _main.Run(() =>
            {
                doc = MainThread.ActiveDocument(drawing);
                doc.CommandEnded += ended;
                doc.CommandCancelled += cancelled;
                doc.CommandFailed += failed;
                string cmd = command.Replace("\\n", "\n");
                doc.SendStringToExecute(cmd, true, false, true);
                return 0;
            }, Timeout);

            var start = DateTime.UtcNow;
            bool idle = false;
            string inProgress = null;
            try
            {
                Thread.Sleep(400);
                while ((DateTime.UtcNow - start).TotalSeconds < waitSeconds)
                {
                    (idle, inProgress) = _main.Run(() => (doc.Editor.IsQuiescent, doc.CommandInProgress), Timeout);
                    // Give the command a moment to start before trusting "idle".
                    if (idle && (events.Count > 0 || (DateTime.UtcNow - start).TotalSeconds > 1.5)) break;
                    Thread.Sleep(300);
                }
            }
            finally
            {
                try
                {
                    _main.Run(() =>
                    {
                        doc.CommandEnded -= ended;
                        doc.CommandCancelled -= cancelled;
                        doc.CommandFailed -= failed;
                        return 0;
                    }, Timeout);
                }
                catch { }
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Drawing: {doc.Name}");
            sb.AppendLine($"Sent: {command.Replace("\n", "\\n").Replace("\u001b", "<Esc>")}");
            sb.AppendLine(events.IsEmpty ? "Command events: none" : "Command events: " + string.Join("; ", events));
            sb.AppendLine(idle
                ? "Civil 3D is idle again."
                : $"Still running after {waitSeconds}s (command in progress: {(string.IsNullOrEmpty(inProgress) ? "?" : inProgress)}). " +
                  "It may be waiting for input or a dialog. Send more input, or '\\u001b\\u001b' to cancel.");
            sb.AppendLine("Note: command-line text is not captured; check the result with c3d_list_objects / c3d_get_object.");
            return sb.ToString();
        }

        // ---------- c3d_execute_csharp ----------

        private string ExecuteCSharp(string drawing, string code, int timeoutSeconds)
        {
            // Compile off the main thread (it can take a second or two), run on it.
            var compiled = ScriptRunner.Compile(code);
            return _main.RunLocked(doc =>
            {
                var output = new StringBuilder();
                object result = compiled.Run(doc, doc.Database, doc.Editor, CivilApplication.ActiveDocument, output);
                if (result != null) output.AppendLine($"Return value: {result}");
                return $"Drawing: {doc.Name}\n" + (output.Length == 0 ? "Done (no output)." : output.ToString());
            }, TimeSpan.FromSeconds(timeoutSeconds), drawing);
        }
    }
}
