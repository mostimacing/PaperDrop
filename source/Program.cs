using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PaperDrop
{
    internal static class Program
    {
        [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();

        [STAThread]
        private static void Main()
        {
            if (Environment.OSVersion.Version.Major >= 6) SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
            {
                MessageBox.Show("操作未完成：" + e.Exception.Message, "PaperDrop", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            Application.Run(new MainWindow());
        }
    }

    internal sealed class MainWindow : Form
    {
        private static readonly Color Ink = Color.FromArgb(28, 43, 54);
        private static readonly Color Muted = Color.FromArgb(101, 116, 128);
        private static readonly Color Accent = Color.FromArgb(18, 111, 94);
        private static readonly Color Border = Color.FromArgb(221, 229, 232);
        private readonly DownloadEngine downloader = new DownloadEngine();
        private readonly CitationEngine citer = new CitationEngine();
        private readonly TextBox links = new TextBox();
        private readonly TextBox folder = new TextBox();
        private readonly DataGridView grid = new DataGridView();
        private readonly TextBox citationText = new TextBox();
        private readonly ComboBox citationFormat = new ComboBox();
        private readonly Label summary = new Label();
        private readonly Label status = new Label();
        private readonly Label citationHint = new Label();
        private readonly ProgressBar progress = new ProgressBar();
        private readonly TabControl tabs = new TabControl();
        private readonly Button downloadButton;
        private readonly Button citeButton;
        private readonly Button cancelButton;
        private readonly Button retryButton;
        private readonly Button browseButton;
        private readonly Button pasteButton;
        private readonly Button exampleButton;
        private readonly Button copyButton;
        private readonly Button saveButton;
        private readonly List<CitationResult> citations = new List<CitationResult>();
        private readonly List<string> failed = new List<string>();
        private CancellationTokenSource cancellation;
        private bool busy, closeAfterCancel, lastWasCitation;
        private int runId;
        private readonly ToolTip tooltips = new ToolTip { AutoPopDelay = 20000, InitialDelay = 350 };

        public MainWindow()
        {
            Text = "PaperDrop · 论文下载与 LaTeX 引用";
            Font = new Font("Microsoft YaHei UI", 9F);
            BackColor = Color.FromArgb(243, 246, 247);
            ForeColor = Ink;
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(1000, 760);
            MinimumSize = new Size(860, 720);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(26, 16, 26, 18), ColumnCount = 1, RowCount = 5 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 238));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
            Controls.Add(root);

            var header = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            header.Controls.Add(new BrandMark { Location = new Point(0, 10), Size = new Size(50, 50) });
            header.Controls.Add(MakeLabel("PaperDrop", 67, 3, 240, 38, 23F, true, Ink));
            header.Controls.Add(MakeLabel("让论文与引用，一起归档。", 69, 44, 400, 26, 10F, false, Muted));
            var badge = MakeLabel("arXiv   /   INSPIRE   /   DOI", 615, 25, 328, 28, 10F, true, Accent);
            badge.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            badge.TextAlign = ContentAlignment.MiddleRight;
            header.Controls.Add(badge);
            root.Controls.Add(header, 0, 0);

            var inputCard = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(18, 12, 18, 14), Margin = Padding.Empty };
            var inputLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Margin = Padding.Empty };
            inputLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            inputLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            inputLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            inputLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
            inputLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
            inputLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            inputCard.Controls.Add(inputLayout);
            var inputTitle = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
            inputTitle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            inputTitle.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
            inputTitle.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
            inputTitle.Controls.Add(new Label { Text = "01   粘贴论文链接", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(Font, FontStyle.Bold) }, 0, 0);
            pasteButton = MakeButton("粘贴", false);
            exampleButton = MakeButton("填入示例", false);
            inputTitle.Controls.Add(pasteButton, 1, 0);
            inputTitle.Controls.Add(exampleButton, 2, 0);
            inputLayout.Controls.Add(inputTitle, 0, 0);
            links.Multiline = true;
            links.ScrollBars = ScrollBars.Vertical;
            links.AcceptsReturn = true;
            links.Dock = DockStyle.Fill;
            links.BorderStyle = BorderStyle.FixedSingle;
            links.Font = new Font("Consolas", 10F);
            links.MaxLength = 65536;
            links.AccessibleName = "论文链接，每行一篇";
            links.BackColor = Color.FromArgb(250, 252, 252);
            inputLayout.Controls.Add(links, 0, 1);
            inputLayout.Controls.Add(new Label { Text = "每行一篇。支持 arXiv 编号、INSPIRE 论文页、DOI，以及 PDF 直链（仅下载）。", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Muted, Font = new Font(Font.FontFamily, 8.5F) }, 0, 2);
            inputLayout.Controls.Add(new Label { Text = "02   保存文件夹", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(Font, FontStyle.Bold) }, 0, 3);
            var folderRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
            folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            folder.Dock = DockStyle.Fill;
            folder.Margin = new Padding(3, 5, 8, 3);
            folder.AccessibleName = "保存文件夹";
            folder.Text = LoadFolder();
            browseButton = MakeButton("选择文件夹", false);
            var openFolder = MakeButton("打开文件夹", false);
            folderRow.Controls.Add(folder, 0, 0);
            folderRow.Controls.Add(browseButton, 1, 0);
            folderRow.Controls.Add(openFolder, 2, 0);
            inputLayout.Controls.Add(folderRow, 0, 4);
            root.Controls.Add(inputCard, 0, 1);

            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 10, 0, 8), Margin = Padding.Empty, WrapContents = false };
            downloadButton = MakeButton("下载 PDF", true); downloadButton.Width = 145;
            citeButton = MakeButton("生成 LaTeX 引用", false); citeButton.Width = 170;
            cancelButton = MakeButton("取消", false); cancelButton.Width = 85; cancelButton.Enabled = false;
            retryButton = MakeButton("重试失败项", false); retryButton.Width = 120; retryButton.Enabled = false;
            foreach (var button in new[] { downloadButton, citeButton, cancelButton, retryButton }) { button.Dock = DockStyle.None; button.Height = 36; button.Margin = new Padding(0, 0, 10, 0); actions.Controls.Add(button); }
            root.Controls.Add(actions, 0, 2);

            tabs.Dock = DockStyle.Fill;
            tabs.Margin = Padding.Empty;
            tabs.Padding = new Point(16, 7);
            var downloadsTab = new TabPage("下载列表") { BackColor = Color.White, Padding = new Padding(12) };
            var citationsTab = new TabPage("LaTeX 引用") { BackColor = Color.White, Padding = new Padding(12) };
            tabs.TabPages.Add(downloadsTab); tabs.TabPages.Add(citationsTab);
            root.Controls.Add(tabs, 0, 3);
            BuildGrid();
            downloadsTab.Controls.Add(grid);
            var downloadTip = new Label { Dock = DockStyle.Bottom, Height = 26, Text = "双击已完成的条目打开 PDF；选中失败条目可查看完整原因。", TextAlign = ContentAlignment.MiddleLeft, ForeColor = Muted, Font = new Font(Font.FontFamily, 8.5F) };
            downloadsTab.Controls.Add(downloadTip);

            var citationLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            citationLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            citationLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            citationLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            citationLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            var citationToolbar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, Margin = Padding.Empty };
            citationToolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 215));
            citationToolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            citationToolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            citationToolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            citationFormat.DropDownStyle = ComboBoxStyle.DropDownList;
            citationFormat.Dock = DockStyle.Fill;
            citationFormat.Items.AddRange(new object[] { "BibTeX · references.bib", "\\bibitem · 带标题超链接", "Overleaf 使用示例" });
            citationFormat.SelectedIndex = 0;
            citationFormat.AccessibleName = "引用格式";
            copyButton = MakeButton("复制内容", false); copyButton.Enabled = false;
            saveButton = MakeButton("导出文件", false); saveButton.Enabled = false;
            citationToolbar.Controls.Add(citationFormat, 0, 0);
            citationToolbar.Controls.Add(copyButton, 2, 0);
            citationToolbar.Controls.Add(saveButton, 3, 0);
            citationLayout.Controls.Add(citationToolbar, 0, 0);
            citationText.Multiline = true;
            citationText.ReadOnly = true;
            citationText.ScrollBars = ScrollBars.Both;
            citationText.WordWrap = false;
            citationText.Dock = DockStyle.Fill;
            citationText.BorderStyle = BorderStyle.FixedSingle;
            citationText.BackColor = Color.FromArgb(249, 251, 252);
            citationText.Font = new Font("Consolas", 10F);
            citationText.AccessibleName = "生成的 LaTeX 引用";
            citationText.Text = "在上方输入论文链接，然后点击「生成 LaTeX 引用」。\r\n\r\nBibTeX 可保存为 references.bib；\\bibitem 可直接加入参考文献列表。\r\n生成后可切换至「Overleaf 使用示例」，查看带超链接的完整用法。";
            citationLayout.Controls.Add(citationText, 0, 1);
            citationHint.Dock = DockStyle.Fill;
            citationHint.ForeColor = Muted;
            citationHint.Font = new Font(Font.FontFamily, 8.5F);
            citationHint.TextAlign = ContentAlignment.MiddleLeft;
            citationHint.Text = "引用由站点元数据生成；PDF 直链无法可靠提取作者与标题。";
            citationLayout.Controls.Add(citationHint, 0, 2);
            citationsTab.Controls.Add(citationLayout);

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(0, 12, 0, 0), Margin = Padding.Empty };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 7));
            footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            progress.Dock = DockStyle.Fill; progress.Margin = Padding.Empty;
            summary.Dock = DockStyle.Fill; summary.Text = "准备就绪"; summary.Font = new Font(Font, FontStyle.Bold); summary.TextAlign = ContentAlignment.MiddleLeft;
            status.Dock = DockStyle.Fill; status.Text = "公开论文 PDF · 本地保存 · 无需账号"; status.ForeColor = Muted; status.AutoEllipsis = true;
            footer.Controls.Add(progress, 0, 0); footer.Controls.Add(summary, 0, 1); footer.Controls.Add(status, 0, 2);
            root.Controls.Add(footer, 0, 4);

            downloadButton.Click += async delegate { await RunAsync(false, null); };
            citeButton.Click += async delegate { await RunAsync(true, null); };
            retryButton.Click += async delegate { await RunAsync(lastWasCitation, failed.ToArray()); };
            cancelButton.Click += delegate { if (cancellation != null) cancellation.Cancel(); cancelButton.Enabled = false; status.Text = "正在取消，已完成的文件会保留…"; };
            browseButton.Click += delegate { ChooseFolder(); };
            openFolder.Click += delegate { OpenOutputFolder(); };
            pasteButton.Click += delegate { try { if (Clipboard.ContainsText()) { links.SelectedText = Clipboard.GetText(); links.Focus(); } } catch (Exception ex) { ShowError(ex.Message); } };
            exampleButton.Click += delegate { links.Text = "https://arxiv.org/abs/1706.03762\r\nhttps://inspirehep.net/literature/451647"; links.Focus(); };
            citationFormat.SelectedIndexChanged += delegate { RefreshCitationText(); };
            copyButton.Click += delegate { try { Clipboard.SetText(citationText.Text); status.Text = "已复制，可直接粘贴到 Overleaf。"; } catch (Exception ex) { ShowError(ex.Message); } };
            saveButton.Click += delegate { ExportCitation(); };
            grid.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs e) { OpenRow(e.RowIndex); };
            grid.SelectionChanged += delegate { if (!busy && grid.SelectedRows.Count > 0) { var row = grid.SelectedRows[0].Tag as RowData; if (row != null && !String.IsNullOrEmpty(row.Detail)) { status.Text = row.Detail; tooltips.SetToolTip(status, row.Detail); } } };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy) { e.Cancel = true; closeAfterCancel = true; if (cancellation != null) cancellation.Cancel(); status.Text = "正在取消任务并关闭…"; } };
            FormClosed += delegate { downloader.Dispose(); citer.Dispose(); tooltips.Dispose(); };
            Shown += delegate { links.Focus(); };
        }

        private void BuildGrid()
        {
            grid.Dock = DockStyle.Fill; grid.BackgroundColor = Color.White; grid.BorderStyle = BorderStyle.None;
            grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false; grid.AllowUserToResizeRows = false;
            grid.ReadOnly = true; grid.RowHeadersVisible = false; grid.MultiSelect = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.GridColor = Color.FromArgb(237, 242, 243);
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(242, 247, 247), ForeColor = Muted, Font = new Font(Font, FontStyle.Bold), Padding = new Padding(9, 0, 0, 0), SelectionBackColor = Color.FromArgb(242, 247, 247) };
            grid.ColumnHeadersHeight = 34;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.DefaultCellStyle = new DataGridViewCellStyle { ForeColor = Ink, SelectionBackColor = Color.FromArgb(227, 241, 236), SelectionForeColor = Ink, Padding = new Padding(9, 0, 0, 0) };
            grid.RowTemplate.Height = 37;
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "source", HeaderText = "来源", FillWeight = 12, MinimumWidth = 85 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "paper", HeaderText = "论文 / 链接", FillWeight = 58, MinimumWidth = 200 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "state", HeaderText = "状态", FillWeight = 30, MinimumWidth = 160 });
            foreach (DataGridViewColumn col in grid.Columns) col.SortMode = DataGridViewColumnSortMode.NotSortable;
        }

        private async Task RunAsync(bool makeCitations, string[] retryInputs)
        {
            if (busy) return;
            string[] inputs = retryInputs ?? links.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
            if (inputs.Length == 0) { ShowError("请先粘贴论文链接，每行一篇。"); links.Focus(); return; }
            if (inputs.Length > 200) { ShowError("一次最多处理 200 篇，请分批操作。"); return; }
            string destination = folder.Text.Trim().Trim('"');
            if (!makeCitations)
            {
                try { if (String.IsNullOrWhiteSpace(destination)) throw new IOException("请选择保存文件夹。"); destination = Path.GetFullPath(destination); Directory.CreateDirectory(destination); folder.Text = destination; SaveFolder(destination); }
                catch (Exception ex) { ShowError("无法使用保存文件夹：" + ex.Message); return; }
            }
            bool appendCitations = makeCitations && retryInputs != null;
            if (makeCitations && !appendCitations) citations.Clear();
            failed.Clear(); grid.Rows.Clear();
            foreach (string input in inputs)
            {
                string kind = "—";
                try { var parsed = DownloadEngine.ParseInput(input); kind = parsed.Kind == "arxiv" ? "arXiv" : parsed.Kind == "inspire" ? "INSPIRE" : parsed.Kind.ToUpperInvariant(); } catch { }
                int index = grid.Rows.Add(kind, input, "等待中");
                grid.Rows[index].Tag = new RowData { Input = input };
                grid.Rows[index].Cells[1].ToolTipText = input;
            }
            lastWasCitation = makeCitations;
            tabs.SelectedIndex = makeCitations ? 1 : 0;
            if (makeCitations) RefreshCitationText();
            SetBusy(true);
            cancellation = new CancellationTokenSource();
            CancellationToken token = cancellation.Token;
            int succeeded = 0, skipped = 0, errors = 0, finished = 0;
            int currentRun = ++runId;
            try
            {
                for (int i = 0; i < inputs.Length; i++)
                {
                    if (token.IsCancellationRequested) break;
                    var row = grid.Rows[i];
                    var data = (RowData)row.Tag;
                    int rowIndex = i;
                    data.State = "running";
                    SetRow(row, makeCitations ? "正在获取引用信息…" : "正在解析…", Accent);
                    summary.Text = String.Format("{0}  {1} / {2}", makeCitations ? "生成引用" : "下载论文", i + 1, inputs.Length);
                    status.Text = inputs[i];
                    progress.Style = ProgressBarStyle.Marquee;
                    try
                    {
                        if (makeCitations)
                        {
                            CitationResult generated = await citer.GenerateAsync(inputs[i], token);
                            CitationResult result = new CitationResult { Key = generated.Key, Title = generated.Title, Url = generated.Url, BibTeX = generated.BibTeX, BibItem = generated.BibItem };
                            // Distinct papers can share an author/year key; keep every key unique.
                            var same = citations.FirstOrDefault(c => c.Url == result.Url);
                            if (same == null)
                            {
                                string baseKey = result.Key;
                                int suffix = 2;
                                while (citations.Any(c => c.Key == result.Key)) result.Key = baseKey + "_" + (suffix++).ToString();
                                if (result.Key != baseKey) { result.BibTeX = result.BibTeX.Replace("{" + baseKey + ",", "{" + result.Key + ","); result.BibItem = result.BibItem.Replace("\\bibitem{" + baseKey + "}", "\\bibitem{" + result.Key + "}"); }
                                citations.Add(result);
                            }
                            row.Cells[1].Value = result.Title;
                            row.Cells[1].ToolTipText = result.Title + "\r\n" + result.Url;
                            data.Detail = "引用键：" + (same == null ? result.Key : same.Key);
                            data.State = "success";
                            SetRow(row, same == null ? "引用已生成" : "相同引用已合并", Accent);
                            succeeded++;
                            RefreshCitationText();
                        }
                        else
                        {
                            var report = new Progress<DownloadProgress>(p =>
                            {
                                if (!busy || currentRun != runId || rowIndex >= grid.Rows.Count || data.State != "running") return;
                                string text = p.Stage;
                                if (p.BytesReceived > 0) text += " · " + FormatBytes(p.BytesReceived);
                                SetRow(row, text, Accent);
                                status.Text = text;
                                if (p.TotalBytes.HasValue && p.TotalBytes.Value > 0) { progress.Style = ProgressBarStyle.Continuous; progress.Value = (int)Math.Min(100, p.BytesReceived * 100L / p.TotalBytes.Value); }
                            });
                            DownloadResult result = await downloader.DownloadAsync(inputs[i], destination, report, token);
                            data.FilePath = result.FilePath; data.Detail = result.FilePath; data.State = "success";
                            row.Cells[1].Value = result.Title;
                            row.Cells[1].ToolTipText = result.Title + "\r\n" + result.FilePath;
                            SetRow(row, result.Skipped ? "已存在 · 已跳过" : "下载完成", Accent);
                            if (result.Skipped) skipped++; else succeeded++;
                        }
                    }
                    catch (OperationCanceledException) { if (token.IsCancellationRequested) { data.State = "cancelled"; SetRow(row, "已取消", Muted); failed.Add(inputs[i]); finished++; break; } errors++; data.State = "failed"; data.Detail = "请求超时，请稍后重试。"; failed.Add(inputs[i]); SetRow(row, data.Detail, Color.Firebrick); }
                    catch (Exception ex) { errors++; data.State = "failed"; data.Detail = ex.Message; failed.Add(inputs[i]); SetRow(row, "失败：" + ex.Message, Color.Firebrick); }
                    finished++;
                    progress.Style = ProgressBarStyle.Continuous; progress.Value = finished * 100 / inputs.Length;
                }
                for (int i = finished; i < inputs.Length; i++) { var data = (RowData)grid.Rows[i].Tag; data.State = "cancelled"; SetRow(grid.Rows[i], "未开始 · 已取消", Muted); failed.Add(inputs[i]); }
                summary.Text = String.Format("{0}  ·  成功 {1}  /  跳过 {2}  /  失败 {3}{4}", token.IsCancellationRequested ? "任务已取消" : "处理完成", succeeded, skipped, errors, token.IsCancellationRequested ? "  /  未完成 " + (inputs.Length - succeeded - skipped - errors) : "");
                status.Text = makeCitations ? (citations.Count > 0 ? "已生成 " + citations.Count + " 条引用；可复制、导出，或查看 Overleaf 使用示例。" : "未能生成引用，请在下载列表中查看具体原因。") : (failed.Count > 0 ? "可点击「重试失败项」继续；双击失败条目查看完整原因。" : "文件已保存到：" + destination);
                if (makeCitations && errors > 0) { citationHint.Text = "有 " + errors + " 项未成功。切换「下载列表」查看原因，或点击「重试失败项」。"; if (citations.Count == 0) tabs.SelectedIndex = 0; }
            }
            finally
            {
                runId++;
                progress.Style = ProgressBarStyle.Continuous;
                if (!token.IsCancellationRequested) progress.Value = 100;
                cancellation.Dispose(); cancellation = null;
                SetBusy(false);
                if (closeAfterCancel) Close();
            }
        }

        private void RefreshCitationText()
        {
            copyButton.Enabled = citations.Count > 0;
            saveButton.Enabled = citations.Count > 0;
            if (citations.Count == 0) { citationText.Text = busy ? "正在获取论文元数据，请稍候…" : "点击「生成 LaTeX 引用」，结果会显示在这里。"; return; }
            if (citationFormat.SelectedIndex == 0) citationText.Text = String.Join("\r\n\r\n", citations.Select(c => c.BibTeX.Trim()).ToArray()) + "\r\n";
            else if (citationFormat.SelectedIndex == 1) citationText.Text = "% 在导言区加入：\\usepackage[hidelinks]{hyperref}\r\n% 若标题含数学公式，还需：\\usepackage{amsmath,amssymb}\r\n% 正文中引用：\\cite{" + citations[0].Key + "}\r\n\r\n\\begin{thebibliography}{99}\r\n\r\n" + String.Join("\r\n\r\n", citations.Select(c => c.BibItem.Trim()).ToArray()) + "\r\n\r\n\\end{thebibliography}\r\n";
            else citationText.Text = "% 1. 将「BibTeX」内容保存为 references.bib，并上传至 Overleaf。\r\n% 2. 下方示例使用 biblatex + Biber，可点击 DOI / URL。\r\n% 3. 如果期刊模板已使用 BibTeX/natbib，请沿用模板，勿混用 biblatex。\r\n% 4. 已有文档只需合并相应导言区设置和正文命令。\r\n\r\n\\documentclass{article}\r\n\\usepackage{amsmath,amssymb}\r\n\\usepackage[backend=biber,style=numeric,sorting=none,url=true,doi=true]{biblatex}\r\n\\addbibresource{references.bib}\r\n\\usepackage[hidelinks]{hyperref}\r\n\r\n\\begin{document}\r\nSee \\cite{" + String.Join(",", citations.Select(c => c.Key).ToArray()) + "}.\r\n\r\n\\printbibliography\r\n\\end{document}\r\n";
            citationHint.Text = citationFormat.SelectedIndex == 1 ? "标题包含可点击的超链接。若文档已有 thebibliography 环境，只复制其中的条目。" : "BibTeX 的链接显示由模板决定；「Overleaf 使用示例」提供可点击 DOI / URL 的配置。";
        }

        private void ExportCitation()
        {
            if (citations.Count == 0) return;
            bool bib = citationFormat.SelectedIndex == 0;
            using (var dialog = new SaveFileDialog { Title = "导出 LaTeX 引用", Filter = bib ? "BibTeX 文件 (*.bib)|*.bib" : "LaTeX 文件 (*.tex)|*.tex", FileName = bib ? "references.bib" : citationFormat.SelectedIndex == 1 ? "references.tex" : "overleaf-example.tex", DefaultExt = bib ? "bib" : "tex", AddExtension = true, OverwritePrompt = true })
            {
                if (Directory.Exists(folder.Text)) dialog.InitialDirectory = folder.Text;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try { File.WriteAllText(dialog.FileName, citationText.Text, new UTF8Encoding(false)); status.Text = "已导出：" + dialog.FileName; }
                catch (Exception ex) { ShowError("导出失败：" + ex.Message); }
            }
        }

        private void SetBusy(bool value)
        {
            busy = value;
            foreach (Control control in new Control[] { links, folder, browseButton, pasteButton, exampleButton, downloadButton, citeButton }) control.Enabled = !value;
            cancelButton.Enabled = value; retryButton.Enabled = !value && failed.Count > 0;
        }

        private void ChooseFolder()
        {
            using (var dialog = new FolderBrowserDialog { Description = "选择论文 PDF 的保存文件夹", ShowNewFolderButton = true })
            {
                if (Directory.Exists(folder.Text)) dialog.SelectedPath = folder.Text;
                if (dialog.ShowDialog(this) == DialogResult.OK) { folder.Text = dialog.SelectedPath; SaveFolder(folder.Text); }
            }
        }

        private void OpenOutputFolder()
        {
            try { string path = Path.GetFullPath(folder.Text.Trim().Trim('"')); if (!Directory.Exists(path)) { ShowError("此文件夹尚未创建，下载时会自动创建。"); return; } Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { ShowError("无法打开文件夹：" + ex.Message); }
        }

        private void OpenRow(int index)
        {
            if (index < 0 || index >= grid.Rows.Count) return;
            var data = grid.Rows[index].Tag as RowData;
            if (data == null) return;
            if (!String.IsNullOrEmpty(data.FilePath) && File.Exists(data.FilePath)) { try { Process.Start(new ProcessStartInfo(data.FilePath) { UseShellExecute = true }); } catch (Exception ex) { ShowError(ex.Message); } }
            else if (!String.IsNullOrEmpty(data.Detail)) MessageBox.Show(this, data.Detail, "条目详情", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private string LoadFolder()
        {
            try { string path = File.ReadAllText(Path.Combine(Application.StartupPath, "preferences.txt"), Encoding.UTF8).Trim(); if (!String.IsNullOrWhiteSpace(path)) return path; } catch { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Papers");
        }

        private void SaveFolder(string path)
        {
            try { File.WriteAllText(Path.Combine(Application.StartupPath, "preferences.txt"), path, new UTF8Encoding(false)); } catch { /* Read-only portable locations still work without preference persistence. */ }
        }

        private void ShowError(string text) { MessageBox.Show(this, text, "PaperDrop", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        private static string FormatBytes(long bytes) { return bytes < 1024 * 1024 ? (bytes / 1024D).ToString("0.0") + " KB" : (bytes / 1048576D).ToString("0.0") + " MB"; }
        private static void SetRow(DataGridViewRow row, string text, Color color) { row.Cells[2].Value = text; row.Cells[2].ToolTipText = text; row.Cells[2].Style.ForeColor = color; }
        private static Label MakeLabel(string text, int x, int y, int w, int h, float size, bool bold, Color color) { return new Label { Text = text, Location = new Point(x, y), Size = new Size(w, h), ForeColor = color, Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular) }; }
        private static Button MakeButton(string text, bool primary)
        {
            var button = new Button { Text = text, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, BackColor = primary ? Accent : Color.White, ForeColor = primary ? Color.White : Ink, Margin = new Padding(3, 0, 3, 3), UseVisualStyleBackColor = false, Font = new Font("Microsoft YaHei UI", 9F), TabStop = true };
            button.FlatAppearance.BorderColor = primary ? Accent : Border;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(13, 91, 77) : Color.FromArgb(235, 246, 241);
            return button;
        }

        private sealed class RowData { public string Input, State, FilePath, Detail; }
    }

    internal sealed class CardPanel : Panel
    {
        public CardPanel() { BackColor = Color.White; DoubleBuffered = true; }
        protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); using (var p = new Pen(Color.FromArgb(221, 229, 232))) e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1); }
    }

    internal sealed class BrandMark : Control
    {
        public BrandMark() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = Math.Min(Width, Height) / 50F; g.ScaleTransform(s, s);
            using (var brush = new SolidBrush(Color.FromArgb(18, 111, 94))) g.FillEllipse(brush, 0, 0, 49, 49);
            using (var pen = new Pen(Color.White, 2.4F)) { pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; g.DrawLines(pen, new[] { new PointF(17, 12), new PointF(29, 12), new PointF(35, 18), new PointF(35, 37), new PointF(16, 37), new PointF(16, 12) }); g.DrawLine(pen, 25, 20, 25, 31); g.DrawLines(pen, new[] { new PointF(21, 27), new PointF(25, 31), new PointF(29, 27) }); }
        }
    }
}
