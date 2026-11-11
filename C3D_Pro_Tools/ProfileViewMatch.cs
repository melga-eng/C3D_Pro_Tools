using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DBObject = Autodesk.AutoCAD.DatabaseServices.DBObject;
using Entity = Autodesk.AutoCAD.DatabaseServices.Entity;
using Surface = Autodesk.Civil.DatabaseServices.Surface;

namespace C3DProTools
{
    /// <summary>A profile shown in a view, with the role it was given.</summary>
    internal sealed class ViewProfile
    {
        public ObjectId Id;
        public string Name = "", Surface = "", Layer = "";
        public ProfileRole Role;
        /// <summary>The one profile used for its role in this view (others of the same role are duplicates).</summary>
        public bool Chosen;
        public bool InBands, Reference, Drawn = true, SurfaceProfile;
        /// <summary>The profile has no geometry (a surface profile sampled outside its surface, or a layout never drawn).</summary>
        public bool Empty;
        public override string ToString() => Name;
    }

    /// <summary>One change Match would make (or made) to a view, for the preview and the report.</summary>
    internal sealed class MatchChange
    {
        public string View = "", What = "", From = "", To = "";
    }

    internal sealed class MatchOptions
    {
        public bool ViewStyle = true, Bands = true, ProfileStyles = true, ProfileLabels = true, Elevation = true;
        /// <summary>Profile styles as the view's overrides (only that view) instead of on the profiles themselves.</summary>
        public bool StylesInView;
        /// <summary>A data band's profiles follow its style name (Existing Levels, Proposed Levels, TOP OF DUCT, Level Difference) over what the source recorded.</summary>
        public bool BandRolesByName = true;
        public bool CreateMissing = true;
        public OtherProfileAction Others = OtherProfileAction.Hide;
        public double Margin = 1.0;
        /// <summary>The elevation range is rounded outward to whole steps of this (m).</summary>
        public double DatumIncrement = 1.0;
        public string TopPrefix = "TOP - ";
    }

    internal sealed class ViewIssue
    {
        public ObjectId ViewId;
        public string View = "", Text = "";
        /// <summary>What the fix does, or null when it cannot be fixed here.</summary>
        public string FixText;
        public Action<Transaction> Fix;
    }

    /// <summary>
    /// Style by example for profile views: a finished view is read by role (existing ground, proposed ground, top of pipe /
    /// duct) and applied to other views, each of which has its own profiles of those roles. Covers the view style, the
    /// band set with every band's profiles, the role profiles' styles and labels, the elevation range, missing and extra
    /// profiles. Also checks views for bands pointing at the wrong profiles. Everything runs in the caller's transaction.
    /// </summary>
    internal static class ProfileViewMatch
    {
        private const string LinkKey = "C3DPRO_PVMATCH";
        // A surface profile matches its surface within this (m) at most of the sampled stations.
        private const double SurfaceTolerance = 0.02;
        private const int SurfaceSamples = 9;

        public static string RoleName(ProfileRole r) =>
            r == ProfileRole.Existing ? "Existing ground" : r == ProfileRole.Proposed ? "Proposed ground" : r == ProfileRole.Top ? "Top of pipe / duct" : "Other";

        public static readonly ProfileRole[] Roles = { ProfileRole.Existing, ProfileRole.Proposed, ProfileRole.Top };

        // =====================================================================================
        // Surfaces and roles
        // =====================================================================================

        /// <summary>The drawing's surfaces (proxies and unreadable ones left out), opened once per run.</summary>
        internal sealed class Surfaces
        {
            public readonly List<(string name, ObjectId id, Surface s)> All = new List<(string, ObjectId, Surface)>();
            private readonly Dictionary<ObjectId, List<string>> _sourcesOf = new Dictionary<ObjectId, List<string>>();
            private readonly Transaction _tr;
            private readonly CivilDocument _doc;
            private ViewPartIndex _parts;

            /// <summary>The parts drawn in each view, read on first use (in the transaction this was made in).</summary>
            public ViewPartIndex Parts => _parts ??= new ViewPartIndex(_tr, _doc);

            public Surfaces(Transaction tr, CivilDocument doc)
            {
                _tr = tr; _doc = doc;
                foreach (ObjectId id in doc.GetSurfaceIds())
                {
                    try { if (tr.GetObject(id, OpenMode.ForRead) is Surface s) All.Add((s.Name, id, s)); }
                    catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                }
            }

            public ObjectId IdOf(string name) => All.FirstOrDefault(x => x.name.Equals(name ?? "", StringComparison.OrdinalIgnoreCase)).id;

            /// <summary>
            /// The surface a profile was sampled from: the one its elevations match at most stations (on a tie, the one named
            /// in the profile's name), else the longest surface name inside the profile's name. Empty when neither.
            /// </summary>
            public string SourceOf(Profile p, Alignment al) => SourcesOf(p, al).FirstOrDefault() ?? "";

            /// <summary>
            /// The existing / proposed role of a surface profile. Surfaces that are the same along the alignment (a copy, a
            /// referenced one, a contour surface built from it) all match, so the rule's surface counts when it is any of them,
            /// not only when it happens to come first in the drawing.
            /// </summary>
            public ProfileRole RoleOf(Profile p, Alignment al, ProfileRoleRules rules)
            {
                List<string> c = SourcesOf(p, al);
                bool Is(string n, string rule) => !string.IsNullOrEmpty(rule) && n.Equals(rule, StringComparison.OrdinalIgnoreCase);
                if (c.Count == 0)
                {
                    // No loaded surface matches: the source may be an unloaded reference (a proxy). The profile's name, which
                    // Civil 3D gives a surface profile from its surface, is then the only clue; the longer rule name wins.
                    bool In(string rule) => !string.IsNullOrWhiteSpace(rule) && p.Name.IndexOf(rule.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
                    bool ex = In(rules.ExistingSurface), pr = In(rules.ProposedSurface);
                    if (ex && pr) return rules.ProposedSurface.Trim().Length >= rules.ExistingSurface.Trim().Length ? ProfileRole.Proposed : ProfileRole.Existing;
                    return ex ? ProfileRole.Existing : pr ? ProfileRole.Proposed : ProfileRole.None;
                }
                if (Is(c[0], rules.ExistingSurface)) return ProfileRole.Existing;
                if (Is(c[0], rules.ProposedSurface)) return ProfileRole.Proposed;
                if (c.Any(n => Is(n, rules.ExistingSurface))) return ProfileRole.Existing;
                if (c.Any(n => Is(n, rules.ProposedSurface))) return ProfileRole.Proposed;
                return ProfileRole.None;
            }

            /// <summary>Every surface the profile's elevations match (best first), or the one named in it when none match.</summary>
            public List<string> SourcesOf(Profile p, Alignment al)
            {
                if (_sourcesOf.TryGetValue(p.ObjectId, out List<string> known)) return known;
                List<(string name, int hits)> matched = new List<(string, int)>();
                double s0, s1;
                try { s0 = Math.Max(p.StartingStation, al.StartingStation); s1 = Math.Min(p.EndingStation, al.EndingStation); }
                catch (Exception) { s0 = s1 = 0; }
                if (s1 > s0)
                {
                    List<(double x, double y, double z)> samples = new List<(double, double, double)>();
                    for (int i = 0; i < SurfaceSamples; i++)
                    {
                        double st = s0 + (s1 - s0) * (i + 0.5) / SurfaceSamples;
                        try
                        {
                            double x = 0, y = 0;
                            al.PointLocation(st, 0, ref x, ref y);
                            samples.Add((x, y, p.ElevationAt(st)));
                        }
                        catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                    }
                    foreach ((string name, ObjectId _, Surface s) in All)
                    {
                        int hits = 0, valid = 0;
                        foreach ((double x, double y, double z) in samples)
                        {
                            double g = CoverRoute.GroundAt(s, x, y);
                            if (double.IsNaN(g)) continue;
                            valid++;
                            if (Math.Abs(g - z) <= SurfaceTolerance) hits++;
                        }
                        if (valid > 0 && hits >= 2 && hits * 10 >= valid * 6) matched.Add((name, hits));
                    }
                }
                int NamedIn(string n) => p.Name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0 ? n.Length : 0;
                List<string> result = matched.OrderByDescending(m => m.hits).ThenByDescending(m => NamedIn(m.name)).Select(m => m.name).ToList();
                if (result.Count == 0)
                {
                    string named = All.Select(x => x.name).Where(n => NamedIn(n) > 0).OrderByDescending(n => n.Length).FirstOrDefault();
                    if (named != null) result.Add(named);
                }
                _sourcesOf[p.ObjectId] = result;
                return result;
            }
        }

