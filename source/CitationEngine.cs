using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Xml;
using System.Xml.Linq;

namespace PaperDrop
{
    public sealed class CitationResult
    {
        public string Key;
        public string Title;
        public string Url;
        public string BibTeX;
        public string BibItem;
    }

    /// <summary>Creates references from public, structured source metadata.</summary>
    public sealed class CitationEngine : IDisposable
    {
        private readonly HttpClient client;
        private readonly bool ownsClient;
        private readonly SemaphoreSlim serial = new SemaphoreSlim(1, 1);
        private readonly Dictionary<string, CitationResult> cache = new Dictionary<string, CitationResult>(StringComparer.Ordinal);
        private DateTime lastArxivRequest = DateTime.MinValue;
        private bool disposed;
        public TimeSpan MinimumArxivInterval { get; set; }

        public CitationEngine() : this(CreateClient(), true) { }
        public CitationEngine(HttpClient client) : this(client, false) { }
        private CitationEngine(HttpClient client, bool ownsClient)
        {
            if (client == null) throw new ArgumentNullException("client");
            this.client = client;
            this.ownsClient = ownsClient;
            MinimumArxivInterval = TimeSpan.FromSeconds(3);
        }

        private static HttpClient CreateClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var result = new HttpClient(new HttpClientHandler {
                AllowAutoRedirect = true, MaxAutomaticRedirections = 8, UseCookies = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            });
            result.Timeout = Timeout.InfiniteTimeSpan;
            result.DefaultRequestHeaders.UserAgent.ParseAdd("PaperDrop/1.0 (personal academic citation tool)");
            return result;
        }

