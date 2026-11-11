using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using C3DProTools.Features.Sheets.Contracts;

// How build tags (on labels, as XData) and build records (in the named object dictionary, as XRecords) are stored
// (docs/sheet-production-contract.md, sections 4 and 10). Reading only, in this branch; the writers come with the build.
namespace C3DProTools.Features.Sheets.Civil3D
{
    internal static class BuildXData
    {
        /// <summary>The format version written first in every tag, so a later format can still read old tags.</summary>
        public const string TagFormat = "tag1";
        /// <summary>The named object dictionary entry holding one XRecord per sheet and step.</summary>
        public const string RecordsDictionary = "C3DPRO_BUILD";

        /// <summary>
        /// The XData of a tag: application name, format, rule, source, layout, slot, pin, then the recorded placement (style
        /// name, dragged, text X, text Y, has-ratio, ratio, rotation).
        /// </summary>
        public static ResultBuffer Encode(BuildTag tag)
        {
            RecordedPlacement p = tag.Placement;
            return new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, BuildTag.XDataApplication),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, TagFormat),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, tag.RuleId ?? ""),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, tag.SourceKey ?? ""),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, tag.Layout ?? ""),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, tag.StyleSlot ?? ""),
                new TypedValue((int)DxfCode.ExtendedDataInteger16, (short)tag.Pin),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, p?.StyleName ?? ""),
                new TypedValue((int)DxfCode.ExtendedDataInteger16, (short)(p != null && p.Dragged ? 1 : 0)),
                new TypedValue((int)DxfCode.ExtendedDataReal, p?.TextXM ?? 0.0),
                new TypedValue((int)DxfCode.ExtendedDataReal, p?.TextYM ?? 0.0),
                new TypedValue((int)DxfCode.ExtendedDataInteger16, (short)(p?.AnchorRatio.HasValue == true ? 1 : 0)),
                new TypedValue((int)DxfCode.ExtendedDataReal, p?.AnchorRatio ?? 0.0),
                new TypedValue((int)DxfCode.ExtendedDataReal, p?.RotationRad ?? 0.0));
        }

        /// <summary>
        /// The tag in an object's XData for <see cref="BuildTag.XDataApplication"/>; null when there is none, or when it is not
        /// in a format this version reads (then the label counts as placed by hand, which is the safe side: never touched).
        /// </summary>
        public static BuildTag Decode(ResultBuffer rb)
        {
            if (rb == null) return null;
            TypedValue[] v = rb.AsArray();
            if (v.Length < 14 || !(v[1].Value is string format) || format != TagFormat) return null;
            try
            {
                RecordedPlacement placement = new RecordedPlacement(
                    (string)v[7].Value, Convert.ToInt16(v[8].Value, CultureInfo.InvariantCulture) != 0,
                    Convert.ToDouble(v[9].Value, CultureInfo.InvariantCulture), Convert.ToDouble(v[10].Value, CultureInfo.InvariantCulture),
                    Convert.ToInt16(v[11].Value, CultureInfo.InvariantCulture) != 0 ? Convert.ToDouble(v[12].Value, CultureInfo.InvariantCulture) : (double?)null,
                    Convert.ToDouble(v[13].Value, CultureInfo.InvariantCulture));
                short pin = Convert.ToInt16(v[6].Value, CultureInfo.InvariantCulture);
                return new BuildTag((string)v[2].Value, (string)v[3].Value, (string)v[4].Value, (string)v[5].Value,
                    pin == (short)PinState.Pinned ? PinState.Pinned : PinState.Auto, placement);
            }
            catch (Exception ex) when (ex is InvalidCastException || ex is FormatException || ex is OverflowException)
            {
                return null;
            }
        }

        /// <summary>Writes (or replaces) the build record of one sheet and step, in the caller's transaction.</summary>
        public static void WriteRecord(Transaction tr, Database db, string layout, BuildStep step, string hash)
        {
            DBDictionary nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
            DBDictionary dict;
            if (nod.Contains(RecordsDictionary)) dict = (DBDictionary)tr.GetObject(nod.GetAt(RecordsDictionary), OpenMode.ForWrite);
            else
            {
                nod.UpgradeOpen();
                dict = new DBDictionary();
                nod.SetAt(RecordsDictionary, dict);
                tr.AddNewlyCreatedDBObject(dict, true);
            }
            string key = RecordKey(layout, step);
            // The sheet and step are stored in the record itself: a layout name may hold characters a dictionary key may not.
            ResultBuffer data = new ResultBuffer(
                new TypedValue((int)DxfCode.Text, hash ?? ""),
                new TypedValue((int)DxfCode.Text, layout ?? ""),
                new TypedValue((int)DxfCode.Text, step.ToString()));
            if (dict.Contains(key))
            {
                Xrecord x = (Xrecord)tr.GetObject(dict.GetAt(key), OpenMode.ForWrite);
                x.Data = data;
            }
            else
            {
                Xrecord x = new Xrecord { Data = data };
                dict.SetAt(key, x);
                tr.AddNewlyCreatedDBObject(x, true);
            }
        }

        /// <summary>
        /// The dictionary key of a sheet's step record: the step and a hex digest of the layout name (ignoring case). Dictionary
        /// keys may not hold &lt;&gt;/\":;?*|,=` (Autodesk), and layout names can; the name itself is in the record's data.
        /// </summary>
        public static string RecordKey(string layout, BuildStep step)
        {
            byte[] h = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes((layout ?? "").ToUpperInvariant()));
            return $"{step}_{Convert.ToHexString(h, 0, 12)}";
        }

        /// <summary>The build records: one XRecord per sheet and step, holding the step's input hash, the layout and the step.</summary>
        public static List<BuildRecord> ReadRecords(Transaction tr, Database db)
        {
            List<BuildRecord> records = new List<BuildRecord>();
            DBDictionary nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
            if (!nod.Contains(RecordsDictionary) || !(tr.GetObject(nod.GetAt(RecordsDictionary), OpenMode.ForRead) is DBDictionary dict)) return records;
            foreach (DBDictionaryEntry e in dict)
            {
                if (!(tr.GetObject(e.Value, OpenMode.ForRead) is Xrecord x) || x.Data == null) continue;
                TypedValue[] data = x.Data.AsArray();
                if (data.Length >= 3 && data[0].Value is string hash && data[1].Value is string layout && data[2].Value is string stepName
                    && Enum.TryParse(stepName, out BuildStep step))
                    records.Add(new BuildRecord(layout, step, hash));
            }
            return records;
        }
    }
}
