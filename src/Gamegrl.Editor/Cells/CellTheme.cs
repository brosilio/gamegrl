using Gamegrl.UI;
namespace Gamegrl.Editor;

/// <summary>
/// Semantic colors for the cell editor, taken from the engine <see cref="Palette"/> that the pixel UI also uses.
/// Structure comes from thin lines and glyphs, not filled blocks: one background, one raised tone for popups and hover,
/// a faint border, dim and normal text, and a single accent. Each token is chosen to stay distinct when the terminal
/// writer quantizes to 256 or 16 colors (the accent becomes 205, then magenta).
/// </summary>
public sealed class CellTheme
{
    public Color32 Background = Palette.Background;
    /// <summary>Popups, tooltips, hovered rows and fields being edited.</summary>
    public Color32 Raised = Palette.Raised;
    /// <summary>Background of selected rows; selected text also takes the accent, so 16-color terminals still show it.</summary>
    public Color32 Selection = Palette.Selection;
    public Color32 Border = Palette.Border;
    public Color32 Text = Palette.Text;
    public Color32 Dim = Palette.Dim;
    public Color32 Accent = Palette.Accent;
    public Color32 Warning = Palette.Warning;
    public Color32 Error = Palette.Error;
    public Color32 AxisX = Palette.AxisX;
    public Color32 AxisY = Palette.AxisY;
    public Color32 AxisZ = Palette.AxisZ;
    /// <summary>Share of a property row given to its label.</summary>
    public float LabelWidthRatio = 0.38f;
}
