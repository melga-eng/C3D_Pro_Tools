using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using C3DProTools.NavisLink;

namespace C3DProTools.Tests
{
    /// <summary>The length-prefixed JSON framing shared by Civil 3D (.NET 10) and the Navisworks add-in (.NET Framework 4.8).</summary>
    public class NavisLinkProtocolTests
    {
        [Fact]
        public void Response_RoundTripsWithPropsAndGeometry()
        {
            NavisResponse sent = new NavisResponse
            {
                Ok = true, Document = @"C:\Models\Site.nwd", Units = "Meters", SelectedCount = 1,
                Elements = new List<NavisElement>
                {
                    new NavisElement
                    {
                        Guid = "a1b2", Name = "Pipe - (1)", ClassName = "AECC_PIPE", Layer = "C-STRM",
                        Props = new List<NavisProp> { new NavisProp { Category = "Civil3D", Name = "Inner Diameter or Width", Value = "300.000mm", Number = 0.3 } },
                        Triangles = new[] { 0.0, 0, 0, 1, 0, 0, 0, 1, 0 },
                        Box = new[] { 0.0, 0, 0, 1, 1, 0 }
                    }
                },
                Warnings = new List<string> { "one", "zwei – ü" }
            };
            using MemoryStream ms = new MemoryStream();
            NavisLinkWire.Write(ms, sent);
            ms.Position = 0;
            NavisResponse got = NavisLinkWire.Read<NavisResponse>(ms);

            Assert.True(got.Ok);
            Assert.Equal(sent.Document, got.Document);
            Assert.Equal(sent.Warnings, got.Warnings);
            NavisElement e = Assert.Single(got.Elements);
            Assert.Equal("AECC_PIPE", e.ClassName);
            Assert.Equal(0.3, e.Props[0].Number);
            Assert.Equal(sent.Elements[0].Triangles, e.Triangles);
            Assert.Equal(ms.Length, ms.Position);
        }

        [Fact]
        public void Header_IsTheBodyLengthLittleEndian()
        {
            using MemoryStream ms = new MemoryStream();
            NavisLinkWire.Write(ms, new NavisRequest { Command = "hello" });
            byte[] all = ms.ToArray();
            Assert.Equal(all.Length - 4, BitConverter.ToInt32(all, 0));
        }

        [Fact]
        public void TwoMessagesOnOneStream_AreReadInOrder()
        {
            using MemoryStream ms = new MemoryStream();
            NavisLinkWire.Write(ms, new NavisRequest { Command = "select", Guids = new List<string> { "g1", "g2" } });
            NavisLinkWire.Write(ms, new NavisRequest { Command = "hello" });
            ms.Position = 0;
            Assert.Equal(new[] { "g1", "g2" }, NavisLinkWire.Read<NavisRequest>(ms).Guids);
            Assert.Equal("hello", NavisLinkWire.Read<NavisRequest>(ms).Command);
        }

        [Fact]
        public void NegativeLength_IsRejected()
        {
            using MemoryStream ms = new MemoryStream(BitConverter.GetBytes(-5));
            Assert.Throws<InvalidDataException>(() => NavisLinkWire.Read<NavisRequest>(ms));
        }

        [Fact]
        public void LengthOverOneGigabyte_IsRejected()
        {
            using MemoryStream ms = new MemoryStream(BitConverter.GetBytes(1024 * 1024 * 1024 + 1));
            Assert.Throws<InvalidDataException>(() => NavisLinkWire.Read<NavisRequest>(ms));
        }

        [Fact]
        public void TruncatedBody_ThrowsEndOfStream()
        {
            using MemoryStream full = new MemoryStream();
            NavisLinkWire.Write(full, new NavisRequest { Command = "selection" });
            byte[] cut = full.ToArray()[..^3];
            using MemoryStream ms = new MemoryStream(cut);
            Assert.Throws<EndOfStreamException>(() => NavisLinkWire.Read<NavisRequest>(ms));
        }

        // ---------------- Over a real named pipe ----------------

        /// <summary>A one-shot server: reads the request, then answers with <paramref name="answer"/> (null = never answers).</summary>
        private static (string name, Task server) Serve(Func<NavisRequest, NavisResponse> answer, CancellationToken stop)
        {
            string name = "C3DProTools.Tests." + Guid.NewGuid().ToString("N");
            Task server = Task.Run(async () =>
            {
                using NamedPipeServerStream pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(stop);
                NavisRequest request = NavisLinkWire.Read<NavisRequest>(pipe);
                NavisResponse response = answer(request);
                if (response == null) { try { await Task.Delay(Timeout.Infinite, stop); } catch (OperationCanceledException) { } return; }
                NavisLinkWire.Write(pipe, response);
            });
            return (name, server);
        }

        [Fact]
        public async Task Send_SameRelease_GetsTheAnswer()
        {
            using CancellationTokenSource stop = new CancellationTokenSource();
            (string name, Task server) = Serve(r => new NavisResponse { Ok = true, SelectedCount = r.Guids.Count }, stop.Token);
            NavisResponse got = NavisLinkWire.Send(name, new NavisRequest { Command = "select", Guids = new List<string> { "a", "b" } }, 2000, 5000);
            Assert.Equal(2, got.SelectedCount);
            await server.WaitAsync(TimeSpan.FromSeconds(5));
        }

        [Fact]
        public void Send_NoAnswer_TimesOutInsteadOfHanging()
        {
            using CancellationTokenSource stop = new CancellationTokenSource();
            (string name, _) = Serve(r => null, stop.Token);
            DateTime started = DateTime.UtcNow;
            Assert.Throws<TimeoutException>(() => NavisLinkWire.Send(name, new NavisRequest { Command = "selection" }, 2000, 300));
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
            stop.Cancel();
        }

        [Fact]
        public void Send_Cancelled_StopsWaiting()
        {
            using CancellationTokenSource stop = new CancellationTokenSource();
            using CancellationTokenSource cancel = new CancellationTokenSource(200);
            (string name, _) = Serve(r => null, stop.Token);
            Assert.ThrowsAny<OperationCanceledException>(() => NavisLinkWire.Send(name, new NavisRequest { Command = "selection" }, 2000, 60000, cancel.Token));
            stop.Cancel();
        }

        [Fact]
        public async Task Send_OlderAddIn_IsToldApart()
        {
            using CancellationTokenSource stop = new CancellationTokenSource();
            // An add-in from before version 2 sends no version at all, which reads as 0.
            (string name, Task server) = Serve(r => new NavisResponse { Ok = true, Version = 0 }, stop.Token);
            InvalidDataException ex = Assert.Throws<InvalidDataException>(() => NavisLinkWire.Send(name, new NavisRequest { Command = "hello" }, 2000, 5000));
            Assert.Contains("another C3D Pro Tools release", ex.Message);
            await server.WaitAsync(TimeSpan.FromSeconds(5));
        }

        [Fact]
        public void Request_CarriesTheProtocolVersion()
        {
            Assert.Equal(2, NavisLinkWire.ProtocolVersion);
            Assert.Equal(NavisLinkWire.ProtocolVersion, new NavisRequest().Version);
        }

        [Fact]
        public void Describe_NamesTheExceptionType()
        {
            Assert.Equal("TimeoutException: late", NavisLinkWire.Describe(new TimeoutException("late")));
            Assert.Equal("", NavisLinkWire.Describe(null));
        }
    }
}
