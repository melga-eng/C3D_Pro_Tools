using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace C3DProTools.Features.CadExport
{
    /// <summary>The window a sheet viewport cuts out of model space (model coordinates of its four corners) and its twist.</summary>
    internal sealed class ViewportWindow
    {
        public string Layout { get; init; } = "";
        /// <summary>Radians, as Civil 3D and AutoCAD store it (counter-clockwise).</summary>
        public double Twist { get; init; }
        public double[] Xs { get; init; } = new double[4];
        public double[] Ys { get; init; } = new double[4];

        /// <summary>
        /// The model-space window of a paper viewport: paper size, view centre (display coordinates), view height, view target
        /// and twist. A display point d maps to model as target + d turned by -twist.
        /// </summary>
        public static ViewportWindow Of(string layout, double paperWidth, double paperHeight, double viewCenterX, double viewCenterY,
            double viewHeight, double targetX, double targetY, double twist)
        {
            double scale = paperHeight > 0 ? viewHeight / paperHeight : 1.0;
            double hw = paperWidth * scale / 2.0, hh = viewHeight / 2.0;
            double c = Math.Cos(-twist), s = Math.Sin(-twist);
            var xs = new double[4];
            var ys = new double[4];
            int i = 0;
            foreach ((double dx, double dy) in new[] { (-hw, -hh), (hw, -hh), (hw, hh), (-hw, hh) })
            {
                double x = viewCenterX + dx, y = viewCenterY + dy;
                xs[i] = targetX + x * c - y * s;
                ys[i] = targetY + x * s + y * c;
                i++;
            }
            return new ViewportWindow { Layout = layout, Twist = twist, Xs = xs, Ys = ys };
        }

        public bool Contains(double x, double y)
        {
            // Convex quadrilateral: the point is on the same side of all four edges.
            int sign = 0;
            for (int i = 0; i < 4; i++)
            {
                int j = (i + 1) % 4;
                double cross = (Xs[j] - Xs[i]) * (y - Ys[i]) - (Ys[j] - Ys[i]) * (x - Xs[i]);
                if (Math.Abs(cross) < 1e-9) continue;
                int sg = cross > 0 ? 1 : -1;
                if (sign == 0) sign = sg;
                else if (sg != sign) return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Which view turn a label is exploded at, so the exploded text reads on the CAD sheet as the label read on the Civil 3D sheet.
    /// Profile labels at true north; plan labels at the twist of the plan viewport they show in. This is the two-step explode
    /// done by hand: the plan viewport restored in model space, its labels exploded, then the profile labels with no turn.
    /// </summary>
    internal static class SheetTwist
    {
        private const double Tol = 1e-6;

        /// <summary>A profile-kind label (its Civil 3D object name says profile, band or section) always explodes at true north.</summary>
        public static bool IsProfileKind(string objectName) =>
            !string.IsNullOrEmpty(objectName) &&
            (objectName.IndexOf("PROFILE", StringComparison.OrdinalIgnoreCase) >= 0 ||
             objectName.IndexOf("BAND", StringComparison.OrdinalIgnoreCase) >= 0 ||
             objectName.IndexOf("SECTION", StringComparison.OrdinalIgnoreCase) >= 0);

        /// <summary>The plan twist of the drawing: the most common non-zero viewport twist, 0 when every viewport is north up.</summary>
        public static double PlanTwist(IEnumerable<ViewportWindow> windows) =>
            windows.Where(w => Math.Abs(Normal(w.Twist)) > Tol)
                   .GroupBy(w => Math.Round(Normal(w.Twist), 6))
                   .OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
                   .Select(g => g.First().Twist).FirstOrDefault();

        /// <summary>
        /// The turn to explode one label at. In order: a profile-kind label, or one inside a profile view, at 0; one inside a
        /// turned viewport at that viewport's twist (the most used one when several show it); otherwise the plan twist.
        /// </summary>
        public static double For(double x, double y, bool profileKind, bool insideProfileView, IReadOnlyList<ViewportWindow> windows, double planTwist)
        {
            if (profileKind || insideProfileView) return 0.0;
            var turned = windows.Where(w => Math.Abs(Normal(w.Twist)) > Tol && w.Contains(x, y)).ToList();
            if (turned.Count > 0)
                return turned.GroupBy(w => Math.Round(Normal(w.Twist), 6)).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().First().Twist;
            return planTwist;
        }

        /// <summary>The same twist in [0, 2π), so 359.99° and -0.01° group together.</summary>
        public static double Normal(double twist)
        {
            double t = twist % (2 * Math.PI);
            if (t < 0) t += 2 * Math.PI;
            if (2 * Math.PI - t < Tol) t = 0;
            return t + 0.0; // -0 becomes 0
        }

        /// <summary>The CAD sheet file name for a layout: the layout name, characters Windows refuses replaced with _.</summary>
        public static string FileName(string layout)
        {
            char[] bad = Path.GetInvalidFileNameChars();
            string name = new string((layout ?? "").Select(ch => Array.IndexOf(bad, ch) >= 0 ? '_' : ch).ToArray()).Trim().TrimEnd('.');
            return (name.Length == 0 ? "Sheet" : name) + ".dwg";
        }
    }
}
