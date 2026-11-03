using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using System.Collections.Generic;
using AcadCoreApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using AcadUiApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(C3DProTools.NetworkToolsCommands))]

namespace C3DProTools
{
    /// <summary>
    /// The network tools. The windows are modal: they hide while you pick in the drawing, and everything one session
    /// changes is a single UNDO. The old command names (Advanced Network Tools, Parts to Profile, Cover Optimizer) still work.
    /// </summary>
    public class NetworkToolsCommands
    {
        [CommandMethod("NetworkEditor")]
        public void RunNetworkEditor() => Open(() => new FormNetworkEditor(), "Network Editor");

        [CommandMethod("AdvancedNetworkTools")]
        public void RunAdvancedNetworkTools() => RunNetworkEditor();

        /// <summary>Rebuilds pipes that name a network but are missing from its list (cloned copies), as real members.</summary>
        [CommandMethod("RepairNetworkPipes")]
        public void RunRepairNetworkPipes()
        {
            try { NetworkRepair.Run(null); }
            catch (System.Exception ex) { AcadCoreApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage($"\nRepair network pipes error: {ex.Message}"); }
        }

        /// <summary>Profile View Tools: match views by example (by role), templates, followers, health check, split long views.</summary>
        [CommandMethod("ProfileViewTools")]
        public void RunProfileViewTools() => Open(() => new FormProfileViewTools(), "Profile View Tools");

        [CommandMethod("ProfileCoverDesigner")]
        public void RunProfileCoverDesigner() => Open(() => new FormRouteDesigner(-1), "Profile & Cover Designer");

        [CommandMethod("CoverOptimizer")]
        public void RunCoverOptimizer() => Open(() => new FormRouteDesigner(FormRouteDesigner.SCover), "Profile & Cover Designer");

        [CommandMethod("PartsToProfile")]
        public void RunPartsToProfile() => Open(() => new FormRouteDesigner(FormRouteDesigner.SProfiles), "Profile & Cover Designer");

        /// <summary>Lines selected beforehand are listed at once; the new pipes can go on to the Network Editor afterwards.</summary>
        [CommandMethod("NetworkFromFeatureLines", CommandFlags.UsePickSet)]
        public void RunNetworkFromFeatureLines()
        {
            List<ObjectId> handOff = null;
            Open(() =>
            {
                FormNetworkFromLines form = new FormNetworkFromLines();
                form.FormClosed += (s, e) => handOff = form.HandOff;
                return form;
            }, "Networks from Feature Lines");
            if (handOff != null && handOff.Count > 0) Open(() => new FormNetworkEditor(handOff), "Network Editor");
        }

        private static void Open(System.Func<System.Windows.Forms.Form> create, string name)
        {
            Document doc = AcadCoreApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            try
            {
                using (System.Windows.Forms.Form form = create()) AcadUiApp.ShowModalDialog(form);
            }
            catch (System.Exception ex)
            {
                doc.Editor.WriteMessage($"\nError in {name}: {ex.Message}");
            }
        }
    }
}
