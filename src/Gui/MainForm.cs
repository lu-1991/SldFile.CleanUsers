using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SldFile.CleanUsers.Gui;

internal sealed class MainForm : Form
{
    private static readonly string[] Extensions = { ".sldprt", ".sldasm", ".slddrw" };

    private readonly DataGridView _fileList = new();
    private readonly TextBox _log = new();
    private readonly RadioButton _modeEmpty = new();
    private readonly RadioButton _modePlaceholder = new();
    private readonly TextBox _placeholderText = new();
    private readonly CheckBox _backup = new();
    private readonly CheckBox _recursive = new();
    private readonly Button _startButton = new();
    private readonly Button _addFiles = new();
    private readonly Button _addFolder = new();
    private readonly Button _removeSelected = new();
    private readonly Button _clearList = new();
    private readonly ProgressBar _progress = new();
    private readonly Label _status = new();
    private bool _busy;

    public MainForm()
    {
        Text = "SolidWorks 用户信息清理工具";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1040, 640);
        Size = new Size(1120, 760);
        Font = new Font("Microsoft YaHei UI", 9F);
        AllowDrop = true;
        BackColor = Color.FromArgb(248, 249, 251);

        BuildLayout();

        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(14),
            BackColor = Color.Transparent
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // 标题
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 60)); // 文件列表（用户名会换行，需要更多高度）
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // 选项
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 40)); // 日志
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // 底部状态
        Controls.Add(root);

        // ── 标题 ──
        var header = new Label
        {
            Text = "SolidWorks 用户信息清理",
            Font = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(2, 0, 0, 2)
        };
        var subtitle = new Label
        {
            Text = "清除 .sldprt / .sldasm / .slddrw 内部记录的用户名、作者与最后保存者。支持拖放文件或文件夹。",
            ForeColor = Color.FromArgb(110, 116, 128),
            AutoSize = true,
            Margin = new Padding(3, 0, 0, 10)
        };
        var headerPanel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill };
        headerPanel.Controls.Add(header);
        headerPanel.Controls.Add(subtitle);
        root.Controls.Add(headerPanel, 0, 0);

        // ── 文件列表 + 工具栏 ──
        var listPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        listPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        listPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var toolbar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 6) };
        Configure(_addFiles, "添加文件…", 100);
        Configure(_addFolder, "添加文件夹…", 110);
        Configure(_removeSelected, "移除选中", 90);
        Configure(_clearList, "清空列表", 90);
        _addFiles.Click += (_, _) => PickFiles();
        _addFolder.Click += (_, _) => PickFolder();
        _removeSelected.Click += (_, _) => RemoveSelected();
        _clearList.Click += (_, _) => { _fileList.Rows.Clear(); UpdateStatus(); };
        toolbar.Controls.AddRange(new Control[] { _addFiles, _addFolder, _removeSelected, _clearList });
        listPanel.Controls.Add(toolbar, 0, 0);

        // 用 DataGridView 而不是 ListView：用户名要完整显示，需要单元格内自动换行。
        _fileList.Dock = DockStyle.Fill;
        _fileList.ReadOnly = true;
        _fileList.AllowUserToAddRows = false;
        _fileList.AllowUserToDeleteRows = false;
        _fileList.AllowUserToResizeRows = false;
        _fileList.RowHeadersVisible = false;
        _fileList.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _fileList.MultiSelect = true;
        _fileList.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _fileList.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCells;
        _fileList.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        _fileList.BackgroundColor = Color.White;
        _fileList.BorderStyle = BorderStyle.FixedSingle;
        _fileList.GridColor = Color.FromArgb(232, 234, 238);
        _fileList.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        _fileList.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        _fileList.EnableHeadersVisualStyles = false;
        _fileList.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(245, 246, 249);
        _fileList.ColumnHeadersDefaultCellStyle.Padding = new Padding(6, 4, 6, 4);
        _fileList.DefaultCellStyle.Padding = new Padding(6, 4, 6, 4);
        _fileList.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _fileList.DefaultCellStyle.SelectionBackColor = Color.FromArgb(229, 241, 255);
        _fileList.DefaultCellStyle.SelectionForeColor = Color.Black;
        _fileList.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(252, 252, 253);

        var colFile = new DataGridViewTextBoxColumn
        {
            Name = "colFile",
            HeaderText = "文件",
            FillWeight = 26,
            MinimumWidth = 150,
            SortMode = DataGridViewColumnSortMode.NotSortable
        };
        var colUsers = new DataGridViewTextBoxColumn
        {
            Name = "colUsers",
            HeaderText = "用户名",
            FillWeight = 52,
            MinimumWidth = 220,
            SortMode = DataGridViewColumnSortMode.NotSortable
        };
        var colStatus = new DataGridViewTextBoxColumn
        {
            Name = "colStatus",
            HeaderText = "状态",
            FillWeight = 22,
            MinimumWidth = 160,
            SortMode = DataGridViewColumnSortMode.NotSortable
        };
        _fileList.Columns.AddRange(colFile, colUsers, colStatus);

        _fileList.AllowDrop = true;
        _fileList.DragEnter += OnDragEnter;
        _fileList.DragDrop += OnDragDrop;
        listPanel.Controls.Add(_fileList, 0, 1);
        root.Controls.Add(listPanel, 0, 1);

        // ── 选项 ──
        var options = new GroupBox
        {
            Text = "清理选项",
            AutoSize = true,
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 8, 12, 10),
            Margin = new Padding(0, 8, 0, 8),
            BackColor = Color.White
        };
        var optionGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        optionGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        optionGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        _modeEmpty.Text = "彻底删除用户列表（清空，显示为空）";
        _modeEmpty.Checked = true;
        _modeEmpty.AutoSize = true;
        _modeEmpty.Margin = new Padding(0, 4, 0, 4);

        var placeholderRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
        _modePlaceholder.Text = "替换为占位符：";
        _modePlaceholder.AutoSize = true;
        _modePlaceholder.Margin = new Padding(0, 6, 4, 0);
        _placeholderText.Width = 120;
        _placeholderText.Text = "user";
        _placeholderText.Enabled = false;
        _modePlaceholder.CheckedChanged += (_, _) => _placeholderText.Enabled = _modePlaceholder.Checked;
        placeholderRow.Controls.Add(_modePlaceholder);
        placeholderRow.Controls.Add(_placeholderText);

        _backup.Text = "备份原文件（.bak-时间戳）";
        _backup.Checked = true;
        _backup.AutoSize = true;
        _backup.Margin = new Padding(0, 4, 0, 4);
        _recursive.Text = "文件夹包含子目录";
        _recursive.Checked = true;
        _recursive.AutoSize = true;
        _recursive.Margin = new Padding(0, 4, 0, 4);

        optionGrid.Controls.Add(_modeEmpty, 0, 0);
        optionGrid.SetColumnSpan(_modeEmpty, 2);
        optionGrid.Controls.Add(placeholderRow, 0, 1);
        optionGrid.SetColumnSpan(placeholderRow, 2);
        optionGrid.Controls.Add(_backup, 0, 2);
        optionGrid.Controls.Add(_recursive, 1, 2);
        options.Controls.Add(optionGrid);
        root.Controls.Add(options, 0, 2);

        // ── 日志 ──
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.Dock = DockStyle.Fill;
        _log.BackColor = Color.FromArgb(252, 252, 253);
        _log.Font = new Font("Consolas", 9F);
        _log.BorderStyle = BorderStyle.FixedSingle;
        root.Controls.Add(_log, 0, 3);

        // ── 底部 ──
        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        Configure(_startButton, "开始清理", 130);
        _startButton.Height = 38;
        _startButton.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
        _startButton.BackColor = Color.FromArgb(0, 120, 212);
        _startButton.ForeColor = Color.White;
        _startButton.FlatStyle = FlatStyle.Flat;
        _startButton.FlatAppearance.BorderSize = 0;
        _startButton.Click += async (_, _) => await RunCleanupAsync();

        _progress.Dock = DockStyle.Fill;
        _progress.Height = 18;
        _progress.Margin = new Padding(12, 10, 12, 10);
        _progress.Style = ProgressBarStyle.Continuous;

        _status.AutoSize = true;
        _status.Text = "就绪";
        _status.ForeColor = Color.FromArgb(110, 116, 128);
        _status.Margin = new Padding(0, 10, 0, 0);

        bottom.Controls.Add(_startButton, 0, 0);
        bottom.Controls.Add(_progress, 1, 0);
        bottom.Controls.Add(_status, 2, 0);
        root.Controls.Add(bottom, 0, 4);
    }

    private static void Configure(Button button, string text, int width)
    {
        button.Text = text;
        button.Width = width;
        button.Height = 30;
        button.Margin = new Padding(0, 0, 8, 0);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Color.FromArgb(210, 214, 220);
        button.BackColor = Color.White;
        button.Cursor = Cursors.Hand;
    }

    // ── 文件收集 ──

    private void PickFiles()
    {
        using var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "SolidWorks 文件|*.sldprt;*.sldasm;*.slddrw|所有文件|*.*"
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            AddPaths(dialog.FileNames);
    }

    private void PickFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "选择包含 SolidWorks 文件的文件夹" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            AddPaths(new[] { dialog.SelectedPath });
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
            e.Effect = DragDropEffects.Copy;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths)
            AddPaths(paths);
    }

    private void AddPaths(IEnumerable<string> paths)
    {
        var existing = _fileList.Rows.Cast<DataGridViewRow>()
            .Select(r => r.Tag as string)
            .Where(p => p != null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var addedItems = new List<DataGridViewRow>();
        foreach (var path in Expand(paths))
        {
            if (!existing.Add(path)) continue;
            int index = _fileList.Rows.Add(Path.GetFileName(path), "读取中…", "待处理");
            var row = _fileList.Rows[index];
            row.Tag = path;
            row.Cells[0].ToolTipText = path;
            addedItems.Add(row);
        }

        if (addedItems.Count > 0)
        {
            AppendLog($"已添加 {addedItems.Count} 个文件。");
            _ = LoadUsersAsync(addedItems);
        }
        UpdateStatus();
    }

    /// <summary>Reads the recorded user names of the given rows and fills the 用户名 column.</summary>
    private async Task LoadUsersAsync(List<DataGridViewRow> items)
    {
        int total = items.Count, done = 0;
        using var gate = new SemaphoreSlim(Math.Max(2, Environment.ProcessorCount / 2));

        var tasks = items.Select(async item =>
        {
            await gate.WaitAsync();
            try
            {
                string path = (string)item.Tag!;
                string text;
                try
                {
                    using var probe = SldFile.SwFile.Open(path, readOnly: true);
                    text = FormatUsers(probe.Users);
                }
                catch (Exception ex)
                {
                    text = "读取失败：" + ex.Message;
                }
                Invoke(() =>
                {
                    if (item.DataGridView == null) return;   // 已被移除
                    item.Cells[1].Value = text;
                    if (_status != null && !_busy)
                        _status.Text = $"正在读取用户名… {++done}/{total}";
                });
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
        Invoke(() =>
        {
            if (_status != null && !_busy) _status.Text = "就绪";
            UpdateStatus();
        });
    }

    /// <summary>完整列出全部用户名（不省略、不换行符截断），由单元格自动换行显示。</summary>
    private static string FormatUsers(IReadOnlyList<string> users)
    {
        var names = users.Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
        return names.Count == 0 ? "（无）" : string.Join("、", names);
    }

    private IEnumerable<string> Expand(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                if (IsSolidWorksFile(path)) yield return Path.GetFullPath(path);
                else AppendLog($"跳过（不是 SolidWorks 文件）: {path}");
            }
            else if (Directory.Exists(path))
            {
                var option = _recursive.Checked ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                foreach (var ext in Extensions)
                    foreach (var file in Directory.EnumerateFiles(path, "*" + ext, option))
                        yield return Path.GetFullPath(file);
            }
        }
    }

    private static bool IsSolidWorksFile(string path) =>
        Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    private void RemoveSelected()
    {
        // 逆序删除，避免索引前移导致漏删。
        foreach (var index in _fileList.SelectedRows.Cast<DataGridViewRow>()
                     .Select(r => r.Index)
                     .OrderByDescending(i => i)
                     .ToList())
            _fileList.Rows.RemoveAt(index);
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        _status.Text = _fileList.Rows.Count == 0 ? "就绪 · 请添加文件" : $"共 {_fileList.Rows.Count} 个文件";
    }

    // ── 执行 ──

    private async Task RunCleanupAsync()
    {
        if (_fileList.Rows.Count == 0)
        {
            MessageBox.Show(this, "请先添加要清理的文件或文件夹。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!_modeEmpty.Checked && string.IsNullOrWhiteSpace(_placeholderText.Text))
        {
            MessageBox.Show(this, "占位符内容不能为空。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        bool purge = _modeEmpty.Checked;
        string placeholder = _placeholderText.Text.Trim();
        bool backup = _backup.Checked;

        SetBusy(true);
        _log.Clear();
        AppendLog(purge ? "模式：彻底删除用户列表（清空）" : $"模式：替换为占位符 \"{placeholder}\"");
        AppendLog(backup ? "备份：开启" : "备份：关闭");
        AppendLog(new string('─', 72));

        var items = _fileList.Rows.Cast<DataGridViewRow>().ToList();
        _progress.Maximum = items.Count;
        _progress.Value = 0;

        int ok = 0, skipped = 0, failed = 0;

        await Task.Run(() =>
        {
            foreach (var item in items)
            {
                string path = (string)item.Tag!;
                try
                {
                    var outcome = CleanOne(path, purge, placeholder, backup, out string detail, out string? usersText);
                    switch (outcome)
                    {
                        case Outcome.Cleaned: ok++; break;
                        case Outcome.Skipped: skipped++; break;
                        default: failed++; break;
                    }
                    Report(item, detail, usersText, outcome);
                }
                catch (Exception ex)
                {
                    failed++;
                    Report(item, "失败: " + ex.Message, null, Outcome.Failed);
                }

                Invoke(() => _progress.Value = Math.Min(_progress.Maximum, _progress.Value + 1));
            }
        });

        AppendLog(new string('─', 72));
        AppendLog($"完成：成功 {ok} 个，跳过 {skipped} 个，失败 {failed} 个。");
        _status.Text = $"完成 · 成功 {ok} / 失败 {failed}";
        SetBusy(false);

        if (failed > 0)
            MessageBox.Show(this, $"有 {failed} 个文件处理失败，详见日志。", "完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private enum Outcome { Cleaned, Skipped, Failed }

    private Outcome CleanOne(string path, bool purge, string placeholder, bool backup, out string detail, out string? usersText)
    {
        List<string> users;
        string? author, lastSavedBy;
        using (var probe = SwFile.Open(path, readOnly: true))
        {
            users = probe.Users.Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
            author = probe.Metadata.Author;
            lastSavedBy = probe.Metadata.LastSavedBy;
        }

        usersText = FormatUsers(users);
        bool hasUsers = users.Count > 0;
        bool hasAuthor = !string.IsNullOrWhiteSpace(author) || !string.IsNullOrWhiteSpace(lastSavedBy);
        if (!hasUsers && !hasAuthor)
        {
            detail = "无需清理";
            return Outcome.Skipped;
        }

        string? backupPath = null;
        if (backup)
        {
            backupPath = path + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Copy(path, backupPath, overwrite: false);
        }

        List<string> removed;
        using (var doc = SwFile.Open(path, readOnly: false))
        {
            doc.Metadata.Author = "";
            doc.Metadata.LastSavedBy = "";
            removed = (purge ? doc.ClearUsers() : doc.SanitizeUsers(placeholder)).ToList();
            doc.Save();
        }

        List<string> remainingUsers;
        using (var verify = SwFile.Open(path, readOnly: true))
            remainingUsers = verify.Users.Where(u => !string.IsNullOrWhiteSpace(u)).ToList();

        usersText = FormatUsers(remainingUsers);
        detail = purge
            ? $"已删除 {removed.Count} 个用户名 · 现 {remainingUsers.Count} 个"
            : $"已替换 {removed.Count} 个用户名";
        if (backupPath != null)
        {
            detail += " · 已备份";
            AppendLog($"   备份: {Path.GetFileName(backupPath)}");
        }
        return Outcome.Cleaned;
    }

    private void Report(DataGridViewRow item, string detail, string? usersText, Outcome outcome)
    {
        Invoke(() =>
        {
            if (item.DataGridView == null) return;   // 已被移除
            if (usersText != null)
                item.Cells[1].Value = usersText;     // 用户名
            item.Cells[2].Value = detail;            // 状态
            item.DefaultCellStyle.ForeColor = outcome switch
            {
                Outcome.Cleaned => Color.FromArgb(16, 124, 16),
                Outcome.Skipped => Color.FromArgb(120, 120, 120),
                _ => Color.FromArgb(196, 43, 28)
            };
            AppendLog($"[{outcome}] {item.Cells[0].Value} — {detail}");
        });
    }

    private void AppendLog(string message)
    {
        if (_log.InvokeRequired)
        {
            Invoke(() => AppendLog(message));
            return;
        }
        _log.AppendText(message + Environment.NewLine);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _startButton.Enabled = !busy;
        _addFiles.Enabled = !busy;
        _addFolder.Enabled = !busy;
        _removeSelected.Enabled = !busy;
        _clearList.Enabled = !busy;
        _modeEmpty.Enabled = !busy;
        _modePlaceholder.Enabled = !busy;
        _backup.Enabled = !busy;
        _recursive.Enabled = !busy;
        _startButton.Text = busy ? "正在清理…" : "开始清理";
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }
}
