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
            { MessageBox.Show("操作未完成：" + e.Exception.Message, "PaperDrop", MessageBoxButtons.OK, MessageBoxIcon.Information); };
            Application.Run(new MainWindow());
        }
    }

    internal sealed class MainWindow : Form
    {
        private static readonly Color Ink = UiTheme.Ink;
        private static readonly Color Muted = UiTheme.Muted;
        private static readonly Color Accent = UiTheme.Accent;
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
        private readonly Label inputCount = new Label();
        private readonly Label taskCount = new Label();
        private readonly Panel viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        private TableLayoutPanel rootLayout;
        private float displayScale = 1F;
        private bool arrangingCanvas;
        private readonly ProgressBar progress = new ProgressBar();
        private readonly Panel tasksView = new Panel();
        private readonly Panel citationsView = new Panel();
        private readonly EmptyState taskEmpty = new EmptyState { Heading = "还没有任务", Hint = "添加论文链接，开始下载或生成引用。" };
        private readonly EmptyState citationEmpty = new EmptyState { Heading = "引用会显示在这里", Hint = "支持 BibTeX、\\bibitem 和 Overleaf 示例。", IsCitation = true };
        private readonly ActionButton taskTab = MakeButton("任务", ButtonTone.Segment);
        private readonly ActionButton citationTab = MakeButton("引用", ButtonTone.Segment);
        private readonly ActionButton downloadButton = MakeButton("下载 PDF", ButtonTone.Primary);
        private readonly ActionButton citeButton = MakeButton("生成 LaTeX 引用", ButtonTone.Secondary);
        private readonly ActionButton cancelButton = MakeButton("取消", ButtonTone.Quiet);
        private readonly ActionButton retryButton = MakeButton("重试失败项", ButtonTone.Quiet);
        private readonly ActionButton browseButton = MakeButton("选择", ButtonTone.Secondary);
        private readonly ActionButton pasteButton = MakeButton("粘贴", ButtonTone.Quiet);
        private readonly ActionButton exampleButton = MakeButton("示例", ButtonTone.Quiet);
        private readonly ActionButton copyButton = MakeButton("复制", ButtonTone.Secondary);
        private readonly ActionButton saveButton = MakeButton("导出", ButtonTone.Secondary);
        private readonly ActionButton detailsButton = MakeButton("查看详情", ButtonTone.Quiet);
        private readonly List<CitationResult> citations = new List<CitationResult>();
        private readonly List<string> failed = new List<string>();
        private CancellationTokenSource cancellation;
        private bool busy, closeAfterCancel, lastWasCitation;
        private int runId;
        private readonly ToolTip tooltips = new ToolTip { AutoPopDelay = 20000, InitialDelay = 400 };

        public MainWindow()
        {
            Text = "PaperDrop";
            Font = new Font("Microsoft YaHei UI", 9F);
            BackColor = UiTheme.Canvas; ForeColor = Ink;
            AutoScaleDimensions = new SizeF(96F, 96F); AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(1100, 720); MinimumSize = new Size(940, 630);
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var root = MakeLayout(1, 3);
            rootLayout = root; root.Dock = DockStyle.None;
            root.Padding = new Padding(24, 16, 24, 12);
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            Controls.Add(viewport); viewport.Controls.Add(root);

            var header = MakeLayout(4, 1);
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 166));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
            header.Controls.Add(new BrandMark { Width = 44, Height = 44, Margin = new Padding(0, 4, 0, 0) }, 0, 0);
            var wordmark = LabelFor("PaperDrop", 22F, true);
            wordmark.Font = new Font("Segoe UI Semibold", 22F, FontStyle.Bold);
            wordmark.Margin = new Padding(0, 0, 0, 16);
            header.Controls.Add(wordmark, 1, 0);
            var descriptor = LabelFor("论文下载与引用", 9F, false); descriptor.ForeColor = Muted; descriptor.Margin = new Padding(4, 4, 0, 17);
            header.Controls.Add(descriptor, 2, 0);
            var help = MakeButton("使用帮助", ButtonTone.Quiet); help.Margin = new Padding(0, 4, 0, 22);
            header.Controls.Add(help, 3, 0); help.Click += delegate { ShowHelp(); };
            root.Controls.Add(header, 0, 0);

            var body = MakeLayout(3, 1);
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 318));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 18));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.Controls.Add(body, 0, 1);
            var inputCard = new Surface { Dock = DockStyle.Fill, Padding = new Padding(20), Margin = Padding.Empty };
            body.Controls.Add(inputCard, 0, 0);
            var inputLayout = MakeLayout(1, 11); inputLayout.BackColor = Color.White;
            inputLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in new[] { 38, -1, 29, 38, 38, 34, 18, 44, 10, 44, 26 })
                inputLayout.RowStyles.Add(height < 0 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, height));
            inputCard.Controls.Add(inputLayout);

            var inputTitle = MakeLayout(3, 1); inputTitle.BackColor = Color.White;
            inputTitle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            inputTitle.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
            inputTitle.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
            inputTitle.Controls.Add(LabelFor("论文链接", 11F, true), 0, 0);
            inputTitle.Controls.Add(exampleButton, 1, 0); inputTitle.Controls.Add(pasteButton, 2, 0);
            inputLayout.Controls.Add(inputTitle, 0, 0);
            var linkFrame = new Surface { Dock = DockStyle.Fill, CornerRadius = 8, SurfaceColor = UiTheme.Soft, BackColor = UiTheme.Soft, Padding = new Padding(12), Margin = Padding.Empty };
            links.Multiline = true; links.ScrollBars = ScrollBars.Vertical; links.AcceptsReturn = true;
            links.Dock = DockStyle.Fill; links.BorderStyle = BorderStyle.None;
            links.Font = new Font("Consolas", 10F); links.MaxLength = 65536;
            links.BackColor = UiTheme.Soft; links.ForeColor = Ink; links.AccessibleName = "论文链接，每行一篇";
            linkFrame.Controls.Add(links);
            var placeholder = new Label { Text = "粘贴论文链接或编号\r\n每行一篇", Dock = DockStyle.Fill, BackColor = UiTheme.Soft, ForeColor = Muted, Padding = new Padding(1, 2, 0, 0), Font = new Font(Font.FontFamily, 9.5F) };
            linkFrame.Controls.Add(placeholder); placeholder.BringToFront();
            placeholder.Click += delegate { links.Focus(); };
            links.Enter += delegate { placeholder.Visible = false; };
            links.Leave += delegate { placeholder.Visible = links.TextLength == 0; };
            links.TextChanged += delegate
            {
                int count = links.Lines.Count(line => !String.IsNullOrWhiteSpace(line));
                inputCount.Text = count == 0 ? "arXiv · INSPIRE · DOI · PDF" : count + " 篇论文";
                placeholder.Visible = links.TextLength == 0 && !links.Focused;
            };
            inputLayout.Controls.Add(linkFrame, 0, 1);
            inputCount.Text = "arXiv · INSPIRE · DOI · PDF"; inputCount.ForeColor = Muted;
            inputCount.Font = new Font(Font.FontFamily, 8F); inputCount.Dock = DockStyle.Fill; inputCount.TextAlign = ContentAlignment.MiddleLeft;
            inputLayout.Controls.Add(inputCount, 0, 2);
            inputLayout.Controls.Add(LabelFor("保存位置", 10F, true), 0, 3);
            var folderRow = MakeLayout(3, 1); folderRow.BackColor = Color.White;
            folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 8));
            folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));
            var folderFrame = new Surface { Dock = DockStyle.Fill, CornerRadius = 8, Padding = new Padding(9, 9, 8, 6), Margin = Padding.Empty };
            folder.Dock = DockStyle.Fill; folder.BorderStyle = BorderStyle.None; folder.Text = LoadFolder(); folder.AccessibleName = "保存文件夹";
            folderFrame.Controls.Add(folder); folderRow.Controls.Add(folderFrame, 0, 0); folderRow.Controls.Add(browseButton, 2, 0);
            inputLayout.Controls.Add(folderRow, 0, 4);
            var folderTools = MakeLayout(2, 1); folderTools.BackColor = Color.White;
            folderTools.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); folderTools.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 98));
            var openFolder = MakeButton("打开文件夹", ButtonTone.Quiet); folderTools.Controls.Add(openFolder, 1, 0);
            inputLayout.Controls.Add(folderTools, 0, 5);
            var divider = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = UiTheme.Border, Margin = new Padding(0, 6, 0, 0) };
            inputLayout.Controls.Add(divider, 0, 6);
            downloadButton.Font = new Font(Font.FontFamily, 10F, FontStyle.Bold);
            inputLayout.Controls.Add(downloadButton, 0, 7);
            inputLayout.Controls.Add(citeButton, 0, 9);
            tooltips.SetToolTip(downloadButton, "下载到所选文件夹（Ctrl + Enter）");
            tooltips.SetToolTip(citeButton, "生成 BibTeX 或 LaTeX 引用（Ctrl + Shift + Enter）");
            tooltips.SetToolTip(inputCount, "支持 arXiv 编号、INSPIRE 单篇论文链接、DOI，以及 PDF 直链。PDF 直链仅支持下载。");
            tooltips.SetToolTip(folder, folder.Text);
            folder.TextChanged += delegate { tooltips.SetToolTip(folder, folder.Text); };

            var workspace = new Surface { Dock = DockStyle.Fill, Padding = new Padding(20), Margin = Padding.Empty };
            body.Controls.Add(workspace, 2, 0);
            var workspaceLayout = MakeLayout(1, 3); workspaceLayout.BackColor = Color.White;
            workspaceLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            workspaceLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            workspaceLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            workspaceLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 5));
            workspace.Controls.Add(workspaceLayout);
            var workspaceHeader = MakeLayout(5, 1); workspaceHeader.BackColor = Color.White;
            workspaceHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 164));
            workspaceHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            workspaceHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 94));
            workspaceHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 68));
            workspaceHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0));
            var switcher = new Surface { Dock = DockStyle.Fill, CornerRadius = 9, SurfaceColor = UiTheme.Soft, BackColor = UiTheme.Soft, Padding = new Padding(3), Margin = new Padding(0, 0, 0, 12) };
            var switchLayout = MakeLayout(2, 1); switchLayout.BackColor = UiTheme.Soft;
            switchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); switchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            switchLayout.Controls.Add(taskTab, 0, 0); switchLayout.Controls.Add(citationTab, 1, 0); switcher.Controls.Add(switchLayout);
            workspaceHeader.Controls.Add(switcher, 0, 0);
            retryButton.Margin = new Padding(0, 0, 0, 12); cancelButton.Margin = new Padding(0, 0, 0, 12);
            retryButton.Enabled = false; cancelButton.Enabled = false;
            workspaceHeader.Controls.Add(retryButton, 2, 0); workspaceHeader.Controls.Add(cancelButton, 3, 0);
            workspaceLayout.Controls.Add(workspaceHeader, 0, 0);
            var views = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Margin = Padding.Empty };
            tasksView.Dock = citationsView.Dock = DockStyle.Fill; tasksView.BackColor = citationsView.BackColor = Color.White;
            views.Controls.Add(tasksView); views.Controls.Add(citationsView); workspaceLayout.Controls.Add(views, 0, 1);

            var taskLayout = MakeLayout(1, 2); taskLayout.BackColor = Color.White;
            taskLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            taskLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); taskLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            tasksView.Controls.Add(taskLayout);
            var gridHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Margin = Padding.Empty };
            BuildGrid(); gridHost.Controls.Add(grid); gridHost.Controls.Add(taskEmpty); taskEmpty.BringToFront();
            taskLayout.Controls.Add(gridHost, 0, 0);
            var taskFooter = MakeLayout(2, 1); taskFooter.BackColor = Color.White;
            taskFooter.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); taskFooter.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
            taskCount.Text = "0 个任务"; taskCount.ForeColor = Muted; taskCount.Font = new Font(Font.FontFamily, 8F); taskCount.Dock = DockStyle.Fill; taskCount.TextAlign = ContentAlignment.MiddleLeft;
            taskFooter.Controls.Add(taskCount, 0, 0); detailsButton.Enabled = false; taskFooter.Controls.Add(detailsButton, 1, 0);
            taskLayout.Controls.Add(taskFooter, 0, 1);

            var citationLayout = MakeLayout(1, 3); citationLayout.BackColor = Color.White;
            citationLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            citationLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); citationLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); citationLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            citationsView.Controls.Add(citationLayout);
            var citationToolbar = MakeLayout(4, 1); citationToolbar.BackColor = Color.White;
            citationToolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); citationToolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 10));
            citationToolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62)); citationToolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62));
            citationFormat.DropDownStyle = ComboBoxStyle.DropDownList; citationFormat.Dock = DockStyle.Fill; citationFormat.Margin = new Padding(0, 4, 0, 0);
            citationFormat.FlatStyle = FlatStyle.Flat; citationFormat.BackColor = UiTheme.Soft;
            citationFormat.Items.AddRange(new object[] { "BibTeX", "\\bibitem", "Overleaf 示例" }); citationFormat.SelectedIndex = 0; citationFormat.AccessibleName = "引用格式";
            copyButton.Enabled = saveButton.Enabled = false; copyButton.Margin = new Padding(0, 0, 6, 8); saveButton.Margin = new Padding(0, 0, 0, 8);
            citationToolbar.Controls.Add(citationFormat, 0, 0); citationToolbar.Controls.Add(copyButton, 2, 0); citationToolbar.Controls.Add(saveButton, 3, 0);
            citationLayout.Controls.Add(citationToolbar, 0, 0);
            var editorFrame = new Surface { Dock = DockStyle.Fill, CornerRadius = 8, Padding = new Padding(12), SurfaceColor = UiTheme.Soft, BackColor = UiTheme.Soft, Margin = Padding.Empty };
            citationText.Multiline = true; citationText.ReadOnly = true; citationText.ScrollBars = ScrollBars.Both; citationText.WordWrap = false;
            citationText.Dock = DockStyle.Fill; citationText.BorderStyle = BorderStyle.None; citationText.BackColor = UiTheme.Soft; citationText.ForeColor = Color.FromArgb(43, 65, 63);
            citationText.Font = new Font("Consolas", 10F); citationText.AccessibleName = "生成的 LaTeX 引用";
            editorFrame.Controls.Add(citationText); citationEmpty.BackColor = UiTheme.Soft; editorFrame.Controls.Add(citationEmpty); citationEmpty.BringToFront();
            citationLayout.Controls.Add(editorFrame, 0, 1);
            citationHint.Dock = DockStyle.Fill; citationHint.ForeColor = Muted; citationHint.Font = new Font(Font.FontFamily, 8F); citationHint.TextAlign = ContentAlignment.MiddleLeft; citationHint.AutoEllipsis = true;
            citationHint.Text = "0 条引用"; citationLayout.Controls.Add(citationHint, 0, 2);
            progress.Dock = DockStyle.Fill; progress.Margin = Padding.Empty; progress.Visible = false;
            workspaceLayout.Controls.Add(progress, 0, 2);

            var footer = MakeLayout(2, 1);
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 318)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            summary.Dock = DockStyle.Fill; summary.Text = "就绪"; summary.ForeColor = Muted; summary.Font = new Font(Font.FontFamily, 8F); summary.TextAlign = ContentAlignment.MiddleLeft;
            status.Dock = DockStyle.Fill; status.Text = ""; status.ForeColor = Muted; status.Font = new Font(Font.FontFamily, 8F); status.AutoEllipsis = true; status.TextAlign = ContentAlignment.MiddleRight;
            footer.Controls.Add(summary, 0, 0); footer.Controls.Add(status, 1, 0); root.Controls.Add(footer, 0, 2);

            downloadButton.Click += async delegate { await RunAsync(false, null); };
            citeButton.Click += async delegate { await RunAsync(true, null); };
            retryButton.Click += async delegate { await RunAsync(lastWasCitation, failed.ToArray()); };
            cancelButton.Click += delegate { if (cancellation != null) cancellation.Cancel(); cancelButton.Enabled = false; summary.Text = "正在取消…"; };
            browseButton.Click += delegate { ChooseFolder(); }; openFolder.Click += delegate { OpenOutputFolder(); };
            pasteButton.Click += delegate { try { if (Clipboard.ContainsText()) { links.SelectedText = Clipboard.GetText(); links.Focus(); } } catch (Exception ex) { ShowError(ex.Message); } };
            exampleButton.Click += delegate { links.Text = "https://arxiv.org/abs/1706.03762\r\nhttps://inspirehep.net/literature/451647"; links.Focus(); };
            taskTab.Click += delegate { SelectView(false); }; citationTab.Click += delegate { SelectView(true); };
            citationFormat.SelectedIndexChanged += delegate { RefreshCitationText(); };
            copyButton.Click += delegate { try { Clipboard.SetText(citationText.Text); status.Text = "已复制引用"; } catch (Exception ex) { ShowError(ex.Message); } };
            saveButton.Click += delegate { ExportCitation(); };
            detailsButton.Click += delegate { if (grid.SelectedRows.Count > 0) OpenRow(grid.SelectedRows[0].Index); };
            grid.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs e) { OpenRow(e.RowIndex); };
            grid.SelectionChanged += delegate
            {
                RowData row = grid.SelectedRows.Count == 0 ? null : grid.SelectedRows[0].Tag as RowData;
                detailsButton.Enabled = row != null && (!String.IsNullOrEmpty(row.FilePath) || !String.IsNullOrEmpty(row.Detail));
                detailsButton.Text = row != null && !String.IsNullOrEmpty(row.FilePath) ? "打开 PDF" : "查看详情";
                if (!busy && row != null && !String.IsNullOrEmpty(row.Detail)) { status.Text = row.Detail; tooltips.SetToolTip(status, row.Detail); }
            };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy) { e.Cancel = true; closeAfterCancel = true; if (cancellation != null) cancellation.Cancel(); summary.Text = "正在关闭…"; } };
            FormClosed += delegate { downloader.Dispose(); citer.Dispose(); tooltips.Dispose(); };
            Shown += delegate
            {
                UpdateMetrics();
                Rectangle area = Screen.FromControl(this).WorkingArea;
                MinimumSize = new Size(Math.Min(MinimumSize.Width, area.Width), Math.Min(MinimumSize.Height, area.Height));
                if (Width > area.Width || Height > area.Height) { Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height)); CenterToScreen(); }
                ArrangeCanvas();
                // Keep the empty input cue visible until the user starts typing.
                ActiveControl = pasteButton;
            };
            viewport.SizeChanged += delegate { ArrangeCanvas(); };
            KeyDown += async delegate(object sender, KeyEventArgs e)
            {
                if (e.Control && e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; e.Handled = true; if (!busy) await RunAsync(e.Shift, null); }
            };
            SelectView(false);
            ArrangeCanvas();
        }

        private void BuildGrid()
        {
            grid.Dock = DockStyle.Fill; grid.BackgroundColor = Color.White; grid.BorderStyle = BorderStyle.None;
            grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false; grid.AllowUserToResizeRows = false;
            grid.ReadOnly = true; grid.RowHeadersVisible = false; grid.MultiSelect = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.GridColor = Color.FromArgb(238, 242, 239);
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = UiTheme.Soft, ForeColor = Muted, Font = new Font(Font.FontFamily, 8F), Padding = new Padding(9, 0, 0, 0), SelectionBackColor = UiTheme.Soft };
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            grid.ColumnHeadersHeight = 32;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.DefaultCellStyle = new DataGridViewCellStyle { ForeColor = Muted, SelectionBackColor = Color.FromArgb(242, 247, 244), SelectionForeColor = Ink, Padding = new Padding(9, 0, 0, 0) };
            grid.RowTemplate.Height = 62;
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "source", HeaderText = "来源", AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = 80, MinimumWidth = 50 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "paper", HeaderText = "论文", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 120 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "state", HeaderText = "状态", AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = 100, MinimumWidth = 70 });
            foreach (DataGridViewColumn col in grid.Columns) col.SortMode = DataGridViewColumnSortMode.NotSortable;
            grid.CellPainting += PaintGridCell;
            grid.AccessibleName = "任务列表，双击查看文件或详情";
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
            failed.Clear(); grid.Rows.Clear(); taskEmpty.Visible = false;
            taskCount.Text = inputs.Length + " 个任务";
            status.Text = "";
            foreach (string input in inputs)
            {
                string kind = "—";
                try { var parsed = DownloadEngine.ParseInput(input); kind = parsed.Kind == "arxiv" ? "arXiv" : parsed.Kind == "inspire" ? "INSPIRE" : parsed.Kind.ToUpperInvariant(); } catch { }
                int index = grid.Rows.Add(kind, input, "等待中");
                grid.Rows[index].Tag = new RowData { Input = input };
                grid.Rows[index].Cells[1].ToolTipText = input;
            }
            lastWasCitation = makeCitations;
            SelectView(makeCitations);
            SetBusy(true);
            if (makeCitations) RefreshCitationText();
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
                summary.Text = String.Format("{0} {1} · 跳过 {2} · 失败 {3}", token.IsCancellationRequested ? "已取消 / 完成" : "完成", succeeded, skipped, errors);
                taskCount.Text = inputs.Length + " 个任务" + (failed.Count > 0 ? " · " + failed.Count + " 项可重试" : " · 全部完成");
                status.Text = makeCitations ? (citations.Count > 0 ? "已生成 " + citations.Count + " 条引用" : "选择失败任务查看原因") : (failed.Count > 0 ? "选择失败任务查看原因" : destination);
                tooltips.SetToolTip(status, status.Text);
                if (makeCitations && errors > 0) { citationHint.Text = citations.Count + " 条引用 · " + errors + " 项失败，可在任务中查看"; if (citations.Count == 0) SelectView(false); }
            }
            finally
            {
                runId++;
                progress.Style = ProgressBarStyle.Continuous;
                if (!token.IsCancellationRequested) progress.Value = 100;
                cancellation.Dispose(); cancellation = null;
                SetBusy(false);
                if (citations.Count == 0) RefreshCitationText();
                if (closeAfterCancel) Close();
            }
        }

        private void RefreshCitationText()
        {
            copyButton.Enabled = citations.Count > 0;
            saveButton.Enabled = citations.Count > 0;
            citationTab.Text = citations.Count == 0 ? "引用" : "引用 " + citations.Count;
            citationEmpty.Visible = citations.Count == 0;
            if (citations.Count == 0)
            {
                bool generating = busy && lastWasCitation;
                citationText.Clear(); citationEmpty.Heading = generating ? "正在生成引用" : "引用会显示在这里";
                citationEmpty.Hint = generating ? "正在读取论文信息…" : "支持 BibTeX、\\bibitem 和 Overleaf 示例。";
                citationEmpty.Invalidate(); citationEmpty.BringToFront(); citationHint.Text = "0 条引用"; return;
            }
            if (citationFormat.SelectedIndex == 0) citationText.Text = String.Join("\r\n\r\n", citations.Select(c => c.BibTeX.Trim()).ToArray()) + "\r\n";
            else if (citationFormat.SelectedIndex == 1) citationText.Text = "% 在导言区加入：\\usepackage[hidelinks]{hyperref}\r\n% 若标题含数学公式，还需：\\usepackage{amsmath,amssymb}\r\n% 正文中引用：\\cite{" + citations[0].Key + "}\r\n\r\n\\begin{thebibliography}{99}\r\n\r\n" + String.Join("\r\n\r\n", citations.Select(c => c.BibItem.Trim()).ToArray()) + "\r\n\r\n\\end{thebibliography}\r\n";
            else citationText.Text = "% 1. 将「BibTeX」内容保存为 references.bib，并上传至 Overleaf。\r\n% 2. 下方示例使用 biblatex + Biber，可点击 DOI / URL。\r\n% 3. 如果期刊模板已使用 BibTeX/natbib，请沿用模板，勿混用 biblatex。\r\n% 4. 已有文档只需合并相应导言区设置和正文命令。\r\n\r\n\\documentclass{article}\r\n\\usepackage{amsmath,amssymb}\r\n\\usepackage[backend=biber,style=numeric,sorting=none,url=true,doi=true]{biblatex}\r\n\\addbibresource{references.bib}\r\n\\usepackage[hidelinks]{hyperref}\r\n\r\n\\begin{document}\r\nSee \\cite{" + String.Join(",", citations.Select(c => c.Key).ToArray()) + "}.\r\n\r\n\\printbibliography\r\n\\end{document}\r\n";
            citationHint.Text = citations.Count + " 条引用" + (citationFormat.SelectedIndex == 1 ? " · 标题可点击" : citationFormat.SelectedIndex == 2 ? " · BibLaTeX / Biber" : " · references.bib");
            tooltips.SetToolTip(citationHint, citationFormat.SelectedIndex == 1 ? "如果文档已有 thebibliography 环境，只复制其中的条目。" : "BibTeX 的链接显示由模板决定，Overleaf 示例提供可点击 DOI / URL 的配置。");
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
            progress.Visible = value;
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
        private void SetRow(DataGridViewRow row, string text, Color color)
        {
            var data = row.Tag as RowData;
            string label = text;
            if (data != null)
            {
                if (data.State == "failed") label = "失败";
                else if (data.State == "cancelled") label = "已取消";
                else if (data.State == "running") label = text.Contains("下载") ? "下载中" : text.Contains("重试") ? "重试中" : "解析中";
                else if (data.State == "success") label = text.Contains("跳过") ? "已跳过" : text.Contains("合并") ? "已合并" : text.Contains("引用") ? "已生成" : "已完成";
                if (row.Selected)
                {
                    detailsButton.Enabled = !String.IsNullOrEmpty(data.FilePath) || !String.IsNullOrEmpty(data.Detail);
                    detailsButton.Text = !String.IsNullOrEmpty(data.FilePath) ? "打开 PDF" : "查看详情";
                }
            }
            row.Cells[2].Value = label; row.Cells[2].ToolTipText = text; row.Cells[2].Style.ForeColor = color;
            grid.InvalidateRow(row.Index);
        }
        private static ActionButton MakeButton(string text, ButtonTone tone)
        { return new ActionButton { Text = text, Tone = tone, AccessibleName = text }; }

        private static TableLayoutPanel MakeLayout(int columns, int rows)
        { return new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = columns, RowCount = rows, Margin = Padding.Empty, Padding = Padding.Empty }; }

        private static Label LabelFor(string text, float size, bool bold)
        { return new Label { Text = text, Dock = DockStyle.Fill, Margin = Padding.Empty, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Ink, Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular), AutoEllipsis = true }; }

        private void SelectView(bool showCitations)
        {
            tasksView.Visible = !showCitations; citationsView.Visible = showCitations;
            if (showCitations) citationsView.BringToFront(); else tasksView.BringToFront();
            taskTab.Selected = !showCitations; citationTab.Selected = showCitations;
        }

        private void UpdateMetrics()
        {
            float scale;
            using (Graphics g = CreateGraphics()) scale = g.DpiX / 96F;
            displayScale = scale;
            grid.RowTemplate.Height = (int)(62 * scale);
            foreach (DataGridViewRow row in grid.Rows) row.Height = grid.RowTemplate.Height;
            grid.ColumnHeadersHeight = (int)(32 * scale);
            grid.Columns[0].Width = (int)(80 * scale);
            grid.Columns[2].Width = (int)(100 * scale);
            grid.Columns[1].MinimumWidth = (int)(120 * scale);
        }

        private void ArrangeCanvas()
        {
            if (arrangingCanvas || rootLayout == null) return;
            arrangingCanvas = true;
            try
            {
                // On very small work areas a scrollable canvas keeps every action reachable.
                Size minimum = new Size((int)(900 * displayScale), (int)(580 * displayScale));
                viewport.AutoScrollMinSize = minimum;
                rootLayout.Location = viewport.AutoScrollPosition;
                rootLayout.Size = new Size(Math.Max(viewport.ClientSize.Width, minimum.Width), Math.Max(viewport.ClientSize.Height, minimum.Height));
            }
            finally { arrangingCanvas = false; }
        }

        private void PaintGridCell(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || (e.ColumnIndex != 1 && e.ColumnIndex != 2)) return;
            e.PaintBackground(e.CellBounds, true);
            float scale = e.Graphics.DpiX / 96F;
            var data = grid.Rows[e.RowIndex].Tag as RowData;
            int pad = (int)(10 * scale);
            if (e.ColumnIndex == 1)
            {
                string title = Convert.ToString(e.FormattedValue);
                string subtitle = data == null ? "" : data.Input;
                Rectangle first = new Rectangle(e.CellBounds.X + pad, e.CellBounds.Y + (int)(11 * scale), e.CellBounds.Width - pad * 2, (int)(22 * scale));
                Rectangle second = new Rectangle(e.CellBounds.X + pad, e.CellBounds.Y + (int)(35 * scale), e.CellBounds.Width - pad * 2, (int)(18 * scale));
                TextRenderer.DrawText(e.Graphics, title, Font, first, Ink, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                using (var small = new Font(Font.FontFamily, 8F))
                    TextRenderer.DrawText(e.Graphics, subtitle == title ? "" : subtitle, small, second, Muted, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            }
            else
            {
                string state = data == null ? "" : data.State;
                Color fill = Color.FromArgb(242, 245, 243), color = Muted;
                if (state == "success") { fill = Color.FromArgb(232, 244, 236); color = Color.FromArgb(39, 111, 77); }
                else if (state == "running") { fill = Color.FromArgb(231, 241, 244); color = Color.FromArgb(41, 105, 121); }
                else if (state == "failed") { fill = Color.FromArgb(251, 235, 232); color = Color.FromArgb(167, 66, 49); }
                var badge = new RectangleF(e.CellBounds.X + 6 * scale, e.CellBounds.Y + (e.CellBounds.Height - 26 * scale) / 2, Math.Min(e.CellBounds.Width - 12 * scale, 82 * scale), 26 * scale);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath path = UiTheme.Rounded(badge, 6 * scale))
                using (var brush = new SolidBrush(fill)) e.Graphics.FillPath(brush, path);
                using (var small = new Font(Font.FontFamily, 8F))
                    TextRenderer.DrawText(e.Graphics, Convert.ToString(e.FormattedValue), small, Rectangle.Round(badge), color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
            using (var line = new Pen(Color.FromArgb(238, 242, 239))) e.Graphics.DrawLine(line, e.CellBounds.Left, e.CellBounds.Bottom - 1, e.CellBounds.Right, e.CellBounds.Bottom - 1);
            e.Handled = true;
        }

        private void ShowHelp()
        {
            MessageBox.Show(this,
                "支持的输入\r\n\r\n" +
                "• arXiv 链接或编号，如 1706.03762\r\n" +
                "• INSPIRE 单篇论文链接\r\n" +
                "• DOI，如 10.4310/ATMP.1998.v2.n2.a1\r\n" +
                "• 公开 PDF 直链（仅下载）\r\n\r\n" +
                "每行一篇，最多 200 篇。DOI 通过 INSPIRE 查找。\r\n" +
                "已有完整 PDF 会跳过；双击完成的任务打开文件。\r\n\r\n" +
                "Ctrl + Enter：下载 PDF\r\n" +
                "Ctrl + Shift + Enter：生成引用\r\n\r\n" +
                "引用格式可在「引用」页切换。Overleaf 示例包含完整用法。",
                "使用帮助", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private sealed class RowData { public string Input, State, FilePath, Detail; }
    }
}
