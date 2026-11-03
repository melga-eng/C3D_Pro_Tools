using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;

namespace C3DProTools.Tests
{
    /// <summary>
    /// Method A (any-size families) and Method B (adding sizes to stock families) on small catalog files written to a
    /// temporary folder per test; nothing touches a real Civil 3D catalog.
    /// </summary>
    public sealed class NavisCatalogEditorTests : IDisposable
    {
        private const string BoxGuid = "E0156B07-4726-443F-88E5-F725B7B32298";
        private const string XLink = "http://www.w3.org/1999/xlink";
        private readonly string _root = Path.Combine(Path.GetTempPath(), "c3dpro-tests", Guid.NewGuid().ToString("N"));

        public NavisCatalogEditorTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try
            {
                // Copies of a read-only file (the .bak of a read-only catalog index) are read-only too.
                foreach (string f in Directory.GetFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(_root, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private const string FamilyTable =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
            "<Table xmlns:xlink=\"http://www.w3.org/1999/xlink\">\n" +
            "  <ColumnConst context=\"Catalog_PartName\" desc=\"Part Name\">BaseBox</ColumnConst>\n" +
            "  <ColumnConst context=\"Catalog_PartDesc\" desc=\"Part Description\">Base box culvert</ColumnConst>\n" +
            "  <ColumnConst context=\"Catalog_PartID\" desc=\"Part ID\">" + BoxGuid + "</ColumnConst>\n" +
            "  <ColumnConstView context=\"Catalog_View\"><Recipe>BaseBox.dwg</Recipe><URL xlink:href=\"BaseBox.bmp\" /></ColumnConstView>\n" +
            "  <ColumnUnique context=\"Catalog_Unique\"><RowUnique id=\"r0\">AAA</RowUnique><RowUnique id=\"r1\">BBB</RowUnique></ColumnUnique>\n" +
            "  <Column context=\"PipeInnerWidth\" desc=\"Inner Width\"><Row id=\"r0\">1000.0000</Row><Row id=\"r1\">1500.0000</Row></Column>\n" +
            "  <Column context=\"PipeInnerHeight\" desc=\"Inner Height\"><Row id=\"r0\">1000.0000</Row><Row id=\"r1\">1000.0000</Row></Column>\n" +
            "  <ColumnConstList context=\"WallThickness\" desc=\"Wall Thickness\"><Item id=\"i0\">150.0000</Item></ColumnConstList>\n" +
            "  <ColumnRangeList context=\"PipeLength\" desc=\"Length\"><Item id=\"i0\" rangeVal=\"Min\">1000</Item><Item id=\"i1\" rangeVal=\"Max\">5000</Item><Item id=\"i2\" rangeVal=\"Default\">2000</Item></ColumnRangeList>\n" +
            "  <ColumnConst context=\"FrameHeight\" desc=\"Frame Height\">100</ColumnConst>\n" +
            "  <ColumnCalc context=\"PipeOuterWidth\" desc=\"Outer Width\">PipeInnerWidth+2*WallThickness</ColumnCalc>\n" +
            "</Table>\n";

        private const string CatalogIndex =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
            "<PartCatalog xmlns:xlink=\"http://www.w3.org/1999/xlink\">\n" +
            "  <Chapter name=\"Box\">Box<Desc>Box culverts</Desc>\n" +
            "    <Part id=\"" + BoxGuid + "\" name=\"BaseBox\"><Desc>Base box culvert</Desc>\n" +
            "      <TableRef><URL xlink:href=\"Box\\BaseBox.xml\" xlink:title=\"Base box culvert\" /></TableRef>\n" +
            "      <Preview><URL xlink:href=\"Box\\BaseBox.bmp\" /></Preview>\n" +
            "    </Part>\n" +
            "  </Chapter>\n" +
            "</PartCatalog>\n";

        private string Family()
        {
            string path = Path.Combine(_root, "BaseBox.xml");
            File.WriteAllText(path, FamilyTable);
            return path;
        }

        private CatalogInfo Catalog()
        {
            string box = Path.Combine(_root, "Box");
            Directory.CreateDirectory(box);
            File.WriteAllText(Path.Combine(box, "BaseBox.xml"), FamilyTable);
            File.WriteAllText(Path.Combine(box, "BaseBox.dwg"), "recipe");
            File.WriteAllText(Path.Combine(box, "BaseBox.bmp"), "preview");
            string apc = Path.Combine(_root, "Test Catalog.apc");
            File.WriteAllText(apc, CatalogIndex);
            CatalogInfo info = new CatalogInfo { Guid = "{TEST}", ApcPath = apc, Name = "Test Catalog" };
            info.PartIds.Add(BoxGuid);
            return info;
        }

        private static string[] Backups(string path) => Directory.GetFiles(Path.GetDirectoryName(path), Path.GetFileName(path) + ".c3dpro-*.bak");

        // ---------------- Reading ----------------

        [Fact]
        public void KindOf_ReadsEachColumnType()
        {
            string xml = Family();
            Assert.Equal(NavisCatalogEditor.ParamKind.Table, NavisCatalogEditor.KindOf(xml, "PipeInnerWidth"));
            Assert.Equal(NavisCatalogEditor.ParamKind.List, NavisCatalogEditor.KindOf(xml, "WallThickness"));
            Assert.Equal(NavisCatalogEditor.ParamKind.Range, NavisCatalogEditor.KindOf(xml, "PipeLength"));
            Assert.Equal(NavisCatalogEditor.ParamKind.Constant, NavisCatalogEditor.KindOf(xml, "FrameHeight"));
            Assert.Equal(NavisCatalogEditor.ParamKind.Calculated, NavisCatalogEditor.KindOf(xml, "PipeOuterWidth"));
            Assert.Equal(NavisCatalogEditor.ParamKind.Missing, NavisCatalogEditor.KindOf(xml, "NoSuchContext"));
            Assert.Equal(new[] { "PipeInnerWidth", "PipeInnerHeight" }, NavisCatalogEditor.TableContexts(xml));
        }

        [Fact]
        public void TableKeyConflict_SameKeyWithAnotherValue()
        {
            string xml = Family();
            Assert.True(NavisCatalogEditor.TableKeyConflict(xml, new Dictionary<string, double> { ["PipeInnerWidth"] = 1500, ["PipeInnerHeight"] = 1200 }, "PipeInnerHeight"));
            Assert.False(NavisCatalogEditor.TableKeyConflict(xml, new Dictionary<string, double> { ["PipeInnerWidth"] = 2000, ["PipeInnerHeight"] = 1200 }, "PipeInnerHeight"));
        }

        [Fact]
        public void ActiveCatalog_PrefersTheExactFamilySet()
        {
            CatalogInfo wide = new CatalogInfo { Name = "Wide" }, exact = new CatalogInfo { Name = "Exact" };
            foreach (string id in new[] { "x", "y", "z" }) wide.PartIds.Add(id);
            foreach (string id in new[] { "x", "y" }) exact.PartIds.Add(id);
            List<CatalogInfo> all = new List<CatalogInfo> { wide, exact };

            Assert.Same(exact, NavisCatalogEditor.ActiveCatalog(all, new[] { "X", "Y" }));
            Assert.Null(NavisCatalogEditor.ActiveCatalog(all, new string[0]));
        }

        [Fact]
        public void FamilyXml_FollowsTheTableReference()
        {
            CatalogInfo cat = Catalog();
            Assert.Equal(Path.Combine(_root, "Box", "BaseBox.xml"), NavisCatalogEditor.FamilyXml(cat, BoxGuid.ToLowerInvariant()));
            Assert.Null(NavisCatalogEditor.FamilyXml(cat, "00000000-0000-0000-0000-000000000000"));
        }

        // ---------------- Method B ----------------

        [Fact]
        public void AddSize_NewTableRow_WritesRowUniqueIdAndOneBackup()
        {
            string xml = Family();
            List<string> changes = NavisCatalogEditor.AddSize(xml, new Dictionary<string, double> { ["PipeInnerWidth"] = 2000, ["PipeInnerHeight"] = 1000 });

            Assert.Equal(new[] { "table row Inner Width 2000.0000 × Inner Height 1000.0000" }, changes);
            XmlDocument doc = new XmlDocument();
            doc.Load(xml);
            Assert.Equal("2000.0000", doc.SelectSingleNode("//Column[@context='PipeInnerWidth']/Row[@id='r2']")?.InnerText);
            Assert.Equal("1000.0000", doc.SelectSingleNode("//Column[@context='PipeInnerHeight']/Row[@id='r2']")?.InnerText);
            Assert.NotNull(doc.SelectSingleNode("//ColumnUnique/RowUnique[@id='r2']"));
            Assert.Single(Backups(xml));
            Assert.False(File.Exists(xml + ".tmp"));
        }

        [Fact]
        public void AddSize_ExistingRow_ChangesNothing()
        {
            string xml = Family();
            List<string> changes = NavisCatalogEditor.AddSize(xml, new Dictionary<string, double> { ["PipeInnerWidth"] = 1500, ["PipeInnerHeight"] = 1000 });

            Assert.Empty(changes);
            Assert.Equal(FamilyTable, File.ReadAllText(xml));
            Assert.Empty(Backups(xml));
        }

        [Fact]
        public void AddSize_TableRowNeedsEveryColumn()
        {
            string xml = Family();
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
                NavisCatalogEditor.AddSize(xml, new Dictionary<string, double> { ["PipeInnerWidth"] = 2000 }));
            Assert.Contains("Inner Height", ex.Message);
            Assert.Equal(FamilyTable, File.ReadAllText(xml));
        }

        [Fact]
        public void AddSize_ListGetsANewItem_UnlessWithinHalfAMicron()
        {
            string xml = Family();
            Assert.Empty(NavisCatalogEditor.AddSize(xml, new Dictionary<string, double> { ["WallThickness"] = 150.0003 }));
            Assert.Equal(new[] { "Wall Thickness 200.0000" }, NavisCatalogEditor.AddSize(xml, new Dictionary<string, double> { ["WallThickness"] = 200 }));

            XmlDocument doc = new XmlDocument();
            doc.Load(xml);
            Assert.Equal("200.0000", doc.SelectSingleNode("//ColumnConstList[@context='WallThickness']/Item[@id='i1']")?.InnerText);
        }

        [Fact]
        public void AddSize_RangeIsWidenedBothWays()
        {
            string xml = Family();
            Assert.Equal(new[] { "Length range max 6000.0000" }, NavisCatalogEditor.AddSize(xml, new Dictionary<string, double> { ["PipeLength"] = 6000 }));
            Assert.Equal(new[] { "Length range min 500.0000" }, NavisCatalogEditor.AddSize(xml, new Dictionary<string, double> { ["PipeLength"] = 500 }));
            Assert.Empty(NavisCatalogEditor.AddSize(xml, new Dictionary<string, double> { ["PipeLength"] = 3000 }));
        }

        [Fact]
        public void AddSize_DifferentFixedValue_Throws()
        {
            string xml = Family();
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
                NavisCatalogEditor.AddSize(xml, new Dictionary<string, double> { ["FrameHeight"] = 120 }));
            Assert.Contains("fixed at 100", ex.Message);
        }

        // ---------------- Method A ----------------

        [Fact]
        public void EnsureAnySizeFamily_CopiesTheBaseFamilyWithRangesAndListsIt()
        {
            CatalogInfo cat = Catalog();
            AnySizeFamily box = NavisCatalogEditor.AnySize.Single(f => f.Key == "box");
            List<string> log = new List<string>();

            string xmlPath = NavisCatalogEditor.EnsureAnySizeFamily(cat, box, log);

            string folder = Path.Combine(_root, NavisCatalogEditor.ChapterName);
            Assert.Equal(Path.Combine(folder, box.PartName + ".xml"), xmlPath);
            Assert.True(File.Exists(Path.Combine(folder, box.PartName + ".dwg")));
            Assert.True(File.Exists(Path.Combine(folder, box.PartName + ".bmp")));
            Assert.Equal(new[] { $"Added \"{box.Description}\" to the Test Catalog catalog." }, log);

            XmlDocument family = new XmlDocument();
            family.Load(xmlPath);
            foreach (string context in box.RangeContexts)
                Assert.NotNull(family.SelectSingleNode($"/Table/ColumnRangeList[@context='{context}']"));
            Assert.Null(family.SelectSingleNode("/Table/Column"));
            Assert.Null(family.SelectSingleNode("/Table/ColumnUnique"));
            Assert.Equal(box.Guid, family.SelectSingleNode("/Table/ColumnConst[@context='Catalog_PartID']")?.InnerText);
            Assert.Equal(box.PartName + ".dwg", family.SelectSingleNode("//Recipe")?.InnerText);
            Assert.Equal("0.0000", family.SelectSingleNode("/Table/ColumnRangeList[@context='WallThickness']/Item[@rangeVal='Min']")?.InnerText);
            Assert.Equal("150.0000", family.SelectSingleNode("/Table/ColumnRangeList[@context='WallThickness']/Item[@rangeVal='Default']")?.InnerText);

            XmlDocument index = new XmlDocument();
            index.Load(cat.ApcPath);
            XmlNamespaceManager ns = new XmlNamespaceManager(index.NameTable);
            ns.AddNamespace("xlink", XLink);
            XmlElement part = (XmlElement)index.SelectSingleNode($"//Chapter[@name='{NavisCatalogEditor.ChapterName}']/Part[@id='{box.Guid}']");
            Assert.NotNull(part);
            Assert.Equal(NavisCatalogEditor.ChapterName + "\\" + box.PartName + ".xml", part.SelectSingleNode("TableRef/URL/@xlink:href", ns)?.Value);
            Assert.Contains(box.Guid, cat.PartIds);
            Assert.Single(Backups(cat.ApcPath));
        }

        [Fact]
        public void EnsureAnySizeFamily_SecondCallChangesNothing()
        {
            CatalogInfo cat = Catalog();
            AnySizeFamily box = NavisCatalogEditor.AnySize.Single(f => f.Key == "box");
            NavisCatalogEditor.EnsureAnySizeFamily(cat, box, new List<string>());
            string index = File.ReadAllText(cat.ApcPath);

            List<string> log = new List<string>();
            NavisCatalogEditor.EnsureAnySizeFamily(cat, box, log);

            Assert.Empty(log);
            Assert.Equal(index, File.ReadAllText(cat.ApcPath));
        }

        // ---------------- Safety: locking, atomic writes, rollback ----------------

        [Fact]
        public void Journal_Rollback_PutsChangedFilesBackAndRemovesNewOnes()
        {
            string xml = Family();
            string created = Path.Combine(_root, "new.xml");
            CatalogJournal journal = new CatalogJournal();

            NavisCatalogEditor.AddSize(xml, new Dictionary<string, double> { ["WallThickness"] = 200 }, journal);
            journal.Record(created);
            File.WriteAllText(created, "<x/>");
            Assert.NotEqual(FamilyTable, File.ReadAllText(xml));

            Assert.Empty(journal.Rollback());
            Assert.Equal(FamilyTable, File.ReadAllText(xml));
            Assert.False(File.Exists(created));
            Assert.True(journal.IsEmpty);
        }

        [Fact]
        public void Lock_IsExclusiveUntilReleased()
        {
            using (CatalogLock.Acquire(_root))
            {
                IOException ex = Assert.Throws<IOException>(() => CatalogLock.Acquire(_root, 200));
                Assert.Contains("being changed by someone else", ex.Message);
            }
            using (CatalogLock.Acquire(_root, 200)) { }
            Assert.False(File.Exists(Path.Combine(_root, CatalogLock.FileName)), "the lock file goes with the last holder");
        }

        [Fact]
        public async System.Threading.Tasks.Task AddSize_WaitsForTheLockThenWrites()
        {
            string xml = Family();
            IDisposable held = CatalogLock.Acquire(_root);
            System.Threading.Tasks.Task<List<string>> adding = System.Threading.Tasks.Task.Run(() =>
                NavisCatalogEditor.AddSize(xml, new Dictionary<string, double> { ["WallThickness"] = 250 }));
            await System.Threading.Tasks.Task.Delay(300);
            Assert.False(adding.IsCompleted, "the write waits while another holds the folder");
            held.Dispose();
            Assert.Equal(new[] { "Wall Thickness 250.0000" }, await adding);
        }

        [Fact]
        public void EnsureAnySizeFamily_IndexCannotBeWritten_LeavesNoFamilyFilesBehind()
        {
            CatalogInfo cat = Catalog();
            AnySizeFamily box = NavisCatalogEditor.AnySize.Single(f => f.Key == "box");
            File.SetAttributes(cat.ApcPath, FileAttributes.ReadOnly);
            try
            {
                Assert.ThrowsAny<Exception>(() => NavisCatalogEditor.EnsureAnySizeFamily(cat, box, new List<string>()));
            }
            finally { File.SetAttributes(cat.ApcPath, FileAttributes.Normal); }

            string folder = Path.Combine(_root, NavisCatalogEditor.ChapterName);
            Assert.False(File.Exists(Path.Combine(folder, box.PartName + ".xml")));
            Assert.False(File.Exists(Path.Combine(folder, box.PartName + ".dwg")));
            Assert.False(File.Exists(Path.Combine(folder, box.PartName + ".bmp")));
            Assert.Equal(CatalogIndex, File.ReadAllText(cat.ApcPath));
            Assert.DoesNotContain(box.Guid, cat.PartIds);
        }

        [Fact]
        public void EnsureAnySizeFamily_BaseFamilyMissing_IsLoggedNotThrown()
        {
            CatalogInfo cat = Catalog();
            AnySizeFamily cyl = NavisCatalogEditor.AnySize.Single(f => f.Key == "cyl");
            List<string> log = new List<string>();

            Assert.Null(NavisCatalogEditor.EnsureAnySizeFamily(cat, cyl, log));
            Assert.Contains("its base family is not in the Test Catalog catalog", Assert.Single(log));
        }
    }
}
