using System.Globalization;
using System.Reflection;
using Autodesk.AutoCAD.Colors;
using Color = Autodesk.AutoCAD.Colors.Color;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;

namespace C3DProTools.ProfileTool
{
    public enum ValueKind { Length, Angle, Number, Bool, Enum, Color, Text }

    /// <summary>One settable value of one style (style-level setting or one component's setting).</summary>
    public sealed class PropSlot
    {
        /// <summary>Same key across styles = same row in the editor.</summary>
        public string Key;
        public string Group;
        public string Name;
        public ValueKind Kind;
        public Type ValueType;
        public object Holder;          // the Civil Property<T> object
        public PropertyInfo ValueProp; // its (most-derived) Value
        public object Value;
    }

    /// <summary>
    /// Lists every settable value of a label style by reflection: Properties.* (label, leader, dragged state,
    /// behavior, plan readability) and each component's General/Text/Border/Line/Block/... values.
    /// Lengths are paper sizes in drawing units; angles are radians.
    /// </summary>
    public static class StyleProps
    {
        public static List<PropSlot> Read(Transaction tr, LabelStyle style, OpenMode mode, bool splitComponentsByName)
        {
            var slots = new List<PropSlot>();
            Walk(style.Properties, "Style", "", 0, slots);

            foreach (LabelStyleComponentType ct in Enum.GetValues(typeof(LabelStyleComponentType)))
            {
                ObjectIdCollection ids;
                try { ids = style.GetComponents(ct); } catch { continue; }
                if (ids == null) continue;
                foreach (ObjectId cid in ids)
                {
                    object comp;
                    try { comp = tr.GetObject(cid, mode); } catch { continue; }
                    string group = Words(ct.ToString()) + " component";
                    if (splitComponentsByName) group += " '" + ComponentName(comp) + "'";
                    Walk(comp, group, "", 0, slots);
                }
            }
            return slots;
        }

        private static void Walk(object node, string group, string path, int depth, List<PropSlot> slots)
        {
            if (node == null || depth > 3) return;
            foreach (var p in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                object v;
                try { v = p.GetValue(node); } catch { continue; }
                if (v == null || v is string || v.GetType().IsEnum || v.GetType().IsPrimitive) continue;
                var ns = v.GetType().Namespace;
                if (ns == null || !ns.StartsWith("Autodesk.Civil") || v is DBObject) continue;

                string here = path.Length == 0 ? p.Name : path + "." + p.Name;
                var vp = ValueProperty(v.GetType());
                if (vp != null)
                {
                    if (!vp.CanWrite) continue;
                    object val;
                    try { val = vp.GetValue(v); } catch { continue; }
                    var slot = new PropSlot
                    {
                        Key = group + "|" + here,
                        Group = depth == 0 ? group : GroupFor(group, path),
                        Name = Words(p.Name),
                        Holder = v,
                        ValueProp = vp,
                        ValueType = vp.PropertyType,
                        Value = val,
                    };
                    slot.Kind = KindOf(slot.ValueType, p.Name);
                    slots.Add(slot);
                }
                else
                {
                    Walk(v, group, here, depth + 1, slots);
                }
            }
        }

        private static string GroupFor(string group, string path)
        {
            string section = Words(path.Split('.')[0]);
            return group == "Style" ? section : group + " › " + section;
        }

        /// <summary>PropertyEnum&lt;T&gt; hides the base Value with `new`; take the most-derived one.</summary>
        private static PropertyInfo ValueProperty(Type t)
        {
            for (var x = t; x != null && x != typeof(object); x = x.BaseType)
            {
                var p = x.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (p != null) return p;
            }
            return null;
        }

        private static ValueKind KindOf(Type t, string name)
        {
            if (t == typeof(bool)) return ValueKind.Bool;
            if (t.IsEnum) return ValueKind.Enum;
            if (t == typeof(Color)) return ValueKind.Color;
            if (t == typeof(double))
            {
                if (name.Contains("Percent")) return ValueKind.Number;
                if (name.Contains("Angle") || name.Contains("Bias")) return ValueKind.Angle;
                if (name.Contains("Height") || name.Contains("Size") || name.Contains("Gap") || name.Contains("Offset")
                    || name.Contains("Width") || name.Contains("Length")) return ValueKind.Length;
                return ValueKind.Number;
            }
            if (t == typeof(int) || t == typeof(short) || t == typeof(float)) return ValueKind.Number;
            return ValueKind.Text;
        }

