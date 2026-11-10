using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using C3DProTools.Features.Sheets.UI;
using C3DProTools.Features.Workspace;

namespace C3DProTools.Tests.Sheets
{
    /// <summary>
    /// Builds the whole workspace view off screen against the synthetic service, as the palette does, so a view that throws
    /// while it is built fails here and not inside Civil 3D. On 2026-11-10 a two-way binding to a read-only view-model property
    /// threw inside PaletteSet.AddVisual and ended Civil 3D with a fatal error.
    /// </summary>
    public class WorkspaceSmokeTests
    {
        private static void OnSta(Action action)
        {
            ExceptionDispatchInfo failure = null;
            Thread t = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            failure?.Throw();
        }

        [Fact]
        public void TheWorkspaceView_BuildsAndLaysOut_WithoutThrowing()
        {
            OnSta(() =>
            {
                WorkspaceView view = new WorkspaceView(new SyntheticSheetProduction());
                Window window = new Window
                {
                    Content = view, Width = 480, Height = 720, ShowInTaskbar = false, ShowActivated = false,
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, WindowStyle = WindowStyle.None
                };
                try
                {
                    window.Show();              // binds and lays out the whole tree, as AddVisual does
                    window.UpdateLayout();
                    Assert.True(view.IsLoaded);
                }
                finally { window.Close(); }
            });
        }
    }
}
