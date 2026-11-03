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
    }
}
