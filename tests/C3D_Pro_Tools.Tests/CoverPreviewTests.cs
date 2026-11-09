using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace C3DProTools.Tests
{
    /// <summary>
    /// The Cover Optimizer preview crashed with a NullReferenceException when a route had nothing on either side (a new path
    /// with no layout has no current pipes and no proposal). The form must open, and draw, with missing lists, rows or options.
    /// </summary>
    public class CoverPreviewTests
    {
        private static void Render(Form f)
        {
            using (Bitmap bmp = new Bitmap(800, 560)) f.DrawToBitmap(bmp, new Rectangle(0, 0, 800, 560));
        }

        [Fact]
        public void Preview_opens_with_no_rows_on_either_side()
        {
            // The case that crashed: existing and proposed both null (and no options, as when reading the route stopped early).
            using (FormCoverPreview f = new FormCoverPreview("t", "s", null, null, null, null, canApply: false))
                Render(f);
        }

        [Fact]
        public void Null_rows_and_rows_without_a_pipe_are_left_out()
        {
            List<CoverRow> rows = new List<CoverRow>
            {
                null,
                new CoverRow { Pipe = null, Status = "?" },
                new CoverRow { Pipe = new ProfilePipe { Name = "P1", S0 = 0, S1 = 10, Z0 = 5, Z1 = 4.9 }, Status = "OK" },
            };
            using (FormCoverPreview f = new FormCoverPreview("t", "s", null, new CoverOptions(), rows, null, canApply: true))
            {
                ListView list = FindList(f);
                Assert.NotNull(list);
                ListViewItem only = Assert.Single(list.Items.Cast<ListViewItem>());
                Assert.Equal("P1", only.SubItems[1].Text);
                Render(f);
            }
        }

        private static ListView FindList(Control c)
        {
            foreach (Control child in c.Controls)
            {
                if (child is ListView lv) return lv;
                ListView inner = FindList(child);
                if (inner != null) return inner;
            }
            return null;
        }
    }
}
