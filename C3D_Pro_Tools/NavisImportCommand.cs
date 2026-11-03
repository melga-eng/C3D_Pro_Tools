using Autodesk.AutoCAD.Runtime;
using AcadCoreApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(C3DProTools.NavisImportCommand))]

namespace C3DProTools
{
    public class NavisImportCommand
    {
        /// <summary>Opens Navisworks Import. The window is modeless and reads the live Navisworks selection over the local link.</summary>
        [CommandMethod("ImportNavisPipe")]
        public void RunImport()
        {
            try
            {
                FormNavisImport.ShowTool();
            }
            catch (System.Exception ex)
            {
                AcadCoreApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage($"\nNavisworks Import error: {ex.Message}");
            }
        }
    }
}
