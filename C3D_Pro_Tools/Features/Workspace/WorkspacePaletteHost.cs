using System;
using Autodesk.AutoCAD.Windows;
using C3DProTools.Features.Sheets.UI;

namespace C3DProTools.Features.Workspace
{
    /// <summary>
    /// Hosts the WPF WorkspaceView inside an AutoCAD / Civil 3D PaletteSet.
    /// This is the isolated boundary where Autodesk PaletteSet API is used.
    /// Follows AGENTS.md: pure UI views do not call the Autodesk API.
    /// </summary>
    internal static class WorkspacePaletteHost
    {
        private static PaletteSet _paletteSet;
        private static WorkspaceView _workspaceView;
        private static SyntheticSheetProduction _syntheticService;

        private static readonly Guid WorkspacePaletteGuid = new Guid("4A7E9C12-88F4-49D1-B127-99E8F3D12A45");

        public static void Show()
        {
            if (_paletteSet == null)
            {
                _paletteSet = new PaletteSet("C3D Pro Workspace", WorkspacePaletteGuid)
                {
                    Style = PaletteSetStyles.ShowAutoHideButton |
                            PaletteSetStyles.ShowCloseButton |
                            PaletteSetStyles.ShowPropertiesMenu,
                    MinimumSize = new System.Drawing.Size(440, 560),
                    Dock = DockSides.None
                };

                _syntheticService = new SyntheticSheetProduction();
                _workspaceView = new WorkspaceView(_syntheticService);
                _paletteSet.AddVisual("Workspace", _workspaceView);
            }

            _paletteSet.Visible = true;
        }

        public static void Toggle()
        {
            if (_paletteSet == null)
            {
                Show();
            }
            else
            {
                _paletteSet.Visible = !_paletteSet.Visible;
            }
        }
    }
}
