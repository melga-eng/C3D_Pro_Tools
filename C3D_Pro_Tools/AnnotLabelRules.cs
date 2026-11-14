using System;
using System.Collections.Generic;

namespace C3DProTools
{
    internal enum LabelKind { PlanPipe, PlanStructure, ProfilePipe, ProfileStructure }

    internal sealed record AnnotLabelPosition(LabelKind Kind, string Style, string View, double? Ratio);

    /// <summary>Which ends of a pipe leave a label out when that end is at a manhole (any structure; ducts join each other directly).</summary>
    internal enum SkipAtManhole { No, Start, End, Both }

    internal static class AnnotLabelRules
    {
        /// <summary>True when a pipe label is left out: its row skips that end and the pipe's end there is at a manhole.</summary>
        internal static bool LeftOut(SkipAtManhole skip, bool startAtManhole, bool endAtManhole) =>
            (skip == SkipAtManhole.Start || skip == SkipAtManhole.Both) && startAtManhole
            || (skip == SkipAtManhole.End || skip == SkipAtManhole.Both) && endAtManhole;

        internal static string StyleKey(LabelKind kind, string style) => kind + ":" + style;

        /// <summary>
        /// Consumes one existing label matching the requested kind, style, view and pipe position. A requested pipe label with
        /// no ratio matches at any position (the only label of its style on the pipe, which Arrange may have slid along it).
        /// </summary>
        internal static bool TakeExisting(List<AnnotLabelPosition> existing, AnnotLabelPosition requested)
        {
            bool profile = requested.Kind == LabelKind.ProfilePipe || requested.Kind == LabelKind.ProfileStructure;
            bool pipe = requested.Kind == LabelKind.PlanPipe || requested.Kind == LabelKind.ProfilePipe;
            int index = existing.FindIndex(l => l.Kind == requested.Kind && l.Style == requested.Style
                && (!profile || l.View == requested.View)
                && (!pipe || !requested.Ratio.HasValue || (l.Ratio.HasValue
                    && Math.Abs(l.Ratio.Value - requested.Ratio.Value) <= 0.02 + 1e-12)));
            if (index < 0) return false;
            existing.RemoveAt(index);
            return true;
        }
    }
}
