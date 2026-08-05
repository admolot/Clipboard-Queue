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

    /// <summary>
    /// The rich HTML that the source application (e.g. a browser)
    /// put on the clipboard, if any.
    /// </summary>
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
    private SynchronizationContext? _uiContext;

    private System.Windows.Forms.Timer? _clipboardTimer;
    private uint _lastClipboardSequence;

    private bool _exitRequested;
    private bool _cleanedUp;
    private bool _pauseMonitoring;
    private bool _updatingPause;
    private bool _updatingStartup;

    private string _lastProgrammaticClipboardText = string.Empty;
    private DateTime _lastProgrammaticClipboardTime = DateTime.MinValue;

    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public MainForm(bool startHidden)
    {
        _settings = SettingsManager.Load();
        _startHidden = startHidden;

        Text = "Clipboard Queue 1.3";
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

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
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

        try
        {
            _keyboardHook = new KeyboardHook
            {
                ShouldHandleCtrlV = () => _settings.OverrideCtrlV && GetCount() > 0,
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
    }

    private void HideQueueWindow()
    {
        Hide();
        ShowInTaskbar = false;
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

            string text = Clipboard.GetText();

            // Also capture the rich HTML that the source app (e.g. browser)
            // placed on the clipboard, so we can paste it back exactly.
            string? html = null;

            try
            {
                if (Clipboard.ContainsText(TextDataFormat.Html))
                {
                    string rawHtml = Clipboard.GetText(TextDataFormat.Html);
                    html = HtmlClipboardHelper.ExtractFragment(rawHtml);
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
            // Clipboard may be locked by another application.
            // The timer will try again shortly.
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

        // Avoid immediately re-adding content that this app just placed on the clipboard.
        if (DateTime.UtcNow - _lastProgrammaticClipboardTime < TimeSpan.FromSeconds(2) &&
            text == _lastProgrammaticClipboardText)
        {
            return;
        }

        lock (_sync)
        {
            _items.Enqueue(new ClipItem(text, html));

            while (_items.Count > MaxItems)
            {
                _items.Dequeue();
            }
        }

        RefreshUi();
    }

    private void RefreshUi()
    {
        ClipItem[] items;

        lock (_sync)
        {
            items = _items.ToArray();
        }

        _listView.BeginUpdate();
        _listView.Items.Clear();

        foreach (ClipItem item in items)
        {
            _listView.Items.Add(new ListViewItem(MakePreview(item.Text)));
        }

        _listView.EndUpdate();

        _countLabel.Text = $"{items.Length} item(s)";

        string tooltip = $"Clipboard Queue: {items.Length} item(s)";

        if (tooltip.Length > 127)
            tooltip = tooltip[..127];

        _notifyIcon.Text = tooltip;
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
    }

    private void ClearAll()
    {
        lock (_sync)
        {
            _items.Clear();
        }

        RefreshUi();
    }

    private async void PasteNext()
    {
        ClipItem? item = null;

        lock (_sync)
        {
            if (_items.Count > 0)
            {
                item = _items.Dequeue();
            }
        }

        if (item == null)
            return;

        RefreshUi();
        await PasteRichAsync(item.Text, item.Html);
    }

    private async void PasteAll()
    {
        ClipItem[] items;

        lock (_sync)
        {
            if (_items.Count == 0)
                return;

            items = _items.ToArray();
            _items.Clear();
        }

        RefreshUi();

        string separator = string.IsNullOrEmpty(_settings.PasteAllSeparator)
            ? Environment.NewLine + Environment.NewLine
            : _settings.PasteAllSeparator;

        // Build combined text + combined HTML in the background.
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
                        ? Markdown.ToHtml(item.Text, MarkdownPipeline)
                        : item.Html);

                if (i < items.Length - 1)
                {
                    textBuilder.Append(separator);

                    // A blank line between items in the rich version.
                    htmlBuilder.Append("<p><br></p>");
                }
            }

            return (Text: textBuilder.ToString(), Html: htmlBuilder.ToString());
        });

        await PasteRichAsync(combined.Text, combined.Html);
    }

    private async Task PasteRichAsync(string text, string? html)
    {
        try
        {
            string htmlToUse = html;

            // No stored HTML (e.g. copied from Notepad): render Markdown.
            if (string.IsNullOrWhiteSpace(htmlToUse))
            {
                htmlToUse = await Task.Run(() => Markdown.ToHtml(text, MarkdownPipeline));
            }

            string htmlClipboardData = HtmlClipboardHelper.CreateHtmlClipboardData(htmlToUse);

            // Preferred: native clipboard write with guaranteed UTF-8 CF_HTML.
            bool clipboardSet = NativeClipboard.TrySetHtmlAndText(text, htmlClipboardData);

            if (!clipboardSet)
            {
                // Fallback: managed WinForms clipboard.
                var data = new DataObject();
                data.SetData(DataFormats.UnicodeText, text);
                data.SetData(DataFormats.Html, htmlClipboardData);
                clipboardSet = await TrySetClipboardAsync(data);
            }

            if (!clipboardSet)
                return;

            _lastProgrammaticClipboardText = text;
            _lastProgrammaticClipboardTime = DateTime.UtcNow;
            _lastClipboardSequence = NativeMethods.GetClipboardSequenceNumber();

            // Small delay helps some apps accept the clipboard change.
            await Task.Delay(100);

            // Wait until the user has physically released Ctrl/Alt/Shift,
            // otherwise the simulated Ctrl+V could become Ctrl+Alt+V.
            NativeMethods.WaitForModifierKeysRelease();

            NativeMethods.SendCtrlV();
        }
        catch
        {
            // Ignore paste errors.
        }
    }

    private static async Task<bool> TrySetClipboardAsync(IDataObject data, int retries = 10)
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
                await Task.Delay(100);
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
        }
        catch
        {
        }

        _keyboardHook?.Dispose();

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
