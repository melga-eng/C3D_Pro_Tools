using System;
using System.Globalization;

// Plain values that name a drawing, an object in it, and a point in it. No Autodesk or UI types: the Civil 3D adapter turns
// them into documents, ObjectIds and Point3d, and back. See ARCHITECTURE.md, "Contracts".
namespace C3DProTools.Features.Shared.Contracts
{
    /// <summary>
    /// Which open drawing an operation is meant for. The adapter makes it from the document when a request is built, and checks
    /// it again just before writing: a request or plan for another drawing (or for a drawing closed and opened again) is
    /// rejected, never applied to whatever drawing is active.
    /// </summary>
    /// <param name="SessionKey">
    /// Names one open document for as long as it stays open in this Civil 3D session (the adapter keeps one per document). Not
    /// saved; a drawing closed and opened again gets a new key, so a plan made before that is stale.
    /// </param>
    /// <param name="FingerprintGuid">The drawing's own fingerprint (Database.FingerprintGuid), kept across saves.</param>
    /// <param name="FilePath">The drawing's file, for messages only (an unsaved drawing has none); never used to match.</param>
    /// <param name="UnitLabel">
    /// The drawing's length unit as messages show it ("m", "ft"); the adapter reads it from the drawing. Display only: lengths
    /// in the contracts are in drawing units and are never converted. Null or empty: "drawing units".
    /// </param>
    internal sealed record DrawingIdentity(string SessionKey, string FingerprintGuid, string FilePath, string UnitLabel = null)
    {
        /// <summary>The unit for messages: <see cref="UnitLabel"/>, or "drawing units".</summary>
        public string Unit => string.IsNullOrWhiteSpace(UnitLabel) ? "drawing units" : UnitLabel.Trim();

        /// <summary>True when both name the same open document: same session key and same fingerprint.</summary>
        public bool Matches(DrawingIdentity other) =>
            other != null && !string.IsNullOrEmpty(SessionKey)
            && string.Equals(SessionKey, other.SessionKey, StringComparison.Ordinal)
            && string.Equals(FingerprintGuid ?? "", other.FingerprintGuid ?? "", StringComparison.OrdinalIgnoreCase);

        public bool IsComplete => !string.IsNullOrWhiteSpace(SessionKey) && !string.IsNullOrWhiteSpace(FingerprintGuid);
    }

    /// <summary>
    /// An object of a drawing, by its handle (hexadecimal, as AutoCAD shows it). A handle means something only in the drawing
    /// named by the request or plan that carries it; the adapter resolves it in that drawing and reports a missing or erased
    /// object as an issue. Handles last across saves, unlike ObjectIds, so a plan can name the same pipe the user saw.
    /// </summary>
    internal readonly record struct DrawingObjectRef
    {
        /// <summary>Upper-case hexadecimal, no leading zeros (as <see cref="TryCreate"/> made it).</summary>
        public string Handle { get; }

        private DrawingObjectRef(string handle) { Handle = handle; }

        /// <summary>
        /// A reference from a handle such as "2A3F" (case and leading zeros do not matter). False for an empty, zero, non-hex
        /// or over-long (more than 16 digits) handle.
        /// </summary>
        public static bool TryCreate(string handle, out DrawingObjectRef reference)
        {
            reference = default;
            string h = (handle ?? "").Trim();
            if (h.Length == 0 || h.Length > 16) return false;
            if (!ulong.TryParse(h, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong value) || value == 0) return false;
            reference = new DrawingObjectRef(value.ToString("X", CultureInfo.InvariantCulture));
            return true;
        }

        /// <summary>A reference from a handle that must be valid (throws otherwise); for values the adapter read from the drawing.</summary>
        public static DrawingObjectRef Of(string handle) =>
            TryCreate(handle, out DrawingObjectRef r) ? r : throw new ArgumentException($"\"{handle}\" is not a drawing handle.", nameof(handle));

        /// <summary>False for <c>default</c> (no handle).</summary>
        public bool IsValid => !string.IsNullOrEmpty(Handle);

        public override string ToString() => Handle ?? "";
    }

    /// <summary>A point in the drawing's world coordinates, in drawing units (never converted; each feature names its units in its contract).</summary>
    internal readonly record struct Point3(double X, double Y, double Z)
    {
        public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);

        /// <summary>Distance in plan (X, Y), as the split measures along pipes.</summary>
        public double PlanDistanceTo(Point3 other) => Math.Sqrt((X - other.X) * (X - other.X) + (Y - other.Y) * (Y - other.Y));

        public double DistanceTo(Point3 other) => Math.Sqrt((X - other.X) * (X - other.X) + (Y - other.Y) * (Y - other.Y) + (Z - other.Z) * (Z - other.Z));
    }
}
