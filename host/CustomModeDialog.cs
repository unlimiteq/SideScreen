using System.Runtime.InteropServices;

namespace SideScreen;

/// <summary>Small dark dialog for a custom resolution (width × height) or a custom refresh rate.</summary>
public sealed class CustomModeDialog : Form
{
    static readonly Color Bg = Color.FromArgb(0x14, 0x16, 0x1B);
    static readonly Color Card = Color.FromArgb(0x1E, 0x21, 0x29);
    static readonly Color TextMain = Color.FromArgb(0xF2, 0xF4, 0xF8);
    static readonly Color TextDim = Color.FromArgb(0x8D, 0x94, 0xA3);
    static readonly Color Accent = Color.FromArgb(0x4C, 0x8D, 0xFF);

    readonly NumericUpDown _a = new(), _b = new();
    public int ValueA => (int)_a.Value;
    public int ValueB => (int)_b.Value;

    /// <summary>Resolution dialog.</summary>
    public static (int w, int h)? AskResolution(IWin32Window owner, int width, int height)
    {
        using var d = new CustomModeDialog("Custom resolution",
            "Even numbers only. A different shape than the tablet's screen is shown with black bars.",
            ("Width", width, 640, 7680), ("Height", height, 480, 4320));
        if (d.ShowDialog(owner) != DialogResult.OK) return null;
        return (d.ValueA & ~1, d.ValueB & ~1);
    }

    /// <summary>Refresh rate dialog.</summary>
    public static int? AskRefreshRate(IWin32Window owner, int hz)
    {
        using var d = new CustomModeDialog("Custom refresh rate",
            "Rates above what the tablet's screen supports are capped to its maximum.",
            ("Refresh rate (Hz)", hz, 24, 240), null);
        if (d.ShowDialog(owner) != DialogResult.OK) return null;
        return d.ValueA;
    }

    CustomModeDialog(string title, string hint, (string label, int value, int min, int max) a, (string label, int value, int min, int max)? b)
    {
        float s = DeviceDpi / 96f;
        int S(int v) => (int)Math.Round(v * s);
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Bg;
        ForeColor = TextMain;
        Font = new Font("Segoe UI", 9.5f);
        ClientSize = new Size(S(320), S(196));

        void AddField(NumericUpDown box, (string label, int value, int min, int max) f, int x, int width)
        {
            Controls.Add(new Label { Text = f.label.ToUpperInvariant(), Font = new Font("Segoe UI Semibold", 7.5f), ForeColor = TextDim, AutoSize = true, Location = new Point(S(x + 1), S(18)) });
            box.Minimum = f.min;
            box.Maximum = f.max;
            box.Value = Math.Clamp(f.value, f.min, f.max);
            box.Increment = b == null ? 1 : 2;
            box.BackColor = Card;
            box.ForeColor = TextMain;
            box.BorderStyle = BorderStyle.FixedSingle;
            box.Font = new Font("Segoe UI", 11f);
            box.TextAlign = HorizontalAlignment.Center;
            box.SetBounds(S(x), S(38), S(width), S(30));
            Controls.Add(box);
        }

        if (b is { } second)
        {
            AddField(_a, a, 20, 125);
            Controls.Add(new Label { Text = "×", Font = new Font("Segoe UI", 12f), ForeColor = TextDim, AutoSize = true, Location = new Point(S(152), S(40)) });
            AddField(_b, second, 175, 125);
        }
        else AddField(_a, a, 20, 280);

        Controls.Add(new Label { Text = hint, ForeColor = TextDim, Font = new Font("Segoe UI", 8.5f), Location = new Point(S(20), S(82)), Size = new Size(S(280), S(40)) });

        var ok = MakeButton("Apply", Accent, Color.White);
        var cancel = MakeButton("Cancel", Card, TextMain);
        ok.SetBounds(S(166), S(140), S(134), S(36));
        cancel.SetBounds(S(20), S(140), S(134), S(36));
        ok.DialogResult = DialogResult.OK;
        cancel.DialogResult = DialogResult.Cancel;
        AcceptButton = ok;
        CancelButton = cancel;
        Controls.AddRange([ok, cancel]);
    }

    static Button MakeButton(string text, Color back, Color fore)
    {
        var bt = new Button { Text = text, BackColor = back, ForeColor = fore, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Font = new Font("Segoe UI Semibold", 9.5f) };
        bt.FlatAppearance.BorderSize = 0;
        return bt;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int on = 1;
        DwmSetWindowAttribute(Handle, 20, ref on, 4);
        int color = Bg.R | (Bg.G << 8) | (Bg.B << 16);
        DwmSetWindowAttribute(Handle, 35, ref color, 4);
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