        // ---------- display / parse ----------

        /// <summary>Paper length in drawing units -> shown in mm when lengthScale is 1000 (metres), else as is.</summary>
        public static string Format(PropSlot s, double lengthScale)
        {
            var v = s.Value;
            switch (s.Kind)
            {
                case ValueKind.Length: return Num(Convert.ToDouble(v) * lengthScale);
                case ValueKind.Angle: return Num(Convert.ToDouble(v) * 180.0 / Math.PI);
                case ValueKind.Number: return Convert.ToString(v, CultureInfo.InvariantCulture);
                case ValueKind.Bool: return (bool)v ? "True" : "False";
                case ValueKind.Color: return ColorText(v as Color);
                default: return v?.ToString() ?? "";
            }
        }

        public static bool TryParse(PropSlot s, string text, double lengthScale, out object value, out string error)
        {
            value = null; error = null;
            text = (text ?? "").Trim();
            try
            {
                switch (s.Kind)
                {
                    case ValueKind.Length:
                        value = ParseDouble(text) / lengthScale; return true;
                    case ValueKind.Angle:
                        value = ParseDouble(text) * Math.PI / 180.0; return true;
                    case ValueKind.Number:
                        value = Convert.ChangeType(ParseDouble(text), s.ValueType, CultureInfo.InvariantCulture); return true;
                    case ValueKind.Bool:
                        value = text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1" || text.Equals("yes", StringComparison.OrdinalIgnoreCase);
                        return true;
                    case ValueKind.Enum:
                        value = Enum.Parse(s.ValueType, text, true); return true;
                    case ValueKind.Color:
                        value = ParseColor(text); return true;
                    default:
                        if (s.ValueType != typeof(string)) { value = Convert.ChangeType(text, s.ValueType, CultureInfo.InvariantCulture); return true; }
                        value = text; return true;
                }
            }
            catch (Exception ex)
            {
                error = $"'{text}' is not a valid {s.Kind.ToString().ToLowerInvariant()} ({ex.Message})";
                return false;
            }
        }

        public static string UnitOf(PropSlot s, double lengthScale) => s.Kind switch
        {
            ValueKind.Length => lengthScale == 1000 ? "mm" : "units",
            ValueKind.Angle => "deg",
            _ => "",
        };

        private static double ParseDouble(string t) => double.Parse(t.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);
        private static string Num(double d) => Math.Round(d, 6).ToString("0.######", CultureInfo.InvariantCulture);

        private static string ColorText(Color c)
        {
            if (c == null) return "";
            if (c.IsByLayer) return "BYLAYER";
            if (c.IsByBlock) return "BYBLOCK";
            if (c.ColorMethod == ColorMethod.ByAci) return c.ColorIndex.ToString(CultureInfo.InvariantCulture);
            return $"{c.Red},{c.Green},{c.Blue}";
        }

        private static Color ParseColor(string t)
        {
            if (t.Equals("BYLAYER", StringComparison.OrdinalIgnoreCase)) return Color.FromColorIndex(ColorMethod.ByLayer, 256);
            if (t.Equals("BYBLOCK", StringComparison.OrdinalIgnoreCase)) return Color.FromColorIndex(ColorMethod.ByBlock, 0);
            var parts = t.Split(',');
            if (parts.Length == 3) return Color.FromRgb(byte.Parse(parts[0].Trim()), byte.Parse(parts[1].Trim()), byte.Parse(parts[2].Trim()));
            short aci = short.Parse(t, CultureInfo.InvariantCulture);
            if (aci < 1 || aci > 255) throw new FormatException("ACI must be 1-255, BYLAYER, BYBLOCK or r,g,b");
            return Color.FromColorIndex(ColorMethod.ByAci, aci);
        }

        public static string ComponentName(object comp)
        {
            try
            {
                var general = comp.GetType().GetProperty("General")?.GetValue(comp);
                var name = general?.GetType().GetProperty("Name")?.GetValue(general) as string;
                if (!string.IsNullOrEmpty(name)) return name;
            }
            catch { }
            try { return comp.GetType().GetProperty("Name")?.GetValue(comp) as string ?? "?"; } catch { return "?"; }
        }

        public static string Words(string name)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1])) sb.Append(' ');
                sb.Append(name[i]);
            }
            return sb.ToString();
        }
    }
}
