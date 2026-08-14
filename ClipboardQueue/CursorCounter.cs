using System;
using System.Drawing;
using System.Windows.Forms;

namespace ClipboardQueue;

/// <summary>
/// A tiny click-through overlay that briefly shows the current queue count
/// to the right of the mouse pointer whenever the count changes.
/// </summary>
internal sealed class CursorCounter : Form
{
    private readonly Label _label;
    private readonly System.Windows.Forms.Timer _hideTimer;

    public CursorCounter()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Size = new Size(48, 34);
        BackColor = Color.FromArgb(30, 30, 30);
        Opacity = 0.85;

        _label = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 14F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            Text = "0"
        };

        Controls.Add(_label);

        _hideTimer = new System.Windows.Forms.Timer
        {
            Interval = 900
        };

        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            Hide();
        };
    }

    // Never steal focus from the app the user is working in.
    protected override bool ShowWithoutActivation
    {
        get { return true; }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
            cp.ExStyle |= 0x00000008; // WS_EX_TOPMOST
            cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            cp.ExStyle |= 0x00000020; // WS_EX_TRANSPARENT (click-through)
            return cp;
        }
    }

    public void ShowCount(int count)
    {
        try
        {
            Point p = Cursor.Position;

            Location = new Point(p.X + 26, p.Y - 12);
            _label.Text = count.ToString();

            _hideTimer.Stop();
            Show();
            _hideTimer.Start();
        }
        catch
        {
            // The counter must never break anything.
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hideTimer.Dispose();
        }

        base.Dispose(disposing);
    }
}
