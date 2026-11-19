using Autodesk.AutoCAD.DatabaseServices;
using C3DProTools.NavisLink;
using System;
using System.Collections.Generic;
using Document = Autodesk.AutoCAD.ApplicationServices.Document;

namespace C3DProTools
{
    /// <summary>
    /// The state of one Navisworks Import window apart from its controls: the rows read from Navisworks and what was planned,
    /// chosen and imported for them, the coordinate transform, and the drawing they are matched against. The form shows it.
    /// </summary>
    internal sealed class NavisImportSession
    {
        public NavisImportSettings Settings { get; } = NavisImportSettings.Load();
        /// <summary>The items read from Navisworks, in its order.</summary>
        public List<ImportCandidate> Rows { get; } = new List<ImportCandidate>();
        /// <summary>What importing each row would do (the preview), and what it did once imported.</summary>
        public Dictionary<ImportCandidate, RowOutcome> Plan { get; } = new Dictionary<ImportCandidate, RowOutcome>();
        public Dictionary<ImportCandidate, RowOutcome> Done { get; } = new Dictionary<ImportCandidate, RowOutcome>();
        public HashSet<ImportCandidate> Ticked { get; } = new HashSet<ImportCandidate>();
        /// <summary>Family and size the user picked for a row; the automatic match never replaces these.</summary>
        public Dictionary<ImportCandidate, (ObjectId family, ObjectId size)> Overrides { get; } = new Dictionary<ImportCandidate, (ObjectId, ObjectId)>();
        /// <summary>Network the user sent a row to; wins over the destination setting.</summary>
        public Dictionary<ImportCandidate, string> NetworkOverrides { get; } = new Dictionary<ImportCandidate, string>();
        /// <summary>The network each row goes to, from the last matching.</summary>
        public Dictionary<ImportCandidate, RowDestination> Destinations { get; set; } = new Dictionary<ImportCandidate, RowDestination>();
        /// <summary>Navisworks GUIDs already imported into <see cref="Document"/>.</summary>
        public HashSet<string> Existing { get; private set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>The Navisworks model the rows came from.</summary>
        public string SourceDocument { get; private set; } = "";
        /// <summary>The drawing the matched parts, chosen parts (overrides) and "already imported" marks belong to.</summary>
        public Document Document { get; private set; }
        /// <summary>Navisworks → drawing: turn about the origin, then shift; applied as rows are read.</summary>
        public NavisTransform Transform { get; set; } = new NavisTransform();

        /// <summary>
        /// Makes the matched parts, chosen parts and "already imported" marks those of <paramref name="doc"/>. For another drawing
        /// they are dropped (chosen parts are object ids in the previous drawing's parts lists); <paramref name="droppedChoices"/>
        /// says whether any chosen parts were lost. Returns true when the drawing changed.
        /// </summary>
        public bool Bind(Document doc, out bool droppedChoices)
        {
            droppedChoices = false;
            if (doc == Document) return false;
            droppedChoices = Overrides.Count > 0 || NetworkOverrides.Count > 0;
            Overrides.Clear();
            NetworkOverrides.Clear();
            Destinations.Clear();
            Plan.Clear();
            Done.Clear();
            Document = doc;
            RefreshExisting(doc);
            return true;
        }

        /// <summary>The bound drawing is closing: its plans and chosen parts go with it. False when <paramref name="doc"/> is not the bound one.</summary>
        public bool Unbind(Document doc)
        {
            if (doc == null || Document != doc) return false;
            Document = null;
            Overrides.Clear();
            NetworkOverrides.Clear();
            Destinations.Clear();
            Plan.Clear();
            Done.Clear();
            return true;
        }

        /// <summary>Reads which Navisworks items are already in <paramref name="doc"/>.</summary>
        public void RefreshExisting(Document doc) =>
            Existing = doc == null ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : NavisImporter.ExistingGuids(doc);

        /// <summary>Replaces the rows with a Navisworks answer, each interpreted and moved by <see cref="Transform"/>; all but skipped rows are ticked.</summary>
        public void Load(NavisResponse response)
        {
            SourceDocument = response.Document;
            Rows.Clear(); Plan.Clear(); Done.Clear(); Ticked.Clear(); Overrides.Clear(); NetworkOverrides.Clear(); Destinations.Clear();
            foreach (NavisElement e in response.Elements)
            {
                ImportCandidate c;
                try
                {
                    c = NavisInterpreter.Read(e);
                    Transform.Apply(c);
                }
                catch (Exception ex)
                {
                    C3DProLogger.Warn($"Navisworks Import: item {e.Name} ({e.Guid}) could not be read", ex);
                    c = new ImportCandidate { Source = e, Name = e.Name, Guid = e.Guid, Detected = ImportKind.Solid, Action = ImportKind.Solid };
                    c.Notes.Add("Could not be read: " + ex.Message);
                }
                Rows.Add(c);
                if (c.Action != ImportKind.Skip) Ticked.Add(c);
            }
        }

        /// <summary>Where the last Navisworks selection is kept, so a run can be repeated without Navisworks (tests, problem reports).</summary>
        public static string LastSelectionPath => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "C3DProTools", "NavisLastSelection.json");

        /// <summary>Best effort: keeps the selection just read (never stops the tool).</summary>
        public static void SaveSelection(NavisResponse response)
        {
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LastSelectionPath));
                using (System.IO.FileStream f = System.IO.File.Create(LastSelectionPath))
                    new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(NavisResponse)).WriteObject(f, response);
            }
            catch (Exception ex) { C3DProLogger.Warn("Navisworks Import: the selection could not be saved", ex); }
        }

        public static NavisResponse LoadSelection(string path)
        {
            using (System.IO.FileStream f = System.IO.File.OpenRead(path))
                return (NavisResponse)new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(NavisResponse)).ReadObject(f);
        }

        /// <summary>Takes in what an import did: its outcomes, and the drawing's imported items read again.</summary>
        public void Record(ImportReport report, Document doc)
        {
            foreach (KeyValuePair<ImportCandidate, RowOutcome> kv in report.Rows) { Done[kv.Key] = kv.Value; Plan[kv.Key] = kv.Value; }
            RefreshExisting(doc);
        }

        /// <summary>The import options from the settings, the chosen surface and the parts chosen for rows.</summary>
        public NavisImportOptions Options(ObjectId surfaceId) => new NavisImportOptions
        {
            PartsListName = Settings.PartsListName,
            Grouping = (NetworkGrouping)Math.Max(0, Settings.Grouping),
            Prefix = Settings.Prefix,
            SingleNetworkName = (Settings.Prefix ?? "").Trim().Length > 0 ? Settings.Prefix + "Import" : "NAVIS - Import",
            SurfaceId = surfaceId,
            ConnectToStructures = Settings.ConnectToStructures,
            Existing = (ExistingMode)Math.Max(0, Settings.Existing),
            SolidLayer = Settings.SolidLayer,
            MaxPartError = Settings.MaxPartErrorMm / 1000.0,
            SolidWhenNoSize = Settings.NoSizeMode == 0,
            SearchDrawingLists = Settings.SearchDrawingLists,
            Mode = (CatalogMode)Math.Max(0, Math.Min(2, Settings.SizeMode)),
            Overrides = new Dictionary<ImportCandidate, (ObjectId, ObjectId)>(Overrides),
            Destination = (DestinationMode)Math.Max(0, Math.Min(2, Settings.Destination)),
            ChosenNetwork = Settings.ChosenNetwork ?? "",
            NetworkOverrides = new Dictionary<ImportCandidate, string>(NetworkOverrides)
        };
    }
}
