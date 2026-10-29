using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PaperDrop;

internal static class CitationTests
{
    private static int count;
    private static string Atom(string id, string title)
    {
        return "<?xml version='1.0'?><feed xmlns='http://www.w3.org/2005/Atom' xmlns:arxiv='http://arxiv.org/schemas/atom'><entry>" +
            "<id>https://arxiv.org/abs/" + id + "</id><title>" + title + "</title><published>2023-03-14T00:00:00Z</published><updated>2024-05-10T00:00:00Z</updated>" +
            "<author><name>Alice Smith</name></author><author><name>Bob Jones</name></author><arxiv:doi>10.1234/test_ab</arxiv:doi><arxiv:primary_category term='hep-th'/></entry></feed>";
    }
    private const string Json = "{\"id\":\"451647\",\"metadata\":{\"control_number\":451647,\"titles\":[{\"title\":\"The Large $N$ limit & test_50%\"}],\"authors\":[{\"full_name\":\"Maldacena, Juan Martin\"}],\"texkeys\":[\"Maldacena:1997re\"],\"publication_info\":[{\"journal_title\":\"Adv. Theor. Math. Phys.\",\"journal_volume\":\"2\",\"journal_issue\":\"2\",\"year\":1998,\"page_start\":\"231\",\"page_end\":\"252\"}],\"arxiv_eprints\":[{\"value\":\"hep-th/9711200\",\"categories\":[\"hep-th\"]}],\"dois\":[{\"value\":\"10.4310/ATMP.1998.v2.n2.a1\"}],\"preprint_date\":\"1997-11\"}}";
    private sealed class Fake : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Send;
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { Calls++; return Send(request, token); }
    }
    private sealed class StalledStream : Stream
    {
        private readonly TaskCompletionSource<int> pending = new TaskCompletionSource<int>();
        public bool WasDisposed;
        public override bool CanRead { get { return true; } }
        public override bool CanWrite { get { return false; } }
        public override bool CanSeek { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellation) { return pending.Task; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        protected override void Dispose(bool disposing) { WasDisposed = true; pending.TrySetException(new ObjectDisposedException("StalledStream")); base.Dispose(disposing); }
    }
    private static Task<HttpResponseMessage> Reply(string text) { return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8) }); }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); count++; Console.WriteLine("PASS " + message); }
    private static void Contains(string value, string needle, string message) { Assert(value.Contains(needle), message + " [" + needle + "]"); }
    private static async Task Reject(Func<Task> action, string message)
    {
        try { await action(); } catch (InvalidOperationException) { Assert(true, message); return; }
        throw new Exception("Expected rejection: " + message);
    }
    private static async Task Run()
    {
        var fake = new Fake { Send = (request, token) => Reply(Atom("2303.08774v3", "A $N$ study &amp; test_50%")) };
        using (var http = new HttpClient(fake))
        using (var engine = new CitationEngine(http) { MinimumArxivInterval = TimeSpan.Zero })
        {
            CitationResult result = await engine.GenerateAsync("https://arxiv.org/pdf/2303.08774", CancellationToken.None);
            Assert(result.Url == "https://arxiv.org/abs/2303.08774", "unversioned canonical citation URL");
            Contains(result.BibTeX, "year = {2023}", "original publication year");
            Contains(result.BibTeX, "Alice Smith and Bob Jones", "all authors retained");
            Contains(result.BibTeX, "eprint = {2303.08774}", "arxiv eprint included");
            Contains(result.BibTeX, "archivePrefix = {arXiv}", "arxiv archive included");
            Contains(result.BibTeX, "primaryClass = {hep-th}", "arxiv subject included");
            Contains(result.BibTeX, "doi = {10.1234/test_ab}", "DOI is verbatim metadata");
            Contains(result.BibTeX, "A $N$ study \\& test\\_50\\%", "math preserved and ordinary text escaped");
            Contains(result.BibItem, "\\href{https://arxiv.org/abs/2303.08774}", "clickable title link");
            await engine.GenerateAsync("arxiv:2303.08774", CancellationToken.None);
            Assert(fake.Calls == 1, "equivalent citation inputs cached");
            CitationResult version = await engine.GenerateAsync("2303.08774v3", CancellationToken.None);
            Assert(version.Url.EndsWith("v3"), "explicit version URL preserved");
            Contains(version.BibTeX, "year = {2024}", "explicit revision date used");
            Assert(version.Key != result.Key, "explicit version gets distinct key");
            result.Key = "changed_by_gui";
            result.BibTeX = "changed_by_gui";
            CitationResult fresh = await engine.GenerateAsync("2303.08774", CancellationToken.None);
            Assert(fresh.Key != "changed_by_gui" && fresh.BibTeX != "changed_by_gui", "caller mutation cannot poison citation cache");
        }
        fake = new Fake { Send = (request, token) => Reply(Json) };
        using (var http = new HttpClient(fake))
        using (var engine = new CitationEngine(http))
        {
            CitationResult result = await engine.GenerateAsync("inspire:451647", CancellationToken.None);
            Assert(result.Key == "Maldacena:1997re", "INSPIRE citation key retained");
            Assert(result.Url == "https://inspirehep.net/literature/451647", "INSPIRE public link");
            Contains(result.BibTeX, "@article{", "published entry is an article");
            Contains(result.BibTeX, "pages = {231--252}", "page interval retained");
            Contains(result.BibTeX, "year = {1998}", "journal publication year wins");
            Contains(result.BibTeX, "volume = {2}", "journal volume retained");
            Contains(result.BibItem, "\\href{https://doi.org/10.4310/ATMP.1998.v2.n2.a1}", "clickable DOI");
            CitationResult doi = await engine.GenerateAsync("doi:10.4310/ATMP.1998.v2.n2.a1", CancellationToken.None);
            Assert(doi.Title == result.Title, "DOI resolves metadata");
            int calls = fake.Calls;
            await Reject(() => engine.GenerateAsync("https://example.org/paper.pdf", CancellationToken.None), "PDF-only inputs do not invent authors");
            Assert(fake.Calls == calls, "unresolvable direct PDF does not query metadata");
        }
        string mixed = Json.Replace("\"publication_info\":[{", "\"publication_info\":[{\"material\":\"reprint\",\"journal_title\":\"Reprint Journal\",\"year\":1999},{\"material\":\"publication\",")
            .Replace("\"dois\":[{\"value\":", "\"dois\":[{\"value\":\"10.1234/reprint\",\"material\":\"reprint\"},{\"material\":\"publication\",\"value\":");
        fake = new Fake { Send = (request, token) => Reply(mixed) };
        using (var http = new HttpClient(fake))
        using (var engine = new CitationEngine(http))
        {
            CitationResult original = await engine.GenerateAsync("inspire:451647", CancellationToken.None);
            Contains(original.BibTeX, "journal = {Adv. Theor. Math. Phys.}", "original publication preferred over first-listed reprint");
            Contains(original.BibTeX, "doi = {10.4310/ATMP.1998.v2.n2.a1}", "original DOI aligned with publication");
            CitationResult reprint = await engine.GenerateAsync("doi:10.1234/reprint", CancellationToken.None);
            Contains(reprint.BibTeX, "journal = {Reprint Journal}", "explicit reprint DOI selects matching publication");
            Contains(reprint.BibTeX, "doi = {10.1234/reprint}", "explicit DOI preserved");
        }
        fake = new Fake { Send = (request, token) => request.RequestUri.Host == "export.arxiv.org" ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)) : Reply(Json) };
        using (var http = new HttpClient(fake))
        using (var engine = new CitationEngine(http) { MinimumArxivInterval = TimeSpan.Zero })
        {
            CitationResult fallback = await engine.GenerateAsync("hep-th/9711200", CancellationToken.None);
            Assert(fallback.Url == "https://arxiv.org/abs/hep-th/9711200", "INSPIRE fallback retains arxiv link");
            await Reject(() => engine.GenerateAsync("hep-th/9711200v1", CancellationToken.None), "unverified historical version is refused");
            await Reject(() => engine.GenerateAsync("2303.08774", CancellationToken.None), "mismatched fallback paper is refused");
        }
        fake = new Fake { Send = (request, token) => Reply(Atom("2303.08774v1", "Normal $\\input{secret}$ \\input{outside} $\\mathbb{R}$")) };
        using (var http = new HttpClient(fake))
        using (var engine = new CitationEngine(http))
        {
            CitationResult result = await engine.GenerateAsync("2303.08774v1", CancellationToken.None);
            Assert(!result.BibTeX.Contains("\\input{"), "metadata cannot inject arbitrary TeX commands");
            Contains(result.BibTeX, "$\\mathbb{R}$", "safe mathematical commands retained");
            Contains(result.BibTeX, "\\textbackslash{}input", "unsafe commands become readable text");
        }
        fake = new Fake { Send = (request, token) => Reply("{\"metadata\":{\"titles\":[]}}") };
        using (var http = new HttpClient(fake))
        using (var engine = new CitationEngine(http))
            await Reject(() => engine.GenerateAsync("inspire:123", CancellationToken.None), "missing title is not fabricated");
        fake = new Fake { Send = async (request, token) => { await Task.Delay(5000, token); return new HttpResponseMessage(HttpStatusCode.OK); } };
        using (var http = new HttpClient(fake))
        using (var engine = new CitationEngine(http))
        using (var cancel = new CancellationTokenSource(80))
        {
            try { await engine.GenerateAsync("inspire:451647", cancel.Token); throw new Exception("Cancellation missing"); }
            catch (OperationCanceledException) { Assert(true, "network generation respects cancellation"); }
        }
        fake = new Fake { Send = (request, token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)) };
        using (var http = new HttpClient(fake))
        using (var engine = new CitationEngine(http))
        {
            try { await engine.GenerateAsync("doi:10.1234/missing", CancellationToken.None); throw new Exception("Missing DOI accepted"); }
            catch (InvalidOperationException ex) { Contains(ex.Message, "INSPIRE", "unsupported DOI error identifies coverage limitation"); }
        }
        var stall = new StalledStream();
        fake = new Fake { Send = (request, token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stall) }) };
        using (var http = new HttpClient(fake))
        using (var engine = new CitationEngine(http))
        using (var cancel = new CancellationTokenSource(100))
        {
            var timer = Stopwatch.StartNew();
            try { await engine.GenerateAsync("inspire:451647", cancel.Token); throw new Exception("Body cancellation missing"); }
            catch (OperationCanceledException)
            {
                Assert(stall.WasDisposed, "stalled response stream forcibly closed on cancellation");
                Assert(timer.Elapsed < TimeSpan.FromSeconds(2), "ignored ReadAsync cancellation cannot hang GUI close");
            }
        }
        Console.WriteLine("TOTAL " + count + " assertions passed");
    }
    private static async Task Smoke(string[] inputs)
    {
        var bibs = new List<string>();
        var items = new List<string>();
        using (var engine = new CitationEngine())
            foreach (string input in inputs)
            {
                var timer = Stopwatch.StartNew();
                CitationResult result = await engine.GenerateAsync(input, CancellationToken.None);
                Console.WriteLine("LIVE " + input + " [" + timer.Elapsed.TotalSeconds.ToString("F1") + "s]");
                Console.WriteLine(result.BibTeX);
                Console.WriteLine(result.BibItem);
                bibs.Add(result.BibTeX);
                items.Add(result.BibItem);
            }
        Directory.CreateDirectory("work/citation-smoke");
        File.WriteAllText("work/citation-smoke/references.bib", String.Join("\r\n\r\n", bibs), new UTF8Encoding(false));
        File.WriteAllText("work/citation-smoke/bibitems.tex", String.Join("\r\n\r\n", items), new UTF8Encoding(false));
    }
    public static int Main(string[] args)
    {
        try { if (args.Length > 0) Smoke(args).GetAwaiter().GetResult(); else Run().GetAwaiter().GetResult(); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
