using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace C3DProTools.Bridge
{
    /// <summary>
    /// A minimal MCP server over Streamable HTTP (JSON responses only, no SSE): clients POST JSON-RPC to /mcp.
    /// Plain TcpListener on the loopback address, so it needs no admin rights or URL reservation. It rejects
    /// requests with a non-local Host or a browser Origin header, so web pages cannot drive Civil 3D through it
    /// (the DNS-rebinding / CSRF protection the MCP spec asks for).
    /// </summary>
    internal sealed class McpServer
    {
        private const string ProtocolVersion = "2025-06-18";
        private static readonly string[] SupportedVersions = { "2025-06-18", "2025-03-26", "2024-11-05" };
        private const int MaxBody = 4 * 1024 * 1024;

        private readonly BridgeTools _tools;
        private TcpListener _listener;
        private volatile bool _running;

        public int Port { get; }
        public string Url => $"http://127.0.0.1:{Port}/mcp";
        public bool IsRunning => _running;
        public int CallCount;
        public string LastCall;

        public McpServer(int port, BridgeTools tools)
        {
            Port = port;
            _tools = tools;
        }

        public void Start()
        {
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start();
            _running = true;
            var thread = new Thread(Loop) { IsBackground = true, Name = "C3D Bridge MCP" };
            thread.Start();
        }

        public void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
        }

        private void Loop()
        {
            while (_running)
            {
                TcpClient client;
                try { client = _listener.AcceptTcpClient(); }
                catch { break; }
                ThreadPool.QueueUserWorkItem(_ => Serve(client));
            }
            _running = false;
        }

        // ---------- HTTP/1.1 (just enough for MCP clients) ----------

        private void Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 30000;
                    var stream = client.GetStream();
                    // Keep-alive: serve requests until the client closes or goes quiet.
                    while (_running)
                    {
                        var req = HttpRequest.Read(stream);
                        if (req == null) return;
                        var (status, contentType, body, extra) = Route(req);
                        HttpRequest.Write(stream, status, contentType, body, extra);
                        if (req.Close) return;
                    }
                }
                catch { /* client went away */ }
            }
        }

        private (int, string, string, string) Route(HttpRequest req)
        {
            if (!IsLocal(req)) return (403, "text/plain", "Forbidden: browser or non-local request.", null);
            if (req.Path != "/mcp") return (404, "text/plain", "C3D Bridge: the MCP endpoint is /mcp", null);
            if (req.Method != "POST") return (405, "text/plain", "Method not allowed", "Allow: POST\r\n");
            if (req.Body == null) return (413, "text/plain", "Request too large", null);

            JsonNode parsed;
            try { parsed = JsonNode.Parse(req.Body); }
            catch { return (200, "application/json", Error(null, -32700, "Parse error").ToJsonString(), null); }

            if (parsed is JsonArray batch)
            {
                var outArr = new JsonArray();
                foreach (var item in batch)
                {
                    var r = Dispatch(item as JsonObject);
                    if (r != null) outArr.Add(r);
                }
                return outArr.Count == 0 ? (202, null, null, null) : (200, "application/json", outArr.ToJsonString(), null);
            }
            var response = Dispatch(parsed as JsonObject);
            return response == null ? (202, null, null, null) : (200, "application/json", response.ToJsonString(), null);
        }

        private static bool IsLocal(HttpRequest req)
        {
            string host = req.Header("host") ?? "";
            int colon = host.LastIndexOf(':');
            if (colon > 0 && !host.EndsWith("]")) host = host.Substring(0, colon);
            if (!(host == "127.0.0.1" || host == "localhost" || host == "[::1]")) return false;

            string origin = req.Header("origin");
            if (string.IsNullOrEmpty(origin)) return true; // agents do not send Origin; browsers always do on POST
            return Uri.TryCreate(origin, UriKind.Absolute, out var o) && (o.Host == "127.0.0.1" || o.Host == "localhost");
        }

        // ---------- JSON-RPC / MCP ----------

        private JsonNode Dispatch(JsonObject msg)
        {
            if (msg == null) return Error(null, -32600, "Invalid request");
            JsonNode id = msg["id"]?.DeepClone();
            string method = (string)msg["method"];
            if (method == null) return null; // a response from the client: nothing to do
            bool isNotification = !msg.ContainsKey("id");

            try
            {
                switch (method)
                {
                    case "initialize":
                        {
                            string requested = (string)msg["params"]?["protocolVersion"];
                            string version = Array.IndexOf(SupportedVersions, requested) >= 0 ? requested : ProtocolVersion;
                            return Result(id, new JsonObject
                            {
                                ["protocolVersion"] = version,
                                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                                ["serverInfo"] = new JsonObject { ["name"] = "c3d-bridge", ["version"] = "0.1.0" },
                                ["instructions"] = BridgeTools.Instructions,
                            });
                        }
                    case "ping":
                        return Result(id, new JsonObject());
                    case "tools/list":
                        return Result(id, new JsonObject { ["tools"] = _tools.Definitions() });
                    case "tools/call":
                        {
                            string name = (string)msg["params"]?["name"];
                            var args = msg["params"]?["arguments"] as JsonObject ?? new JsonObject();
                            Interlocked.Increment(ref CallCount);
                            LastCall = $"{name} at {DateTime.Now:HH:mm:ss}";
                            return Result(id, _tools.Call(name, args));
                        }
                    default:
                        if (isNotification) return null; // notifications/initialized, notifications/cancelled, ...
                        return Error(id, -32601, $"Method not found: {method}");
                }
            }
            catch (Exception ex)
            {
                return isNotification ? null : Error(id, -32603, ex.Message);
            }
        }

        private static JsonObject Result(JsonNode id, JsonNode result) =>
            new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

        private static JsonObject Error(JsonNode id, int code, string message) =>
            new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
    }

    /// <summary>A parsed HTTP request: request line, headers, and a Content-Length or chunked body.</summary>
    internal sealed class HttpRequest
    {
        public string Method;
        public string Path;
        public string Body;
        public bool Close;
        private readonly Dictionary<string, string> _headers = new(StringComparer.OrdinalIgnoreCase);

        public string Header(string name) => _headers.TryGetValue(name, out var v) ? v : null;

        public static HttpRequest Read(Stream s)
        {
            string requestLine = ReadLine(s);
            if (string.IsNullOrEmpty(requestLine)) return null;
            var parts = requestLine.Split(' ');
            if (parts.Length < 2) return null;

            var req = new HttpRequest { Method = parts[0].ToUpperInvariant() };
            string target = parts[1];
            int q = target.IndexOf('?');
            req.Path = (q >= 0 ? target.Substring(0, q) : target).TrimEnd('/');
            if (req.Path == "") req.Path = "/";

            string line;
            while (!string.IsNullOrEmpty(line = ReadLine(s)))
            {
                int c = line.IndexOf(':');
                if (c > 0) req._headers[line.Substring(0, c).Trim()] = line.Substring(c + 1).Trim();
            }
            req.Close = string.Equals(req.Header("connection"), "close", StringComparison.OrdinalIgnoreCase)
                        || (parts.Length > 2 && parts[2] == "HTTP/1.0");

            byte[] body;
            if (string.Equals(req.Header("transfer-encoding"), "chunked", StringComparison.OrdinalIgnoreCase))
                body = ReadChunked(s);
            else if (int.TryParse(req.Header("content-length"), out int len) && len > 0)
                body = len > 4 * 1024 * 1024 ? null : ReadExact(s, len);
            else
                body = Array.Empty<byte>();
            req.Body = body == null ? null : Encoding.UTF8.GetString(body);
            if (body == null) req.Close = true; // the unread body is still on the wire: drop the connection
            return req;
        }

        public static void Write(Stream s, int status, string contentType, string body, string extraHeaders)
        {
            byte[] bytes = body == null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(body);
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(Reason(status)).Append("\r\n");
            if (contentType != null) sb.Append("Content-Type: ").Append(contentType).Append("; charset=utf-8\r\n");
            sb.Append("Content-Length: ").Append(bytes.Length).Append("\r\n");
            if (extraHeaders != null) sb.Append(extraHeaders);
            sb.Append("\r\n");
            byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
            s.Write(head, 0, head.Length);
            s.Write(bytes, 0, bytes.Length);
            s.Flush();
        }

        private static string Reason(int status) => status switch
        {
            200 => "OK", 202 => "Accepted", 403 => "Forbidden", 404 => "Not Found",
            405 => "Method Not Allowed", 413 => "Payload Too Large", _ => "Error",
        };

        private static string ReadLine(Stream s)
        {
            var sb = new StringBuilder();
            while (true)
            {
                int b = s.ReadByte();
                if (b < 0) return sb.Length == 0 ? null : sb.ToString();
                if (b == '\n') break;
                if (b != '\r') sb.Append((char)b);
                if (sb.Length > 16384) throw new InvalidDataException("Header line too long");
            }
            return sb.ToString();
        }

        private static byte[] ReadExact(Stream s, int len)
        {
            var buf = new byte[len];
            int read = 0;
            while (read < len)
            {
                int n = s.Read(buf, read, len - read);
                if (n <= 0) throw new EndOfStreamException();
                read += n;
            }
            return buf;
        }

        private static byte[] ReadChunked(Stream s)
        {
            using var ms = new MemoryStream();
            while (true)
            {
                string sizeLine = ReadLine(s) ?? throw new EndOfStreamException();
                int semi = sizeLine.IndexOf(';');
                int size = Convert.ToInt32(semi >= 0 ? sizeLine.Substring(0, semi) : sizeLine, 16);
                if (size == 0)
                {
                    while (!string.IsNullOrEmpty(ReadLine(s))) { } // trailers
                    return ms.ToArray();
                }
                if (ms.Length + size > 4 * 1024 * 1024) return null;
                var chunk = ReadExact(s, size);
                ms.Write(chunk, 0, chunk.Length);
                ReadLine(s); // CRLF after the chunk
            }
        }
    }
}
