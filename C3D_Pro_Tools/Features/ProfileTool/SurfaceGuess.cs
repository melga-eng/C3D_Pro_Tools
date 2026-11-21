using System;
using System.Collections.Generic;
using System.Linq;

namespace C3DProTools.Features.ProfileTool
{
    /// <summary>
    /// The drawing's existing-ground or proposed surface when the name saved for it is not in the drawing (settings carried over
    /// from another drawing: the MV drawing 460201 got the ICT drawing's KSIA_Existing-Surface, 2026-11-21, and drew no cover DIMs).
    /// Plain data.
    /// </summary>
    internal static class SurfaceGuess
    {
        private static readonly (string word, int score)[] Existing = { ("EXISTING", 3), ("NGL", 3), ("OGL", 3), ("EXIST", 2), ("EG", 2), ("GROUND", 1), ("ORIGINAL", 2), ("SURVEY", 1), ("TOPO", 1) };
        private static readonly (string word, int score)[] Proposed = { ("PROPOSED", 3), ("FGL", 3), ("FINISH", 3), ("FINISHED", 3), ("DESIGN", 2), ("PROP", 2), ("FG", 2) };

        /// <summary>
        /// The surface to use: the saved name when the drawing has it; else the drawing's surface whose name says most clearly
        /// that it is existing ground (NGL, EXISTING…) or proposed (PROPOSED, FGL…), the shorter name first; null when none does.
        /// A surface already taken for the other role is not offered again.
        /// </summary>
        public static string Pick(bool existing, string saved, IReadOnlyCollection<string> surfaces, string taken = null)
        {
            if (!string.IsNullOrWhiteSpace(saved))
            {
                string exact = surfaces.FirstOrDefault(n => n.Equals(saved.Trim(), StringComparison.OrdinalIgnoreCase));
                if (exact != null) return exact;
            }
            var words = existing ? Existing : Proposed;
            var other = existing ? Proposed : Existing;
            return surfaces
                .Where(n => taken == null || !n.Equals(taken, StringComparison.OrdinalIgnoreCase))
                .Select(n => (n, score: Score(n, words) - Score(n, other)))
                .Where(x => x.score > 0)
                .OrderByDescending(x => x.score).ThenBy(x => x.n.Length).ThenBy(x => x.n, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.n).FirstOrDefault();
        }

        private static int Score(string name, (string word, int score)[] words)
        {
            var tokens = new HashSet<string>(name.ToUpperInvariant().Split(new[] { ' ', '_', '-', '.', '(', ')' }, StringSplitOptions.RemoveEmptyEntries));
            return words.Where(w => tokens.Contains(w.word)).Select(w => w.score).DefaultIfEmpty(0).Max();
        }
    }
}
