using System.Drawing;
using System.Windows.Forms;

internal static class UiTheme
{
    public static readonly Color Background = Color.FromArgb(243, 245, 249);
    public static readonly Color Surface = Color.White;
    public static readonly Color Text = Color.FromArgb(27, 39, 61);
    public static readonly Color Muted = Color.FromArgb(92, 106, 128);
    public static readonly Color Accent = Color.FromArgb(36, 94, 219);
    public static readonly Color Border = Color.FromArgb(218, 225, 235);
    public static readonly Color Danger = Color.FromArgb(180, 45, 58);

    public static void StyleForm(Form form)
    {
        form.Font = new Font("맑은 고딕", 10);
        form.BackColor = Background; form.ForeColor = Text;
    }

    public static void StyleButton(Button button, string variant = "secondary")
    {
        bool primary = variant == "primary", danger = variant == "danger", quiet = variant == "quiet";
        button.FlatStyle = FlatStyle.Flat; button.UseVisualStyleBackColor = false;
        button.BackColor = primary ? Accent : danger ? Color.FromArgb(255, 244, 245) : Surface;
        button.ForeColor = primary ? Color.White : danger ? Danger : Text;
        button.FlatAppearance.BorderColor = primary ? Accent : danger ? Color.FromArgb(239, 195, 201) : Border;
        button.FlatAppearance.BorderSize = quiet ? 0 : 1;
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(28, 78, 190) : danger ? Color.FromArgb(255, 229, 232) : Color.FromArgb(232, 239, 252);
        button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(23, 65, 163) : Color.FromArgb(219, 229, 246);
        button.Padding = new Padding(12, 5, 12, 5);
        button.Cursor = Cursors.Hand;
    }

    public static void StyleInput(Control control)
    {
        control.BackColor = Surface; control.ForeColor = Text;
        var textBox = control as TextBox;
        if (textBox != null) textBox.BorderStyle = BorderStyle.FixedSingle;
        var combo = control as ComboBox;
        if (combo != null) combo.FlatStyle = FlatStyle.Flat;
    }
}
