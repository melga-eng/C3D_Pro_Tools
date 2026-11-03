using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcadCoreApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using AcadUiApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(C3DProTools.DrawingCleanerCommand))]

namespace C3DProTools
{
    public class DrawingCleanerCommand
    {
        [CommandMethod("C3DDrawingCleaner")]
        [CommandMethod("CleanSubmittalDrawing")]
        public void RunDrawingCleaner()
        {
            Document doc = AcadCoreApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;

            try
            {
                using (FormDrawingCleaner form = new FormDrawingCleaner())
                {
                    AcadUiApp.ShowModalDialog(form);
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nError running C3D Drawing Cleaner: {ex.Message}");
            }
        }
    }
}