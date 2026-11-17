namespace Gamegrl.UI;

/// <summary>
/// The engine's one palette. The editor's cell chrome (<c>CellTheme</c>) and the pixel <see cref="Theme"/> both start
/// from it, so in-view overlays, game debug UI and editor panels read as one tool.
/// </summary>
public static class Palette
{
    public static readonly Color32 Background = Color32.Hex(0x16161b);
    public static readonly Color32 Raised = Color32.Hex(0x25252d);
    public static readonly Color32 Selection = Color32.Hex(0x3d2234);
    public static readonly Color32 Border = Color32.Hex(0x45454f);
    public static readonly Color32 Text = Color32.Hex(0xdedee4);
    public static readonly Color32 Dim = Color32.Hex(0x8b8b97);
    public static readonly Color32 Accent = Color32.Hex(0xff5fa2);
    public static readonly Color32 Warning = Color32.Hex(0xf0c674);
    public static readonly Color32 Error = Color32.Hex(0xff6b6b);
    public static readonly Color32 AxisX = Color32.Hex(0xff6b6b);
    public static readonly Color32 AxisY = Color32.Hex(0x9ad97c);
    public static readonly Color32 AxisZ = Color32.Hex(0x6aa9ff);
}

/// <summary>
/// Colors and metrics for <see cref="Ui"/>, in UI units (pixels at scale 1). The defaults are flat and line-drawn like
/// the editor: translucent panels over the view, one-pixel borders, one accent. Games replace any of it.
/// </summary>
public sealed class Theme
{
    public float RowHeight = 22;
    public float Padding = 6;
    public float Spacing = 3;
    public float Indent = 14;
    public float LabelWidthRatio = 0.38f;
    public float ScrollbarWidth = 6;
    public float TabHeight = 24;
    public float TitleHeight = 22;

    /// <summary>Window and panel fill; translucent so the view stays readable behind debug windows.</summary>
    public Color32 Panel = Palette.Background.WithAlpha(232);
    /// <summary>Title bars, popups and tooltips.</summary>
    public Color32 PanelHeader = Palette.Raised;
    public Color32 Border = Palette.Border;
    public Color32 Text = Palette.Text;
    public Color32 TextDim = Palette.Dim;
    /// <summary>Text drawn straight over the view (labels, tags) gets this outline so it reads on any background.</summary>
    public Color32 TextShadow = new(0, 0, 0, 200);
    public Color32 Widget = Palette.Raised;
    public Color32 WidgetHover = Color32.Hex(0x32323c);
    public Color32 WidgetActive = Palette.Selection;
    public Color32 Field = Color32.Hex(0x101014);
    public Color32 Accent = Palette.Accent;
    public Color32 Selection = Palette.Selection;
    public Color32 AxisX = Palette.AxisX;
    public Color32 AxisY = Palette.AxisY;
    public Color32 AxisZ = Palette.AxisZ;
    public Color32 Warning = Palette.Warning;
    public Color32 Error = Palette.Error;
}
