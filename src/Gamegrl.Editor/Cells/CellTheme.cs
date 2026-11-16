using Gamegrl.UI;
namespace Gamegrl.Editor;

/// <summary>
/// Semantic colors for the cell editor. Structure comes from thin lines and glyphs, not filled blocks: one background,
/// one raised tone for popups and hover, a faint border, dim and normal text, and a single accent. Each token is chosen
/// to stay distinct when the terminal writer quantizes to 256 or 16 colors (the accent becomes 205, then magenta).
/// </summary>
public sealed class CellTheme
{
    public Color32 Background = Color32.Hex(0x16161b);
    /// <summary>Popups, tooltips, hovered rows and fields being edited.</summary>
    public Color32 Raised = Color32.Hex(0x25252d);
    /// <summary>Background of selected rows; selected text also takes the accent, so 16-color terminals still show it.</summary>
    public Color32 Selection = Color32.Hex(0x3d2234);
    public Color32 Border = Color32.Hex(0x45454f);
    public Color32 Text = Color32.Hex(0xdedee4);
    public Color32 Dim = Color32.Hex(0x8b8b97);
    public Color32 Accent = Color32.Hex(0xff5fa2);
    public Color32 Warning = Color32.Hex(0xf0c674);
    public Color32 Error = Color32.Hex(0xff6b6b);
    public Color32 AxisX = Color32.Hex(0xff6b6b);
    public Color32 AxisY = Color32.Hex(0x9ad97c);
    public Color32 AxisZ = Color32.Hex(0x6aa9ff);
    /// <summary>Share of a property row given to its label.</summary>
    public float LabelWidthRatio = 0.38f;
}
