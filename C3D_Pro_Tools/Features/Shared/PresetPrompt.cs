using Autodesk.AutoCAD.EditorInput;

namespace C3DProTools
{
    /// <summary>Command-line questions shared by the preset-run commands (C3DPROPV, C3DPRONET, C3DPROLBL).</summary>
    internal static class PresetPrompt
    {
        /// <summary>
        /// Before a step that changes the drawing, shows the note (for example a name filter that was widened) and asks to go
        /// on; Enter means Yes. A step that only reads goes on without asking (the note is in its report).
        /// </summary>
        public static bool Continue(Editor ed, string note, bool writes)
        {
            if (!writes) return true;
            ed.WriteMessage($"\n{note}");
            var kw = new PromptKeywordOptions("\nContinue") { AllowNone = true };
            kw.Keywords.Add("Yes");
            kw.Keywords.Add("No");
            kw.Keywords.Default = "Yes";
            PromptResult r = ed.GetKeywords(kw);
            if (r.Status == PromptStatus.None) return true;
            return r.Status == PromptStatus.OK && r.StringResult == "Yes";
        }
    }
}
