using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ErrorStatus = Autodesk.AutoCAD.Runtime.ErrorStatus;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace C3DProTools
{
    /// <summary>
    /// A filer that records the hard pointers an object writes when it saves itself. A hard pointer is how AutoCAD and
    /// Civil 3D store "this object uses that one" (an entity's style, a label style's text style, a network's parts list...),
    /// so it finds references that reading StyleId alone misses. Everything else written is discarded.
    /// </summary>
    internal sealed class ReferenceFiler : DwgFiler
    {
        public readonly List<ObjectId> HardPointers = new List<ObjectId>();
        private ErrorStatus _status = ErrorStatus.OK;

        public void Clear() => HardPointers.Clear();

        public override ErrorStatus FilerStatus { get => _status; set => _status = value; }
        public override FilerType FilerType => FilerType.CopyFiler;
        public override void ResetFilerStatus() => _status = ErrorStatus.OK;

        public override void WriteHardPointerId(ObjectId value) { if (!value.IsNull) HardPointers.Add(value); }
        public override void WriteHardOwnershipId(ObjectId value) { }
        public override void WriteSoftOwnershipId(ObjectId value) { }
        public override void WriteSoftPointerId(ObjectId value) { }
        public override void WriteString(string value) { }
        public override void WriteHandle(Handle handle) { }
        public override void WriteInt64(long value) { }
        public override void WriteInt32(int value) { }
        public override void WriteInt16(short value) { }
        public override void WriteUInt64(ulong value) { }
        public override void WriteUInt32(uint value) { }
        public override void WriteUInt16(ushort value) { }
        public override void WriteByte(byte value) { }
        public override void WriteBoolean(bool value) { }
        public override void WriteDouble(double value) { }
        public override void WritePoint2d(Point2d value) { }
        public override void WritePoint3d(Point3d value) { }
        public override void WriteVector2d(Vector2d value) { }
        public override void WriteVector3d(Vector3d value) { }
        public override void WriteScale3d(Scale3d value) { }
        public override void WriteBytes(byte[] value) { }
        public override void WriteBinaryChunk(byte[] chunk) { }
        public override void WriteAddress(IntPtr value) { }

        public override ObjectId ReadHardOwnershipId() => ObjectId.Null;
        public override ObjectId ReadSoftOwnershipId() => ObjectId.Null;
        public override ObjectId ReadHardPointerId() => ObjectId.Null;
        public override ObjectId ReadSoftPointerId() => ObjectId.Null;
        public override string ReadString() => null;
        public override Handle ReadHandle() => new Handle();
        public override long ReadInt64() => 0;
        public override int ReadInt32() => 0;
        public override short ReadInt16() => 0;
        public override ulong ReadUInt64() => 0;
        public override uint ReadUInt32() => 0;
        public override ushort ReadUInt16() => 0;
        public override byte ReadByte() => 0;
        public override bool ReadBoolean() => false;
        public override double ReadDouble() => 0;
        public override Point2d ReadPoint2d() => Point2d.Origin;
        public override Point3d ReadPoint3d() => Point3d.Origin;
        public override Vector2d ReadVector2d() => Vector2d.XAxis;
        public override Vector3d ReadVector3d() => Vector3d.XAxis;
        public override Scale3d ReadScale3d() => new Scale3d(1.0);
        public override void ReadBytes(byte[] value) { }
        public override byte[] ReadBinaryChunk() => null;
        public override IntPtr ReadAddress() => IntPtr.Zero;
        public override void Seek(long offset, int method) { }
        public override long Position => 0;
    }

    internal sealed class ScanResult
    {
        public int ObjectsScanned;
        public int Unreadable;
        public double Seconds;
        /// <summary>False when Purge refused nearly every unreferenced style of that group, so its answer is not used.</summary>
        public bool CivilPurgeTrusted = true, AcadPurgeTrusted = true;
        public readonly List<string> Notes = new List<string>();
    }

    /// <summary>Fills each style's users and referrers, then works out whether it is used, unused or held by a hidden reference.</summary>
    internal static class StyleReferenceScanner
    {
        public static ScanResult Scan(Database db, Transaction tr, List<StyleItem> items, Action<double> progress = null)
        {
            DateTime started = DateTime.Now;
            ScanResult result = new ScanResult();
            Dictionary<ObjectId, StyleItem> byId = new Dictionary<ObjectId, StyleItem>();
            foreach (StyleItem item in items) { item.EntityUsers.Clear(); item.OtherReferrers.Clear(); item.SettingsDefaults = 0; byId[item.Id] = item; }

            // Defaults held by the Civil 3D settings are not hard pointers, so the walk below cannot see them.
            foreach (KeyValuePair<ObjectId, int> kv in SettingsReferences.Collect(db, result.Notes))
                if (byId.TryGetValue(kv.Key, out StyleItem held)) held.SettingsDefaults = kv.Value;

            // Objects referencing a big share of all styles are registries (style managers), not real use.
            Dictionary<ObjectId, List<StyleItem>> referrerHits = new Dictionary<ObjectId, List<StyleItem>>();
            ReferenceFiler filer = new ReferenceFiler();
            HashSet<ObjectId> hitThisObject = new HashSet<ObjectId>();
            long seed = db.Handseed.Value;

            for (long h = 1; h < seed; h++)
            {
                if (progress != null && h % 20000 == 0) progress((double)h / seed);
                ObjectId id;
                try { if (!db.TryGetObjectId(new Handle(h), out id)) continue; } catch (Exception) { continue; }
                if (id.IsNull || id.IsErased || !id.IsValid) continue;

                DBObject obj;
                try { obj = tr.GetObject(id, OpenMode.ForRead, false); } catch (Exception) { result.Unreadable++; continue; }
                if (obj == null || obj is DBDictionary) continue;
                result.ObjectsScanned++;

                ObjectId direct = ObjectId.Null;
                if (obj is Entity)
                {
                    direct = StyleEntityHelper.GetStyleId(obj);
                    if (byId.TryGetValue(direct, out StyleItem used) && !InAnonymousBlock(tr, obj)) used.EntityUsers.Add(id);
                }

                // Surfaces write their whole TIN; their only style reference is the one read above.
                if (obj is CivilSurface) continue;

                filer.Clear();
                try { obj.DwgOut(filer); } catch (Exception) { result.Unreadable++; continue; }

                hitThisObject.Clear();
                byId.TryGetValue(id, out StyleItem self);
                foreach (ObjectId r in filer.HardPointers)
                {
                    if (r == id || r == direct || r == obj.OwnerId || !hitThisObject.Add(r)) continue;
                    if (!byId.TryGetValue(r, out StyleItem target)) continue;
                    // A child label style points back at its parent; that is ownership, not use.
                    if (self != null && self.ParentId == r) continue;
                    if (!referrerHits.TryGetValue(id, out List<StyleItem> hits)) referrerHits[id] = hits = new List<StyleItem>();
                    hits.Add(target);
                }
            }

            int registryLimit = Math.Max(60, items.Count / 3);
            foreach (KeyValuePair<ObjectId, List<StyleItem>> pair in referrerHits)
            {
                if (pair.Value.Count > registryLimit && !byId.ContainsKey(pair.Key))
                {
                    result.Notes.Add($"Ignored {pair.Key.ObjectClass.DxfName} <{pair.Key.Handle}>: it lists {pair.Value.Count} styles (a style registry, not real use).");
                    continue;
                }
                foreach (StyleItem target in pair.Value) target.OtherReferrers.Add(pair.Key);
            }

            ApplyPurge(db, items, result);
            result.Seconds = (DateTime.Now - started).TotalSeconds;
            progress?.Invoke(1.0);
            return result;
        }

        /// <summary>Asks AutoCAD which styles it would purge, and combines that with the scanned references.</summary>
        private static void ApplyPurge(Database db, List<StyleItem> items, ScanResult result)
        {
            ObjectIdCollection ids = new ObjectIdCollection();
            foreach (StyleItem item in items) { item.Purgeable = false; if (!item.Protected) ids.Add(item.Id); }
            try { db.Purge(ids); } catch (Exception) { ids.Clear(); result.Notes.Add("Purge check failed; using the reference scan only."); }
            HashSet<ObjectId> purgeable = new HashSet<ObjectId>();
            foreach (ObjectId id in ids) purgeable.Add(id);

            int civilFree = 0, civilRefused = 0, acadFree = 0, acadRefused = 0;
            foreach (StyleItem item in items)
            {
                item.Purgeable = purgeable.Contains(item.Id);
                if (item.Protected || item.Uses > 0) continue;
                if (item.IsCivil) { if (item.Purgeable) civilFree++; else civilRefused++; }
                else { if (item.Purgeable) acadFree++; else acadRefused++; }
            }

            // If Purge refuses almost every style nothing points at, it does not understand that kind; the scan's answer is
            // shown instead, but only for reporting: deleting needs Purge's own confirmation (StyleOperations.Delete).
            result.CivilPurgeTrusted = !(civilRefused >= 10 && civilFree < civilRefused / 10);
            result.AcadPurgeTrusted = !(acadRefused >= 10 && acadFree < acadRefused / 10);
            if (!result.CivilPurgeTrusted) result.Notes.Add("AutoCAD Purge rejected nearly all unreferenced Civil 3D styles: their status comes from the reference scan, and they are reported but never deleted.");

            foreach (StyleItem item in items)
            {
                bool trusted = item.IsCivil ? result.CivilPurgeTrusted : result.AcadPurgeTrusted;
                if (item.Uses > 0) item.Usage = StyleUsage.Used;
                else if (item.Purgeable || (!trusted && !item.Protected)) item.Usage = StyleUsage.Unused;
                else item.Usage = StyleUsage.Hidden;
            }
        }

        /// <summary>Entities inside anonymous blocks (dimension *D blocks, dynamic block copies) are regenerated by AutoCAD,
        /// so they are not counted as users; their references still keep the style from being purged.</summary>
        private static bool InAnonymousBlock(Transaction tr, DBObject obj)
        {
            try { return tr.GetObject(obj.OwnerId, OpenMode.ForRead) is BlockTableRecord owner && owner.IsAnonymous && !owner.IsLayout; }
            catch (Exception) { return false; }
        }
    }

    /// <summary>
    /// The styles (and other objects) the Civil 3D settings name as defaults: drawing settings and every feature and command
    /// settings class. They are read by reflection (each settings class, then its nested settings groups, down to the
    /// object ids their properties hold), so new settings classes in later releases are covered without a list here.
    /// </summary>
    internal static class SettingsReferences
    {
        private const int MaxDepth = 4;

        /// <summary>How many settings name each object id. Settings that cannot be read are skipped (one note says so).</summary>
        public static Dictionary<ObjectId, int> Collect(Database db, List<string> notes)
        {
            Dictionary<ObjectId, int> found = new Dictionary<ObjectId, int>();
            Autodesk.Civil.Settings.SettingsRoot root;
            try { root = Autodesk.Civil.ApplicationServices.CivilDocument.GetCivilDocument(db).Settings; }
            catch (Exception) { notes.Add("The Civil 3D settings could not be read; styles used only as settings defaults may show as unused."); return found; }

            HashSet<object> visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            int unreadable = 0;
            Walk(root.DrawingSettings, 0, found, visited, ref unreadable);

            MethodInfo getSettings = typeof(Autodesk.Civil.Settings.SettingsRoot).GetMethod(nameof(Autodesk.Civil.Settings.SettingsRoot.GetSettings));
            Type wrapper = typeof(Autodesk.Civil.Settings.SettingsRoot).BaseType;
            foreach (Type t in SettingsTypes(wrapper))
            {
                object settings;
                try { settings = getSettings.MakeGenericMethod(t).Invoke(root, null); }
                catch (Exception) { continue; }    // not a feature or command settings class
                Walk(settings, 0, found, visited, ref unreadable);
            }
            if (unreadable > 0) notes.Add($"{unreadable} Civil 3D setting(s) could not be read while looking for default styles.");
            return found;
        }

        private static IEnumerable<Type> SettingsTypes(Type wrapper)
        {
            Type[] all;
            try { all = wrapper.Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { all = ex.Types.Where(x => x != null).ToArray(); }
            return all.Where(t => t.IsClass && !t.IsAbstract && t.Namespace == "Autodesk.Civil.Settings" && t.Name.StartsWith("Settings") && wrapper.IsAssignableFrom(t));
        }

        private static void Walk(object node, int depth, Dictionary<ObjectId, int> found, HashSet<object> visited, ref int unreadable)
        {
            if (node == null || depth > MaxDepth || !visited.Add(node)) return;
            foreach (PropertyInfo p in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                Type type = p.PropertyType;
                bool id = type == typeof(ObjectId);
                // Settings values are wrappers (PropertyObjectId and the like) whose Value holds the setting.
                PropertyInfo wrapped = !id && type.Name.StartsWith("Property") ? type.GetProperty("Value") : null;
                bool group = !id && wrapped == null && type.IsClass && type.Namespace == "Autodesk.Civil.Settings";
                if (!id && (wrapped == null || wrapped.PropertyType != typeof(ObjectId)) && !group) continue;
                object value;
                try { value = p.GetValue(node); }
                catch (Exception) { unreadable++; continue; }
                if (group) { Walk(value, depth + 1, found, visited, ref unreadable); continue; }
                ObjectId held;
                try { held = id ? (ObjectId)value : (ObjectId)wrapped.GetValue(value); }
                catch (Exception) { unreadable++; continue; }
                if (held.IsNull || held.IsErased) continue;
                found[held] = found.TryGetValue(held, out int n) ? n + 1 : 1;
            }
        }
    }
}
