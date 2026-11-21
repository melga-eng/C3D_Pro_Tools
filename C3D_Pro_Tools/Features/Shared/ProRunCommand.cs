using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

#if !PUBLIC   // full edition only (see Edition.cs)
[assembly: CommandClass(typeof(C3DProTools.Features.Shared.ProRunCommand))]
#endif

namespace C3DProTools.Features.Shared
{
    public sealed class ProRunCommand
    {
        /// <summary>C3D Pro Run: the profile view, network and label steps in one window, settings saved in the drawing.</summary>
        [CommandMethod("C3DPRO", "C3DPRORUN", CommandFlags.Modal)]
        public void Open()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            using var form = new FormProRun(doc);
            AcApp.ShowModalDialog(form);
        }
    }
}
