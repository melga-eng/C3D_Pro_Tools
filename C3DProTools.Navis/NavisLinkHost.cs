using System;
using System.Diagnostics;
using System.IO.Pipes;
using System.Threading;
using System.Windows.Forms;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Plugins;
using C3DProTools.NavisLink;
using NavisApp = Autodesk.Navisworks.Api.Application;

namespace C3DProTools.Navis
{
    /// <summary>
    /// Loads with Navisworks and answers Civil 3D on a local named pipe: "what is selected" (properties and geometry) and
    /// "select these items". The pipe only accepts connections from this computer.
    /// </summary>
    [Plugin("C3DProTools.NavisLink", "C3DPro", DisplayName = "C3D Pro Tools Link")]
    public class NavisLinkHost : EventWatcherPlugin
    {
        internal static string PipeName { get; private set; } = "";
        internal static int Served;
        internal static string LastError = "";

        private static Control _ui;
        private static Thread _thread;
        private static volatile bool _stopping;

        public override void OnLoaded()
        {
            if (_thread != null) return;
            // A control created now lives on the Navisworks main thread; the API may only be used from there.
            _ui = new Control();
            _ui.CreateControl();
            IntPtr force = _ui.Handle;
            PipeName = NavisLinkWire.PipePrefix + Process.GetCurrentProcess().Id;
            _stopping = false;
            _thread = new Thread(Serve) { IsBackground = true, Name = "C3D Pro Tools Link" };
            _thread.Start();
        }

        public override void OnUnloading()
        {
            _stopping = true;
            // Wake the waiting server so the thread can end.
            try { using (NamedPipeClientStream c = new NamedPipeClientStream(".", PipeName)) c.Connect(200); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
        }

        private static void Serve()
        {
            while (!_stopping)
            {
                try
                {
                    using (NamedPipeServerStream pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.None))
                    {
                        pipe.WaitForConnection();
                        if (_stopping) return;
                        NavisResponse response;
                        try
                        {
                            NavisRequest request = NavisLinkWire.Read<NavisRequest>(pipe);
                            response = (NavisResponse)_ui.Invoke(new Func<NavisResponse>(() => Handle(request)));
                        }
                        catch (Exception ex)
                        {
                            response = new NavisResponse { Ok = false, Error = NavisLinkWire.Describe(ex) };
                        }
                        // Civil 3D may have stopped waiting (timeout, window closed): the answer has no one to go to.
                        try { NavisLinkWire.Write(pipe, response); } catch (Exception ex) { C3DProLogger.Warn("Navis link: the answer could not be sent back to Civil 3D", ex); }
                        Served++;
                    }
                }
                catch (Exception ex)
                {
                    LastError = NavisLinkWire.Describe(ex);
                    Thread.Sleep(500);
                }
            }
        }

        private static NavisResponse Handle(NavisRequest request)
        {
            Document doc = NavisApp.ActiveDocument;
            if (doc == null || doc.IsClear) return new NavisResponse { Ok = false, Error = "No model is open in Navisworks." };
            switch (request.Command)
            {
                case "hello":
                    return new NavisResponse { Ok = true, Document = doc.FileName ?? "", Units = doc.Units.ToString(), SelectedCount = doc.CurrentSelection.SelectedItems.Count };
                case "selection":
                    return NavisExtractor.ReadSelection(doc);
                case "select":
                    return NavisExtractor.Select(doc, request.Guids);
                default:
                    return new NavisResponse { Ok = false, Error = "Unknown command: " + request.Command };
            }
        }
    }

    /// <summary>Add-ins tab button showing whether the link is running.</summary>
    [Plugin("C3DProTools.LinkStatus", "C3DPro", DisplayName = "Civil 3D Link", ToolTip = "Shows the C3D Pro Tools link to Civil 3D")]
    [AddInPlugin(AddInLocation.AddIn)]
    public class LinkStatusCommand : AddInPlugin
    {
        public override int Execute(params string[] parameters)
        {
            Document doc = NavisApp.ActiveDocument;
            int selected = doc == null ? 0 : doc.CurrentSelection.SelectedItems.Count;
            MessageBox.Show(
                "C3D Pro Tools link is running.\n\n" +
                $"Selected items: {selected}\nRequests answered: {NavisLinkHost.Served}\n" +
                (NavisLinkHost.LastError.Length > 0 ? $"Last error: {NavisLinkHost.LastError}\n" : "") +
                "\nIn Civil 3D, open Navisworks Import and click \"Get selection\".",
                "Civil 3D Link", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
    }
}
