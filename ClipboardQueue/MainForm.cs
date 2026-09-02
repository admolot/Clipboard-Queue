using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Markdig;

namespace ClipboardQueue;

internal sealed class ClipItem
{
    public ClipItem(string text, string? html)
    {
        Text = text;
        Html = html;
    }

    public string Text { get; }

    public string? Html { get; }
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
    private bool _poisoned;
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

    private string _lastProgrammaticClipboardText = string.Empty;
    private DateTime _lastProgrammaticClipboardTime = DateTime.MinValue;

    private string _lastStoredText = string.Empty;
    private string? _lastStoredHtml;
    private DateTime _lastStoredTime = DateTime.MinValue;

    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseSoftlineBreakAsHardlineBreak()
        .Build();

    public MainForm(bool startHidden)
    {
        _settings = SettingsManager.Load();
        _startHidden = startHidden;

        Text = "Clipboard Queue 1.30";
        Width = 800;
        Height = 500;
        MinimumSize = new Size(500, 300);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2
        };

        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _listView = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            MultiSelect = true
        };

        _listView.Columns.Add("Stored clipboard items (oldest first)", 750);

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true
        };

        var pasteNextButton = new Button
        {
            Text = "Paste next (Ctrl+V)",
            AutoSize = true
        };
        pasteNextButton.Click += (_, _) => PasteNext();

        var pasteAllButton = new Button
        {
            Text = "Paste all (Ctrl+Alt+V or Ctrl+V + left mouse)",
            AutoSize = true
        };
        pasteAllButton.Click += (_, _) => PasteAll();

        var deleteSelectedButton = new Button
        {
            Text = "Delete selected",
            AutoSize = true
        };
        deleteSelectedButton.Click += (_, _) => DeleteSelected();

        var clearAllButton = new Button
        {
            Text = "Clear all",
            AutoSize = true
        };
        clearAllButton.Click += (_, _) => ClearAll();

        var minimizeToTrayButton = new Button
        {
            Text = "Minimize to tray",
            AutoSize = true
        };
        minimizeToTrayButton.Click += (_, _) => HideQueueWindow();

        _pauseCheckBox = new CheckBox
        {
            Text = "Pause monitoring",
            AutoSize = true,
            Checked = false
        };
        _pauseCheckBox.CheckedChanged += (_, _) => SetPauseMonitoring(_pauseCheckBox.Checked);

        _startupCheckBox = new CheckBox
        {
            Text = "Start with Windows",
            AutoSize = true,
            Checked = StartupManager.IsEnabled()
        };
        _startupCheckBox.CheckedChanged += (_, _) => SetStartWithWindows(_startupCheckBox.Checked);

        _loggingCheckBox = new CheckBox
        {
            Text = "Logging",
            AutoSize = true,
            Checked = _settings.Diagnostics
        };
        _loggingCheckBox.CheckedChanged += (_, _) => SetLogging(_loggingCheckBox.Checked);

        _countLabel = new Label
        {
            AutoSize = true,
            Text = "0 items",
            TextAlign = ContentAlignment.MiddleLeft
        };

        buttonPanel.Controls.Add(pasteNextButton);
        buttonPanel.Controls.Add(pasteAllButton);
        buttonPanel.Controls.Add(deleteSelectedButton);
        buttonPanel.Controls.Add(clearAllButton);
        buttonPanel.Controls.Add(minimizeToTrayButton);
        buttonPanel.Controls.Add(_pauseCheckBox);
        buttonPanel.Controls.Add(_startupCheckBox);
        buttonPanel.Controls.Add(_loggingCheckBox);
        buttonPanel.Controls.Add(_countLabel);

        root.Controls.Add(_listView, 0, 0);
        root.Controls.Add(buttonPanel, 0, 1);

        Controls.Add(root);

        var trayMenu = new ContextMenuStrip();

        var openItem = new ToolStripMenuItem("Open");
        openItem.Click += (_, _) => ShowQueueWindow();

        var trayPasteNext = new ToolStripMenuItem("Paste next");
        trayPasteNext.Click += (_, _) => PasteNext();

        var trayPasteAll = new ToolStripMenuItem("Paste all");
        trayPasteAll.Click += (_, _) => PasteAll();

        _pauseMenuItem = new ToolStripMenuItem("Pause monitoring")
        {
            CheckOnClick = true,
            Checked = false
        };
        _pauseMenuItem.Click += (_, _) => SetPauseMonitoring(_pauseMenuItem.Checked);

        _startupMenuItem = new ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
            Checked = StartupManager.IsEnabled()
        };
        _startupMenuItem.Click += (_, _) => SetStartWithWindows(_startupMenuItem.Checked);

        var clearMenuItem = new ToolStripMenuItem("Clear all");
        clearMenuItem.Click += (_, _) => ClearAll();

        var minimizeToTrayItem = new ToolStripMenuItem("Minimize to tray");
        minimizeToTrayItem.Click += (_, _) => HideQueueWindow();

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitApplication();

        trayMenu.Items.Add(openItem);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(trayPasteNext);
        trayMenu.Items.Add(trayPasteAll);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(_pauseMenuItem);
        trayMenu.Items.Add(_startupMenuItem);
        trayMenu.Items.Add(clearMenuItem);
        trayMenu.Items.Add(minimizeToTrayItem);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(exitItem);

        Icon trayIcon = SystemIcons.Application;

        try
        {
            string? exePath = Environment.ProcessPath;

            if (!string.IsNullOrEmpty(exePath))
            {
                trayIcon = Icon.ExtractAssociatedIcon(exePath) ?? SystemIcons.Application;
            }
        }
        catch
        {
            trayIcon = SystemIcons.Application;
        }

        _notifyIcon = new NotifyIcon
        {
            Icon = trayIcon,
            Text = "Clipboard Queue: 0 items",
            Visible = true,
            ContextMenuStrip = trayMenu
        };

        _notifyIcon.DoubleClick += (_, _) => ShowQueueWindow();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();

        _cursorCounter = new CursorCounter();

        NativeMethods.AddClipboardFormatListener(Handle);

        _lastClipboardSequence = NativeMethods.GetClipboardSequenceNumber();

        _clipboardTimer = new System.Windows.Forms.Timer
        {
            Interval = 400
        };

        _clipboardTimer.Tick += (_, _) => OnClipboardUpdate();
        _clipboardTimer.Start();

        _syncTimer = new System.Windows.Forms.Timer
        {
            Interval = SyncDelayMs
        };

        _syncTimer.Tick += (_, _) =>
        {
            _syncTimer.Stop();
            SyncClipboardOwnership();
        };

        _focusTimer = new System.Windows.Forms.Timer
        {
            Interval = FocusPollMs
        };

        _focusTimer.Tick += (_, _) => OnFocusPoll();
        _focusTimer.Start();

        _renderConsumeTimer = new System.Windows.Forms.Timer
        {
            Interval = 250
        };

        _renderConsumeTimer.Tick += (_, _) => ConsumeRenderedItem();

        _menuConfirmTimer = new System.Windows.Forms.Timer
        {
            Interval = 400
        };

        _menuConfirmTimer.Tick += (_, _) => OnMenuConfirmTick();

        try
        {
            _keyboardHook = new KeyboardHook
            {
                ShouldHandleCtrlV = () =>
                    _settings.OverrideCtrlV && GetCount() > 0 &&
                    (_realMode || !_armed || _poisoned),
                ShouldHandleCtrlAltV = () => GetCount() > 0,
                CtrlVPressed = () => PostToUi(PasteNext),
                CtrlAltVPressed = () => PostToUi(PasteAll)
            };
        }
        catch
        {
            _notifyIcon.ShowBalloonTip(
                3000,
                "Clipboard Queue",
                "Could not install the global keyboard hook. Tray buttons still work.",
                ToolTipIcon.Warning);
        }

        try
        {
            _mouseHook = new MouseHook
            {
                LeftClickAfterRightClick = (seq, firstAfterRight) =>
                    PostToUi(() => OnLeftClick(seq, firstAfterRight))
            };
        }
        catch
        {
        }

        OnFocusPoll();

        if (_startHidden)
            HideQueueWindow();
        else
            ShowQueueWindow();

        RefreshUi();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_CLIPBOARDUPDATE)
        {
            OnClipboardUpdate();
            base.WndProc(ref m);
            return;
        }

        if (m.Msg == NativeClipboard.WM_DESTROYCLIPBOARD)
        {
            _armed = false;
            base.WndProc(ref m);
            return;
        }

        if (m.Msg == NativeClipboard.WM_RENDERFORMAT)
        {
            HandleRenderFormat((uint)m.WParam.ToInt64());
            return;
        }

        if (m.Msg == NativeClipboard.WM_RENDERALLFORMATS)
        {
            HandleRenderFormat(NativeClipboard.CF_UNICODETEXT);
            HandleRenderFormat(NativeClipboard.CfHtml);
            return;
        }

        base.WndProc(ref m);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);

        if (WindowState == FormWindowState.Minimized)
        {
            HideQueueWindow();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exitRequested && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideQueueWindow();
            base.OnFormClosing(e);
            return;
        }

        Cleanup();
        base.OnFormClosing(e);
    }

    private void OnFocusPoll()
    {
        string name = GetForegroundProcessName();

        if (name == _lastFgName)
            return;

        _lastFgName = name;

        bool real = _settings.RealDataApps != null &&
                    _settings.RealDataApps.Any(n =>
                        string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

        if (real != _realMode || !_armed)
        {
            _realMode = real;
            Diag($"MODE {(real ? "real" : "delayed")} app={name}");
            SyncClipboardOwnership();
        }
    }

    private static string GetForegroundProcessName()
    {
        try
        {
            IntPtr fg = NativeMethods.GetForegroundWindow();

            if (fg == IntPtr.Zero)
                return string.Empty;

            NativeMethods.GetWindowThreadProcessId(fg, out uint pid);

            return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    private void SetLogging(bool value)
    {
        if (_settings.Diagnostics == value)
            return;

        _settings.Diagnostics = value;
        SettingsManager.Save(_settings);

        if (value)
            Diag("LOGGING ON");
    }

    private void Diag(string message)
    {
        if (!_settings.Diagnostics)
            return;

        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "diagnostics.log");

            var info = new FileInfo(path);

            if (info.Exists && info.Length > 1_000_000)
                File.Delete(path);

            File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private void ScheduleSync()
    {
        _syncTimer?.Stop();
        _syncTimer?.Start();
    }

    private bool ClipboardProtected => DateTime.UtcNow < _protectClipboardUntil;

    private void ProtectClipboard()
    {
        _protectClipboardUntil = DateTime.UtcNow.AddMilliseconds(ClipboardProtectMs);
    }

    // ------------------------------------------------------------------
    // Mouse-paste consumption for real-mode apps (Anki).
    // ------------------------------------------------------------------

    private void OnLeftClick(uint clickSeq, bool firstAfterRight)
    {
        if (GetCount() == 0 || !_realMode)
            return;

        if (firstAfterRight &&
            (DateTime.UtcNow - InputActivity.LastRightButtonUp).TotalSeconds < CustomMenuWindowSeconds)
        {
            StartConfirm(clickSeq, "CUSTOMSELECT");
        }
    }

    private void StartConfirm(uint clickSeq, string tag)
    {
        _menuConfirmSeq = clickSeq;
        _confirmConsumedCount = _consumedCount;
        _confirmStage = 0;
        _menuConfirmTimer?.Stop();
        _menuConfirmTimer?.Start();

        Diag($"{tag} seq={clickSeq}");
    }

    private void OnMenuConfirmTick()
    {
        uint seqNow = NativeMethods.GetClipboardSequenceNumber();

        if (seqNow != _menuConfirmSeq)
        {
            Diag("CONFIRM cancel: clipboard changed");
            _menuConfirmTimer?.Stop();
            return;
        }

        if (_consumedCount != _confirmConsumedCount)
        {
            Diag("CONFIRM cancel: already consumed");
            _menuConfirmTimer?.Stop();
            return;
        }

        if (_confirmStage == 0)
        {
            _confirmStage = 1;
            _menuConfirmTimer?.Stop();
            _menuConfirmTimer?.Start();
            Diag("CONFIRM stage1");
            return;
        }

        string? headText;

        lock (_sync)
        {
            headText = _items.Count > 0 ? _items.Peek().Text : null;
        }

        string? clipText = null;

        try
        {
            if (Clipboard.ContainsText())
                clipText = Clipboard.GetText();
        }
        catch
        {
        }

        if (headText == null || clipText == null || clipText != headText)
        {
            Diag("CONFIRM cancel: content mismatch");
            _menuConfirmTimer?.Stop();
            return;
        }

        Diag("CONFIRM consume");
        _menuConfirmTimer?.Stop();
        ConsumeHead();
    }

    private void ConsumeHead()
    {
        lock (_sync)
        {
            if (_items.Count > 0)
            {
                _items.Dequeue();
                _consumedCount++;
                Diag($"CONSUME confirm count={_items.Count}");
            }
        }

        RefreshUi();
        ScheduleSync();
    }

    // ------------------------------------------------------------------
    // Delayed-render consumption (Notepad etc.).
    // ------------------------------------------------------------------

    private void HandleRenderFormat(uint format)
    {
        try
        {
            ClipItem? item;

            lock (_sync)
            {
                item = _renderedItem ?? (_items.Count > 0 ? _items.Peek() : null);
            }

            if (item == null)
                return;

            _renderedItem = item;

            bool knownFormat =
                format == NativeClipboard.CF_UNICODETEXT ||
                format == NativeClipboard.CfHtml;

            if (format == NativeClipboard.CF_UNICODETEXT)
            {
                NativeClipboard.ProvideData(
                    format,
                    Encoding.Unicode.GetBytes(item.Text + "\0"));
            }
            else if (format == NativeClipboard.CfHtml)
            {
                NativeClipboard.ProvideData(
                    format,
                    Encoding.UTF8.GetBytes(BuildHtmlData(item) + "\0"));
            }
            else
            {
                return;
            }

            bool pasteRead =
                knownFormat &&
                InputActivity.LastGesture > _armedAt &&
                (DateTime.UtcNow - InputActivity.LastGesture).TotalMilliseconds < GestureFreshnessMs &&
                DateTime.UtcNow >= _consumeCooldownUntil;

            Diag($"RENDER fmt={format} pasteRead={pasteRead} " +
                 $"gestureAgeMs={(int)(DateTime.UtcNow - InputActivity.LastGesture).TotalMilliseconds}");

            if (pasteRead)
            {
                _renderConsumeTimer?.Stop();
                _renderConsumeTimer?.Start();
            }
            else
            {
                if ((DateTime.UtcNow - _lastArmTime).TotalMilliseconds > 600)
                {
                    PostToUi(ScheduleSync);
                }
                else
                {
                    _poisoned = true;
                    _armed = false;
                    Diag("POISON");
                }
            }
        }
        catch
        {
        }
    }

    private void ConsumeRenderedItem()
    {
        _renderConsumeTimer?.Stop();

        ClipItem? rendered = _renderedItem;

        if (rendered == null)
            return;

        _renderedItem = null;
        _consumeCooldownUntil = DateTime.UtcNow.AddMilliseconds(ConsumeCooldownMs);

        lock (_sync)
        {
            if (_items.Count > 0 && ReferenceEquals(_items.Peek(), rendered))
            {
                _items.Dequeue();
                _consumedCount++;
                Diag($"CONSUME render count={_items.Count}");
            }
        }

        RefreshUi();
        ScheduleSync();
    }

    // ------------------------------------------------------------------
    // Clipboard ownership.
    // ------------------------------------------------------------------

    private void SyncClipboardOwnership()
    {
        // Never touch the clipboard right after a programmatic paste.
        if (ClipboardProtected)
            return;

        if (!_settings.InterceptAllPastes || GetCount() == 0)
        {
            _armed = false;
            _lastClipboardSequence = NativeMethods.GetClipboardSequenceNumber();
            return;
        }

        ClipItem? head;

        lock (_sync)
        {
            head = _items.Peek();
        }

        if (head == null)
            return;

        bool ok;

        if (_realMode)
        {
            string htmlData = HtmlClipboardHelper.CreateHtmlClipboardData(BuildHtmlData(head));
            ok = NativeClipboard.TrySetHtmlAndText(head.Text, htmlData);
        }
        else
        {
            ok = NativeClipboard.ArmDelayed(Handle);
        }

        if (ok)
        {
            _armed = true;
            _poisoned = false;
            _armedAt = DateTime.UtcNow;
            _lastArmTime = DateTime.UtcNow;
            _renderedItem = null;

            _lastProgrammaticClipboardText = head.Text;
            _lastProgrammaticClipboardTime = DateTime.UtcNow;
            _lastClipboardSequence = NativeMethods.GetClipboardSequenceNumber();

            Diag($"ARM {(_realMode ? "real" : "delayed")}");
        }
    }

    private string BuildHtmlData(ClipItem item)
    {
        string html;

        if (!string.IsNullOrWhiteSpace(item.Html))
        {
            html = item.Html;
        }
        else if (_settings.RenderMarkdownForPlainText)
        {
            html = Markdown.ToHtml(item.Text, MarkdownPipeline);
        }
        else
        {
            html = HtmlClipboardHelper.PlainTextToHtml(item.Text);
        }

        return html;
    }

    private static long SizeOf(ClipItem item)
    {
        return item.Text.Length + (item.Html?.Length ?? 0);
    }

    private void OnClipboardUpdate()
    {
        try
        {
            uint current = NativeMethods.GetClipboardSequenceNumber();

            if (_pauseMonitoring)
            {
                _lastClipboardSequence = current;
                return;
            }

            if (current == _lastClipboardSequence)
                return;

            if (!Clipboard.ContainsText())
                return;

            if (Clipboard.ContainsImage() || Clipboard.ContainsFileDropList())
            {
                _armed = false;
                _lastClipboardSequence = current;
                return;
            }

            string text = Clipboard.GetText();

            string? html = null;

            try
            {
                if (Clipboard.ContainsText(TextDataFormat.Html))
                {
                    string rawHtml = Clipboard.GetText(TextDataFormat.Html);
                    html = HtmlClipboardHelper.ExtractFragment(rawHtml);

                    if (html != null)
                    {
                        html = HtmlClipboardHelper.NormalizeLineBreaks(html);

                        if (html.Length > MaxHtmlLength)
                            html = null;
                    }
                }
            }
            catch
            {
                html = null;
            }

            _lastClipboardSequence = current;

            AddClipboardItem(text, html);
        }
        catch
        {
        }
    }

    private void AddClipboardItem(string text, string? html)
    {
        if (_pauseMonitoring)
            return;

        if (string.IsNullOrWhiteSpace(text))
            return;

        if (text.Length > MaxItemLength)
            return;

        if (DateTime.UtcNow - _lastProgrammaticClipboardTime < TimeSpan.FromSeconds(2) &&
            text == _lastProgrammaticClipboardText)
        {
            return;
        }

        if (text == _lastStoredText &&
            html == _lastStoredHtml &&
            (DateTime.UtcNow - _lastStoredTime).TotalSeconds < RepeatCopyWindowSeconds)
        {
            _lastStoredTime = DateTime.UtcNow;
            return;
        }

        lock (_sync)
        {
            _items.Enqueue(new ClipItem(text, html));
            Diag($"STORE count={_items.Count}");
        }

        _lastStoredText = text;
        _lastStoredHtml = html;
        _lastStoredTime = DateTime.UtcNow;

        RefreshUi();
        ScheduleSync();
    }

    private void PostToUi(Action action)
    {
        try
        {
            _uiContext?.Post(_ => action(), null);
        }
        catch
        {
        }
    }

    private int GetCount()
    {
        lock (_sync)
        {
            return _items.Count;
        }
    }

    private void SetPauseMonitoring(bool value)
    {
        if (_updatingPause)
            return;

        _updatingPause = true;

        _pauseMonitoring = value;
        _pauseCheckBox.Checked = value;
        _pauseMenuItem.Checked = value;

        _updatingPause = false;
    }

    private void SetStartWithWindows(bool value)
    {
        if (_updatingStartup)
            return;

        _updatingStartup = true;

        StartupManager.SetEnabled(value);

        bool enabled = StartupManager.IsEnabled();

        _startupCheckBox.Checked = enabled;
        _startupMenuItem.Checked = enabled;

        _updatingStartup = false;
    }

    private void ShowQueueWindow()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = FormWindowState.Normal;
        Activate();

        _suppressCounter = true;
        RefreshUi();
    }

    private void HideQueueWindow()
    {
        Hide();
        ShowInTaskbar = false;
    }

    private void RefreshUi()
    {
        ClipItem[] items;

        lock (_sync)
        {
            while (_items.Count > MaxItems)
            {
                _items.Dequeue();
            }

            long total = 0;

            foreach (ClipItem it in _items)
            {
                total += SizeOf(it);
            }

            while (total > MaxTotalChars && _items.Count > 0)
            {
                ClipItem old = _items.Dequeue();
                total -= SizeOf(old);
            }

            items = _items.ToArray();
        }

        if (Visible)
            RebuildList(items);

        _countLabel.Text = $"{items.Length} item(s)";

        string tooltip = $"Clipboard Queue: {items.Length} item(s)";

        if (tooltip.Length > 127)
            tooltip = tooltip[..127];

        _notifyIcon.Text = tooltip;

        if (_suppressCounter)
        {
            _suppressCounter = false;
        }
        else
        {
            _cursorCounter?.ShowCount(items.Length, items.Length > _lastCount);
        }

        _lastCount = items.Length;
    }

    private void RebuildList(ClipItem[] items)
    {
        _listView.BeginUpdate();
        _listView.Items.Clear();

        foreach (ClipItem item in items)
        {
            _listView.Items.Add(new ListViewItem(MakePreview(item.Text)));
        }

        _listView.EndUpdate();
    }

    private static string MakePreview(string text)
    {
        string oneLine = text
            .Replace("\r", string.Empty)
            .Replace("\n", " ⏎ ");

        if (oneLine.Length <= PreviewLength)
            return oneLine;

        return oneLine[..PreviewLength] + "…";
    }

    private void DeleteSelected()
    {
        if (_listView.SelectedIndices.Count == 0)
            return;

        HashSet<int> selected = _listView.SelectedIndices.Cast<int>().ToHashSet();

        lock (_sync)
        {
            ClipItem[] current = _items.ToArray();
            _items.Clear();

            for (int i = 0; i < current.Length; i++)
            {
                if (!selected.Contains(i))
                {
                    _items.Enqueue(current[i]);
                }
            }
        }

        RefreshUi();
        ScheduleSync();
    }

    private void ClearAll()
    {
        lock (_sync)
        {
            _items.Clear();
        }

        RefreshUi();
        ScheduleSync();
    }

    private async void PasteNext()
    {
        if (_pasteBusy)
            return;

        _pasteBusy = true;

        try
        {
            ClipItem? item;

            lock (_sync)
            {
                item = _items.Count > 0 ? _items.Peek() : null;
            }

            if (item == null)
                return;

            await PasteRichAsync(item.Text, item.Html, false, () =>
            {
                lock (_sync)
                {
                    if (_items.Count > 0 && ReferenceEquals(_items.Peek(), item))
                    {
                        _items.Dequeue();
                        _consumedCount++;
                        Diag($"CONSUME key count={_items.Count}");
                    }
                }
            });
        }
        finally
        {
            _pasteBusy = false;
        }
    }

    private async void PasteAll()
    {
        if (_pasteBusy)
            return;

        _pasteBusy = true;

        try
        {
            ClipItem[] items;

            lock (_sync)
            {
                if (_items.Count == 0)
                    return;

                items = _items.ToArray();
            }

            string separator = string.IsNullOrEmpty(_settings.PasteAllSeparator)
                ? Environment.NewLine + Environment.NewLine
                : _settings.PasteAllSeparator;

            bool renderMarkdown = _settings.RenderMarkdownForPlainText;

            var combined = await Task.Run(() =>
            {
                var textBuilder = new StringBuilder();
                var htmlBuilder = new StringBuilder();

                for (int i = 0; i < items.Length; i++)
                {
                    ClipItem item = items[i];

                    textBuilder.Append(item.Text);

                    htmlBuilder.Append(
                        string.IsNullOrWhiteSpace(item.Html)
                            ? (renderMarkdown
                                ? Markdown.ToHtml(item.Text, MarkdownPipeline)
                                : HtmlClipboardHelper.PlainTextToHtml(item.Text))
                            : item.Html);

                    if (i < items.Length - 1)
                    {
                        textBuilder.Append(separator);
                        htmlBuilder.Append("<p><br></p>");
                    }
                }

                return (Text: textBuilder.ToString(), Html: htmlBuilder.ToString());
            });

            await PasteRichAsync(combined.Text, combined.Html, true, () =>
            {
                lock (_sync)
                {
                    for (int i = 0; i < items.Length; i++)
                    {
                        if (_items.Count > 0 && ReferenceEquals(_items.Peek(), items[i]))
                        {
                            _items.Dequeue();
                            _consumedCount++;
                        }
                        else
                        {
                            break;
                        }
                    }

                    Diag($"CONSUME pasteall count={_items.Count}");
                }
            });
        }
        finally
        {
            _pasteBusy = false;
        }
    }

    private async Task PasteRichAsync(string text, string? html, bool waitModifiers, Action? onSuccess)
    {
        try
        {
            string htmlToUse = html ?? string.Empty;

            if (string.IsNullOrWhiteSpace(htmlToUse))
            {
                bool renderMarkdown = _settings.RenderMarkdownForPlainText;
                string textCopy = text;

                htmlToUse = await Task.Run(() =>
                    renderMarkdown
                        ? Markdown.ToHtml(textCopy, MarkdownPipeline)
                        : HtmlClipboardHelper.PlainTextToHtml(textCopy));
            }

            string htmlClipboardData = HtmlClipboardHelper.CreateHtmlClipboardData(htmlToUse);

            bool clipboardSet = NativeClipboard.TrySetHtmlAndText(text, htmlClipboardData);

            if (!clipboardSet)
            {
                var data = new DataObject();
                data.SetData(DataFormats.UnicodeText, text);
                data.SetData(DataFormats.Html, htmlClipboardData);
                clipboardSet = await TrySetClipboardAsync(data);
            }

            if (!clipboardSet)
                return;

            // Keep everything (including our own maintenance) off the
            // clipboard until the target app had time to read it.
            ProtectClipboard();

            _lastProgrammaticClipboardText = text;
            _lastProgrammaticClipboardTime = DateTime.UtcNow;
            _lastClipboardSequence = NativeMethods.GetClipboardSequenceNumber();

            onSuccess?.Invoke();
            RefreshUi();

            await Task.Delay(50);

            if (waitModifiers)
                NativeMethods.WaitForModifierKeysRelease();

            NativeMethods.SendCtrlV();

            ProtectClipboard();

            _renderedItem = null;
            ScheduleSync();
        }
        catch
        {
        }
    }

    private static async Task<bool> TrySetClipboardAsync(IDataObject data, int retries = 5)
    {
        for (int i = 0; i < retries; i++)
        {
            try
            {
                Clipboard.SetDataObject(data, true);
                return true;
            }
            catch
            {
                await Task.Delay(80);
            }
        }

        return false;
    }

    private void ExitApplication()
    {
        _exitRequested = true;
        Cleanup();
        Application.Exit();
    }

    private void Cleanup()
    {
        if (_cleanedUp)
            return;

        _cleanedUp = true;

        try
        {
            if (IsHandleCreated)
                NativeMethods.RemoveClipboardFormatListener(Handle);
        }
        catch
        {
        }

        try
        {
            _clipboardTimer?.Stop();
            _clipboardTimer?.Dispose();

            _syncTimer?.Stop();
            _syncTimer?.Dispose();

            _focusTimer?.Stop();
            _focusTimer?.Dispose();

            _renderConsumeTimer?.Stop();
            _renderConsumeTimer?.Dispose();

            _menuConfirmTimer?.Stop();
            _menuConfirmTimer?.Dispose();
        }
        catch
        {
        }

        _keyboardHook?.Dispose();
        _mouseHook?.Dispose();
        _cursorCounter?.Dispose();

        try
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
        catch
        {
        }
    }
}