        public async Task<CitationResult> GenerateAsync(string input, CancellationToken cancellation)
        {
            if (disposed) throw new ObjectDisposedException("CitationEngine");
            InputInfo info = DownloadEngine.ParseInput(input);
            if (info.Kind == "pdf")
                throw new InvalidOperationException("PDF 直链通常不含可靠的作者和题名信息。生成引用时，请改用这篇论文的 arXiv、INSPIRE 或 DOI 链接。");
            await serial.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                cancellation.ThrowIfCancellationRequested();
                string cacheKey = info.Kind + ":" + info.Identifier;
                CitationResult existing;
                if (cache.TryGetValue(cacheKey, out existing)) return Copy(existing);
                Paper paper = null;
                if (info.Kind == "arxiv")
                {
                    Exception firstFailure = null;
                    try
                    {
                        string atom = await FetchAsync("https://export.arxiv.org/api/query?id_list=" + Uri.EscapeDataString(info.Identifier), true, cancellation).ConfigureAwait(false);
                        paper = ParseAtom(atom, info.Identifier);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception first)
                    {
                        if (!(first is InvalidOperationException) && !(first is HttpRequestException) && !(first is IOException) && !(first is XmlException)) throw;
                        firstFailure = first;
                    }
                    if (paper == null)
                    {
                        try
                        {
                            string json = await FetchAsync("https://inspirehep.net/api/arxiv/" + EscapePath(Regex.Replace(info.Identifier, @"v\d+$", "")), false, cancellation).ConfigureAwait(false);
                            paper = ParseInspire(json, info);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception fallback)
                        {
                            if (!(fallback is InvalidOperationException) && !(fallback is HttpRequestException) && !(fallback is IOException) && !(fallback is ArgumentException)) throw;
                            throw new InvalidOperationException("暂时无法取得这篇论文的引用信息。arXiv：" + firstFailure.Message + "；INSPIRE：" + fallback.Message, fallback);
                        }
                    }
                }
                else
                {
                    try
                    {
                        string json = await FetchAsync(info.CanonicalUrl, false, cancellation).ConfigureAwait(false);
                        paper = ParseInspire(json, info);
                    }
                    catch (MetadataHttpException ex)
                    {
                        if (ex.Status == 404 && info.Kind == "doi")
                            throw new InvalidOperationException("INSPIRE 未收录这个 DOI。请改用论文的 arXiv 或 INSPIRE 链接；此版本不查询其他学科的 DOI 数据库。", ex);
                        throw;
                    }
                }
                cancellation.ThrowIfCancellationRequested();
                CitationResult result = Render(paper);
                cache[cacheKey] = result;
                return Copy(result);
            }
            finally { serial.Release(); }
        }

        private async Task<string> FetchAsync(string url, bool arxiv, CancellationToken cancellation)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (arxiv)
                {
                    TimeSpan remaining = MinimumArxivInterval - (DateTime.UtcNow - lastArxivRequest);
                    if (remaining > TimeSpan.Zero) await Task.Delay(remaining, cancellation).ConfigureAwait(false);
                    lastArxivRequest = DateTime.UtcNow;
                }
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                {
                    deadline.CancelAfter(TimeSpan.FromSeconds(25));
                    try
                    {
                        using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                        {
                            request.Headers.Accept.ParseAdd(arxiv ? "application/atom+xml" : "application/json");
                            using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false))
                            {
                                int status = (int)response.StatusCode;
                                if ((status == 429 || status == 408 || status >= 500) && attempt == 0)
                                {
                                    double wait = status == 429 ? 5 : 2;
                                    if (response.Headers.RetryAfter != null)
                                    {
                                        if (response.Headers.RetryAfter.Delta.HasValue) wait = Math.Max(wait, response.Headers.RetryAfter.Delta.Value.TotalSeconds);
                                        if (response.Headers.RetryAfter.Date.HasValue) wait = Math.Max(wait, (response.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow).TotalSeconds);
                                    }
                                    if (wait > 120) throw new MetadataHttpException(status, "服务端要求稍后重试，请过几分钟再生成引用。");
                                    response.Dispose();
                                    await Task.Delay(TimeSpan.FromSeconds(wait), cancellation).ConfigureAwait(false);
                                    continue;
                                }
                                if (!response.IsSuccessStatusCode)
                                    throw new MetadataHttpException(status, status == 404 ? "没有找到对应论文。" : "服务端返回 HTTP " + status.ToString(CultureInfo.InvariantCulture) + "。");
                                if (response.Content.Headers.ContentLength.GetValueOrDefault() > 8 * 1024 * 1024)
                                    throw new InvalidOperationException("引用数据异常过大，已停止读取。");
                                using (Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                                using (deadline.Token.Register(delegate { try { stream.Dispose(); } catch { } }))
                                using (var bytes = new MemoryStream())
                                {
                                    byte[] buffer = new byte[16384];
                                    int read;
                                    while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, deadline.Token).ConfigureAwait(false)) > 0)
                                    {
                                        if (bytes.Length + read > 8 * 1024 * 1024) throw new InvalidOperationException("引用数据异常过大，已停止读取。");
                                        bytes.Write(buffer, 0, read);
                                    }
                                    cancellation.ThrowIfCancellationRequested();
                                    return Encoding.UTF8.GetString(bytes.ToArray()).TrimStart('\uFEFF');
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (attempt == 1) throw new InvalidOperationException("查询引用信息超时，请检查网络后重试。");
                    }
                    catch (HttpRequestException ex)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (attempt == 1) throw new InvalidOperationException("无法连接引用服务，请检查网络或系统代理。" + ex.Message, ex);
                    }
                    catch (ObjectDisposedException)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (!deadline.IsCancellationRequested) throw;
                        if (attempt == 1) throw new InvalidOperationException("查询引用信息超时，请检查网络后重试。");
                    }
                    catch (IOException ex)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (attempt == 1) throw new InvalidOperationException("引用信息读取中断，请检查网络后重试。", ex);
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(2), cancellation).ConfigureAwait(false);
            }
            throw new InvalidOperationException("暂时无法读取引用信息。");
        }

        private static Paper ParseAtom(string text, string requestedId)
        {
            XDocument doc;
            using (var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 8 * 1024 * 1024
            })) doc = XDocument.Load(reader);
            XNamespace atom = "http://www.w3.org/2005/Atom";
            XNamespace ax = "http://arxiv.org/schemas/atom";
            XElement entry = doc.Descendants(atom + "entry").FirstOrDefault();
            if (entry == null) throw new InvalidOperationException("arXiv 没有返回对应论文。");
            string entryId;
            try { entryId = DownloadEngine.ParseInput(Value(entry, atom + "id")).Identifier; }
            catch (InvalidOperationException) { throw new InvalidOperationException("arXiv 返回的记录无法识别。"); }
            bool versioned = Regex.IsMatch(requestedId, @"v\d+$");
            if (!String.Equals(versioned ? entryId : Regex.Replace(entryId, @"v\d+$", ""), requestedId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("arXiv 返回的论文编号或版本与输入不一致。");
            var paper = new Paper();
            paper.Title = Clean(Value(entry, atom + "title"));
            foreach (var author in entry.Elements(atom + "author"))
            {
                string name = Clean(Value(author, atom + "name"));
                if (name.Length > 0) paper.Authors.Add(name);
            }
            string date = Value(entry, versioned ? atom + "updated" : atom + "published");
            if (date.Length >= 10 && Regex.IsMatch(date.Substring(0, 10), @"^\d{4}-\d{2}-\d{2}$")) paper.Date = date.Substring(0, 10);
            paper.Year = ExtractYear(date);
            paper.Doi = Clean(Value(entry, ax + "doi"));
            paper.Note = Clean(Value(entry, ax + "journal_ref"));
            XElement category = entry.Element(ax + "primary_category");
            if (category != null) paper.Category = (string)category.Attribute("term") ?? "";
            paper.Arxiv = requestedId;
            paper.Url = "https://arxiv.org/abs/" + requestedId;
            paper.Key = "arxiv" + Regex.Replace(requestedId, @"[^A-Za-z0-9]", "");
            return paper;
        }

        private static Paper ParseInspire(string json, InputInfo input)
        {
            Dictionary<string, object> root;
            try { root = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.DeserializeObject(json) as Dictionary<string, object>; }
            catch (ArgumentException ex) { throw new InvalidOperationException("INSPIRE 返回的引用数据不是有效 JSON。", ex); }
            var metadata = Map(root, "metadata");
            if (metadata == null) throw new InvalidOperationException("INSPIRE 没有返回完整的论文信息。");
            var paper = new Paper();
            paper.Title = Clean(FirstField(metadata, "titles", "title"));
            foreach (var author in Objects(metadata, "authors"))
            {
                string name = Clean(Field(author, "full_name"));
                if (name.Length > 0) paper.Authors.Add(name);
            }
            // Collective authors remain a single BibTeX name, rather than being split on spaces.
            if (paper.Authors.Count == 0)
                foreach (var collaboration in Objects(metadata, "collaborations"))
                {
                    string name = Clean(Field(collaboration, "value"));
                    if (name.Length > 0) paper.Collaborations.Add(name);
                }
            var publications = Objects(metadata, "publication_info").Where(p => Field(p, "journal_title").Length > 0).ToList();
            var dois = Objects(metadata, "dois").Where(d => Field(d, "value").Length > 0).ToList();
            Dictionary<string, object> requestedDoi = input.Kind == "doi" ? dois.FirstOrDefault(d => String.Equals(Field(d, "value"), input.Identifier, StringComparison.OrdinalIgnoreCase)) : null;
            string requestedMaterial = requestedDoi == null ? "" : Field(requestedDoi, "material");
            // A record may list a later reprint before the original journal publication.
            Dictionary<string, object> publication = requestedMaterial.Length > 0 ? publications.FirstOrDefault(p => Field(p, "material") == requestedMaterial) : null;
            if (publication == null) publication = publications.FirstOrDefault(p => Field(p, "material") == "publication");
            if (publication == null) publication = publications.FirstOrDefault(p => Field(p, "material") != "reprint");
            if (publication == null) publication = publications.FirstOrDefault();
            if (publication != null)
            {
                paper.Journal = Clean(Field(publication, "journal_title"));
                paper.Volume = Clean(Field(publication, "journal_volume"));
                paper.Number = Clean(Field(publication, "journal_issue"));
                paper.Year = ExtractYear(Field(publication, "year"));
                string start = Field(publication, "page_start"), end = Field(publication, "page_end");
                paper.Pages = start.Length > 0 ? start + (end.Length > 0 && end != start ? "--" + end : "") : "";
                paper.ArticleNumber = Clean(Field(publication, "artid"));
            }
            paper.Date = Clean(Field(metadata, "preprint_date"));
            if (paper.Year.Length == 0) paper.Year = ExtractYear(paper.Date);
            if (paper.Year.Length == 0) paper.Year = ExtractYear(Field(metadata, "earliest_date"));
            string material = publication == null ? "" : Field(publication, "material");
            Dictionary<string, object> chosenDoi = material.Length > 0 ? dois.FirstOrDefault(d => Field(d, "material") == material) : null;
            if (chosenDoi == null) chosenDoi = dois.FirstOrDefault(d => Field(d, "material").Length == 0);
            if (chosenDoi == null && material.Length == 0) chosenDoi = dois.FirstOrDefault();
            paper.Doi = input.Kind == "doi" ? input.Identifier : Clean(Field(chosenDoi, "value"));
            Dictionary<string, object> eprint = Objects(metadata, "arxiv_eprints").FirstOrDefault();
            if (input.Kind == "arxiv")
            {
                eprint = Objects(metadata, "arxiv_eprints").FirstOrDefault(p => String.Equals(Regex.Replace(Field(p, "value"), @"v\d+$", ""), Regex.Replace(input.Identifier, @"v\d+$", ""), StringComparison.OrdinalIgnoreCase));
                if (eprint == null) throw new InvalidOperationException("INSPIRE 返回的 arXiv 编号与输入不一致。");
                if (Regex.IsMatch(input.Identifier, @"v\d+$") && !String.Equals(input.Identifier, Field(eprint, "value"), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("INSPIRE 无法确认指定 arXiv 版本的题名和作者，请稍后重试 arXiv。");
                paper.Arxiv = input.Identifier;
                paper.Url = "https://arxiv.org/abs/" + input.Identifier;
            }
            else
            {
                if (eprint != null) paper.Arxiv = Field(eprint, "value");
                string record = Field(metadata, "control_number");
                if (!Regex.IsMatch(record, @"^[1-9]\d*$")) record = Field(root, "id");
                if (!Regex.IsMatch(record, @"^[1-9]\d*$") && input.Kind == "inspire") record = input.Identifier;
                paper.Url = Regex.IsMatch(record, @"^[1-9]\d*$") ? "https://inspirehep.net/literature/" + record : "https://doi.org/" + EscapePath(input.Identifier);
            }
            if (eprint != null) paper.Category = Strings(eprint, "categories").FirstOrDefault() ?? "";
            string key = Strings(metadata, "texkeys").FirstOrDefault() ?? "";
            key = Regex.Replace(key, @"[^A-Za-z0-9:._-]", "");
            if (key.Length == 0)
                key = input.Kind == "arxiv" ? "arxiv" + Regex.Replace(input.Identifier, @"[^A-Za-z0-9]", "") : input.Kind == "inspire" ? "inspire" + input.Identifier : "doi" + StableHash(input.Identifier);
            else if (input.Kind == "arxiv" && Regex.IsMatch(input.Identifier, @"v\d+$")) key += Regex.Match(input.Identifier, @"v\d+$").Value;
            paper.Key = key;
            return paper;
        }

        private static CitationResult Render(Paper paper)
        {
            if (paper.Title.Length == 0) throw new InvalidOperationException("来源没有提供论文题名，无法生成可靠引用。");
            var fields = new List<string>();
            Add(fields, "title", "{" + TexTitle(paper.Title) + "}");
            var authors = paper.Authors.Select(TexText).Concat(paper.Collaborations.Select(n => "{" + TexText(n) + "}")).ToList();
            Add(fields, "author", String.Join(" and ", authors));
            Add(fields, "year", TexText(paper.Year));
            if (paper.Journal.Length == 0 && Regex.IsMatch(paper.Date, @"^\d{4}(-\d{2})?(-\d{2})?$")) Add(fields, "date", paper.Date);
            Add(fields, "journal", TexText(paper.Journal));
            Add(fields, "volume", TexText(paper.Volume));
            Add(fields, "number", TexText(paper.Number));
            Add(fields, "pages", TexText(paper.Pages));
            Add(fields, "eid", TexText(paper.ArticleNumber));
            Add(fields, "doi", BibUrl(paper.Doi));
            Add(fields, "url", BibUrl(paper.Url));
            if (paper.Arxiv.Length > 0)
            {
                Add(fields, "eprint", TexText(paper.Arxiv));
                Add(fields, "archivePrefix", "arXiv");
                Add(fields, "primaryClass", TexText(paper.Category));
            }
            if (paper.Journal.Length == 0) Add(fields, "note", TexText(paper.Note));
            string bib = "@" + (paper.Journal.Length > 0 ? "article" : "misc") + "{" + paper.Key + ",\r\n" + String.Join(",\r\n", fields) + "\r\n}";
            var parts = new List<string>();
            var displayedAuthors = paper.Authors.Concat(paper.Collaborations).ToList();
            if (displayedAuthors.Count > 0)
            {
                string names = displayedAuthors.Count <= 8 ? String.Join(", ", displayedAuthors.Select(TexText)) : String.Join(", ", displayedAuthors.Take(3).Select(TexText)) + ", et al.";
                parts.Add(names + ".");
            }
            parts.Add("\\href{" + HrefUrl(paper.Url) + "}{" + TexTitle(paper.Title) + "}.");
            if (paper.Journal.Length > 0)
            {
                string journal = "\\textit{" + TexText(paper.Journal) + "}";
                if (paper.Volume.Length > 0) journal += " \\textbf{" + TexText(paper.Volume) + "}";
                if (paper.Number.Length > 0) journal += "(" + TexText(paper.Number) + ")";
                if (paper.Pages.Length > 0) journal += ", " + TexText(paper.Pages);
                else if (paper.ArticleNumber.Length > 0) journal += ", " + TexText(paper.ArticleNumber);
                if (paper.Year.Length > 0) journal += " (" + TexText(paper.Year) + ")";
                parts.Add(journal + ".");
            }
            else if (paper.Year.Length > 0) parts.Add("(" + TexText(paper.Year) + ").");
            if (paper.Arxiv.Length > 0) parts.Add("\\href{" + HrefUrl("https://arxiv.org/abs/" + paper.Arxiv) + "}{arXiv:" + TexText(paper.Arxiv) + "}.");
            if (paper.Doi.Length > 0) parts.Add("\\href{" + HrefUrl("https://doi.org/" + EscapePath(paper.Doi)) + "}{doi:" + TexText(paper.Doi) + "}.");
            return new CitationResult { Key = paper.Key, Title = paper.Title, Url = paper.Url, BibTeX = bib, BibItem = "\\bibitem{" + paper.Key + "}\r\n" + String.Join(" ", parts) };
        }

        private static void Add(List<string> fields, string key, string value)
        { if (!String.IsNullOrWhiteSpace(value)) fields.Add("  " + key + " = {" + value + "}"); }

        private static CitationResult Copy(CitationResult result)
        { return new CitationResult { Key = result.Key, Title = result.Title, Url = result.Url, BibTeX = result.BibTeX, BibItem = result.BibItem }; }

        // Metadata is treated as text. Only balanced inline math with a small TeX command allow-list is retained.
        private static string TexTitle(string text)
        {
            var result = new StringBuilder();
            int position = 0;
            foreach (Match math in Regex.Matches(text, @"\$[^$\r\n]+\$"))
            {
                result.Append(TexText(text.Substring(position, math.Index - position)));
                result.Append(SafeMath(math.Value) ? math.Value : TexText(math.Value));
                position = math.Index + math.Length;
            }
            result.Append(TexText(text.Substring(position)));
            return result.ToString();
        }

        private static bool SafeMath(string value)
        {
            if (value.Contains("%") || value.Contains("#") || value.Contains("&") || value.Contains("\\\\")) return false;
            int depth = 0;
            for (int i = 1; i < value.Length - 1; i++)
            {
                if (value[i] == '{' && (i == 0 || value[i - 1] != '\\')) depth++;
                if (value[i] == '}' && (i == 0 || value[i - 1] != '\\') && --depth < 0) return false;
            }
            if (depth != 0) return false;
            const string allowed = " alpha beta gamma delta epsilon varepsilon zeta eta theta vartheta iota kappa lambda mu nu xi pi varpi rho varrho sigma varsigma tau upsilon phi varphi chi psi omega Gamma Delta Theta Lambda Xi Pi Sigma Upsilon Phi Psi Omega mathrm mathbf mathit mathcal mathbb mathsf mathtt cal rm bf it text frac dfrac tfrac sqrt bar overline hat widehat tilde widetilde vec dot ddot left right big Big bigg Bigg langle rangle lvert rvert lVert rVert vert Vert prime dagger ddagger ast star circ bullet cdot times otimes oplus pm mp le leq ge geq neq sim simeq approx equiv propto in notin subset supset subseteq supseteq to rightarrow leftarrow Rightarrow Leftarrow leftrightarrow Leftrightarrow mapsto infinity infty partial nabla sum prod int oint lim log ln exp sin cos tan min max det Re Im operatorname quad qquad textstyle displaystyle " ;
            foreach (Match command in Regex.Matches(value, @"\\([A-Za-z]+|.)"))
            {
                string name = command.Groups[1].Value;
                if (name.Length == 1 && ",;:! {}_|".IndexOf(name, StringComparison.Ordinal) >= 0) continue;
                if (allowed.IndexOf(" " + name + " ", StringComparison.Ordinal) < 0) return false;
            }
            return true;
        }

        private static string TexText(string value)
        {
            var result = new StringBuilder();
            foreach (char c in value ?? "")
            {
                switch (c)
                {
                    case '\\': result.Append("\\textbackslash{}"); break;
                    case '{': result.Append("\\{"); break;
                    case '}': result.Append("\\}"); break;
                    case '$': result.Append("\\$"); break;
                    case '&': result.Append("\\&"); break;
                    case '#': result.Append("\\#"); break;
                    case '%': result.Append("\\%"); break;
                    case '_': result.Append("\\_"); break;
                    case '^': result.Append("\\textasciicircum{}"); break;
                    case '~': result.Append("\\textasciitilde{}"); break;
                    default: if (!Char.IsControl(c)) result.Append(c); else result.Append(' '); break;
                }
            }
            return result.ToString();
        }

        // BibLaTeX's verbatim URL/DOI fields must keep & and _ literal. Encode braces and backslashes.
        private static string BibUrl(string value)
        { return (value ?? "").Replace("\\", "%5C").Replace("{", "%7B").Replace("}", "%7D").Replace("\r", "").Replace("\n", "").Replace(" ", "%20"); }
        private static string HrefUrl(string value)
        { return BibUrl(value).Replace("%", "\\%").Replace("#", "\\#"); }
        private static string Clean(string value) { return Regex.Replace(value ?? "", @"\s+", " ").Trim(); }
        private static string ExtractYear(string value) { Match year = Regex.Match(value ?? "", @"(?:^|\D)((?:18|19|20|21)\d{2})(?:\D|$)"); return year.Success ? year.Groups[1].Value : ""; }
        private static string Value(XElement element, XName name) { XElement value = element.Element(name); return value == null ? "" : value.Value; }
        private static string Field(Dictionary<string, object> map, string name) { object value; return map != null && map.TryGetValue(name, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : ""; }
        private static Dictionary<string, object> Map(Dictionary<string, object> map, string name) { object value; return map != null && map.TryGetValue(name, out value) ? value as Dictionary<string, object> : null; }
        private static IEnumerable<Dictionary<string, object>> Objects(Dictionary<string, object> map, string name)
        {
            object value;
            if (map != null && map.TryGetValue(name, out value) && value is IEnumerable && !(value is string))
                foreach (object entry in (IEnumerable)value) { var record = entry as Dictionary<string, object>; if (record != null) yield return record; }
        }
        private static IEnumerable<string> Strings(Dictionary<string, object> map, string name)
        {
            object value;
            if (map != null && map.TryGetValue(name, out value) && value is IEnumerable && !(value is string))
                foreach (object entry in (IEnumerable)value) if (entry is string) yield return (string)entry;
        }
        private static string FirstField(Dictionary<string, object> map, string list, string field) { return Objects(map, list).Select(p => Field(p, field)).FirstOrDefault(v => v.Length > 0) ?? ""; }
        private static string EscapePath(string value) { return String.Join("/", value.Split('/').Select(Uri.EscapeDataString)); }
        private static string StableHash(string value)
        {
            using (var hash = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").Substring(0, 12).ToLowerInvariant();
        }
        public void Dispose() { if (disposed) return; disposed = true; if (ownsClient) client.Dispose(); }
        private sealed class MetadataHttpException : InvalidOperationException
        { public readonly int Status; public MetadataHttpException(int status, string message) : base(message) { Status = status; } }
        private sealed class Paper
        {
            public string Key = "", Title = "", Year = "", Date = "", Journal = "", Volume = "", Number = "", Pages = "", ArticleNumber = "", Doi = "", Arxiv = "", Category = "", Url = "", Note = "";
            public readonly List<string> Authors = new List<string>();
            public readonly List<string> Collaborations = new List<string>();
        }
    }
}
