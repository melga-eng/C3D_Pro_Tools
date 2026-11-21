using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Autodesk.AutoCAD.Runtime;
using AcadUiApp = Autodesk.AutoCAD.ApplicationServices.Application;
using Label = System.Windows.Forms.Label;
using Exception = System.Exception;

[assembly: CommandClass(typeof(C3DProTools.AboutCommands))]

namespace C3DProTools
{
    /// <summary>About (version, what is in it, where the log is) and Feedback, in both editions.</summary>
    public class AboutCommands
    {
        [CommandMethod("C3DPRO", "C3DPROABOUT", CommandFlags.Modal)]
        public void About()
        {
            using (Form f = new Form
            {
                Text = Edition.Name + " - About", Font = StyleUi.Base, BackColor = StyleUi.Surface, StartPosition = FormStartPosition.CenterScreen,
                ClientSize = new Size(520, 400), MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.FixedDialog
            })
            {
                var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(20, 16, 20, 8) };
                body.Controls.Add(new Label { Text = Edition.Name, Font = StyleUi.Title, AutoSize = true, Margin = new Padding(0, 0, 0, 2) });
                body.Controls.Add(new Label { Text = $"Version {Edition.Version}  ·  Civil 3D 2026 (update 2026.2.2 or later)", ForeColor = StyleUi.Muted, AutoSize = true, Margin = new Padding(0, 0, 0, 12) });
                body.Controls.Add(new Label
                {
                    AutoSize = true, MaximumSize = new Size(480, 0), Margin = new Padding(0, 0, 0, 10),
                    Text = Edition.IsPublic
                        ? "Navisworks Import: pipes, duct banks and structures from a Navisworks Manage 2026 selection as Civil 3D parts, into your networks.\n\n" +
                          "Surface from Solids: a TIN surface from the top or bottom of 3D solids and meshes.\n\n" +
                          "Rebuild Network: a network you cannot edit (a data-shortcut reference whose source is gone) drawn again as an ordinary one."
                        : "Every C3D Pro tool (full edition)."
                });
                body.Controls.Add(new Label
                {
                    AutoSize = true, MaximumSize = new Size(480, 0), ForeColor = StyleUi.Muted, Margin = new Padding(0, 0, 0, 10),
                    Text = "Free to use, as is, without warranty. Every change is one UNDO step: check the result before you save.\nLog file (attach it to a problem report): " + C3DProLogger.LogPath
                });
                var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft, Padding = new Padding(12, 6, 12, 12) };
                Button close = StyleUi.Secondary("Close", (s, e) => f.Close());
                buttons.Controls.Add(close);
                buttons.Controls.Add(StyleUi.Primary("Send feedback", (s, e) => OpenFeedback(f)));
                buttons.Controls.Add(StyleUi.Secondary("Open log folder", (s, e) => Open(System.IO.Path.GetDirectoryName(C3DProLogger.LogPath), f)));
                f.CancelButton = close;
                f.Controls.Add(body);
                f.Controls.Add(buttons);
                AcadUiApp.ShowModalDialog(f);
            }
        }

        [CommandMethod("C3DPRO", "C3DPROFEEDBACK", CommandFlags.Modal)]
        public void Feedback() => OpenFeedback(null);

        private static void OpenFeedback(IWin32Window owner)
        {
            if (string.IsNullOrEmpty(Edition.FeedbackUrl))
            {
                MessageBox.Show(owner, "The feedback form is not set up in this build yet.", Edition.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Open(Edition.FeedbackUrl, owner);
        }

        private static void Open(string target, IWin32Window owner)
        {
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(owner, "Could not open " + target + ": " + ex.Message, Edition.Name); }
        }
    }
}
