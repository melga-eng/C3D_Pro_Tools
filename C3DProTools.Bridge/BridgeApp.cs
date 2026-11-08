using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: ExtensionApplication(typeof(C3DProTools.Bridge.BridgeApp))]
[assembly: CommandClass(typeof(C3DProTools.Bridge.BridgeCommands))]

namespace C3DProTools.Bridge
{
    /// <summary>
    /// Starts the MCP server when the DLL loads in Civil 3D and owns the hidden control used to run
    /// tool calls on the AutoCAD main thread (the AutoCAD API is not thread-safe).
    /// </summary>
    public class BridgeApp : IExtensionApplication
    {
        public const int DefaultPort = 47321;

        internal static McpServer Server { get; private set; }
        internal static MainThread Main { get; private set; }

        public void Initialize()
        {
            // Initialize runs on the main thread: the control's window handle belongs to it.
            Main = new MainThread();
            int port = DefaultPort;
            string env = Environment.GetEnvironmentVariable("C3D_BRIDGE_PORT");
            if (int.TryParse(env, out int p) && p > 1024 && p < 65536) port = p;
            Start(port);
        }

        public void Terminate()
        {
            Server?.Stop();
        }

        internal static void Start(int port)
        {
            Server?.Stop();
            Server = new McpServer(port, new BridgeTools(Main));
            try
            {
                Server.Start();
                Write($"\nC3D Bridge: listening for agents on {Server.Url}");
            }
            catch (System.Exception ex)
            {
                Write($"\nC3D Bridge: could not start on port {port}: {ex.Message}");
            }
        }

        internal static void Write(string message)
        {
            try { AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage(message); } catch { }
        }
    }

    public class BridgeCommands
    {
        /// <summary>C3DBRIDGE: shows the bridge status, or restarts / stops it.</summary>
        [CommandMethod("C3DBRIDGE", CommandFlags.Session)]
        public void BridgeCommand()
        {
            var ed = AcApp.DocumentManager.MdiActiveDocument?.Editor;
            if (ed == null) return;
            var s = BridgeApp.Server;
            ed.WriteMessage(s != null && s.IsRunning
                ? $"\nC3D Bridge is running on {s.Url} ({s.CallCount} tool calls, last: {s.LastCall ?? "none"})."
                : "\nC3D Bridge is stopped.");
            var opts = new Autodesk.AutoCAD.EditorInput.PromptKeywordOptions("\nBridge [Restart/Stop/eXit]", "Restart Stop eXit")
            {
                AllowNone = true
            };
            var res = ed.GetKeywords(opts);
            if (res.Status != Autodesk.AutoCAD.EditorInput.PromptStatus.OK) return;
            if (res.StringResult == "Restart") BridgeApp.Start(s?.Port ?? BridgeApp.DefaultPort);
            else if (res.StringResult == "Stop") { s?.Stop(); ed.WriteMessage("\nC3D Bridge stopped."); }
        }
    }

    /// <summary>Runs work on the AutoCAD main thread from the HTTP listener thread.</summary>
    internal sealed class MainThread
    {
        private readonly Control _invoker;

        public MainThread()
        {
            _invoker = new Control();
            _invoker.CreateControl();
            _ = _invoker.Handle;
        }

        private const int Queued = 0, Running = 1, Cancelled = 2;

        /// <summary>
        /// Queues work for the main thread and waits for it. If the wait times out before the work has started, the
        /// work is cancelled and never runs (so a write the agent was told failed cannot happen later). If it has
        /// already started, it cannot be stopped: the error says so, and the agent must re-read the drawing.
        /// </summary>
        public T Run<T>(Func<T> work, TimeSpan timeout)
        {
            int state = Queued;
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _invoker.BeginInvoke(new Action(() =>
            {
                if (Interlocked.CompareExchange(ref state, Running, Queued) != Queued) return; // timed out: skip
                try { tcs.SetResult(work()); }
                catch (System.Exception ex) { tcs.SetException(ex); }
            }));
            if (tcs.Task.Wait(timeout)) return tcs.Task.Result;

            if (Interlocked.CompareExchange(ref state, Cancelled, Queued) == Queued)
                throw new TimeoutException(
                    "Civil 3D did not start the request in time (a command or dialog may be active). " +
                    "It was cancelled and will not run: nothing was changed.");
            throw new TimeoutException(
                "The request started in Civil 3D but did not finish in time. It is still running and may still " +
                "change the drawing: wait, then re-read the drawing before retrying.");
        }

        /// <summary>
        /// Runs work on the main thread with the active document locked. When expectedDrawing is given, the work
        /// runs only if the active drawing at execution time matches it (full path, or file name).
        /// </summary>
        public T RunLocked<T>(Func<Document, T> work, TimeSpan timeout, string expectedDrawing = null)
        {
            return Run(() =>
            {
                Document doc = ActiveDocument(expectedDrawing);
                using (doc.LockDocument())
                {
                    return work(doc);
                }
            }, timeout);
        }

        /// <summary>The active document; when expectedDrawing is given, refuses if a different drawing is active.</summary>
        public static Document ActiveDocument(string expectedDrawing)
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument
                ?? throw new InvalidOperationException("No drawing is open in Civil 3D.");
            if (!string.IsNullOrWhiteSpace(expectedDrawing) && !SameDrawing(doc.Name, expectedDrawing))
                throw new InvalidOperationException(
                    $"The active drawing is '{doc.Name}', not '{expectedDrawing}'. Nothing was run. " +
                    "Ask the user to switch drawings, or pass the active drawing's name.");
            return doc;
        }

        private static bool SameDrawing(string actual, string expected)
        {
            expected = expected.Trim().Trim('"');
            if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) return true;
            // A bare file name matches only when the caller gave no folder.
            return expected.IndexOfAny(new[] { '\\', '/' }) < 0
                   && string.Equals(Path.GetFileName(actual), expected, StringComparison.OrdinalIgnoreCase);
        }
    }
}
