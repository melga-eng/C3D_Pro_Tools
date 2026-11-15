using Autodesk.AutoCAD.Runtime;
using AcadCoreApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(C3DProTools.StyleManagerCommand))]

namespace C3DProTools
{
    public class StyleManagerCommand
    {
        /// <summary>Opens the Style Manager. The window is modeless, so it stays open while you work and follows the active drawing.</summary>
        [CommandMethod("StyleManager")]
        public void RunStyleManager()
        {
            try
            {
                FormStyleManager.ShowTool();
            }
            catch (System.Exception ex)
            {
                AcadCoreApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage($"\nStyle Manager error: {ex.Message}");
            }
        }

        /// <summary>The objects the Style Manager asked to select; taken by <see cref="SelectStyleUsers"/>.</summary>
        internal static Autodesk.AutoCAD.DatabaseServices.ObjectId[] PendingSelection;

        /// <summary>Selects the objects the Style Manager handed over (a selection made inside a command shows with grips).</summary>
        [CommandMethod("C3DPROSELECTSTYLEUSERS", CommandFlags.Redraw | CommandFlags.NoHistory)]
        public void SelectStyleUsers()
        {
            var ids = PendingSelection;
            PendingSelection = null;
            var ed = AcadCoreApp.DocumentManager.MdiActiveDocument?.Editor;
            if (ed == null || ids == null || ids.Length == 0) return;
            ed.SetImpliedSelection(ids);
        }
    }
}
