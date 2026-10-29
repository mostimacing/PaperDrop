using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace PaperDrop
{
    public sealed class InputInfo
    {
        public string Kind;
        public string Identifier;
        public string CanonicalUrl;
    }

    public sealed class DownloadProgress
    {
        public string Stage;
        public long BytesReceived;
        public long? TotalBytes;
    }

    public sealed class DownloadResult
    {
        public string FilePath;
        public string Title;
        public bool Skipped;
        public string Source;
    }

    /// <summary>Serial, cancellable PDF downloader using public arXiv and INSPIRE endpoints.</summary>
    public sealed class DownloadEngine : IDisposable
    {
        private const long MaxPdfBytes = 300L * 1024 * 1024;
        private const int MaxMetadataBytes = 8 * 1024 * 1024;
        private readonly HttpClient client;
        private readonly bool ownsClient;
        private readonly SemaphoreSlim serial = new SemaphoreSlim(1, 1);
        private DateTime lastArxivRequest = DateTime.MinValue;
        private bool disposed;

        // The override supports deterministic offline tests. Production uses three seconds.
        public TimeSpan MinimumArxivInterval { get; set; }

        public DownloadEngine() : this(CreateClient(), true) { }
        public DownloadEngine(HttpClient client) : this(client, false) { }

        private DownloadEngine(HttpClient client, bool ownsClient)
        {
            if (client == null) throw new ArgumentNullException("client");
            this.client = client;
            this.ownsClient = ownsClient;
            MinimumArxivInterval = TimeSpan.FromSeconds(3);
        }

        private static HttpClient CreateClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 8,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                UseCookies = false
            };
            var value = new HttpClient(handler);
            value.Timeout = Timeout.InfiniteTimeSpan;
            value.DefaultRequestHeaders.UserAgent.ParseAdd("PaperDrop/1.0 (personal academic PDF downloader)");
            return value;
        }

        public static InputInfo ParseInput(string input)
        {
            if (String.IsNullOrWhiteSpace(input))
                throw new InvalidOperationException("请粘贴论文链接或编号。");
            string value = input.Trim().Trim('\uFEFF');
            if (value.Length > 4096)
                throw new InvalidOperationException("链接过长，请检查是否误粘贴了整段正文。");
            if (value.StartsWith("arxiv:", StringComparison.OrdinalIgnoreCase))
                return Arxiv(value.Substring(6).Trim());
            if (value.StartsWith("inspire:", StringComparison.OrdinalIgnoreCase))
            {
                string id = value.Substring(8).Trim();
                if (!Regex.IsMatch(id, @"^[1-9][0-9]*$"))
                    throw new InvalidOperationException("INSPIRE 编号应为正整数，例如 inspire:451647。");
                return Inspire(id);
            }
            if (value.StartsWith("doi:", StringComparison.OrdinalIgnoreCase))
                return Doi(value.Substring(4).Trim());
            if (IsArxivId(value)) return Arxiv(value);
            if (Regex.IsMatch(value, @"^10\.\d{4,9}/\S+$", RegexOptions.IgnoreCase))
                return Doi(value);
            if (Regex.IsMatch(value, @"^(www\.)?(arxiv\.org|export\.arxiv\.org|inspirehep\.net|doi\.org|dx\.doi\.org)/", RegexOptions.IgnoreCase))
                value = "https://" + value;
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || !IsHttp(uri) || !String.IsNullOrEmpty(uri.UserInfo))
                throw new InvalidOperationException("无法识别输入。请使用 arXiv、INSPIRE、DOI 链接或直接指向 PDF 的 http/https 链接。");
            string host = uri.Host.ToLowerInvariant();
            string path = Uri.UnescapeDataString(uri.AbsolutePath).TrimEnd('/');
            if (host == "arxiv.org" || host == "www.arxiv.org" || host == "export.arxiv.org")
            {
                var match = Regex.Match(path, @"^/(?:abs|pdf|html)/(.+)$", RegexOptions.IgnoreCase);
                if (!match.Success)
                    throw new InvalidOperationException("请使用 arXiv 单篇论文的 abs、pdf 或 html 链接。");
                string id = Regex.Replace(match.Groups[1].Value, @"\.pdf$", "", RegexOptions.IgnoreCase);
                return Arxiv(id);
            }
            if (host == "inspirehep.net" || host == "www.inspirehep.net")
            {
                var match = Regex.Match(path, @"^/(?:api/)?(?:literature|record)/([1-9][0-9]*)$", RegexOptions.IgnoreCase);
                if (match.Success) return Inspire(match.Groups[1].Value);
                if (path.StartsWith("/api/doi/", StringComparison.OrdinalIgnoreCase))
                    return Doi(path.Substring(9));
                if (path.StartsWith("/api/arxiv/", StringComparison.OrdinalIgnoreCase))
                    return Arxiv(path.Substring(11));
                if (!path.StartsWith("/files/", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("请使用 INSPIRE 单篇论文链接，例如 https://inspirehep.net/literature/451647；搜索结果页暂不支持。");
            }
            if (host == "doi.org" || host == "dx.doi.org" || host == "www.doi.org")
                return Doi(path.TrimStart('/'));
            var clean = new UriBuilder(uri) { Fragment = "" }.Uri;
            return new InputInfo { Kind = "pdf", Identifier = clean.AbsoluteUri, CanonicalUrl = clean.AbsoluteUri };
        }

        private static bool IsArxivId(string value)
        {
            return Regex.IsMatch(value, @"^(?:\d{2}(?:0[1-9]|1[0-2])\.\d{4,5}|[a-z][a-z0-9.-]*/\d{2}(?:0[1-9]|1[0-2])\d{3})(?:v[1-9]\d*)?$", RegexOptions.IgnoreCase);
        }

        private static InputInfo Arxiv(string id)
        {
            if (!IsArxivId(id)) throw new InvalidOperationException("arXiv 编号格式不正确，例如 2303.08774、hep-th/9711200 或 2303.08774v1。");
            id = id.ToLowerInvariant();
            return new InputInfo { Kind = "arxiv", Identifier = id, CanonicalUrl = "https://arxiv.org/pdf/" + id };
        }

        private static InputInfo Inspire(string id)
        {
            return new InputInfo { Kind = "inspire", Identifier = id, CanonicalUrl = "https://inspirehep.net/api/literature/" + id };
        }

        private static InputInfo Doi(string id)
        {
            if (!Regex.IsMatch(id, @"^10\.\d{4,9}/\S+$", RegexOptions.IgnoreCase))
                throw new InvalidOperationException("DOI 格式不正确，例如 10.4310/ATMP.1998.v2.n2.a1。");
            return new InputInfo { Kind = "doi", Identifier = id, CanonicalUrl = "https://inspirehep.net/api/doi/" + EscapePath(id) };
        }

        private static string EscapePath(string value)
        {
            string[] pieces = value.Split('/');
            for (int i = 0; i < pieces.Length; i++) pieces[i] = Uri.EscapeDataString(pieces[i]);
            return String.Join("/", pieces);
        }

        public async Task<DownloadResult> DownloadAsync(string input, string outputDirectory, IProgress<DownloadProgress> progress, CancellationToken cancellation)
        {
            if (disposed) throw new ObjectDisposedException("DownloadEngine");
            InputInfo info = ParseInput(input);
            if (String.IsNullOrWhiteSpace(outputDirectory)) throw new InvalidOperationException("请先选择保存文件夹。");
            string directory;
            try
            {
                directory = Path.GetFullPath(outputDirectory);
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex)
            {
                if (!(ex is IOException) && !(ex is UnauthorizedAccessException) && !(ex is ArgumentException) && !(ex is NotSupportedException)) throw;
                throw new InvalidOperationException("无法使用保存文件夹：" + ex.Message, ex);
            }
            await serial.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                cancellation.ThrowIfCancellationRequested();
                string fileStem = FileStem(info);
                string defaultTitle = info.Kind == "pdf" ? Path.GetFileName(fileStem) : info.Kind.ToUpperInvariant() + " " + info.Identifier;
                string target = SelectTarget(directory, fileStem);
                if (IsCompletePdf(target))
                {
                    Report(progress, "已存在，跳过下载", 0, null);
                    return new DownloadResult { FilePath = target, Title = defaultTitle, Skipped = true, Source = info.CanonicalUrl };
                }
                Report(progress, "正在解析论文信息", 0, null);
                ResolvedPaper paper;
                try
                {
                    paper = await ResolveAsync(info, progress, cancellation).ConfigureAwait(false);
                }
                catch (RemoteException ex)
                {
                    if (info.Kind == "doi" && ex.Status == 404)
                        throw new InvalidOperationException("INSPIRE 未收录这个 DOI。请改用 arXiv 链接或可公开访问的 PDF 直链。", ex);
                    throw;
                }
                var errors = new List<string>();
                foreach (string url in paper.Candidates)
                {
                    cancellation.ThrowIfCancellationRequested();
                    try
                    {
                        string saved = await SavePdfAsync(url, target, progress, cancellation).ConfigureAwait(false);
                        return new DownloadResult { FilePath = saved, Title = paper.Title ?? defaultTitle, Skipped = false, Source = url };
                    }
                    catch (RemoteException ex) { errors.Add(ex.Message); }
                    catch (InvalidDataException ex) { errors.Add(ex.Message); }
                    if (paper.Candidates.Count > 1) Report(progress, "此来源不可用，尝试其他公开全文", 0, null);
                }
                string detail = errors.Count == 0 ? "未找到公开 PDF。" : errors[errors.Count - 1];
                throw new InvalidOperationException("下载失败：" + detail);
            }
            catch (HttpRequestException ex)
            {
                throw new InvalidOperationException("网络连接失败，请检查网络或系统代理后重试。" + Detail(ex), ex);
            }
            finally { serial.Release(); }
        }

        private sealed class ResolvedPaper
        {
            public string Title;
            public List<string> Candidates = new List<string>();
        }

        private async Task<ResolvedPaper> ResolveAsync(InputInfo input, IProgress<DownloadProgress> progress, CancellationToken cancellation)
        {
            var result = new ResolvedPaper();
            if (input.Kind == "arxiv" || input.Kind == "pdf")
            {
                result.Candidates.Add(input.CanonicalUrl);
                return result;
            }
            string json = await GetJsonAsync(input.CanonicalUrl, progress, cancellation).ConfigureAwait(false);
            Dictionary<string, object> root;
            try { root = new JavaScriptSerializer { MaxJsonLength = MaxMetadataBytes, RecursionLimit = 120 }.DeserializeObject(json) as Dictionary<string, object>; }
            catch (Exception ex)
            {
                if (!(ex is ArgumentException) && !(ex is InvalidOperationException)) throw;
                throw new InvalidOperationException("INSPIRE 返回的信息无法解析，请稍后重试。", ex);
            }
            var metadata = Map(Get(root, "metadata"));
            if (metadata == null) throw new InvalidOperationException("INSPIRE 未返回有效的论文记录。");
            foreach (var title in Items(Get(metadata, "titles")))
            {
                string text = Text(Get(title, "title"));
                if (!String.IsNullOrWhiteSpace(text)) { result.Title = text; break; }
            }
            foreach (var eprint in Items(Get(metadata, "arxiv_eprints")))
            {
                string id = Text(Get(eprint, "value"));
                if (id != null && IsArxivId(id)) AddCandidate(result, Arxiv(id).CanonicalUrl);
            }
            foreach (var doc in Items(Get(metadata, "documents")))
            {
                object hidden = Get(doc, "hidden");
                if (hidden is bool && (bool)hidden) continue;
                string fulltext = Text(Get(doc, "url"));
                AddCandidate(result, fulltext);
            }
            foreach (var link in Items(Get(metadata, "urls")))
            {
                string url = Text(Get(link, "value"));
                Uri parsed;
                if (url != null && Uri.TryCreate(url, UriKind.Absolute, out parsed) &&
                    (parsed.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || Text(Get(link, "description")).IndexOf("pdf", StringComparison.OrdinalIgnoreCase) >= 0))
                    AddCandidate(result, url);
            }
            if (result.Candidates.Count == 0)
                throw new InvalidOperationException("该 INSPIRE 记录没有 arXiv 或可公开下载的 PDF。请提供论文的公开 PDF 直链。");
            return result;
        }

        private static void AddCandidate(ResolvedPaper result, string url)
        {
            Uri uri;
            if (String.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out uri) || !IsHttp(uri) || !String.IsNullOrEmpty(uri.UserInfo)) return;
            string canonical = uri.AbsoluteUri;
            if (!result.Candidates.Contains(canonical) && result.Candidates.Count < 20) result.Candidates.Add(canonical);
        }

        private async Task<string> GetJsonAsync(string url, IProgress<DownloadProgress> progress, CancellationToken cancellation)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                    {
                        deadline.CancelAfter(TimeSpan.FromSeconds(60));
                        using (HttpResponseMessage response = await SendAsync(url, "application/json", deadline.Token).ConfigureAwait(false))
                        {
                            if (await ShouldRetryAsync(response, attempt, progress, cancellation).ConfigureAwait(false)) continue;
                            CheckStatus(response);
                            if (response.Content.Headers.ContentLength > MaxMetadataBytes) throw new InvalidOperationException("INSPIRE 返回的数据过大，请尝试单篇论文链接。");
                            using (Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            using (var buffer = new MemoryStream())
                            using (deadline.Token.Register(delegate { stream.Dispose(); }))
                            {
                                byte[] bytes = new byte[32768];
                                int read;
                                while ((read = await stream.ReadAsync(bytes, 0, bytes.Length, deadline.Token).ConfigureAwait(false)) > 0)
                                {
                                    if (buffer.Length + read > MaxMetadataBytes) throw new InvalidOperationException("INSPIRE 返回的数据过大，请尝试单篇论文链接。");
                                    buffer.Write(bytes, 0, read);
                                }
                                return Encoding.UTF8.GetString(buffer.ToArray());
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (attempt >= 2) throw new RemoteException("读取 INSPIRE 信息超时，请检查网络后重试。", 0);
                }
                catch (HttpRequestException ex)
                {
                    if (attempt >= 2) throw new RemoteException("无法连接 INSPIRE，请检查网络或系统代理。" + Detail(ex), 0);
                }
                catch (IOException ex)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (attempt >= 2) throw new RemoteException("读取 INSPIRE 信息时连接中断。" + Detail(ex), 0);
                }
                catch (ObjectDisposedException)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (attempt >= 2) throw new RemoteException("读取 INSPIRE 信息超时，请重试。", 0);
                }
                await RetryDelayAsync(attempt, progress, cancellation).ConfigureAwait(false);
            }
        }

        private async Task<string> SavePdfAsync(string url, string target, IProgress<DownloadProgress> progress, CancellationToken cancellation)
        {
            for (int attempt = 0; ; attempt++)
            {
                string temporary = target + "." + Guid.NewGuid().ToString("N") + ".part";
                try
                {
                    using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                    {
                        deadline.CancelAfter(TimeSpan.FromMinutes(10));
                        using (HttpResponseMessage response = await SendAsync(url, "application/pdf, application/octet-stream;q=0.9, */*;q=0.5", deadline.Token).ConfigureAwait(false))
                        {
                            if (await ShouldRetryAsync(response, attempt, progress, cancellation).ConfigureAwait(false)) continue;
                            CheckStatus(response);
                            long? expected = response.Content.Headers.ContentLength;
                            if (expected > MaxPdfBytes) throw new InvalidDataException("文件超过 300 MB，本工具仅用于单篇论文下载。");
                            long count = 0;
                            byte[] head = new byte[1024];
                            int headLength = 0;
                            DateTime nextProgress = DateTime.MinValue;
                            using (Stream incoming = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            using (deadline.Token.Register(delegate { incoming.Dispose(); }))
                            using (var outgoing = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                            {
                                byte[] buffer = new byte[65536];
                                int read;
                                while ((read = await incoming.ReadAsync(buffer, 0, buffer.Length, deadline.Token).ConfigureAwait(false)) > 0)
                                {
                                    cancellation.ThrowIfCancellationRequested();
                                    count += read;
                                    if (count > MaxPdfBytes) throw new InvalidDataException("文件超过 300 MB，已停止下载。");
                                    if (headLength < head.Length)
                                    {
                                        int copy = Math.Min(read, head.Length - headLength);
                                        Buffer.BlockCopy(buffer, 0, head, headLength, copy);
                                        headLength += copy;
                                        if (headLength == head.Length && !HasPdfHeader(head, headLength))
                                            throw new InvalidDataException("链接返回的是网页或其他内容，没有返回 PDF。可能需要登录，或该链接不是 PDF 直链。");
                                    }
                                    await outgoing.WriteAsync(buffer, 0, read, deadline.Token).ConfigureAwait(false);
                                    if (DateTime.UtcNow >= nextProgress)
                                    {
                                        Report(progress, "正在下载 PDF", count, expected);
                                        nextProgress = DateTime.UtcNow.AddMilliseconds(120);
                                    }
                                }
                                if (!HasPdfHeader(head, headLength)) throw new InvalidDataException("链接没有返回有效 PDF，请使用公开 PDF 直链。");
                                if (expected.HasValue && count != expected.Value) throw new InvalidDataException("下载不完整，实际大小与服务器声明不一致，请重试。");
                                await outgoing.FlushAsync(deadline.Token).ConfigureAwait(false);
                            }
                            if (!IsCompletePdf(temporary)) throw new InvalidDataException("PDF 文件不完整（缺少文件结束标记），请重试。");
                            cancellation.ThrowIfCancellationRequested();
                            // File.Move is atomic on this volume and never overwrites a user's file.
                            string finalPath = target;
                            for (int suffix = 2; File.Exists(finalPath); suffix++)
                                finalPath = Path.Combine(Path.GetDirectoryName(target), Path.GetFileNameWithoutExtension(target) + " (" + suffix.ToString(CultureInfo.InvariantCulture) + ").pdf");
                            File.Move(temporary, finalPath);
                            Report(progress, "下载完成", count, count);
                            return finalPath;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (attempt >= 2) throw new RemoteException("下载超时，请检查网络后重试。", 0);
                }
                catch (HttpRequestException ex)
                {
                    if (attempt >= 2) throw new RemoteException("无法连接 PDF 来源，请检查网络或系统代理。" + Detail(ex), 0);
                }
                catch (ObjectDisposedException)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (attempt >= 2) throw new RemoteException("下载超时或连接中断，请重试。", 0);
                }
                catch (IOException ex)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (attempt >= 2) throw new RemoteException("下载中断或无法写入文件：" + ex.Message, 0);
                }
                finally
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                await RetryDelayAsync(attempt, progress, cancellation).ConfigureAwait(false);
            }
        }

        private async Task<HttpResponseMessage> SendAsync(string url, string accept, CancellationToken cancellation)
        {
            Uri uri = new Uri(url);
            if (uri.Host == "arxiv.org" || uri.Host.EndsWith(".arxiv.org", StringComparison.OrdinalIgnoreCase))
            {
                TimeSpan delay = MinimumArxivInterval - (DateTime.UtcNow - lastArxivRequest);
                if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellation).ConfigureAwait(false);
                lastArxivRequest = DateTime.UtcNow;
            }
            using (var request = new HttpRequestMessage(HttpMethod.Get, uri))
            {
                request.Headers.Accept.ParseAdd(accept);
                return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            }
        }

        private static async Task<bool> ShouldRetryAsync(HttpResponseMessage response, int attempt, IProgress<DownloadProgress> progress, CancellationToken cancellation)
        {
            int status = (int)response.StatusCode;
            if ((status != 429 && status != 408 && status < 500) || attempt >= 2) return false;
            TimeSpan wait = TimeSpan.FromSeconds(status == 429 ? 5 * (attempt + 1) : 2 * (attempt + 1));
            var retry = response.Headers.RetryAfter;
            if (retry != null)
            {
                TimeSpan requested = retry.Delta ?? (retry.Date.HasValue ? retry.Date.Value - DateTimeOffset.UtcNow : TimeSpan.Zero);
                if (requested > wait) wait = requested;
            }
            if (wait > TimeSpan.FromMinutes(10))
                throw new RemoteException("来源服务器要求较长时间后重试，请稍后再次下载。", status);
            Report(progress, "服务器繁忙，" + Math.Ceiling(wait.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " 秒后重试", 0, null);
            response.Dispose();
            await Task.Delay(wait, cancellation).ConfigureAwait(false);
            return true;
        }

        private static Task RetryDelayAsync(int attempt, IProgress<DownloadProgress> progress, CancellationToken cancellation)
        {
            Report(progress, "连接中断，正在重试", 0, null);
            return Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), cancellation);
        }

        private static void CheckStatus(HttpResponseMessage response)
        {
            if (response.IsSuccessStatusCode) return;
            int code = (int)response.StatusCode;
            string description;
            if (code == 404) description = "论文或文件不存在（404），请检查链接或编号。";
            else if (code == 401 || code == 403) description = "来源拒绝访问（" + code + "），可能需要登录或订阅。";
            else if (code == 429) description = "来源请求过于频繁（429），请稍后重试。";
            else description = "来源服务器返回错误（HTTP " + code + "），请稍后重试。";
            throw new RemoteException(description, code);
        }

        private sealed class RemoteException : InvalidOperationException
        {
            public int Status;
            public RemoteException(string message, int status) : base(message) { Status = status; }
        }

        private static string FileStem(InputInfo input)
        {
            if (input.Kind == "arxiv" || input.Kind == "inspire") return input.Kind + "-" + input.Identifier.Replace('/', '-');
            if (input.Kind == "doi") return "doi-" + Hash(input.Identifier.ToLowerInvariant());
            string name = Uri.UnescapeDataString(new Uri(input.CanonicalUrl).AbsolutePath);
            name = name.Substring(name.LastIndexOf('/') + 1);
            if (name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
            name = Regex.Replace(name, @"[<>:""/\\|?*\x00-\x1f]", "_").Trim(' ', '.');
            if (name.Length > 70) name = name.Substring(0, 70);
            if (String.IsNullOrEmpty(name)) name = "paper";
            // Prefix also prevents Windows reserved names such as CON.pdf.
            return "pdf-" + name + "-" + Hash(input.CanonicalUrl);
        }

        private static string Hash(string value)
        {
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").Substring(0, 12).ToLowerInvariant();
        }

        private static string SelectTarget(string directory, string fileStem)
        {
            string target = Path.Combine(directory, fileStem + ".pdf");
            // Preserve broken or unrelated files; subsequent runs find the completed numbered file.
            for (int suffix = 2; File.Exists(target) && !IsCompletePdf(target); suffix++)
                target = Path.Combine(directory, fileStem + " (" + suffix.ToString(CultureInfo.InvariantCulture) + ").pdf");
            return target;
        }

        public static bool IsCompletePdf(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Length < 12) return false;
                    byte[] head = new byte[Math.Min(1024L, stream.Length)];
                    int read = stream.Read(head, 0, head.Length);
                    if (!HasPdfHeader(head, read)) return false;
                    int tailLength = (int)Math.Min(2048L, stream.Length);
                    stream.Seek(-tailLength, SeekOrigin.End);
                    byte[] tail = new byte[tailLength];
                    read = stream.Read(tail, 0, tail.Length);
                    return Encoding.ASCII.GetString(tail, 0, read).Contains("%%EOF");
                }
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        private static bool HasPdfHeader(byte[] bytes, int length)
        {
            string start = Encoding.ASCII.GetString(bytes, 0, length);
            int index = start.IndexOf("%PDF-", StringComparison.Ordinal);
            if (index < 0) return false;
            // HTML error pages occasionally quote a PDF header in an error message.
            string prefix = start.Substring(0, index).Trim('\uFEFF', ' ', '\t', '\r', '\n', '\0');
            return prefix.Length == 0;
        }

        private static object Get(Dictionary<string, object> map, string key)
        {
            object value;
            return map != null && map.TryGetValue(key, out value) ? value : null;
        }
        private static Dictionary<string, object> Map(object value) { return value as Dictionary<string, object>; }
        private static string Text(object value) { return value == null ? "" : Convert.ToString(value, CultureInfo.InvariantCulture); }
        private static IEnumerable<Dictionary<string, object>> Items(object value)
        {
            var sequence = value as IEnumerable;
            if (sequence == null || value is string || value is IDictionary) yield break;
            foreach (object item in sequence)
            {
                var map = Map(item);
                if (map != null) yield return map;
            }
        }
        private static bool IsHttp(Uri uri) { return uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp; }
        private static void Report(IProgress<DownloadProgress> progress, string stage, long bytes, long? total)
        {
            if (progress != null) progress.Report(new DownloadProgress { Stage = stage, BytesReceived = bytes, TotalBytes = total });
        }
        private static string Detail(Exception ex)
        {
            Exception inner = ex.InnerException;
            return inner == null ? "" : "（" + inner.Message + "）";
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (ownsClient) client.Dispose();
        }
    }
}
