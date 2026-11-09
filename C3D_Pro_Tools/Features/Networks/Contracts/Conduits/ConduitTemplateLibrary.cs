using System;
using System.Collections.Generic;
using System.Linq;

// The standard conduit templates, transcribed from the two supplied typical-detail sheets (see
// docs/conduit-generation-contract.md for the transcription and its review notes). The PDFs remain the source of truth.
// Lengths in metres.
namespace C3DProTools.Features.Networks.Contracts
{
    internal static class ConduitTemplateLibrary
    {
        public const string IctDrawing = "SW00670060000D-C00002-JCB-ZZ-DWG-IC-XX-006501";
        public const string IctRevision = "N2";
        public const string IctSheet = "ICT Services / NCRD - Telecommunications Network / Typical Details - Trench/Duct Bank";
        public const string MvDrawing = "SW00650052000D-C00002-JCB-ZZ-DWG-EL-XX-466501";
        public const string MvRevision = "N1";
        public const string MvSheet = "Electrical / NCRD - Medium Voltage Network / Typical Details Trench";

        // ICT main ducts: 110 mm OD, 50 mm clear gaps (160 mm pitch = 110 + 50), 100 mm duct outside to encasement edge.
        private const double IctOd = 0.110, IctGap = 0.050, IctPitch = 0.160, IctEdge = 0.100;

        private static readonly ConduitCapacity IctMain = new ConduitCapacity(SubductsPerConduit: 5, SubductOutsideDiameterM: 0.032, SpareCapacityFraction: 0.5, Material: "HDPE");
        private static readonly ConduitCapacity IctSingle = new ConduitCapacity(SubductsPerConduit: 0, SubductOutsideDiameterM: null, SpareCapacityFraction: 0.5, Material: "HDPE");
        private static readonly ConduitCapacity MvPower = new ConduitCapacity(Material: "UPVC");

        private static readonly string[] IctNotes =
        {
            "Pitch 160 mm is derived as 110 mm OD + 50 mm clear gap (ICT sheet dimensions).",
            "Envelope is the duct-bank section only: not excavation, reinforcement cover or depth below ground.",
            "Note 7 (50% spare capacity) is metadata: every duct position is generated.",
            "Note 12 burial depths refer to AF0002136002D-C00002-JCB-ZZ-DWG-CE-XX-006108, not supplied or reviewed: no depth rule is applied.",
        };

        private static readonly string[] MvNotes =
        {
            "Callout is 200 mm UPVC; the conduit's outside diameter and wall are not established: check the chosen part's size.",
            "Envelope size, edge clearance and placement reference are not established by the sheet summary: centred placement is a default to confirm.",
            "Concrete, reinforcement and lean concrete layers are not generated.",
        };

        private static ConduitTemplate Ict(string key, string label, int columns, int rows, string variantA, string variantB)
        {
            double width = (columns - 1) * IctPitch + IctOd + 2 * IctEdge, height = (rows - 1) * IctPitch + IctOd + 2 * IctEdge;
            return new ConduitTemplate(key, $"ICT {label} ({rows} row{(rows == 1 ? "" : "s")} × {columns} columns)", ConduitUtility.Ict,
                new ConduitArrangement(columns, rows, IctPitch, IctPitch), ConduitSection.Centred,
                new ConduitDesignReference("110 mm OD HDPE duct with five 32 mm OD subducts", IctOd, IctGap, IctEdge, Math.Round(width, 6), Math.Round(height, 6)),
                IctMain, new ConduitSourceReference(IctDrawing, IctRevision, IctSheet, label, ArrangementLabelOrder.RowsByColumns),
                new[]
                {
                    new ConstructionVariant(variantA, "Duct bank at pavement, under taxiway", ConstructionContext.PavementUnderTaxiway),
                    new ConstructionVariant(variantB, "Normal duct bank under roads and in unpaved areas", ConstructionContext.NormalRoadsAndUnpaved),
                })
            { IsStandard = true, Notes = IctNotes };
        }

        private static ConduitTemplate Mv(string key, string label, int columns, int rows, string typeA, string typeB, string arrangement)
        {
            return new ConduitTemplate(key, $"MV {label} ({columns} columns × {rows} row{(rows == 1 ? "" : "s")})", ConduitUtility.Mv,
                new ConduitArrangement(columns, rows, 0.300, 0.500), ConduitSection.Centred,
                new ConduitDesignReference("200 mm UPVC for power cable", null, null, null, null, null),
                MvPower, new ConduitSourceReference(MvDrawing, MvRevision, MvSheet, label, ArrangementLabelOrder.ColumnsByRows),
                new[]
                {
                    new ConstructionVariant(typeA, $"{arrangement} duct bank at pavement, under taxiway", ConstructionContext.PavementUnderTaxiway),
                    new ConstructionVariant(typeB, $"{arrangement} normal duct bank, under road crossing", ConstructionContext.NormalRoadCrossing),
                })
            { IsStandard = true, Notes = MvNotes };
        }

        /// <summary>The seven standard templates, in the order the form lists them. A new list each call; the templates are immutable.</summary>
        public static IReadOnlyList<ConduitTemplate> Standard() => Frozen.List(new[]
        {
            Ict("ICT-2C-1R", "1X2", 2, 1, "1A", "1B"),
            Ict("ICT-2C-3R", "3X2", 2, 3, "2A", "2B"),
            Ict("ICT-4C-6R", "6X4", 4, 6, "3A", "3B"),
            Ict("ICT-4C-3R", "3X4", 4, 3, "4A", "4B"),
            new ConduitTemplate("ICT-1C-1R-50", "ICT 1X1 special (single 50 mm duct)", ConduitUtility.Ict,
                new ConduitArrangement(1, 1, 0, 0), ConduitSection.Centred,
                new ConduitDesignReference("50 mm HDPE duct, no subducts", 0.050, null, 0.100, 0.250, 0.250),
                IctSingle, new ConduitSourceReference(IctDrawing, IctRevision, IctSheet, "1X1", ArrangementLabelOrder.RowsByColumns),
                new[] { new ConstructionVariant("1C", "Normal single 50 mm duct under roads and in unpaved areas", ConstructionContext.NormalRoadsAndUnpaved) })
            {
                IsStandard = true,
                Notes = new[]
                {
                    "Special template: detail 1C only, no pavement variant.",
                    "Edge clearance 100 mm is inferred from the 250 × 250 mm section and the 50 mm duct; confirm against detail 1C.",
                    IctNotes[3],
                }
            },
            Mv("MV-5C-3R", "5 X 3", 5, 3, "Type 01", "Type 02", "5 x 3"),
            Mv("MV-5C-1R", "5 X 1", 5, 1, "Type 03", "Type 04", "5 x 1"),
        });

        /// <summary>The standard template with this key (ignoring case), or null.</summary>
        public static ConduitTemplate Find(string key) =>
            Standard().FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));
    }
}
