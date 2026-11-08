using System;

namespace C3DProTools
{
    /// <summary>What setting a structure's rim on the surface would do.</summary>
    internal enum RimCheck
    {
        /// <summary>The rim moves to the surface.</summary>
        Adjust,
        /// <summary>The rim is on the surface already.</summary>
        AlreadyMatches,
        /// <summary>The structure lies outside the surface: there is no level to take.</summary>
        OffSurface,
        /// <summary>The new rim would be at or under the sump: not a structure that can be built.</summary>
        BelowSump,
        /// <summary>A connected pipe's top would stand above the new rim: possible (shallow ground), but worth a look.</summary>
        BelowPipe
    }

    /// <summary>The rule behind Rims to surface, apart from the drawing. Levels in metres; NaN means unknown.</summary>
    internal static class RimRules
    {
        /// <summary>A rim this close to its target (m) already matches.</summary>
        public const double Tolerance = 0.001;

        /// <param name="rim">The structure's rim now.</param>
        /// <param name="ground">The surface at the structure; NaN outside the surface.</param>
        /// <param name="aboveSurface">The rim's height above the surface (negative: below).</param>
        /// <param name="sump">The structure's sump level.</param>
        /// <param name="highestPipeTop">The highest outside top of the pipes connected to it; NaN when it has none.</param>
        /// <returns>The check, and the new rim (NaN outside the surface).</returns>
        public static (RimCheck check, double newRim) Check(double rim, double ground, double aboveSurface, double sump, double highestPipeTop)
        {
            if (double.IsNaN(ground)) return (RimCheck.OffSurface, double.NaN);
            double target = ground + aboveSurface;
            if (Math.Abs(rim - target) <= Tolerance) return (RimCheck.AlreadyMatches, target);
            if (target <= sump) return (RimCheck.BelowSump, target);
            if (target < highestPipeTop) return (RimCheck.BelowPipe, target);
            return (RimCheck.Adjust, target);
        }
    }
}