        public static bool IsTopName(string name, ProfileRoleRules rules)
        {
            foreach (string w in rules.TopWords ?? new List<string>())
            {
                if (string.IsNullOrWhiteSpace(w)) continue;
                if (Regex.IsMatch(name ?? "", $@"(^|[^A-Za-z]){Regex.Escape(w.Trim())}([^A-Za-z]|$)", RegexOptions.IgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>
        /// The profiles of the view's alignment shown in the view, each with its role. Of several profiles of one role, the
        /// one the view's bands use is chosen, then one without a "(2)" style copy number, then by name.
        /// </summary>
        public static List<ViewProfile> Classify(Transaction tr, ProfileView view, ProfileRoleRules rules, Surfaces surfaces)
        {
            Alignment al = (Alignment)tr.GetObject(view.AlignmentId, OpenMode.ForRead);
            HashSet<ObjectId> inBands = new HashSet<ObjectId>();
            foreach (ProfileViewBandItem b in AllBands(view))
            {
                if (!b.Profile1Id.IsNull) inBands.Add(b.Profile1Id);
                if (!b.Profile2Id.IsNull) inBands.Add(b.Profile2Id);
            }
            Dictionary<ObjectId, bool> drawn = new Dictionary<ObjectId, bool>();
            foreach (ProfileOverride o in view.GraphOverrides)
                try { drawn[o.ProfileId] = o.Draw; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }

            List<ViewProfile> list = new List<ViewProfile>();
            foreach (ObjectId id in al.GetProfileIds())
            {
                if (id.IsErased || !(tr.GetObject(id, OpenMode.ForRead) is Profile p)) continue;
                ViewProfile vp = new ViewProfile { Id = id, Name = p.Name, Layer = p.Layer, InBands = inBands.Contains(id), Drawn = !drawn.TryGetValue(id, out bool d) || d };
                try { vp.Reference = p.IsReferenceObject; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                ProfileType type = ProfileType.FG;
                try { type = p.ProfileType; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                try { vp.Empty = p.Entities.Count == 0; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                vp.SurfaceProfile = type == ProfileType.EG;
                if (vp.SurfaceProfile)
                {
                    vp.Surface = surfaces.SourceOf(p, al);
                    vp.Role = surfaces.RoleOf(p, al, rules);
                    if (vp.Role == ProfileRole.Existing) vp.Surface = rules.ExistingSurface;
                    else if (vp.Role == ProfileRole.Proposed) vp.Surface = rules.ProposedSurface;
                }
                else if (IsTopName(p.Name, rules)) vp.Role = ProfileRole.Top;
                list.Add(vp);
            }
            foreach (ProfileRole r in Roles)
            {
                // An empty profile (nothing sampled, no geometry) never wins its role over one that draws something.
                ViewProfile chosen = list.Where(x => x.Role == r)
                    .OrderBy(x => x.Empty).ThenByDescending(x => x.InBands).ThenBy(x => Regex.IsMatch(x.Name, @"\(\d+\)\s*$")).ThenByDescending(x => x.Drawn).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (chosen != null) chosen.Chosen = true;
            }
            return list;
        }

        public static ObjectId RoleProfile(List<ViewProfile> profiles, ProfileRole r) => profiles.FirstOrDefault(x => x.Role == r && x.Chosen)?.Id ?? ObjectId.Null;

        /// <summary>Surfaces the view's surface profiles were sampled from, to offer as the existing / proposed ground.</summary>
        public static List<string> SurfacesShown(Transaction tr, ProfileView view, Surfaces surfaces)
        {
            Alignment al = (Alignment)tr.GetObject(view.AlignmentId, OpenMode.ForRead);
            List<string> names = new List<string>();
            foreach (ObjectId id in al.GetProfileIds())
            {
                if (id.IsErased || !(tr.GetObject(id, OpenMode.ForRead) is Profile p)) continue;
                ProfileType type = ProfileType.FG;
                try { type = p.ProfileType; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                if (type != ProfileType.EG) continue;
                string s = surfaces.SourceOf(p, al);
                if (s.Length > 0 && !names.Contains(s)) names.Add(s);
            }
            return names;
        }

        private static IEnumerable<ProfileViewBandItem> AllBands(ProfileView view)
        {
            List<ProfileViewBandItem> all = new List<ProfileViewBandItem>();
            try { foreach (ProfileViewBandItem b in view.Bands.GetBottomBandItems()) all.Add(b); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            try { foreach (ProfileViewBandItem b in view.Bands.GetTopBandItems()) all.Add(b); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            return all;
        }

        // =====================================================================================
        // Reading a view into a recipe
        // =====================================================================================

        private static readonly string[] LabelKinds = { "Line", "PVI", "Sag", "Crest", "MajorStation", "MinorStation", "Geometry" };

        private static ObjectIdCollection LabelGroups(string kind, ObjectId viewId, ObjectId profileId)
        {
            try
            {
                switch (kind)
                {
                    case "Line": return ProfileLineLabelGroup.GetAvailableLabelGroupIds(viewId, profileId);
                    case "PVI": return ProfilePVILabelGroup.GetAvailableLabelGroupIds(viewId, profileId);
                    case "Sag": return ProfileSagCurveLabelGroup.GetAvailableLabelGroupIds(viewId, profileId);
                    case "Crest": return ProfileCrestCurveLabelGroup.GetAvailableLabelGroupIds(viewId, profileId);
                    case "MajorStation": return ProfileStationLabelGroup.GetAvailableLabelGroupIds(viewId, profileId, false);
                    case "MinorStation": return ProfileMinorStationLabelGroup.GetAvailableLabelGroupIds(viewId, profileId);
                    case "Geometry": return ProfileHorizontalGeometryPointLabelGroup.GetAvailableLabelGroupIds(viewId, profileId);
                }
            }
            catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            return new ObjectIdCollection();
        }

        /// <summary>
        /// The recipe's styles this drawing lacks: view style, band styles (by type and name), role profile styles and label
        /// styles, for the parts the options apply. Apply replaces bands and labels, so it must not start while any is missing.
        /// </summary>
        public static List<string> MissingStyles(Transaction tr, CivilDocument doc, ProfileViewRecipe recipe, MatchOptions o)
        {
            List<string> missing = new List<string>();
            Database db = doc.Styles.ProfileViewStyles.Count > 0 ? tr.GetObject(doc.Styles.ProfileViewStyles[0], OpenMode.ForRead).Database : null;
            if (o.ViewStyle && recipe.ViewStyle.Length > 0 && StyleId(tr, doc.Styles.ProfileViewStyles, recipe.ViewStyle).IsNull)
                missing.Add($"view style \"{recipe.ViewStyle}\"");
            if (o.Bands && db != null)
                foreach (BandRecipe b in recipe.Bands)
                {
                    ObjectId id = ObjectId.Null;
                    try { id = Autodesk.Civil.DatabaseServices.Styles.BandStyle.GetBandStyleId(db, (BandType)Enum.Parse(typeof(BandType), b.BandType), b.Style); }
                    catch (Exception) { }
                    if (id.IsNull) missing.Add($"{b.BandType} band style \"{b.Style}\"");
                }
            foreach (RoleRecipe r in recipe.Roles)
            {
                if (o.ProfileStyles && r.Style.Length > 0 && StyleId(tr, doc.Styles.ProfileStyles, r.Style).IsNull)
                    missing.Add($"profile style \"{r.Style}\"");
                if (o.ProfileLabels)
                    foreach (LabelGroupRecipe l in r.Labels)
                        if (l.Style.Length > 0 && StyleId(tr, LabelStyles(doc, l.Kind), l.Style).IsNull)
                            missing.Add($"{l.Kind} label style \"{l.Style}\"");
            }
            return missing.Distinct().ToList();
        }

        private static IEnumerable LabelStyles(CivilDocument doc, string kind)
        {
            LabelStylesProfileRoot r = doc.Styles.LabelStyles.ProfileLabelStyles;
            switch (kind)
            {
                case "Line": return r.LineLabelStyles;
                case "PVI": return r.GradeBreakLabelStyles;
                case "Sag": case "Crest": return r.CurveLabelStyles;
                case "MajorStation": return r.MajorStationLabelStyles;
                case "MinorStation": return r.MinorStationLabelStyles;
                case "Geometry": return r.HorizontalGeometryPointLabelStyles;
            }
            return new ArrayList();
        }

        public static string StyleName(Transaction tr, ObjectId id)
        {
            if (id.IsNull || id.IsErased) return "";
            try { return tr.GetObject(id, OpenMode.ForRead) is StyleBase s ? s.Name : ""; } catch (Exception) { return ""; }
        }

        public static ObjectId StyleId(Transaction tr, IEnumerable collection, string name)
        {
            if (string.IsNullOrEmpty(name)) return ObjectId.Null;
            foreach (object o in collection)
            {
                if (!(o is ObjectId id) || id.IsErased) continue;
                try { if (tr.GetObject(id, OpenMode.ForRead) is StyleBase s && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return id; }
                catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            }
            return ObjectId.Null;
        }

        public static ProfileViewRecipe Capture(Transaction tr, ProfileView view, ProfileRoleRules rules, Surfaces surfaces)
        {
            List<ViewProfile> profiles = Classify(tr, view, rules, surfaces);
            Dictionary<ObjectId, ProfileRole> roleOf = profiles.ToDictionary(p => p.Id, p => p.Role);
            ProfileRole RoleOf(ObjectId id) => !id.IsNull && roleOf.TryGetValue(id, out ProfileRole r) ? r : ProfileRole.None;

            ProfileViewRecipe recipe = new ProfileViewRecipe { Name = view.Name, SourceView = view.Name, ViewStyle = StyleName(tr, view.StyleId), Rules = rules.Copy() };
            foreach ((ProfileViewBandItemCollection items, bool top) in new[] { (view.Bands.GetBottomBandItems(), false), (view.Bands.GetTopBandItems(), true) })
                foreach (ProfileViewBandItem b in items)
                {
                    BandRecipe br = new BandRecipe { Top = top, BandType = b.BandType.ToString(), Style = StyleName(tr, b.BandStyleId) };
                    try { br.Gap = b.Gap; br.Weeding = b.Weeding; br.ShowLabels = b.ShowLabels; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                    try { br.LabelAtStart = b.LabelAtStartStation; br.LabelAtEnd = b.LabelAtEndStation; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                    try { br.MajorInterval = b.MajorInterval; br.MinorInterval = b.MinorInterval; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                    try { br.Profile1 = RoleOf(b.Profile1Id); br.Profile2 = RoleOf(b.Profile2Id); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                    recipe.Bands.Add(br);
                }

            Dictionary<ObjectId, ProfileOverride> overrides = new Dictionary<ObjectId, ProfileOverride>();
            foreach (ProfileOverride o in view.GraphOverrides) try { overrides[o.ProfileId] = o; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            foreach (ProfileRole role in Roles)
            {
                ObjectId pid = RoleProfile(profiles, role);
                if (pid.IsNull) continue;
                Profile p = (Profile)tr.GetObject(pid, OpenMode.ForRead);
                RoleRecipe rr = new RoleRecipe { Role = role, Layer = p.Layer, ProfileName = p.Name, Style = StyleName(tr, p.StyleId) };
                if (overrides.TryGetValue(pid, out ProfileOverride ov) && ov.UseOverrideStyle && !ov.OverrideStyleId.IsNull)
                {
                    rr.InView = true;
                    rr.Style = StyleName(tr, ov.OverrideStyleId);
                }
                foreach (string kind in LabelKinds)
                    foreach (ObjectId gid in LabelGroups(kind, view.ObjectId, pid))
                    {
                        if (!(tr.GetObject(gid, OpenMode.ForRead) is LabelGroup g)) continue;
                        LabelGroupRecipe lg = new LabelGroupRecipe { Kind = kind, Style = StyleName(tr, g.StyleId) };
                        if (g is ProfileStationLabelGroup sg) try { lg.Increment = sg.Increment; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                        rr.Labels.Add(lg);
                    }
                recipe.Roles.Add(rr);
            }
            return recipe;
        }

        // =====================================================================================
        // Applying a recipe
        // =====================================================================================

        /// <summary>
        /// Makes the view like the recipe. With <paramref name="write"/> false nothing changes: the changes it would make are
        /// listed (profiles it would create are listed as such and their later steps assumed).
        /// </summary>
        public static void Apply(Transaction tr, CivilDocument doc, ProfileViewRecipe recipe, ObjectId viewId, MatchOptions o, Surfaces surfaces,
            bool write, List<MatchChange> changes, List<string> log)
        {
            ProfileView view = (ProfileView)tr.GetObject(viewId, write ? OpenMode.ForWrite : OpenMode.ForRead);
            string vn = view.Name;
            void Change(string what, string from, string to) => changes.Add(new MatchChange { View = vn, What = what, From = from ?? "", To = to ?? "" });
            Alignment al = (Alignment)tr.GetObject(view.AlignmentId, OpenMode.ForRead);
            List<ViewProfile> profiles = Classify(tr, view, recipe.Rules, surfaces);

            // 1. Missing roles.
            foreach (ProfileRole role in Roles)
            {
                if (!RoleProfile(profiles, role).IsNull || recipe.RoleOf(role) == null) continue;
                if (!o.CreateMissing) { Change(RoleName(role), "missing", "left missing"); continue; }
                string surface = role == ProfileRole.Existing ? recipe.Rules.ExistingSurface : role == ProfileRole.Proposed ? recipe.Rules.ProposedSurface : null;
                if (role != ProfileRole.Top && surfaces.IdOf(surface).IsNull) { Change(RoleName(role), "missing", $"cannot create: no surface \"{surface}\""); continue; }
                Change(RoleName(role), "missing", role == ProfileRole.Top ? "create from the parts in the view" : $"create from {surface}");
                if (!write) continue;
                try
                {
                    if (role == ProfileRole.Top) CreateTop(tr, doc, view, recipe, o, log);
                    else CreateSurfaceProfile(tr, doc, al, surfaces.IdOf(surface), surface, recipe.RoleOf(role), log);
                }
                catch (Exception ex) { log.Add($"{vn}: creating the {RoleName(role).ToLowerInvariant()} profile failed: {ex.Message}"); }
            }
            if (write) profiles = Classify(tr, view, recipe.Rules, surfaces);
            Dictionary<ProfileRole, ObjectId> roleIds = Roles.ToDictionary(r => r, r => RoleProfile(profiles, r));

            // 2. View style.
            if (o.ViewStyle && recipe.ViewStyle.Length > 0)
            {
                ObjectId sid = StyleId(tr, doc.Styles.ProfileViewStyles, recipe.ViewStyle);
                string now = StyleName(tr, view.StyleId);
                if (sid.IsNull) log.Add($"{vn}: the view style \"{recipe.ViewStyle}\" is not in this drawing.");
                else if (!now.Equals(recipe.ViewStyle, StringComparison.OrdinalIgnoreCase))
                {
                    Change("View style", now, recipe.ViewStyle);
                    if (write) view.StyleId = sid;
                }
            }

            // 3. Bands.
            if (o.Bands && recipe.Bands.Count > 0) ApplyBands(tr, view, recipe, roleIds, o.BandRolesByName, write, Change, log);

            // 4. Role profiles' styles, visibility and labels.
            Dictionary<ObjectId, ProfileOverride> overrides = new Dictionary<ObjectId, ProfileOverride>();
            foreach (ProfileOverride ov in view.GraphOverrides) try { overrides[ov.ProfileId] = ov; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            foreach (ProfileRole role in Roles)
            {
                RoleRecipe rr = recipe.RoleOf(role);
                ObjectId pid = roleIds[role];
                if (rr == null || pid.IsNull) continue;
                Profile p = (Profile)tr.GetObject(pid, OpenMode.ForRead);
                overrides.TryGetValue(pid, out ProfileOverride ov);
                if (ov != null && !ov.Draw) { Change($"{RoleName(role)} \"{p.Name}\"", "hidden", "shown"); if (write) ov.Draw = true; }
                if (o.ProfileStyles && rr.Style.Length > 0)
                {
                    ObjectId sid = StyleId(tr, doc.Styles.ProfileStyles, rr.Style);
                    if (sid.IsNull) log.Add($"{vn}: the profile style \"{rr.Style}\" is not in this drawing.");
                    else if (o.StylesInView || rr.InView)
                    {
                        string now = ov != null && ov.UseOverrideStyle ? StyleName(tr, ov.OverrideStyleId) : StyleName(tr, p.StyleId);
                        if (ov != null && (!ov.UseOverrideStyle || !now.Equals(rr.Style, StringComparison.OrdinalIgnoreCase)))
                        {
                            Change($"{RoleName(role)} \"{p.Name}\" style (in this view)", now, rr.Style);
                            if (write) { ov.UseOverrideStyle = true; ov.OverrideStyleId = sid; }
                        }
                    }
                    else
                    {
                        string now = StyleName(tr, p.StyleId);
                        if (!now.Equals(rr.Style, StringComparison.OrdinalIgnoreCase))
                        {
                            Change($"{RoleName(role)} \"{p.Name}\" style", now, rr.Style);
                            if (write) { p.UpgradeOpen(); p.StyleId = sid; }
                        }
                        if (ov != null && ov.UseOverrideStyle)
                        {
                            Change($"{RoleName(role)} \"{p.Name}\" view override", StyleName(tr, ov.OverrideStyleId), "none (profile's own style)");
                            if (write) ov.UseOverrideStyle = false;
                        }
                    }
                }
                if (o.ProfileLabels) ApplyLabels(tr, doc, view, p, rr, role, write, Change, log);
            }

            // 5. Profiles of no role, and second profiles of a role.
            if (o.Others != OtherProfileAction.Leave)
                foreach (ViewProfile vp in profiles.Where(x => !x.Chosen))
                {
                    overrides.TryGetValue(vp.Id, out ProfileOverride ov);
                    string what = vp.Role == ProfileRole.None ? $"Other profile \"{vp.Name}\"" : $"Second {RoleName(vp.Role).ToLowerInvariant()} \"{vp.Name}\"";
                    string why = "";
                    bool delete = o.Others == OtherProfileAction.Delete && CanDelete(tr, vp, view, out why);
                    if (delete)
                    {
                        Change(what, "", "delete");
                        if (write) try { tr.GetObject(vp.Id, OpenMode.ForWrite).Erase(); } catch (Exception ex) { log.Add($"{vn}: deleting \"{vp.Name}\" failed: {ex.Message}"); }
                    }
                    else if (ov != null && ov.Draw)
                    {
                        Change(what, "shown", o.Others == OtherProfileAction.Delete ? $"hide ({why})" : "hide");
                        if (write) ov.Draw = false;
                    }
                }

            // 6. Elevation range.
            if (o.Elevation && FitRange(tr, view, roleIds.Values.Where(x => !x.IsNull), view.StationStart, view.StationEnd, out double lo, out double hi, surfaces.Parts))
            {
                (double min, double max) = ProfileViewMath.DatumRange(lo, hi, o.Margin, o.DatumIncrement);
                if (view.ElevationRangeMode != ElevationRangeType.UserSpecified || Math.Abs(view.ElevationMin - min) > 1e-6 || Math.Abs(view.ElevationMax - max) > 1e-6)
                {
                    Change("Elevation range", $"{view.ElevationMin:0.##} – {view.ElevationMax:0.##}{(view.ElevationRangeMode == ElevationRangeType.Automatic ? " (automatic)" : "")}", $"{min:0.##} – {max:0.##}");
                    if (write) try { ProfileCreation.SetElevationRange(view, lo, hi, o.Margin, o.DatumIncrement); } catch (Exception ex) { log.Add($"{vn}: elevation range: {ex.Message}"); }
                }
            }
        }

        private static bool CanDelete(Transaction tr, ViewProfile vp, ProfileView view, out string why)
        {
            why = "";
            if (vp.Reference) { why = "a data reference"; return false; }
            Alignment al = (Alignment)tr.GetObject(view.AlignmentId, OpenMode.ForRead);
            foreach (ObjectId vid in al.GetProfileViewIds())
            {
                if (vid == view.ObjectId || vid.IsErased || !(tr.GetObject(vid, OpenMode.ForRead) is ProfileView other)) continue;
                if (AllBands(other).Any(b => b.Profile1Id == vp.Id || b.Profile2Id == vp.Id)) { why = $"its bands are used by view {other.Name}"; return false; }
            }
            return true;
        }

        private static void ApplyBands(Transaction tr, ProfileView view, ProfileViewRecipe recipe, Dictionary<ProfileRole, ObjectId> roleIds, bool byName, bool write,
            Action<string, string, string> change, List<string> log)
        {
            foreach (bool top in new[] { false, true })
            {
                List<BandRecipe> want = recipe.Bands.Where(b => b.Top == top).ToList();
                ProfileViewBandItemCollection items = top ? view.Bands.GetTopBandItems() : view.Bands.GetBottomBandItems();
                List<ProfileViewBandItem> have = items.ToList();
                string where = top ? "Top band" : "Band";
                bool sameSet = have.Count == want.Count &&
                               have.Zip(want, (h, w) => h.BandType.ToString() == w.BandType && StyleName(tr, h.BandStyleId).Equals(w.Style, StringComparison.OrdinalIgnoreCase)).All(x => x);
                if (!sameSet)
                {
                    change($"{where}s", have.Count == 0 ? "none" : string.Join(", ", have.Select(h => StyleName(tr, h.BandStyleId))), want.Count == 0 ? "none" : string.Join(", ", want.Select(w => w.Style)));
                    if (!write) continue;
                    // A fresh set in the recipe's order; its profiles are set below.
                    items = new ProfileViewBandItemCollection(view.ObjectId, top ? BandLocationType.Top : BandLocationType.Bottom);
                    foreach (BandRecipe w in want)
                    {
                        try
                        {
                            // The style by type and name (the call Civil 3D asks for in place of the obsolete Add(type, name)).
                            ObjectId styleId = Autodesk.Civil.DatabaseServices.Styles.BandStyle.GetBandStyleId(view.Database, (BandType)Enum.Parse(typeof(BandType), w.BandType), w.Style);
                            if (styleId.IsNull) throw new InvalidOperationException("there is no band style of that name");
                            items.Add(styleId);
                        }
                        catch (Exception ex) { log.Add($"{view.Name}: adding the band \"{w.Style}\" failed: {ex.Message}"); }
                    }
                    have = items.ToList();
                }
                for (int i = 0; i < Math.Min(have.Count, want.Count); i++)
                {
                    ProfileViewBandItem h = have[i];
                    BandRecipe w = want[i];
                    string label = $"{where} {i + 1} \"{w.Style}\"";
                    // Each value on its own: a band type without the value (station intervals on some bands) throws on reading
                    // it, and that must not stop the band's profiles from being set.
                    SetIf(label + " gap", () => h.Gap, w.Gap, v => h.Gap = v);
                    SetIf(label + " weeding", () => h.Weeding, w.Weeding, v => h.Weeding = v);
                    SetIf(label + " major interval", () => h.MajorInterval, w.MajorInterval, v => h.MajorInterval = v);
                    SetIf(label + " minor interval", () => h.MinorInterval, w.MinorInterval, v => h.MinorInterval = v);
                    try { if (h.ShowLabels != w.ShowLabels) { change(label + " labels", h.ShowLabels ? "on" : "off", w.ShowLabels ? "on" : "off"); if (write) h.ShowLabels = w.ShowLabels; } }
                    catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                    try
                    {
                        if (h.LabelAtStartStation != w.LabelAtStart) { change(label + " label at start", h.LabelAtStartStation ? "on" : "off", w.LabelAtStart ? "on" : "off"); if (write) h.LabelAtStartStation = w.LabelAtStart; }
                        if (h.LabelAtEndStation != w.LabelAtEnd) { change(label + " label at end", h.LabelAtEndStation ? "on" : "off", w.LabelAtEnd ? "on" : "off"); if (write) h.LabelAtEndStation = w.LabelAtEnd; }
                    }
                    catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                    (ProfileRole p1, ProfileRole p2) = (w.Profile1, w.Profile2);
                    if (byName && w.BandType == nameof(BandType.ProfileData) && ProfileViewMath.BandRolesFromName(w.Style) is (int n1, int n2))
                        (p1, p2) = ((ProfileRole)n1, (ProfileRole)n2);
                    SetProfile(label + " profile 1", p1, () => h.Profile1Id, id => h.Profile1Id = id);
                    SetProfile(label + " profile 2", p2, () => h.Profile2Id, id => h.Profile2Id = id);
                }
                if (write)
                {
                    try { if (top) view.Bands.SetTopBandItems(items); else view.Bands.SetBottomBandItems(items); }
                    catch (Exception ex) { log.Add($"{view.Name}: setting the {where.ToLowerInvariant()}s failed: {ex.Message}"); }
                }
            }

            void SetIf(string what, Func<double> get, double want, Action<double> set)
            {
                double now;
                try { now = get(); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { return; }
                if (Math.Abs(now - want) < 1e-6) return;
                change(what, ToolUi.Format(now), ToolUi.Format(want));
                if (write) try { set(want); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            }

            void SetProfile(string what, ProfileRole role, Func<ObjectId> get, Action<ObjectId> set)
            {
                if (role == ProfileRole.None) return;
                ObjectId want = roleIds.TryGetValue(role, out ObjectId id) ? id : ObjectId.Null;
                ObjectId now;
                try { now = get(); } catch (Exception) { return; }
                if (want.IsNull) { if (write) log.Add($"{view.Name}: {what}: no {RoleName(role).ToLowerInvariant()} profile to show."); return; }
                if (now == want) return;
                change(what, ProfileName(tr, now), $"{ProfileName(tr, want)} ({RoleName(role).ToLowerInvariant()})");
                if (write) try { set(want); } catch (Exception ex) { log.Add($"{view.Name}: {what}: {ex.Message}"); }
            }
        }

        public static string ProfileName(Transaction tr, ObjectId id)
        {
            if (id.IsNull) return "none";
            try { return tr.GetObject(id, OpenMode.ForRead) is Profile p ? p.Name : "?"; } catch (Exception) { return "?"; }
        }

        private static void ApplyLabels(Transaction tr, CivilDocument doc, ProfileView view, Profile p, RoleRecipe rr, ProfileRole role, bool write,
            Action<string, string, string> change, List<string> log)
        {
            List<(string kind, string style, ObjectId id)> have = new List<(string, string, ObjectId)>();
            foreach (string kind in LabelKinds)
                foreach (ObjectId gid in LabelGroups(kind, view.ObjectId, p.ObjectId))
                    if (tr.GetObject(gid, OpenMode.ForRead) is LabelGroup g) have.Add((kind, StyleName(tr, g.StyleId), gid));
            string Text(IEnumerable<string> items) { string t = string.Join(", ", items); return t.Length == 0 ? "none" : t; }
            string now = Text(have.Select(h => $"{h.kind}: {h.style}"));
            string want = Text(rr.Labels.Select(l => $"{l.Kind}: {l.Style}"));
            if (now == want) return;
            change($"{RoleName(role)} \"{p.Name}\" labels", now, want);
            if (!write) return;
            // Minor station labels hang off their major group, so they go first and come last.
            foreach (var h in have.OrderBy(h => h.kind == "MinorStation" ? 0 : 1))
                try { if (!h.id.IsErased) tr.GetObject(h.id, OpenMode.ForWrite).Erase(); } catch (Exception) { }  // cleanup: nothing more to undo if this fails
            ObjectId major = ObjectId.Null;
            foreach (LabelGroupRecipe l in rr.Labels.OrderBy(l => l.Kind == "MinorStation" ? 1 : 0))
            {
                ObjectId sid = StyleId(tr, LabelStyles(doc, l.Kind), l.Style);
                if (sid.IsNull) { log.Add($"{view.Name}: the {l.Kind} label style \"{l.Style}\" is not in this drawing."); continue; }
                try
                {
                    switch (l.Kind)
                    {
                        case "Line": ProfileLineLabelGroup.Create(view.ObjectId, p.ObjectId, sid); break;
                        case "PVI": ProfilePVILabelGroup.Create(view.ObjectId, p.ObjectId, sid); break;
                        case "Sag": ProfileSagCurveLabelGroup.Create(view.ObjectId, p.ObjectId, sid); break;
                        case "Crest": ProfileCrestCurveLabelGroup.Create(view.ObjectId, p.ObjectId, sid); break;
                        case "Geometry": ProfileHorizontalGeometryPointLabelGroup.Create(view.ObjectId, p.ObjectId, sid); break;
                        case "MajorStation": major = ProfileStationLabelGroup.CreateMajor(view.ObjectId, p.ObjectId, sid, l.Increment > 0 ? l.Increment : 20); break;
                        case "MinorStation":
                            if (major.IsNull) log.Add($"{view.Name}: minor station labels need major station labels.");
                            else ProfileMinorStationLabelGroup.Create(sid, major, l.Increment > 0 ? l.Increment : 10);
                            break;
                    }
                }
                catch (Exception ex) { log.Add($"{view.Name}: {l.Kind} labels on \"{p.Name}\": {ex.Message}"); }
            }
        }

        private static void CreateSurfaceProfile(Transaction tr, CivilDocument doc, Alignment al, ObjectId surfaceId, string surface, RoleRecipe rr, List<string> log)
        {
            Database db = al.Database;
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId id in al.GetProfileIds()) names.Add(ProfileName(tr, id));
            string baseName = $"{al.Name}_{surface}", name = baseName;
            for (int i = 2; names.Contains(name); i++) name = $"{baseName} ({i})";
            ObjectId layer = LayerFor(tr, db, rr, name);
            ObjectId style = StyleId(tr, doc.Styles.ProfileStyles, rr.Style);
            if (style.IsNull) foreach (ObjectId id in doc.Styles.ProfileStyles) { style = id; break; }
            ObjectId labels = ObjectId.Null;
            foreach (ObjectId id in doc.Styles.LabelSetStyles.ProfileLabelSetStyles)
            {
                if (labels.IsNull) labels = id;
                if (StyleName(tr, id).IndexOf("No Label", StringComparison.OrdinalIgnoreCase) >= 0) { labels = id; break; }
            }
            Profile.CreateFromSurface(name, al.ObjectId, surfaceId, layer, style, labels);
            log.Add($"{al.Name}: created the profile \"{name}\" from {surface}.");
        }

        /// <summary>
        /// The layer for a new profile: the source profile's layer, or, when that layer is named after its profile (a layer
        /// per profile, "…-VertProf_&lt;profile&gt;"), the same prefix with the new name, made like the source layer.
        /// </summary>
        private static ObjectId LayerFor(Transaction tr, Database db, RoleRecipe rr, string newName)
        {
            LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (string.IsNullOrEmpty(rr.Layer) || !lt.Has(rr.Layer)) return db.Clayer;
            ObjectId source = lt[rr.Layer];
            if (string.IsNullOrEmpty(rr.ProfileName) || !rr.Layer.EndsWith(rr.ProfileName, StringComparison.OrdinalIgnoreCase)) return source;
            string name = rr.Layer.Substring(0, rr.Layer.Length - rr.ProfileName.Length) + newName;
            try { SymbolUtilityServices.ValidateSymbolName(name, false); } catch (Exception) { return source; }
            if (lt.Has(name)) return lt[name];
            LayerTableRecord from = (LayerTableRecord)tr.GetObject(source, OpenMode.ForRead);
            LayerTableRecord layer = (LayerTableRecord)from.Clone();
            layer.Name = name;
            lt.UpgradeOpen();
            ObjectId id = lt.Add(layer);
            tr.AddNewlyCreatedDBObject(layer, true);
            return id;
        }

        private static void CreateTop(Transaction tr, CivilDocument doc, ProfileView view, ProfileViewRecipe recipe, MatchOptions o, List<string> log)
        {
            RoleRecipe rr = recipe.RoleOf(ProfileRole.Top);
            TopProfileOptions to = new TopProfileOptions { Prefix = o.TopPrefix ?? "TOP - ", Highest = true, StyleId = StyleId(tr, doc.Styles.ProfileStyles, rr?.Style) };
            if (!IsTopName(to.Prefix + view.Name, recipe.Rules)) to.Prefix = "TOP - ";
            TopProfileResult r = TopOfPipeProfile.Create(tr, doc, view.ObjectId, to);
            if (r.Created + r.Updated == 0) log.Add($"{view.Name}: no top-of-pipe profile could be made (no pipes along the alignment in the view).");
            else log.Add($"{view.Name}: created {string.Join(", ", r.Names)}.");
            log.AddRange(r.Notes.Select(n => $"{view.Name}: {n}"));
        }

        // =====================================================================================
        // Elevation range
        // =====================================================================================

        /// <summary>
        /// Lowest and highest level to show between two stations: the role profiles there, and the outside of the pipes and
        /// structures drawn in the view there.
        /// </summary>
        public static bool FitRange(Transaction tr, ProfileView view, IEnumerable<ObjectId> profileIds, double s0, double s1, out double lo, out double hi,
            ViewPartIndex index = null)
        {
            lo = double.MaxValue; hi = double.MinValue;
            double l = lo, h = hi;
            void Add(double z) { if (double.IsNaN(z) || double.IsInfinity(z)) return; l = Math.Min(l, z); h = Math.Max(h, z); }
            foreach (ObjectId pid in profileIds)
            {
                if (!(tr.GetObject(pid, OpenMode.ForRead) is Profile p)) continue;
                double a, b;
                try { a = Math.Max(s0, p.StartingStation); b = Math.Min(s1, p.EndingStation); } catch (Exception) { continue; }
                if (b < a) continue;
                int n = Math.Max(2, Math.Min(400, (int)Math.Ceiling((b - a) / 2.0)));
                for (int i = 0; i <= n; i++) try { Add(p.ElevationAt(a + (b - a) * i / n)); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            }
            Alignment al = (Alignment)tr.GetObject(view.AlignmentId, OpenMode.ForRead);
            foreach (ObjectId partId in PartsInView(tr, view, index))
            {
                DBObject obj = tr.GetObject(partId, OpenMode.ForRead);
                if (obj is Pipe pipe)
                {
                    double half = CoverRoute.OuterHeightOf(pipe) / 2.0;
                    foreach (Point3d pt in new[] { pipe.StartPoint, pipe.EndPoint })
                        if (InRange(al, pt, s0, s1)) { Add(pt.Z - half); Add(pt.Z + half); }
                }
                else if (obj is Structure st && InRange(al, st.Location, s0, s1)) { try { Add(st.SumpElevation); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { } }
                else if (obj is PressurePipe pp)
                {
                    double half = 0;
                    try { half = pp.OuterDiameter / 2.0; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                    foreach (Point3d pt in new[] { pp.StartPoint, pp.EndPoint })
                        if (InRange(al, pt, s0, s1)) { Add(pt.Z - half); Add(pt.Z + half); }
                }
            }
            lo = l; hi = h;
            return lo <= hi;
        }

        public static bool InRange(Alignment al, Point3d pt, double s0, double s1)
        {
            try
            {
                double st = 0, off = 0;
                al.StationOffset(pt.X, pt.Y, ref st, ref off);
                return st >= s0 - 1e-6 && st <= s1 + 1e-6;
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// Every network part of the drawing by the profile views drawing it. The views' own override lists leave some drawn
        /// parts out (crossing pipes of other networks), so the parts are asked instead. Built once per run.
        /// </summary>
        internal sealed class ViewPartIndex
        {
            private readonly Dictionary<ObjectId, List<ObjectId>> _byView = new Dictionary<ObjectId, List<ObjectId>>();

            public ViewPartIndex(Transaction tr, CivilDocument doc)
            {
                void Add(ObjectId partId, Func<ObjectIdCollection> views)
                {
                    ObjectIdCollection shown;
                    try { shown = views(); } catch (Exception) { return; }
                    if (shown == null) return;
                    foreach (ObjectId v in shown)
                    {
                        if (!_byView.TryGetValue(v, out List<ObjectId> list)) _byView[v] = list = new List<ObjectId>();
                        list.Add(partId);
                    }
                }
                foreach (ObjectId netId in doc.GetPipeNetworkIds())
                {
                    if (!(tr.GetObject(netId, OpenMode.ForRead) is Network net)) continue;
                    foreach (ObjectId id in net.GetPipeIds().Cast<ObjectId>().Concat(net.GetStructureIds().Cast<ObjectId>()))
                        if (!id.IsErased && tr.GetObject(id, OpenMode.ForRead) is Part p) Add(id, p.GetProfileViewsDisplayingMe);
                }
                foreach (ObjectId netId in doc.GetPressurePipeNetworkIds())
                {
                    if (!(tr.GetObject(netId, OpenMode.ForRead) is PressurePipeNetwork net)) continue;
                    foreach (ObjectId id in net.GetPipeIds().Cast<ObjectId>().Concat(net.GetFittingIds().Cast<ObjectId>()).Concat(net.GetAppurtenanceIds().Cast<ObjectId>()))
                        if (!id.IsErased && tr.GetObject(id, OpenMode.ForRead) is PressurePart p) Add(id, p.GetProfileViewsDisplayingMe);
                }
            }

            public List<ObjectId> Of(ObjectId viewId) => _byView.TryGetValue(viewId, out List<ObjectId> list) ? list : new List<ObjectId>();
        }

        /// <summary>The model parts (gravity pipes and structures, pressure parts, crossings) drawn in the view.</summary>
        public static List<ObjectId> PartsInView(Transaction tr, ProfileView view, ViewPartIndex index = null)
        {
            List<ObjectId> ids = new List<ObjectId>((index ?? new ViewPartIndex(tr, CivilDocument.GetCivilDocument(view.Database))).Of(view.ObjectId));
            // The overrides name the part's representation in the view (a ProfileViewPart), which leads to the model part.
            void AddGravity(ObjectId id)
            {
                if (id.IsNull || id.IsErased) return;
                try { ids.Add(tr.GetObject(id, OpenMode.ForRead) is ProfileViewPart vp ? vp.ModelPartId : id); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            }
            try { foreach (PipeOverride o in view.PipeOverrides) AddGravity(o.PipeId); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            try { foreach (StructureOverride o in view.StructureOverrides) AddGravity(o.StructId); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            try
            {
                foreach (ObjectId id in view.GetPressureNetworkPartsInGraph())
                    if (tr.GetObject(id, OpenMode.ForRead, false, true) is ProfileViewPressurePart vp && !vp.ModelPartId.IsNull) ids.Add(vp.ModelPartId);
            }
            catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            return ids.Where(id => !id.IsErased).Distinct().ToList();
        }

        // =====================================================================================
        // Health check
        // =====================================================================================

        /// <summary>
        /// Problems in a view: bands showing a profile of another alignment or none, missing roles, second profiles of a role,
        /// and an elevation range that cuts off the profiles or parts. Each fixable one carries its fix.
        /// </summary>
        public static List<ViewIssue> Check(Transaction tr, CivilDocument doc, ObjectId viewId, ProfileRoleRules rules, Surfaces surfaces, double margin, double increment = 1.0)
        {
            List<ViewIssue> issues = new List<ViewIssue>();
            ProfileView view = (ProfileView)tr.GetObject(viewId, OpenMode.ForRead);
            string vn = view.Name;
            List<ViewProfile> profiles = Classify(tr, view, rules, surfaces);
            Dictionary<ProfileRole, ObjectId> roleIds = Roles.ToDictionary(r => r, r => RoleProfile(profiles, r));

            foreach ((ProfileViewBandItemCollection items, bool top) in new[] { (view.Bands.GetBottomBandItems(), false), (view.Bands.GetTopBandItems(), true) })
            {
                int i = 0;
                foreach (ProfileViewBandItem b in items)
                {
                    i++;
                    if (b.BandType != BandType.ProfileData) continue;
                    string band = $"{(top ? "Top band" : "Band")} {i} \"{StyleName(tr, b.BandStyleId)}\"";
                    foreach (int k in new[] { 1, 2 })
                    {
                        ObjectId pid;
                        try { pid = k == 1 ? b.Profile1Id : b.Profile2Id; } catch (Exception) { continue; }
                        if (pid.IsNull) { issues.Add(new ViewIssue { ViewId = viewId, View = vn, Text = $"{band}: profile {k} is not set." }); continue; }
                        if (!(tr.GetObject(pid, OpenMode.ForRead) is Profile p) || p.AlignmentId == view.AlignmentId) continue;
                        // A profile of another alignment (left over from a copied view): replaced by this view's profile of the same role.
                        ProfileRole role = RoleOfForeign(tr, p, rules, surfaces);
                        ObjectId fix = role == ProfileRole.None ? ObjectId.Null : roleIds[role];
                        bool isTop = top; int index = i - 1, which = k;
                        issues.Add(new ViewIssue
                        {
                            ViewId = viewId, View = vn,
                            Text = $"{band}: profile {k} is \"{p.Name}\" of another alignment ({AlignmentName(tr, p.AlignmentId)}); the band shows nothing here.",
                            FixText = fix.IsNull ? null : $"Use \"{ProfileName(tr, fix)}\" ({RoleName(role).ToLowerInvariant()})",
                            Fix = fix.IsNull ? null : (Action<Transaction>)(t => SetBandProfile(t, viewId, isTop, index, which, fix))
                        });
                    }
                }
            }
            foreach (ProfileRole r in Roles)
            {
                List<ViewProfile> of = profiles.Where(x => x.Role == r).ToList();
                if (of.Count == 0) issues.Add(new ViewIssue { ViewId = viewId, View = vn, Text = $"No {RoleName(r).ToLowerInvariant()} profile." });
                else if (of.Count > 1)
                {
                    List<ObjectId> extra = of.Where(x => !x.Chosen && x.Drawn).Select(x => x.Id).ToList();
                    if (extra.Count == 0) continue;
                    issues.Add(new ViewIssue
                    {
                        ViewId = viewId, View = vn,
                        Text = $"{of.Count} {RoleName(r).ToLowerInvariant()} profiles: {string.Join(", ", of.Select(x => $"\"{x.Name}\""))}.",
                        FixText = $"Hide the ones not used ({extra.Count})",
                        Fix = t => HideProfiles(t, viewId, extra)
                    });
                }
            }
            if (FitRange(tr, view, roleIds.Values.Where(x => !x.IsNull), view.StationStart, view.StationEnd, out double lo, out double hi) &&
                (lo < view.ElevationMin - 1e-6 || hi > view.ElevationMax + 1e-6))
            {
                issues.Add(new ViewIssue
                {
                    ViewId = viewId, View = vn,
                    Text = $"The elevation range {view.ElevationMin:0.##} – {view.ElevationMax:0.##} cuts off the profiles or parts ({lo:0.##} – {hi:0.##}).",
                    FixText = $"Fit to {ProfileViewMath.DatumRange(lo, hi, margin, increment).low:0.##} – {ProfileViewMath.DatumRange(lo, hi, margin, increment).high:0.##}",
                    Fix = t => { ProfileView v = (ProfileView)t.GetObject(viewId, OpenMode.ForWrite); ProfileCreation.SetElevationRange(v, lo, hi, margin, increment); }
                });
            }
            return issues;
        }

        private static string AlignmentName(Transaction tr, ObjectId id)
        {
            try { return tr.GetObject(id, OpenMode.ForRead) is Alignment a ? a.Name : "?"; } catch (Exception) { return "?"; }
        }

        private static ProfileRole RoleOfForeign(Transaction tr, Profile p, ProfileRoleRules rules, Surfaces surfaces)
        {
            ProfileType type = ProfileType.FG;
            try { type = p.ProfileType; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            if (type != ProfileType.EG) return IsTopName(p.Name, rules) ? ProfileRole.Top : ProfileRole.None;
            if (!(tr.GetObject(p.AlignmentId, OpenMode.ForRead) is Alignment al)) return ProfileRole.None;
            return surfaces.RoleOf(p, al, rules);
        }

        private static void SetBandProfile(Transaction tr, ObjectId viewId, bool top, int index, int which, ObjectId profileId)
        {
            ProfileView v = (ProfileView)tr.GetObject(viewId, OpenMode.ForWrite);
            ProfileViewBandItemCollection items = top ? v.Bands.GetTopBandItems() : v.Bands.GetBottomBandItems();
            ProfileViewBandItem b = items.ToList()[index];
            if (which == 1) b.Profile1Id = profileId; else b.Profile2Id = profileId;
            if (top) v.Bands.SetTopBandItems(items); else v.Bands.SetBottomBandItems(items);
        }

        private static void HideProfiles(Transaction tr, ObjectId viewId, List<ObjectId> ids)
        {
            ProfileView v = (ProfileView)tr.GetObject(viewId, OpenMode.ForWrite);
            foreach (ProfileOverride o in v.GraphOverrides)
                try { if (ids.Contains(o.ProfileId)) o.Draw = false; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
        }

        // =====================================================================================
        // Master and followers
        // =====================================================================================

        /// <summary>Records on the view which view it follows (by handle), so Sync can apply the master to it again later.</summary>
        public static void Link(Transaction tr, ObjectId viewId, ObjectId masterId)
        {
            DBObject v = tr.GetObject(viewId, OpenMode.ForWrite);
            if (v.ExtensionDictionary.IsNull) v.CreateExtensionDictionary();
            DBDictionary dict = (DBDictionary)tr.GetObject(v.ExtensionDictionary, OpenMode.ForWrite);
            Xrecord x = dict.Contains(LinkKey) ? (Xrecord)tr.GetObject(dict.GetAt(LinkKey), OpenMode.ForWrite) : null;
            if (x == null) { x = new Xrecord(); dict.SetAt(LinkKey, x); tr.AddNewlyCreatedDBObject(x, true); }
            x.Data = new ResultBuffer(new TypedValue((int)DxfCode.Text, masterId.Handle.ToString()));
        }

        public static void Unlink(Transaction tr, ObjectId viewId)
        {
            DBObject v = tr.GetObject(viewId, OpenMode.ForRead);
            if (v.ExtensionDictionary.IsNull) return;
            DBDictionary dict = (DBDictionary)tr.GetObject(v.ExtensionDictionary, OpenMode.ForRead);
            if (!dict.Contains(LinkKey)) return;
            dict.UpgradeOpen();
            dict.Remove(LinkKey);
        }

        /// <summary>The master the view follows, or null when it follows none (or the master is gone).</summary>
        public static ObjectId MasterOf(Transaction tr, Database db, ObjectId viewId)
        {
            DBObject v = tr.GetObject(viewId, OpenMode.ForRead);
            if (v.ExtensionDictionary.IsNull) return ObjectId.Null;
            DBDictionary dict = (DBDictionary)tr.GetObject(v.ExtensionDictionary, OpenMode.ForRead);
            if (!dict.Contains(LinkKey)) return ObjectId.Null;
            TypedValue[] data = ((Xrecord)tr.GetObject(dict.GetAt(LinkKey), OpenMode.ForRead)).Data?.AsArray();
            if (data == null || data.Length == 0) return ObjectId.Null;
            try
            {
                Handle h = new Handle(Convert.ToInt64((string)data[0].Value, 16));
                return db.TryGetObjectId(h, out ObjectId id) && !id.IsErased ? id : ObjectId.Null;
            }
            catch (Exception) { return ObjectId.Null; }
        }

        /// <summary>Every profile view in model space.</summary>
        public static List<ObjectId> AllViews(Transaction tr, Database db)
        {
            List<ObjectId> ids = new List<ObjectId>();
            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
            Autodesk.AutoCAD.Runtime.RXClass cls = Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(ProfileView));
            foreach (ObjectId id in ms) if (!id.IsErased && id.ObjectClass.IsDerivedFrom(cls)) ids.Add(id);
            return ids;
        }
    }

    internal static class BandItemsExtensions
    {
        public static List<ProfileViewBandItem> ToList(this ProfileViewBandItemCollection items)
        {
            List<ProfileViewBandItem> list = new List<ProfileViewBandItem>();
            foreach (ProfileViewBandItem b in items) list.Add(b);
            return list;
        }
    }
}
