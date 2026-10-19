using System.Globalization;
using System.Reflection;

namespace FxMonitor;

/// <summary>
/// 主窗体和右侧页的外观：主按钮蓝、次要白底、删除红字。只动颜色和边框，不改点击逻辑。
/// </summary>
internal static class Ui
{
    public static readonly Color Accent = Color.FromArgb(0, 110, 200);
    public static readonly Color Text = Color.FromArgb(36, 36, 36);
    public static readonly Color Muted = Color.FromArgb(108, 112, 118);
    public static readonly Color Line = Color.FromArgb(218, 223, 230);
    public static readonly Color Window = Color.FromArgb(243, 245, 248);
    public static readonly Color Card = Color.White;
    public static readonly Color Zebra = Color.FromArgb(247, 249, 252);
    public static readonly Color Danger = Color.FromArgb(176, 48, 48);
    public static readonly Color Ok = Color.FromArgb(28, 128, 72);
    public static readonly Color Warn = Color.FromArgb(176, 108, 0);
    public static readonly Color Soft = Color.FromArgb(226, 238, 252);

    public static void Primary(Button b) =>
        Flat(b, Accent, Color.White, Accent, Color.FromArgb(0, 96, 168), Color.FromArgb(0, 78, 140));

    public static void Secondary(Button b) =>
        Flat(b, Color.White, Text, Line, Color.FromArgb(238, 244, 250), Color.FromArgb(226, 236, 248));

    public static void DangerBtn(Button b) =>
        Flat(b, Color.White, Danger, Color.FromArgb(220, 160, 160), Color.FromArgb(255, 240, 240), Color.FromArgb(248, 220, 220));

    public static void Quiet(Button b) =>
        Flat(b, Window, Text, Line, Color.FromArgb(232, 236, 240), Color.FromArgb(220, 226, 232));

    public static Control VSep() =>
        new Panel { Width = 1, Height = 22, BackColor = Line, Margin = new Padding(8, 5, 10, 3) };

    public static Label Chip(string text) => new()
    {
        Text = text, AutoSize = true, ForeColor = Accent, BackColor = Soft,
        Padding = new Padding(5, 1, 5, 1), Margin = new Padding(4, 5, 0, 0),
        Font = new Font("Microsoft YaHei UI", 8f),
    };

    public static Button Help()
    {
        var b = new Button
        {
            Text = "?", Width = 24, Height = 24, Margin = new Padding(4, 4, 0, 0),
            Font = new Font("Microsoft YaHei UI", 9f),
        };
        Flat(b, Soft, Accent, Color.FromArgb(196, 220, 244), Color.FromArgb(210, 230, 248), Color.FromArgb(186, 214, 240));
        return b;
    }

    public static Label Unit(string text) => new()
    {
        Text = text, AutoSize = true, ForeColor = Muted, BackColor = Color.FromArgb(240, 242, 245),
        Padding = new Padding(6, 2, 6, 2), Margin = new Padding(6, 6, 0, 0),
        Font = new Font("Microsoft YaHei UI", 8.5f),
    };

    public static void Grid(DataGridView g)
    {
        g.BackgroundColor = Color.White;
        g.BorderStyle = BorderStyle.None;
        g.EnableHeadersVisualStyles = false;
        g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        g.GridColor = Line;
        g.ColumnHeadersDefaultCellStyle.BackColor = Window;
        g.ColumnHeadersDefaultCellStyle.ForeColor = Muted;
        g.ColumnHeadersDefaultCellStyle.SelectionBackColor = Window;
        g.ColumnHeadersHeight = 28;
        g.RowTemplate.Height = 24;
        g.DefaultCellStyle.SelectionBackColor = Color.FromArgb(214, 230, 250);
        g.DefaultCellStyle.SelectionForeColor = Color.Black;
        g.AlternatingRowsDefaultCellStyle.BackColor = Zebra;
        Smooth(g);
    }

    public static void Smooth(Control c)
    {
        typeof(Control).InvokeMember("DoubleBuffered",
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.SetProperty,
            null, c, new object[] { true }, CultureInfo.InvariantCulture);
    }

    private sealed class Spec
    {
        public Color Back, Fore, Border, Hover, Down;
    }

    private static readonly HashSet<Button> Hooked = new();

    private static void Flat(Button b, Color back, Color fore, Color border, Color hover, Color down)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 1;
        b.UseVisualStyleBackColor = false;
        b.Tag = new Spec { Back = back, Fore = fore, Border = border, Hover = hover, Down = down };
        if (Hooked.Add(b)) b.EnabledChanged += (_, _) => Paint(b);
        Paint(b);
    }

    private static void Paint(Button b)
    {
        if (b.Tag is not Spec s) return;
        if (b.Enabled)
        {
            b.BackColor = s.Back;
            b.ForeColor = s.Fore;
            b.FlatAppearance.BorderColor = s.Border;
            b.FlatAppearance.MouseOverBackColor = s.Hover;
            b.FlatAppearance.MouseDownBackColor = s.Down;
            return;
        }

        b.BackColor = Color.FromArgb(236, 238, 241);
        b.ForeColor = Color.FromArgb(150, 154, 160);
        b.FlatAppearance.BorderColor = Line;
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(236, 238, 241);
        b.FlatAppearance.MouseDownBackColor = Color.FromArgb(236, 238, 241);
    }
}
