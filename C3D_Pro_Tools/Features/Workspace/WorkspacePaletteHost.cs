using System;
using Autodesk.AutoCAD.Windows;
using C3DProTools.Features.Sheets.Civil3D;
using C3DProTools.Features.Sheets.Contracts;
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

        private static PaletteSet _demoPaletteSet;
        private static WorkspaceView _demoWorkspaceView;

        private static readonly Guid WorkspacePaletteGuid = new Guid("4A7E9C12-88F4-49D1-B127-99E8F3D12A45");
        private static readonly Guid WorkspaceDemoPaletteGuid = new Guid("4A7E9C12-88F4-49D1-B127-99E8F3D12A46");

        public static void Show()
        {
            if (_paletteSet == null)
            {
                // An exception while the palette is built (a broken WPF binding, say) is reported and logged, and the palette is
                // not shown: left unhandled it ends Civil 3D with a fatal error, as a read-only two-way binding did on 2026-11-10.
                PaletteSet set = null;
                try
                {
                    set = new PaletteSet("C3D Pro Workspace", WorkspacePaletteGuid)
                    {
                        Style = PaletteSetStyles.ShowAutoHideButton |
                                PaletteSetStyles.ShowCloseButton |
                                PaletteSetStyles.ShowPropertiesMenu,
                        MinimumSize = new System.Drawing.Size(440, 560),
                        Dock = DockSides.None
                    };

                    var realService = new SheetProductionService();
                    _workspaceView = new WorkspaceView(realService);
                    set.AddVisual("Workspace", _workspaceView);
                    _paletteSet = set;
                }
                catch (Exception ex)
                {
                    C3DProLogger.Error("Workspace palette could not be built", ex);
                    try { set?.Dispose(); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                    _workspaceView = null;
                    Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument?.Editor
                        .WriteMessage($"\nThe C3D Pro workspace could not be opened: {ex.Message}\nThe details are in the C3D Pro log.\n");
                    return;
                }
            }

            _paletteSet.Visible = true;
        }

        public static void ShowDemo()
        {
            if (_demoPaletteSet == null)
            {
                PaletteSet set = null;
                try
                {
                    set = new PaletteSet("C3D Pro Workspace (Demo)", WorkspaceDemoPaletteGuid)
                    {
                        Style = PaletteSetStyles.ShowAutoHideButton |
                                PaletteSetStyles.ShowCloseButton |
                                PaletteSetStyles.ShowPropertiesMenu,
                        MinimumSize = new System.Drawing.Size(440, 560),
                        Dock = DockSides.None
                    };

                    var syntheticService = new SyntheticSheetProduction();
                    _demoWorkspaceView = new WorkspaceView(syntheticService);
                    set.AddVisual("Workspace (Demo)", _demoWorkspaceView);
                    _demoPaletteSet = set;
                }
                catch (Exception ex)
                {
                    C3DProLogger.Error("Workspace demo palette could not be built", ex);
                    try { set?.Dispose(); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                    _demoWorkspaceView = null;
                    Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument?.Editor
                        .WriteMessage($"\nThe C3D Pro workspace demo could not be opened: {ex.Message}\nThe details are in the C3D Pro log.\n");
                    return;
                }
            }

            _demoPaletteSet.Visible = true;
        }

        public static void Toggle()
        {
            if (_paletteSet == null) Show();
            else _paletteSet.Visible = !_paletteSet.Visible;
        }

        public static void ToggleDemo()
        {
            if (_demoPaletteSet == null) ShowDemo();
            else _demoPaletteSet.Visible = !_demoPaletteSet.Visible;
        }
    }
}
