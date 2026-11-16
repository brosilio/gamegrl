namespace Gamegrl.UI;

/// <summary>Colors and metrics. The defaults are a dark editor theme in the spirit of Unity 5's Pro skin.</summary>
public sealed class Theme
{
    public float RowHeight = 22;
    public float Padding = 6;
    public float Spacing = 3;
    public float Indent = 14;
    public float LabelWidthRatio = 0.38f;
    public float ScrollbarWidth = 8;
    public float SplitterThickness = 4;
    public float TabHeight = 24;

    public Color32 Background = Color32.Hex(0x1e1f22);
    public Color32 Panel = Color32.Hex(0x2b2d31);
    public Color32 PanelHeader = Color32.Hex(0x232428);
    public Color32 Border = Color32.Hex(0x161719);
    public Color32 Text = Color32.Hex(0xdcdde0);
    public Color32 TextDim = Color32.Hex(0x8e9097);
    public Color32 Widget = Color32.Hex(0x3a3c42);
    public Color32 WidgetHover = Color32.Hex(0x464951);
    public Color32 WidgetActive = Color32.Hex(0x53565f);
    public Color32 Field = Color32.Hex(0x1f2023);
    public Color32 Accent = Color32.Hex(0x3d7be0);
    public Color32 AccentDim = Color32.Hex(0x2c4f86);
    public Color32 Selection = Color32.Hex(0x2c5aa0);
    public Color32 AxisX = Color32.Hex(0xd9534f);
    public Color32 AxisY = Color32.Hex(0x5cb85c);
    public Color32 AxisZ = Color32.Hex(0x428bca);
    public Color32 Warning = Color32.Hex(0xe0b13d);
    public Color32 Error = Color32.Hex(0xe0533d);
}
