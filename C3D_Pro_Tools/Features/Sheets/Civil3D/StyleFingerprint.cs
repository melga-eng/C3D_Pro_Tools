using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using ErrorStatus = Autodesk.AutoCAD.Runtime.ErrorStatus;

// Content fingerprints for the scene stamp (docs/sheet-production-contract.md, section 10): a style keeps its name and handle
// when it is edited, so what it contains is hashed instead: the object and the objects it owns. DBObject.DwgOut files each one through this filer;
// every value written is fed to SHA-256. References to other objects are filed as their handles, so pointing at a different
// object changes the hash, while a change inside the referenced object changes only that object's own fingerprint.
namespace C3DProTools.Features.Sheets.Civil3D
{
    internal static class StyleFingerprint
    {
        /// <summary>How deep owned objects are followed (style → component → its own parts …).</summary>
        private const int MaxDepth = 6;

        /// <summary>
        /// The fingerprint of an object and everything it owns, as lowercase hex; empty when the object cannot be filed out.
        /// A Civil 3D label style keeps its text, line and block components as separate objects it owns (checked live,
        /// 2026-11-10: editing a component's text height left the style's own filed data unchanged). So the objects the style
        /// files as owned are followed, and a label style's components are added explicitly too, in case they are not filed.
        /// </summary>
        public static string Of(Transaction tr, DBObject obj)
        {
            if (obj == null) return "";
            using (IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                if (!Add(tr, obj, hash, new HashSet<ObjectId>(), 0)) return "";
                return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }
        }

        private static bool Add(Transaction tr, DBObject obj, IncrementalHash hash, HashSet<ObjectId> seen, int depth)
        {
            if (!seen.Add(obj.ObjectId)) return true;
            List<ObjectId> owned;
            using (HashingDwgFiler filer = new HashingDwgFiler(hash))
            {
                try { obj.DwgOut(filer); }
                catch (Exception ex) { C3DProLogger.Warn($"Sheet scene: {obj.GetType().Name} {obj.Handle} could not be filed out for its fingerprint", ex); return false; }
                owned = filer.Owned;
            }
            if (obj is Autodesk.Civil.DatabaseServices.Styles.LabelStyle style)
                foreach (Autodesk.Civil.DatabaseServices.Styles.LabelStyleComponentType kind in Enum.GetValues(typeof(Autodesk.Civil.DatabaseServices.Styles.LabelStyleComponentType)))
                {
                    try { foreach (ObjectId id in style.GetComponents(kind)) owned.Add(id); }
                    catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                }
            if (depth >= MaxDepth) return true;
            foreach (ObjectId id in owned)
            {
                if (id.IsNull || id.IsErased || seen.Contains(id)) continue;
                DBObject child;
                try { child = tr.GetObject(id, OpenMode.ForRead); } catch (Exception) { continue; }
                if (!Add(tr, child, hash, seen, depth + 1)) return false;
            }
            return true;
        }
    }

    /// <summary>A write-only DWG filer that hashes what it is given. Reads are never asked of it (DwgOut only writes).</summary>
    internal sealed class HashingDwgFiler : DwgFiler
    {
        private readonly IncrementalHash _hash;
        private long _position;
        private ErrorStatus _status = ErrorStatus.OK;

        public HashingDwgFiler(IncrementalHash hash) { _hash = hash; }

        /// <summary>The objects filed as owned (hard or soft ownership), in filing order: the parts the object is made of.</summary>
        public List<ObjectId> Owned { get; } = new List<ObjectId>();

        private void Add(byte tag, ReadOnlySpan<byte> bytes)
        {
            Span<byte> t = stackalloc byte[1];
            t[0] = tag;
            _hash.AppendData(t);
            _hash.AppendData(bytes);
            _position += bytes.Length + 1;
        }

        private void Add(byte tag, double v) => Add(tag, BitConverter.GetBytes(v));
        private void Add(byte tag, long v) => Add(tag, BitConverter.GetBytes(v));
        private void AddHandle(byte tag, ObjectId id) => Add(tag, id.IsNull ? 0L : id.Handle.Value);

