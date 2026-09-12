using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Markdig;

namespace ClipboardQueue;

internal sealed class ClipItem
{
    public ClipItem(string text, string? html) { Text = text; Html = html; }
    public string Text { get; }
    public string? Html { get; }
}

internal sealed class FilterWordsDialog : Form
{
    private readonly TextBox _box;
    public FilterWordsDialog(IEnumerable<string> words)
    {
        Text = "Filter words (one per line)";
        Width = 420; Height = 520;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
        _box = new TextBox { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, WordWrap = false, Text = string.Join(Environment.NewLine, words) };
        var save = new Button { Text = "Save", Dock = DockStyle.Bottom, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Dock = DockStyle.Bottom, DialogResult = DialogResult.Cancel };
        Controls.Add(_box); Controls.Add(save); Controls.Add(cancel);
        CancelButton = cancel;
    }
    public string[] Words => _box.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.None).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
}

public sealed class MainForm : Form
{
    private const int MaxItems = 500;
    private const int MaxItemLength = 50_000;
    private const int MaxHtmlLength = 1_000_000;
    private const long MaxTotalChars = 20_000_000;
    private const int PreviewLength = 300;
    private const double RepeatCopyWindowSeconds = 2.0;
    private const int SyncDelayMs = 300;
    private const int FocusPollMs = 300;
    private const double GestureFreshnessMs = 2000;
    private const double ConsumeCooldownMs = 500;
    private const double CustomMenuWindowSeconds = 2.0;
    private const double ClipboardProtectMs = 700;

    private readonly Queue<ClipItem> _items = new();
    private readonly object _sync = new();
    private readonly AppSettings _settings;
    private readonly bool _startHidden;

    private readonly ListView _listView;
    private readonly Label _countLabel;
    private readonly CheckBox _pauseCheckBox;
    private readonly CheckBox _startupCheckBox;
    private readonly CheckBox _loggingCheckBox;
    private readonly CheckBox _enableFilterCheckBox;
    private readonly CheckBox _stripLinksCheckBox;
    private readonly CheckBox _stripBulletsCheckBox;
    private readonly CheckBox _gapBlanksCheckBox;
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _pauseMenuItem;
    private readonly ToolStripMenuItem _startupMenuItem;

    private KeyboardHook? _keyboardHook;
    private MouseHook? _mouseHook;
    private CursorCounter? _cursorCounter;
    private SynchronizationContext? _uiContext;

    private System.Windows.Forms.Timer? _clipboardTimer;
    private System.Windows.Forms.Timer? _syncTimer;
    private System.Windows.Forms.Timer? _focusTimer;
    private System.Windows.Forms.Timer? _menuConfirmTimer;
    private System.Windows.Forms.Timer? _renderConsumeTimer;
    private uint _lastClipboardSequence;
    private uint _menuConfirmSeq;
    private int _confirmStage;

    private bool _armed;
    private bool _realMode;
    private string _lastFgName = string.Empty;
    private DateTime _armedAt = DateTime.MinValue;
    private DateTime _lastArmTime = DateTime.MinValue;
    private DateTime _consumeCooldownUntil = DateTime.MinValue;
    private DateTime _protectClipboardUntil = DateTime.MinValue;
    private ClipItem? _renderedItem;

    private bool _exitRequested;
    private bool _cleanedUp;
    private bool _pauseMonitoring;
    private bool _updatingPause;
    private bool _updatingStartup;
    private bool _suppressCounter = true;
    private int _lastCount = -1;
    private bool _pasteBusy;

    private long _consumedCount;
    private long _confirmConsumedCount;
    private string? _filterPattern;

    private string _lastProgrammaticClipboardText = string.Empty;
    private DateTime _lastProgrammaticClipboardTime = DateTime.MinValue;
    private string _lastStoredText = string.Empty;
    private string? _lastStoredHtml;
    private DateTime _lastStoredTime = DateTime.MinValue;

    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().UseSoftlineBreakAsHardlineBreak().Build();

