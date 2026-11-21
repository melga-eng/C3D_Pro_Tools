using Autodesk.AutoCAD.Runtime;
using AcadCoreApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

#if !PUBLIC   // full edition only (see Edition.cs)
[assembly: CommandClass(typeof(C3DProTools.AnnotationCommands))]
#endif

namespace C3DProTools
{
    public class AnnotationCommands
    {
        /// <summary>Opens the Annotation Studio. The window is modeless, so it stays open while you move between sheets.</summary>
        [CommandMethod("AnnotationStudio")]
        public void RunAnnotationStudio()
        {
            try
            {
                FormAnnotationStudio.ShowTool();
            }
            catch (System.Exception ex)
            {
                AcadCoreApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage($"\nAnnotation Studio error: {ex.Message}");
            }
        }

        /// <summary>Opens the Sheet Manager (modeless): sheets, title blocks, revisions, viewports, checks and PDF.</summary>
        [CommandMethod("SheetManager")]
        public void RunSheetManager()
        {
            try
            {
                FormSheetManager.ShowTool();
            }
            catch (System.Exception ex)
            {
                AcadCoreApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage($"\nSheet Manager error: {ex.Message}");
            }
        }
    }
}