        public override ErrorStatus FilerStatus { get => _status; set => _status = value; }
        public override FilerType FilerType => FilerType.CopyFiler;
        public override long Position => _position;
        public override void ResetFilerStatus() => _status = ErrorStatus.OK;
        public override void Seek(long offset, int method) { }

        public override void WriteAddress(IntPtr value) { }                       // a memory address: not content
        public override void WriteBinaryChunk(byte[] chunk) => Add(1, chunk ?? Array.Empty<byte>());
        public override void WriteBoolean(bool value) => Add(2, value ? 1L : 0L);
        public override void WriteByte(byte value) => Add(3, (long)value);
        public override void WriteBytes(byte[] value) => Add(4, value ?? Array.Empty<byte>());
        public override void WriteDouble(double value) => Add(5, value);
        public override void WriteHandle(Handle handle) => Add(6, handle.Value);
        public override void WriteHardOwnershipId(ObjectId value) { AddHandle(7, value); if (!value.IsNull) Owned.Add(value); }
        public override void WriteHardPointerId(ObjectId value) => AddHandle(8, value);
        public override void WriteInt16(short value) => Add(9, (long)value);
        public override void WriteInt32(int value) => Add(10, (long)value);
        public override void WriteInt64(long value) => Add(11, value);
        public override void WritePoint2d(Point2d value) { Add(12, value.X); Add(12, value.Y); }
        public override void WritePoint3d(Point3d value) { Add(13, value.X); Add(13, value.Y); Add(13, value.Z); }
        public override void WriteScale3d(Scale3d value) { Add(14, value.X); Add(14, value.Y); Add(14, value.Z); }
        public override void WriteSoftOwnershipId(ObjectId value) { AddHandle(15, value); if (!value.IsNull) Owned.Add(value); }
        public override void WriteSoftPointerId(ObjectId value) => AddHandle(16, value);
        public override void WriteString(string value) => Add(17, Encoding.UTF8.GetBytes(value ?? ""));
        public override void WriteUInt16(ushort value) => Add(18, (long)value);
        public override void WriteUInt32(uint value) => Add(19, (long)value);
        public override void WriteUInt64(ulong value) => Add(20, unchecked((long)value));
        public override void WriteVector2d(Vector2d value) { Add(21, value.X); Add(21, value.Y); }
        public override void WriteVector3d(Vector3d value) { Add(22, value.X); Add(22, value.Y); Add(22, value.Z); }

        private static InvalidDataException NoReads() => new InvalidDataException("The fingerprint filer only writes.");
        public override IntPtr ReadAddress() => throw NoReads();
        public override byte[] ReadBinaryChunk() => throw NoReads();
        public override bool ReadBoolean() => throw NoReads();
        public override byte ReadByte() => throw NoReads();
        public override void ReadBytes(byte[] value) => throw NoReads();
        public override double ReadDouble() => throw NoReads();
        public override Handle ReadHandle() => throw NoReads();
        public override ObjectId ReadHardOwnershipId() => throw NoReads();
        public override ObjectId ReadHardPointerId() => throw NoReads();
        public override short ReadInt16() => throw NoReads();
        public override int ReadInt32() => throw NoReads();
        public override long ReadInt64() => throw NoReads();
        public override Point2d ReadPoint2d() => throw NoReads();
        public override Point3d ReadPoint3d() => throw NoReads();
        public override Scale3d ReadScale3d() => throw NoReads();
        public override ObjectId ReadSoftOwnershipId() => throw NoReads();
        public override ObjectId ReadSoftPointerId() => throw NoReads();
        public override string ReadString() => throw NoReads();
        public override ushort ReadUInt16() => throw NoReads();
        public override uint ReadUInt32() => throw NoReads();
        public override ulong ReadUInt64() => throw NoReads();
        public override Vector2d ReadVector2d() => throw NoReads();
        public override Vector3d ReadVector3d() => throw NoReads();
    }
}