    public MainForm(bool startHidden)
    {
        _settings = SettingsManager.Load();
        _startHidden = startHidden;
        Text = "Clipboard Queue 1.55";
        Width = 800; Height = 500; MinimumSize = new Size(500, 300);
        StartPosition = FormStartPosition.CenterScreen; ShowInTaskbar = false;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _listView = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = true };
        _listView.Columns.Add("Stored clipboard items (oldest first)", 750);
        var buttonPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };

        var pasteNextButton = new Button { Text = "Paste next (Ctrl+V)", AutoSize = true }; pasteNextButton.Click += (_, _) => PasteNext();
        var pasteAllButton = new Button { Text = "Paste all (Ctrl+Alt+V or Ctrl+V + left mouse)", AutoSize = true }; pasteAllButton.Click += (_, _) => PasteAll();
        var deleteSelectedButton = new Button { Text = "Delete selected", AutoSize = true }; deleteSelectedButton.Click += (_, _) => DeleteSelected();
        var clearAllButton = new Button { Text = "Clear all", AutoSize = true }; clearAllButton.Click += (_, _) => ClearAll();
        var minimizeToTrayButton = new Button { Text = "Minimize to tray", AutoSize = true }; minimizeToTrayButton.Click += (_, _) => HideQueueWindow();
        var filterButton = new Button { Text = "Filter words…", AutoSize = true }; filterButton.Click += (_, _) => OpenFilterDialog();

        _pauseCheckBox = new CheckBox { Text = "Pause monitoring", AutoSize = true, Checked = false }; _pauseCheckBox.CheckedChanged += (_, _) => SetPauseMonitoring(_pauseCheckBox.Checked);
        _startupCheckBox = new CheckBox { Text = "Start with Windows", AutoSize = true, Checked = StartupManager.IsEnabled() }; _startupCheckBox.CheckedChanged += (_, _) => SetStartWithWindows(_startupCheckBox.Checked);
        _loggingCheckBox = new CheckBox { Text = "Logging", AutoSize = true, Checked = _settings.Diagnostics }; _loggingCheckBox.CheckedChanged += (_, _) => SetLogging(_loggingCheckBox.Checked);
        _enableFilterCheckBox = new CheckBox { Text = "Enable filter (plain text)", AutoSize = true, Checked = _settings.EnableFilter }; _enableFilterCheckBox.CheckedChanged += (_, _) => SetEnableFilter(_enableFilterCheckBox.Checked);
        _stripLinksCheckBox = new CheckBox { Text = "Strip hyperlinks", AutoSize = true, Checked = _settings.StripHyperlinks }; _stripLinksCheckBox.CheckedChanged += (_, _) => SetStripHyperlinks(_stripLinksCheckBox.Checked);
        _stripBulletsCheckBox = new CheckBox { Text = "Strip bullet points", AutoSize = true, Checked = _settings.StripBulletPoints }; _stripBulletsCheckBox.CheckedChanged += (_, _) => SetStripBulletPoints(_stripBulletsCheckBox.Checked);
        _gapBlanksCheckBox = new CheckBox { Text = "Blank line at paragraph gaps", AutoSize = true, Checked = _settings.ParagraphGapBlankLines }; _gapBlanksCheckBox.CheckedChanged += (_, _) => SetGapBlanks(_gapBlanksCheckBox.Checked);
        _countLabel = new Label { AutoSize = true, Text = "0 items", TextAlign = ContentAlignment.MiddleLeft };

        buttonPanel.Controls.Add(pasteNextButton); buttonPanel.Controls.Add(pasteAllButton); buttonPanel.Controls.Add(deleteSelectedButton);
        buttonPanel.Controls.Add(clearAllButton); buttonPanel.Controls.Add(minimizeToTrayButton); buttonPanel.Controls.Add(filterButton);
        buttonPanel.Controls.Add(_pauseCheckBox); buttonPanel.Controls.Add(_startupCheckBox); buttonPanel.Controls.Add(_loggingCheckBox);
        buttonPanel.Controls.Add(_enableFilterCheckBox); buttonPanel.Controls.Add(_stripLinksCheckBox); buttonPanel.Controls.Add(_stripBulletsCheckBox);
        buttonPanel.Controls.Add(_gapBlanksCheckBox); buttonPanel.Controls.Add(_countLabel);

        root.Controls.Add(_listView, 0, 0); root.Controls.Add(buttonPanel, 0, 1); Controls.Add(root);

        var trayMenu = new ContextMenuStrip();
        var openItem = new ToolStripMenuItem("Open"); openItem.Click += (_, _) => ShowQueueWindow();
        var trayPasteNext = new ToolStripMenuItem("Paste next"); trayPasteNext.Click += (_, _) => PasteNext();
        var trayPasteAll = new ToolStripMenuItem("Paste all"); trayPasteAll.Click += (_, _) => PasteAll();
        _pauseMenuItem = new ToolStripMenuItem("Pause monitoring") { CheckOnClick = true, Checked = false }; _pauseMenuItem.Click += (_, _) => SetPauseMonitoring(_pauseMenuItem.Checked);
        _startupMenuItem = new ToolStripMenuItem("Start with Windows") { CheckOnClick = true, Checked = StartupManager.IsEnabled() }; _startupMenuItem.Click += (_, _) => SetStartWithWindows(_startupMenuItem.Checked);
        var clearMenuItem = new ToolStripMenuItem("Clear all"); clearMenuItem.Click += (_, _) => ClearAll();
        var minimizeToTrayItem = new ToolStripMenuItem("Minimize to tray"); minimizeToTrayItem.Click += (_, _) => HideQueueWindow();
        var exitItem = new ToolStripMenuItem("Exit"); exitItem.Click += (_, _) => ExitApplication();
        trayMenu.Items.Add(openItem); trayMenu.Items.Add(new ToolStripSeparator()); trayMenu.Items.Add(trayPasteNext); trayMenu.Items.Add(trayPasteAll);
        trayMenu.Items.Add(new ToolStripSeparator()); trayMenu.Items.Add(_pauseMenuItem); trayMenu.Items.Add(_startupMenuItem);
        trayMenu.Items.Add(clearMenuItem); trayMenu.Items.Add(minimizeToTrayItem); trayMenu.Items.Add(new ToolStripSeparator()); trayMenu.Items.Add(exitItem);

        Icon trayIcon = SystemIcons.Application;
        try { string? exe = Environment.ProcessPath; if (!string.IsNullOrEmpty(exe)) trayIcon = Icon.ExtractAssociatedIcon(exe) ?? SystemIcons.Application; } catch { }
        _notifyIcon = new NotifyIcon { Icon = trayIcon, Text = "Clipboard Queue: 0 items", Visible = true, ContextMenuStrip = trayMenu };
        _notifyIcon.DoubleClick += (_, _) => ShowQueueWindow();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();
        _cursorCounter = new CursorCounter();
        RebuildFilterRegex();
        NativeMethods.AddClipboardFormatListener(Handle);
        _lastClipboardSequence = NativeMethods.GetClipboardSequenceNumber();

        _clipboardTimer = new System.Windows.Forms.Timer { Interval = 400 }; _clipboardTimer.Tick += (_, _) => OnClipboardUpdate(); _clipboardTimer.Start();
        _syncTimer = new System.Windows.Forms.Timer { Interval = SyncDelayMs }; _syncTimer.Tick += (_, _) => { _syncTimer.Stop(); SyncClipboardOwnership(); };
        _focusTimer = new System.Windows.Forms.Timer { Interval = FocusPollMs }; _focusTimer.Tick += (_, _) => OnFocusPoll(); _focusTimer.Start();
        _renderConsumeTimer = new System.Windows.Forms.Timer { Interval = 250 }; _renderConsumeTimer.Tick += (_, _) => ConsumeRenderedItem();
        _menuConfirmTimer = new System.Windows.Forms.Timer { Interval = 400 }; _menuConfirmTimer.Tick += (_, _) => OnMenuConfirmTick();

        try { _keyboardHook = new KeyboardHook { ShouldHandleCtrlV = () => _settings.OverrideCtrlV && GetCount() > 0, ShouldHandleCtrlAltV = () => GetCount() > 0, CtrlVPressed = () => PostToUi(PasteNext), CtrlAltVPressed = () => PostToUi(PasteAll) }; } catch { }
        try { _mouseHook = new MouseHook { LeftClickAfterRightClick = (seq, first) => PostToUi(() => OnLeftClick(seq, first)) }; } catch { }

        OnFocusPoll();
        if (_startHidden) HideQueueWindow(); else ShowQueueWindow();
        RefreshUi();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_CLIPBOARDUPDATE) { OnClipboardUpdate(); base.WndProc(ref m); return; }
        if (m.Msg == NativeClipboard.WM_DESTROYCLIPBOARD) { _armed = false; base.WndProc(ref m); return; }
        if (m.Msg == NativeClipboard.WM_RENDERFORMAT) { HandleRenderFormat((uint)m.WParam.ToInt64()); return; }
        if (m.Msg == NativeClipboard.WM_RENDERALLFORMATS) { HandleRenderFormat(NativeClipboard.CF_UNICODETEXT); HandleRenderFormat(NativeClipboard.CfHtml); return; }
        base.WndProc(ref m);
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); if (WindowState == FormWindowState.Minimized) HideQueueWindow(); }
    protected override void OnFormClosing(FormClosingEventArgs e) { if (!_exitRequested && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; HideQueueWindow(); base.OnFormClosing(e); return; } Cleanup(); base.OnFormClosing(e); }

    private void OpenFilterDialog()
    {
        using var d = new FilterWordsDialog(_settings.FilterWords);
        if (d.ShowDialog(this) == DialogResult.OK) { _settings.FilterWords = d.Words.ToList(); SettingsManager.Save(_settings); RebuildFilterRegex(); ScheduleSync(); }
    }

    private void RebuildFilterRegex()
    {
        if (!_settings.EnableFilter) { _filterPattern = null; return; }
        var entries = (_settings.FilterWords ?? new List<string>()).Select(w => w.TrimEnd()).Where(w => w.Length > 0).Select(Regex.Escape).ToList();
        if (entries.Count == 0) { _filterPattern = null; return; }
        _filterPattern = @"(?:" + string.Join("|", entries) + @")[ \t]*";
    }

    private string ApplyFilter(string text) => (_filterPattern == null || !_settings.EnableFilter) ? text : Regex.Replace(text, _filterPattern, string.Empty);

    private void OnFocusPoll()
    {
        string name = GetForegroundProcessName();
        if (name == _lastFgName) return;
        _lastFgName = name;
        bool real = _settings.RealDataApps != null && _settings.RealDataApps.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
        if (real != _realMode || !_armed) { _realMode = real; Diag($"MODE {(real ? "real" : "delayed")} app={name}"); SyncClipboardOwnership(); }
    }

    private static string GetForegroundProcessName()
    {
        try { IntPtr fg = NativeMethods.GetForegroundWindow(); if (fg == IntPtr.Zero) return string.Empty; NativeMethods.GetWindowThreadProcessId(fg, out uint pid); return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
        catch { return string.Empty; }
    }

    private void SetLogging(bool v) { if (_settings.Diagnostics == v) return; _settings.Diagnostics = v; SettingsManager.Save(_settings); if (v) Diag("LOGGING ON"); }
    private void SetEnableFilter(bool v) { if (_settings.EnableFilter == v) return; _settings.EnableFilter = v; SettingsManager.Save(_settings); RebuildFilterRegex(); ScheduleSync(); }
    private void SetStripHyperlinks(bool v) { if (_settings.StripHyperlinks == v) return; _settings.StripHyperlinks = v; SettingsManager.Save(_settings); ScheduleSync(); }
    private void SetStripBulletPoints(bool v) { if (_settings.StripBulletPoints == v) return; _settings.StripBulletPoints = v; SettingsManager.Save(_settings); ScheduleSync(); }
    private void SetGapBlanks(bool v) { if (_settings.ParagraphGapBlankLines == v) return; _settings.ParagraphGapBlankLines = v; SettingsManager.Save(_settings); ScheduleSync(); }

    private void Diag(string m) { if (!_settings.Diagnostics) return; try { string p = Path.Combine(AppContext.BaseDirectory, "diagnostics.log"); var fi = new FileInfo(p); if (fi.Exists && fi.Length > 1_000_000) File.Delete(p); File.AppendAllText(p, $"[{DateTime.Now:HH:mm:ss.fff}] {m}{Environment.NewLine}"); } catch { } }
    private void ScheduleSync() { _syncTimer?.Stop(); _syncTimer?.Start(); }
    private bool ClipboardProtected => DateTime.UtcNow < _protectClipboardUntil;
    private void ProtectClipboard() { _protectClipboardUntil = DateTime.UtcNow.AddMilliseconds(ClipboardProtectMs); }

    private void OnLeftClick(uint seq, bool first) { if (GetCount() == 0 || !_realMode) return; if (first && (DateTime.UtcNow - InputActivity.LastRightButtonUp).TotalSeconds < CustomMenuWindowSeconds) StartConfirm(seq, "CUSTOMSELECT"); }
    private void StartConfirm(uint seq, string tag) { _menuConfirmSeq = seq; _confirmConsumedCount = _consumedCount; _confirmStage = 0; _menuConfirmTimer?.Stop(); _menuConfirmTimer?.Start(); Diag($"{tag} seq={seq}"); }
    private void OnMenuConfirmTick()
    {
        uint now = NativeMethods.GetClipboardSequenceNumber();
        if (now != _menuConfirmSeq) { _menuConfirmTimer?.Stop(); return; }
        if (_consumedCount != _confirmConsumedCount) { _menuConfirmTimer?.Stop(); return; }
        if (_confirmStage == 0) { _confirmStage = 1; _menuConfirmTimer?.Stop(); _menuConfirmTimer?.Start(); return; }
        string? head; lock (_sync) { head = _items.Count > 0 ? ApplyFilter(_items.Peek().Text) : null; }
        string? clip = null; try { if (Clipboard.ContainsText()) clip = Clipboard.GetText(); } catch { }
        if (head == null || clip == null || clip != head) { _menuConfirmTimer?.Stop(); return; }
        _menuConfirmTimer?.Stop(); ConsumeHead();
    }
    private void ConsumeHead() { lock (_sync) { if (_items.Count > 0) { _items.Dequeue(); _consumedCount++; Diag($"CONSUME confirm count={_items.Count}"); } } RefreshUi(); ScheduleSync(); }

    private void HandleRenderFormat(uint format)
    {
        try
        {
            ClipItem? item; lock (_sync) { item = _renderedItem ?? (_items.Count > 0 ? _items.Peek() : null); }
            if (item == null) return;
            _renderedItem = item;
            bool known = format == NativeClipboard.CF_UNICODETEXT || format == NativeClipboard.CfHtml;
            if (format == NativeClipboard.CF_UNICODETEXT) NativeClipboard.ProvideData(format, Encoding.Unicode.GetBytes(ApplyFilter(item.Text) + "\0"));
            else if (format == NativeClipboard.CfHtml) NativeClipboard.ProvideData(format, Encoding.UTF8.GetBytes(BuildHtmlData(item) + "\0"));
            else return;
            bool pasteRead = known && InputActivity.LastGesture > _armedAt && (DateTime.UtcNow - InputActivity.LastGesture).TotalMilliseconds < GestureFreshnessMs && DateTime.UtcNow >= _consumeCooldownUntil;
            if (pasteRead) { _renderConsumeTimer?.Stop(); _renderConsumeTimer?.Start(); }
            else { if ((DateTime.UtcNow - _lastArmTime).TotalMilliseconds > 600) PostToUi(ScheduleSync); else { _armed = false; Diag("POISON"); } }
        }
        catch { }
    }

    private void ConsumeRenderedItem()
    {
        _renderConsumeTimer?.Stop();
        var r = _renderedItem; if (r == null) return;
        _renderedItem = null; _consumeCooldownUntil = DateTime.UtcNow.AddMilliseconds(ConsumeCooldownMs);
        lock (_sync) { if (_items.Count > 0 && ReferenceEquals(_items.Peek(), r)) { _items.Dequeue(); _consumedCount++; Diag($"CONSUME render count={_items.Count}"); } }
        RefreshUi(); ScheduleSync();
    }

    private void SyncClipboardOwnership()
    {
        if (ClipboardProtected) return;
        if (!_settings.InterceptAllPastes || GetCount() == 0) { _armed = false; _lastClipboardSequence = NativeMethods.GetClipboardSequenceNumber(); return; }
        ClipItem? head; lock (_sync) { head = _items.Peek(); } if (head == null) return;
        string headText = ApplyFilter(head.Text);
        bool ok = _realMode ? NativeClipboard.TrySetHtmlAndText(headText, HtmlClipboardHelper.CreateHtmlClipboardData(BuildHtmlData(head))) : NativeClipboard.ArmDelayed(Handle);
        if (ok) { _armed = true; _armedAt = DateTime.UtcNow; _lastArmTime = DateTime.UtcNow; _renderedItem = null; _lastProgrammaticClipboardText = headText; _lastProgrammaticClipboardTime = DateTime.UtcNow; _lastClipboardSequence = NativeMethods.GetClipboardSequenceNumber(); Diag($"ARM {(_realMode ? "real" : "delayed")}"); }
    }

    private string CleanText(string text)
    {
        if (_settings.StripBulletPoints) text = Regex.Replace(text, @"(?m)^[ \t]*(?:[•◦▪‣●○■□◆◇✦✧※]|\*)[ \t]+", "");
        return ApplyFilter(text);
    }

    // Rich-HTML cleanup (used only when the filter is OFF).
    private string PrepareRichHtml(string html)
    {
        string result = html;

        if (_settings.StripHyperlinks)
            for (int p = 0; p < 6; p++) { string b = result; result = Regex.Replace(result, @"<a\b[^>]*>(.*?)</a>", "$1", RegexOptions.IgnoreCase | RegexOptions.Singleline); if (b == result) break; }

        for (int p = 0; p < 6; p++) { string b = result; result = Regex.Replace(result, @"<(em|i)\b[^>]*>(.*?)</\1>", "$2", RegexOptions.IgnoreCase | RegexOptions.Singleline); result = Regex.Replace(result, @"<span\b[^>]*font-style:\s*italic[^>]*>(.*?)</span>", "$1", RegexOptions.IgnoreCase | RegexOptions.Singleline); if (b == result) break; }

        // Heading heuristic: a short standalone block line with no ending
        // punctuation is treated as a heading and made explicitly bold, so the
        // boldness survives after we strip its wrapper tag.
        result = Regex.Replace(
            result,
            @"<(div|p|li|h[1-6])\b[^>]*>([^<]{0,60}?)</\1>",
            m =>
            {
                string inner = m.Groups[2].Value;
                string trimmed = inner.Trim();
                if (trimmed.Length > 0 && !Regex.IsMatch(trimmed, @"[.!?…]$"))
                    return "<b>" + inner + "</b><br>";
                return m.Value;
            },
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        if (_settings.StripBulletPoints)
        {
            result = Regex.Replace(result, @"</?(?:ul|ol)\b[^>]*>", "", RegexOptions.IgnoreCase);
            result = Regex.Replace(result, @"(?<=^|>|<br>)[ \t]*(?:[•◦▪‣●○■□◆◇✦✧※]|\*)[ \t]+", "", RegexOptions.IgnoreCase);
        }

        if (_settings.ParagraphGapBlankLines)
        {
            result = Regex.Replace(result, @"</(div|p|h[1-6]|li)>\s*<(div|p|h[1-6]|li)\b[^>]*>", "<br><br>", RegexOptions.IgnoreCase);
            result = Regex.Replace(result, @"<(div|p|h[1-6]|li)\b[^>]*>", "", RegexOptions.IgnoreCase);
            result = Regex.Replace(result, @"</(div|p|h[1-6]|li)>", "", RegexOptions.IgnoreCase);
        }
        else
        {
            result = Regex.Replace(result, @"<(div|p|h[1-6]|li)\b[^>]*>", "", RegexOptions.IgnoreCase);
            result = Regex.Replace(result, @"</(div|p|h[1-6]|li)>", "<br>", RegexOptions.IgnoreCase);
        }

        result = Regex.Replace(result, @"(?:<br\s*/?>\s*){3,}", "<br><br>", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"^\s*(?:<br\s*/?>\s*)+", "", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"(?:\s*<br\s*/?>)+\s*$", "", RegexOptions.IgnoreCase);

        return result;
    }

    private string BuildHtmlData(ClipItem item)
    {
        if (_settings.EnableFilter)
            return HtmlClipboardHelper.PlainTextToHtml(ApplyFilter(item.Text));

        if (!string.IsNullOrWhiteSpace(item.Html)) return PrepareRichHtml(item.Html);
        if (_settings.RenderMarkdownForPlainText) return Markdown.ToHtml(item.Text, MarkdownPipeline);
        return HtmlClipboardHelper.PlainTextToHtml(item.Text);
    }

    private static long SizeOf(ClipItem item) => item.Text.Length + (item.Html?.Length ?? 0);

    private void OnClipboardUpdate()
    {
        try
        {
            uint cur = NativeMethods.GetClipboardSequenceNumber();
            if (_pauseMonitoring) { _lastClipboardSequence = cur; return; }
            if (cur == _lastClipboardSequence) return;
            if (!Clipboard.ContainsText()) return;
            if (Clipboard.ContainsImage() || Clipboard.ContainsFileDropList()) { _armed = false; _lastClipboardSequence = cur; return; }
            string text = Clipboard.GetText(); string? html = null;
            try { if (Clipboard.ContainsText(TextDataFormat.Html)) { string raw = Clipboard.GetText(TextDataFormat.Html); html = HtmlClipboardHelper.ExtractFragment(raw); if (html != null) { html = HtmlClipboardHelper.NormalizeLineBreaks(html); if (html.Length > MaxHtmlLength) html = null; } } } catch { html = null; }
            _lastClipboardSequence = cur;
            AddClipboardItem(text, html);
        }
        catch { }
    }

    private void AddClipboardItem(string text, string? html)
    {
        if (_pauseMonitoring) return;
        text = CleanText(text);
        if (string.IsNullOrWhiteSpace(text)) return;
        if (text.Length > MaxItemLength) return;
        if (DateTime.UtcNow - _lastProgrammaticClipboardTime < TimeSpan.FromSeconds(2) && text == _lastProgrammaticClipboardText) return;
        if (text == _lastStoredText && html == _lastStoredHtml && (DateTime.UtcNow - _lastStoredTime).TotalSeconds < RepeatCopyWindowSeconds) { _lastStoredTime = DateTime.UtcNow; return; }
        lock (_sync) { _items.Enqueue(new ClipItem(text, html)); Diag($"STORE count={_items.Count}"); }
        _lastStoredText = text; _lastStoredHtml = html; _lastStoredTime = DateTime.UtcNow;
        RefreshUi(); ScheduleSync();
    }

    private void PostToUi(Action a) { try { _uiContext?.Post(_ => a(), null); } catch { } }
    private int GetCount() { lock (_sync) return _items.Count; }
    private void SetPauseMonitoring(bool v) { if (_updatingPause) return; _updatingPause = true; _pauseMonitoring = v; _pauseCheckBox.Checked = v; _pauseMenuItem.Checked = v; _updatingPause = false; }
    private void SetStartWithWindows(bool v) { if (_updatingStartup) return; _updatingStartup = true; StartupManager.SetEnabled(v); bool en = StartupManager.IsEnabled(); _startupCheckBox.Checked = en; _startupMenuItem.Checked = en; _updatingStartup = false; }
    private void ShowQueueWindow() { Show(); ShowInTaskbar = true; WindowState = FormWindowState.Normal; Activate(); _suppressCounter = true; RefreshUi(); }
    private void HideQueueWindow() { Hide(); ShowInTaskbar = false; }

    private void RefreshUi()
    {
        ClipItem[] items;
        lock (_sync) { while (_items.Count > MaxItems) _items.Dequeue(); long tot = 0; foreach (var it in _items) tot += SizeOf(it); while (tot > MaxTotalChars && _items.Count > 0) { var o = _items.Dequeue(); tot -= SizeOf(o); } items = _items.ToArray(); }
        if (Visible) RebuildList(items);
        _countLabel.Text = $"{items.Length} item(s)";
        string tip = $"Clipboard Queue: {items.Length} item(s)"; if (tip.Length > 127) tip = tip[..127];
        _notifyIcon.Text = tip;
        if (_suppressCounter) _suppressCounter = false; else _cursorCounter?.ShowCount(items.Length, items.Length > _lastCount);
        _lastCount = items.Length;
    }

    private void RebuildList(ClipItem[] items) { _listView.BeginUpdate(); _listView.Items.Clear(); foreach (var it in items) _listView.Items.Add(new ListViewItem(MakePreview(it.Text))); _listView.EndUpdate(); }
    private string MakePreview(string t) { string s = ApplyFilter(t).Replace("\r", "").Replace("\n", " ⏎ "); return s.Length <= PreviewLength ? s : s[..PreviewLength] + "…"; }

    private void DeleteSelected()
    {
        if (_listView.SelectedIndices.Count == 0) return;
        var sel = _listView.SelectedIndices.Cast<int>().ToHashSet();
        lock (_sync) { var cur = _items.ToArray(); _items.Clear(); for (int i = 0; i < cur.Length; i++) if (!sel.Contains(i)) _items.Enqueue(cur[i]); }
        RefreshUi(); ScheduleSync();
    }
    private void ClearAll() { lock (_sync) _items.Clear(); RefreshUi(); ScheduleSync(); }

    private async void PasteNext()
    {
        if (_pasteBusy) return; _pasteBusy = true;
        try { ClipItem? it; lock (_sync) it = _items.Count > 0 ? _items.Peek() : null; if (it == null) return; await PasteRichAsync(it.Text, it.Html, false, () => { lock (_sync) { if (_items.Count > 0 && ReferenceEquals(_items.Peek(), it)) { _items.Dequeue(); _consumedCount++; Diag($"CONSUME key count={_items.Count}"); } } }); }
        finally { _pasteBusy = false; }
    }

    private async void PasteAll()
    {
        if (_pasteBusy) return; _pasteBusy = true;
        try
        {
            ClipItem[] items; lock (_sync) { if (_items.Count == 0) return; items = _items.ToArray(); }
            string sep = string.IsNullOrEmpty(_settings.PasteAllSeparator) ? Environment.NewLine + Environment.NewLine : _settings.PasteAllSeparator;
            var comb = await Task.Run(() => { var tb = new StringBuilder(); var hb = new StringBuilder(); for (int i = 0; i < items.Length; i++) { tb.Append(items[i].Text); hb.Append(BuildHtmlData(items[i])); if (i < items.Length - 1) { tb.Append(sep); hb.Append("<br><br>"); } } return (Text: tb.ToString(), Html: hb.ToString()); });
            await PasteRichAsync(comb.Text, comb.Html, true, () => { lock (_sync) { for (int i = 0; i < items.Length; i++) { if (_items.Count > 0 && ReferenceEquals(_items.Peek(), items[i])) { _items.Dequeue(); _consumedCount++; } else break; } Diag($"CONSUME pasteall count={_items.Count}"); } });
        }
        finally { _pasteBusy = false; }
    }

    private async Task PasteRichAsync(string text, string? html, bool waitModifiers, Action? onSuccess)
    {
        try
        {
            text = ApplyFilter(text);

            string htmlToUse;
            if (_settings.EnableFilter)
                htmlToUse = HtmlClipboardHelper.PlainTextToHtml(text);
            else if (!string.IsNullOrWhiteSpace(html))
                htmlToUse = PrepareRichHtml(html);
            else if (_settings.RenderMarkdownForPlainText)
                htmlToUse = await Task.Run(() => Markdown.ToHtml(text, MarkdownPipeline));
            else
                htmlToUse = await Task.Run(() => HtmlClipboardHelper.PlainTextToHtml(text));

            string data = HtmlClipboardHelper.CreateHtmlClipboardData(htmlToUse);
            bool ok = NativeClipboard.TrySetHtmlAndText(text, data);
            if (!ok) { var d = new DataObject(); d.SetData(DataFormats.UnicodeText, text); d.SetData(DataFormats.Html, data); ok = await TrySetClipboardAsync(d); }
            if (!ok) return;
            ProtectClipboard();
            _lastProgrammaticClipboardText = text; _lastProgrammaticClipboardTime = DateTime.UtcNow; _lastClipboardSequence = NativeMethods.GetClipboardSequenceNumber();
            onSuccess?.Invoke(); RefreshUi();
            await Task.Delay(50);
            if (waitModifiers) NativeMethods.WaitForModifierKeysRelease();
            NativeMethods.SendCtrlV();
            ProtectClipboard();
            _renderedItem = null; ScheduleSync();
        }
        catch { }
    }

    private static async Task<bool> TrySetClipboardAsync(IDataObject d, int retries = 5) { for (int i = 0; i < retries; i++) { try { Clipboard.SetDataObject(d, true); return true; } catch { await Task.Delay(80); } } return false; }
    private void ExitApplication() { _exitRequested = true; Cleanup(); Application.Exit(); }

    private void Cleanup()
    {
        if (_cleanedUp) return; _cleanedUp = true;
        try { if (IsHandleCreated) NativeMethods.RemoveClipboardFormatListener(Handle); } catch { }
        try { _clipboardTimer?.Stop(); _clipboardTimer?.Dispose(); _syncTimer?.Stop(); _syncTimer?.Dispose(); _focusTimer?.Stop(); _focusTimer?.Dispose(); _renderConsumeTimer?.Stop(); _renderConsumeTimer?.Dispose(); _menuConfirmTimer?.Stop(); _menuConfirmTimer?.Dispose(); } catch { }
        _keyboardHook?.Dispose(); _mouseHook?.Dispose(); _cursorCounter?.Dispose();
        try { _notifyIcon.Visible = false; _notifyIcon.Dispose(); } catch { }
    }
}
