using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;

// Compiled into both the Civil 3D plugin (.NET 10) and the Navisworks add-in (.NET Framework 4.8),
// so it only uses APIs both runtimes have.
namespace C3DProTools.NavisLink
{
    /// <summary>One property of a Navisworks item: its tab (category), name and value as Navisworks shows it.</summary>
    [DataContract]
    public sealed class NavisProp
    {
        [DataMember] public string Category = "";
        [DataMember] public string Name = "";
        [DataMember] public string Value = "";
        /// <summary>Set when Navisworks stored a number (lengths are already converted to metres).</summary>
        [DataMember] public double? Number;
    }

    /// <summary>One selected Navisworks item with its properties and real geometry.</summary>
    [DataContract]
    public sealed class NavisElement
    {
        [DataMember] public string Guid = "";
        [DataMember] public string Name = "";
        /// <summary>Internal class, e.g. AECC_PIPE, AECC_STRUCTURE (Civil 3D), or the Revit/IFC category.</summary>
        [DataMember] public string ClassName = "";
        [DataMember] public string ClassDisplayName = "";
        [DataMember] public string SourceFile = "";
        [DataMember] public string Layer = "";
        [DataMember] public List<NavisProp> Props = new List<NavisProp>();
        /// <summary>Triangles in world coordinates, metres: x1,y1,z1,x2,y2,z2,x3,y3,z3 per triangle.</summary>
        [DataMember] public double[] Triangles = new double[0];
        /// <summary>Bounding box in metres: minX, minY, minZ, maxX, maxY, maxZ.</summary>
        [DataMember] public double[] Box = new double[0];
    }

    [DataContract]
    public sealed class NavisRequest
    {
        /// <summary>"hello", "selection" or "select".</summary>
        [DataMember] public string Command = "";
        [DataMember] public List<string> Guids = new List<string>();
        /// <summary>The sender's <see cref="NavisLinkWire.ProtocolVersion"/>.</summary>
        [DataMember] public int Version = NavisLinkWire.ProtocolVersion;
    }

    [DataContract]
    public sealed class NavisResponse
    {
        [DataMember] public bool Ok;
        [DataMember] public string Error = "";
        [DataMember] public string Document = "";
        [DataMember] public string Units = "";
        [DataMember] public int SelectedCount;
        [DataMember] public List<NavisElement> Elements = new List<NavisElement>();
        [DataMember] public List<string> Warnings = new List<string>();
        /// <summary>The answering add-in's <see cref="NavisLinkWire.ProtocolVersion"/>; 0 from add-ins older than version 2.</summary>
        [DataMember] public int Version = NavisLinkWire.ProtocolVersion;
        /// <summary>
        /// The clash areas sent with this selection by the Clash Grouper (one per group or single clash), for revision clouds;
        /// null or empty for a plain selection (and from older add-ins).
        /// </summary>
        [DataMember(IsRequired = false, EmitDefaultValue = false)] public List<NavisClashArea> ClashAreas = new List<NavisClashArea>();
    }

    /// <summary>A clash group (or single clash) area in Navisworks coordinates, metres: a box in plan and the level of its centre.</summary>
    [DataContract]
    public sealed class NavisClashArea
    {
        [DataMember] public string Name = "";
        [DataMember] public string Status = "";
        [DataMember] public string Test = "";
        [DataMember] public int Clashes;
        [DataMember] public double MinX, MinY, MaxX, MaxY, Z;
    }

    /// <summary>
    /// The clash package: what the Clash Grouper's "Send to Civil 3D" last sent (the other side's items and one area per group or
    /// clash), a <see cref="NavisResponse"/> saved as a file. Navisworks Import's "Import clashes" reads it, so nothing depends on
    /// what is selected in Navisworks afterwards.
    /// </summary>
    public static class NavisClashPackage
    {
        public static string Path => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "C3DProTools", "NavisClashPackage.json");

        public static void Save(NavisResponse package)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
            string temp = Path + ".tmp";
            using (FileStream f = File.Create(temp))
                new DataContractJsonSerializer(typeof(NavisResponse)).WriteObject(f, package);
            if (File.Exists(Path)) File.Delete(Path);
            File.Move(temp, Path);
        }

        public static NavisResponse Load()
        {
            using (FileStream f = File.OpenRead(Path))
                return (NavisResponse)new DataContractJsonSerializer(typeof(NavisResponse)).ReadObject(f);
        }
    }

    /// <summary>Length-prefixed JSON messages over a local named pipe. Each Navisworks window serves its own pipe.</summary>
    public static class NavisLinkWire
    {
        /// <summary>Pipe names are this prefix plus the Navisworks process id.</summary>
        public const string PipePrefix = "C3DProTools.NavisLink.";
        /// <summary>2: requests and answers carry this version, so a Civil 3D plugin and Navisworks add-in of different releases are told apart.</summary>
        public const int ProtocolVersion = 2;

        public static void Write<T>(Stream stream, T message)
        {
            byte[] body;
            using (MemoryStream ms = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(ms, message);
                body = ms.ToArray();
            }
            stream.Write(BitConverter.GetBytes(body.Length), 0, 4);
            stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        public static T Read<T>(Stream stream)
        {
            byte[] header = ReadExactly(stream, 4);
            int length = BitConverter.ToInt32(header, 0);
            if (length < 0 || length > 1024 * 1024 * 1024) throw new InvalidDataException("Bad message length.");
            using (MemoryStream ms = new MemoryStream(ReadExactly(stream, length)))
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(ms);
        }

        private static byte[] ReadExactly(Stream stream, int count)
        {
            byte[] buffer = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = stream.Read(buffer, read, count - read);
                if (n <= 0) throw new EndOfStreamException("The other side closed the connection.");
                read += n;
            }
            return buffer;
        }

        /// <summary>
        /// Sends one request to a Navisworks pipe and waits for its answer: at most <paramref name="connectTimeoutMs"/> to
        /// connect and <paramref name="replyTimeoutMs"/> for the answer (Navisworks answers on its UI thread, which a modal
        /// dialog there holds up), and no longer once <paramref name="cancel"/> is cancelled. Throws
        /// <see cref="TimeoutException"/> or <see cref="OperationCanceledException"/>, and <see cref="InvalidDataException"/>
        /// when the add-in is of another release.
        /// </summary>
        public static NavisResponse Send(string pipeName, NavisRequest request, int connectTimeoutMs = 2000, int replyTimeoutMs = 120000,
            CancellationToken cancel = default(CancellationToken))
        {
            NavisResponse response;
            using (NamedPipeClientStream pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut))
            {
                pipe.Connect(connectTimeoutMs);
                Write(pipe, request);
                // A pipe read cannot time out by itself: it runs aside, and closing the pipe ends it when the wait gives up.
                Task<NavisResponse> read = Task.Run(() => Read<NavisResponse>(pipe));
                bool finished;
                try { finished = read.Wait(replyTimeoutMs, cancel); }
                catch (AggregateException) { finished = true; }
                catch (OperationCanceledException) { pipe.Dispose(); throw; }
                if (!finished)
                {
                    pipe.Dispose();
                    throw new TimeoutException($"Navisworks did not answer within {replyTimeoutMs / 1000} s (is a dialog open there?).");
                }
                response = read.GetAwaiter().GetResult();
            }
            if (response.Version != ProtocolVersion)
                throw new InvalidDataException($"The Navisworks add-in is from another C3D Pro Tools release (link version {response.Version}, this plugin uses {ProtocolVersion}); install the same release in both.");
            return response;
        }

        public static string Describe(Exception ex) => ex == null ? "" : ex.GetType().Name + ": " + ex.Message;

    }
}
