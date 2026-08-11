using System;
using System.Collections.Generic;
using System.Drawing;
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
    private const int PreviewLength = 300;

    private readonly Queue<ClipItem> _items = new();
    private readonly object _sync = new();

    private readonly AppSettings _settings;
    private readonly bool _startHidden;

    private readonly ListView _listView;
    private readonly Label _countLabel;
    private readonly CheckBox _pauseCheckBox;
    private readonly CheckBox _startupCheckBox;
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _pauseMenuItem;
    private readonly ToolStripMenuItem _startupMenuItem;

    private KeyboardHook? _keyboardHook;
    private MouseHook? _mouseHook;
    private SynchronizationContext? _uiContext;

    private System.Windows.Forms.Timer? _clipboardTimer;
    private System.Windows.Forms.Timer? _renderConsumeTimer;
    private uint _lastClipboardSequence;

    private bool _armed;
    private bool _readerDetected;
    private ClipItem? _renderedItem;
    private long _inputCountAtArm;

    private bool _exitRequested;
    private bool _cleanedUp;
    private bool _pauseMonitoring;
    private bool _updatingPause;
    private bool _updatingStartup;

    private string _lastProgrammaticClipboardText = string.Empty;
    private DateTime _lastProgrammaticClipboardTime = DateTime.MinValue;

    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseSoftlineBreakAsHardlineBreak()
        .Build();

    public MainForm(bool startHidden)
    {
        _settings = SettingsManager.Load();
        _startHidden = startHidden;

        Text = "Clipboard Queue 1.7";
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

        NativeMethods.AddClipboardFormatListener(Handle);

        _lastClipboardSequence = NativeMethods.GetClipboardSequenceNumber();

        _clipboardTimer = new System.Windows.Forms.Timer
        {
            Interval = 400
        };

        _clipboardTimer.Tick += (_, _) => OnClipboardUpdate();
        _clipboardTimer.Start();

        _renderConsumeTimer = new System.Windows.Forms.Timer
        {
            Interval = 250
        };

        _renderConsumeTimer.Tick += (_, _) => ConsumeRenderedItem();

        try
        {
            _keyboardHook = new KeyboardHook
            {
                ShouldHandleCtrlV = () => !_armed && _settings.OverrideCtrlV && GetCount() > 0,
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
            _mouseHook = new MouseHook();
        }
        catch
        {
        }

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

            bool userInitiated = InputActivity.Count > _inputCountAtArm;

            if (userInitiated)
            {
                _renderConsumeTimer?.Stop();
                _renderConsumeTimer?.
