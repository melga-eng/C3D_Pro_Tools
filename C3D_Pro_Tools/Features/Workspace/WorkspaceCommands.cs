using Autodesk.AutoCAD.Runtime;

#if !PUBLIC   // full edition only (see Edition.cs)
[assembly: CommandClass(typeof(C3DProTools.Features.Workspace.WorkspaceCommands))]
#endif

namespace C3DProTools.Features.Workspace
{
    /// <summary>
    /// Command registration for the C3D Pro Workspace palette.
    /// Follows AGENTS.md: pure UI code is decoupled from AutoCAD APIs; this command entry point
    /// delegates to the isolated WorkspacePaletteHost.
    /// </summary>
    public class WorkspaceCommands
    {
        [CommandMethod("C3DProWorkspace")]
        public void ShowWorkspace()
        {
            WorkspacePaletteHost.Show();
        }

        [CommandMethod("C3DProWorkspaceDemo")]
        public void ShowWorkspaceDemo()
        {
            WorkspacePaletteHost.ShowDemo();
        }
    }
}
