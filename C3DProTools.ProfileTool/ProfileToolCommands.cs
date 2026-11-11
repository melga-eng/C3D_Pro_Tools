using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(C3DProTools.ProfileTool.ProfileToolCommands))]

namespace C3DProTools.ProfileTool
{
    public class ProfileToolCommands
    {
        /// <summary>Profile Manager: list profiles by surface, delete by surface, create surface profiles on all alignments.</summary>
        [CommandMethod("C3DPRO", "PROFILEMGR", CommandFlags.Modal)]
        public void ProfileManager()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var civil = CivilDocument.GetCivilDocument(doc.Database);
            using var form = new ProfileManagerForm(doc, civil);
            AcApp.ShowModalDialog(form);
        }

        /// <summary>Profile views: list/filter/sort, fit TOP profiles to duct tops, create missing TOP profiles.</summary>
        [CommandMethod("C3DPRO", "PVMGR", CommandFlags.Modal)]
        public void ProfileViewManager()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var civil = CivilDocument.GetCivilDocument(doc.Database);
            using var form = new ProfileViewManagerForm(doc, civil);
            AcApp.ShowModalDialog(form);
        }
    }
}
