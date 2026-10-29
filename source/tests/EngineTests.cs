using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PaperDrop;

internal static class EngineTests
{
    private static int passed;
    private static int failed;
    private static readonly string Root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "engine-test-data", Guid.NewGuid().ToString("N"));
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\nxref\n0 3\n0000000000 65535 f \n0000000009 00000 n \n0000000058 00000 n \ntrailer\n<< /Size 3 /Root 1 0 R >>\nstartxref\n110\n%%EOF\n");

    private static int Main()
    {
        Directory.CreateDirectory(Root);
        Run().GetAwaiter().GetResult();
        Console.WriteLine("RESULT: {0} passed, {1} failed", passed, failed);
        return failed == 0 ? 0 : 1;
    }

    private static async Task Run()
    {
        Check("arXiv modern identifier", delegate { Parsed("1706.03762", "arxiv", "1706.03762"); });
        Check("arXiv prefixed version", delegate { Parsed("arXiv:1706.03762v2", "arxiv", "1706.03762v2"); });
        Check("arXiv legacy identifier", delegate { Parsed("hep-th/9711200", "arxiv", "hep-th/9711200"); });
        Check("arXiv abstract with query", delegate { Parsed("https://arxiv.org/abs/1706.03762?source=test#top", "arxiv", "1706.03762"); });
        Check("arXiv versioned PDF", delegate { Parsed("https://arxiv.org/pdf/1706.03762v2.pdf", "arxiv", "1706.03762v2"); });
        Check("arXiv legacy PDF", delegate { Parsed("http://arxiv.org/pdf/hep-th/9711200.pdf", "arxiv", "hep-th/9711200"); });
        Check("arXiv HTML", delegate { Parsed("https://arxiv.org/html/1706.03762v2", "arxiv", "1706.03762v2"); });
        Check("INSPIRE literature", delegate { Parsed("https://inspirehep.net/literature/451647", "inspire", "451647"); });
        Check("INSPIRE API", delegate { Parsed("https://inspirehep.net/api/literature/451647?format=json", "inspire", "451647"); });
        Check("INSPIRE legacy record", delegate { Parsed("https://inspirehep.net/record/451647", "inspire", "451647"); });
        Check("INSPIRE prefixed identifier", delegate { Parsed("inspire:451647", "inspire", "451647"); });
        Check("DOI bare", delegate { Parsed("10.1000/example", "doi", "10.1000/example"); });
        Check("DOI prefixed", delegate { Parsed("doi:10.1000/example", "doi", "10.1000/example"); });
        Check("DOI URL", delegate { Parsed("https://doi.org/10.1000/example", "doi", "10.1000/example"); });
        Check("direct PDF query preserved", delegate { InputInfo p = DownloadEngine.ParseInput("https://files.example.test/a.pdf?token=abc"); Require(p.Kind == "pdf" && p.CanonicalUrl.EndsWith("?token=abc"), "URL/query changed"); });
        Check("arXiv lookalike host not trusted", delegate { UntrustedHost("https://arxiv.org.evil.example/abs/1706.03762", "arxiv"); });
        Check("INSPIRE lookalike host not trusted", delegate { UntrustedHost("https://inspirehep.net.evil.example/literature/451647", "inspire"); });
        Check("empty input rejected", delegate { Reject(""); });
        Check("file scheme rejected", delegate { Reject("file:///C:/Windows/win.ini"); });
        Check("unsupported text rejected", delegate { Reject("this is not a paper link"); });
        Check("malformed arXiv rejected", delegate { Reject("https://arxiv.org/abs/not-a-paper"); });
        Check("INSPIRE search not record", delegate { Reject("https://inspirehep.net/literature?q=gravity"); });

        await CheckAsync("direct PDF downloaded and repeat skipped", async delegate
        {
            FakeHandler handler = new FakeHandler(delegate { return Response(Pdf, "application/pdf"); });
            using (HttpClient client = new HttpClient(handler))
            {
                DownloadEngine engine = new DownloadEngine(client);
                string folder = Folder("direct");
                DownloadResult first = await engine.DownloadAsync("https://files.example.test/a.pdf", folder, null, CancellationToken.None);
                Require(File.Exists(first.FilePath), "missing final PDF");
                Require(File.ReadAllBytes(first.FilePath).SequenceEqual(Pdf), "downloaded bytes changed");
                Require(!first.Skipped, "initial download marked skipped");
                int calls = handler.Calls;
                DownloadResult second = await engine.DownloadAsync("https://files.example.test/a.pdf", folder, null, CancellationToken.None);
                Require(second.Skipped && second.FilePath == first.FilePath, "repeat not skipped");
                Require(handler.Calls == calls, "repeat made a redundant HTTP call");
                NoPart(folder);
            }
        });
        await CheckAsync("HTML response rejected without saved PDF", async delegate
        {
            await RejectBody("html", Encoding.UTF8.GetBytes("<html><body>Sign in to read this document.</body></html>"), "text/html", null);
        });
        await CheckAsync("truncated PDF rejected without saved PDF", async delegate
        {
            await RejectBody("truncated", Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<< /Type /Catalog >>\nendobj\n"), "application/pdf", null);
        });
        await CheckAsync("Content-Length mismatch rejected", async delegate
        {
            await RejectBody("length", Pdf, "application/pdf", Pdf.Length + 100);
        });
        await CheckAsync("octet-stream PDF accepted", async delegate
        {
            using (HttpClient client = new HttpClient(new FakeHandler(delegate { return Response(Pdf, "application/octet-stream"); })))
            {
                DownloadResult result = await new DownloadEngine(client).DownloadAsync("https://files.example.test/download?id=1", Folder("octet"), null, CancellationToken.None);
                Require(File.Exists(result.FilePath), "valid PDF rejected");
            }
        });
        await CheckAsync("INSPIRE arXiv failure falls back to document", async delegate
        {
            FakeHandler handler = new FakeHandler(delegate(HttpRequestMessage request)
            {
                string host = request.RequestUri.Host;
                if (host == "inspirehep.net") return Json("{\"metadata\":{\"titles\":[{\"title\":\"Fallback paper\"}],\"arxiv_eprints\":[{\"value\":\"1706.03762\"}],\"documents\":[{\"url\":\"https://files.example.test/public.pdf\"}]}}");
                if (host == "arxiv.org") return new HttpResponseMessage(HttpStatusCode.NotFound);
                if (host == "files.example.test") return Response(Pdf, "application/pdf");
                throw new InvalidOperationException("Unexpected URL: " + request.RequestUri);
            });
            using (HttpClient client = new HttpClient(handler))
            {
                DownloadEngine engine = new DownloadEngine(client);
                engine.MinimumArxivInterval = TimeSpan.Zero;
                DownloadResult result = await engine.DownloadAsync("inspire:451647", Folder("fallback"), null, CancellationToken.None);
                Require(File.Exists(result.FilePath), "fallback PDF missing");
                Require(handler.Urls.Any(u => u.Contains("arxiv.org/pdf/1706.03762")), "arXiv was not tried first");
                Require(handler.Urls.Any(u => u == "https://files.example.test/public.pdf"), "public document not used");
            }
        });
        await CheckAsync("INSPIRE hidden documents not downloaded", async delegate
        {
            FakeHandler handler = new FakeHandler(delegate(HttpRequestMessage request)
            {
                if (request.RequestUri.Host == "inspirehep.net") return Json("{\"metadata\":{\"titles\":[{\"title\":\"Hidden only\"}],\"documents\":[{\"hidden\":true,\"url\":\"https://files.example.test/hidden.pdf\"}]}}");
                throw new InvalidOperationException("Hidden URL requested");
            });
            using (HttpClient client = new HttpClient(handler))
            {
                await ThrowsAsync(delegate { return new DownloadEngine(client).DownloadAsync("inspire:451647", Folder("hidden"), null, CancellationToken.None); });
                Require(handler.Urls.All(u => !u.Contains("hidden.pdf")), "hidden document requested");
            }
        });
        await CheckAsync("DOI resolves via INSPIRE", async delegate
        {
            FakeHandler handler = new FakeHandler(delegate(HttpRequestMessage request)
            {
                if (request.RequestUri.Host == "inspirehep.net") return Json("{\"metadata\":{\"titles\":[{\"title\":\"DOI paper\"}],\"documents\":[{\"url\":\"https://files.example.test/doi.pdf\"}]}}");
                return Response(Pdf, "application/pdf");
            });
            using (HttpClient client = new HttpClient(handler))
            {
                DownloadResult result = await new DownloadEngine(client).DownloadAsync("10.1000/example", Folder("doi"), null, CancellationToken.None);
                Require(File.Exists(result.FilePath), "DOI PDF missing");
                Require(handler.Urls[0].Contains("inspirehep.net/api/doi/"), "DOI did not use INSPIRE");
            }
        });
        await CheckAsync("cancellation removes partial download", async delegate
        {
            string folder = Folder("cancel");
            FakeHandler handler = new FakeHandler(delegate
            {
                HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Content = new StreamContent(new SlowStream(Pdf));
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
                return response;
            });
            using (HttpClient client = new HttpClient(handler))
            using (CancellationTokenSource cancel = new CancellationTokenSource())
            {
                cancel.CancelAfter(200);
                bool cancelled = false;
                try { await new DownloadEngine(client).DownloadAsync("https://files.example.test/cancel.pdf", folder, null, cancel.Token); }
                catch (OperationCanceledException) { cancelled = true; }
                Require(cancelled, "cancellation was swallowed");
                Require(Directory.GetFiles(folder).Length == 0, "cancelled file left behind");
            }
        });
        await CheckAsync("bad existing file not treated as successful download", async delegate
        {
            FakeHandler handler = new FakeHandler(delegate { return Response(Pdf, "application/pdf"); });
            using (HttpClient client = new HttpClient(handler))
            {
                DownloadEngine engine = new DownloadEngine(client);
                engine.MinimumArxivInterval = TimeSpan.Zero;
                string folder = Folder("corrupt");
                string bad = Path.Combine(folder, "arxiv-1706.03762.pdf");
                File.WriteAllText(bad, "not a PDF");
                DownloadResult result = await engine.DownloadAsync("1706.03762", folder, null, CancellationToken.None);
                Require(!result.Skipped, "corrupt file marked as skipped success");
                Require(File.ReadAllBytes(result.FilePath).SequenceEqual(Pdf), "valid replacement/new file missing");
                NoPart(folder);
            }
        });
    }

    private static string Folder(string name) { string path = Path.Combine(Root, name); Directory.CreateDirectory(path); return path; }
    private static void NoPart(string folder) { Require(!Directory.GetFiles(folder).Any(p => p.EndsWith(".part", StringComparison.OrdinalIgnoreCase)), "partial file left behind"); }
    private static void Parsed(string input, string kind, string identifier) { InputInfo parsed = DownloadEngine.ParseInput(input); Require(parsed.Kind == kind, "kind " + parsed.Kind); Require(parsed.Identifier == identifier, "identifier " + parsed.Identifier); }
    private static void UntrustedHost(string input, string trustedKind) { InputInfo parsed; try { parsed = DownloadEngine.ParseInput(input); } catch (ArgumentException) { return; } catch (InvalidOperationException) { return; } Require(parsed.Kind != trustedKind, "lookalike interpreted as " + trustedKind); }
    private static void Reject(string input) { bool rejected = false; try { DownloadEngine.ParseInput(input); } catch (Exception) { rejected = true; } Require(rejected, "invalid input accepted: " + input); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Check(string name, Action test) { try { test(); passed++; Console.WriteLine("PASS " + name); } catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e.Message); } }
    private static async Task CheckAsync(string name, Func<Task> test) { try { await test(); passed++; Console.WriteLine("PASS " + name); } catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e.GetType().Name + " " + e.Message); } }
    private static async Task ThrowsAsync(Func<Task> test) { bool threw = false; try { await test(); } catch (Exception) { threw = true; } Require(threw, "expected failure did not occur"); }
    private static HttpResponseMessage Response(byte[] data, string type) { HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.OK); response.Content = new ByteArrayContent(data); response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type); return response; }
    private static HttpResponseMessage Json(string json) { return Response(Encoding.UTF8.GetBytes(json), "application/json"); }
    private static async Task RejectBody(string name, byte[] body, string type, int? length)
    {
        string folder = Folder(name);
        using (HttpClient client = new HttpClient(new FakeHandler(delegate { HttpResponseMessage response = Response(body, type); if (length.HasValue) response.Content.Headers.ContentLength = length.Value; return response; })))
        {
            await ThrowsAsync(delegate { return new DownloadEngine(client).DownloadAsync("https://files.example.test/" + name + ".pdf", folder, null, CancellationToken.None); });
            Require(Directory.GetFiles(folder).Length == 0, "failed download left a file");
        }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> respond;
        public readonly List<string> Urls = new List<string>();
        public int Calls { get { return Urls.Count; } }
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) { this.respond = respond; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Urls.Add(request.RequestUri.AbsoluteUri);
            HttpResponseMessage response = respond(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private sealed class SlowStream : MemoryStream
    {
        public SlowStream(byte[] bytes) : base(bytes) { }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await Task.Delay(10000, cancellationToken);
            return await base.ReadAsync(buffer, offset, count, cancellationToken);
        }
    }
}
