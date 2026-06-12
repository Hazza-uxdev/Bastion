using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SecureVault
{
    public partial class GraphColorDialog : Window
    {
        public Color NodeColor { get; private set; }
        public Color HubColor  { get; private set; }
        public Color LineColor { get; private set; }

        private Color _selNode, _selHub, _selLine;

        private static readonly List<Color> Palette = new()
        {
            Color.FromRgb(0xFF,0x00,0xCC), // magenta
            Color.FromRgb(0xEE,0x00,0xAA), // deep magenta
            Color.FromRgb(0x7C,0x3A,0xED), // purple
            Color.FromRgb(0x00,0xC8,0xDC), // cyan
            Color.FromRgb(0x00,0xD4,0x6A), // green
            Color.FromRgb(0xFF,0xA5,0x00), // orange
            Color.FromRgb(0xFF,0x44,0x44), // red
            Color.FromRgb(0xFF,0xFF,0x00), // yellow
            Color.FromRgb(0x44,0xAA,0xFF), // blue
            Color.FromRgb(0xFF,0xFF,0xFF), // white
            Color.FromRgb(0xAA,0xAA,0xAA), // grey
            Color.FromRgb(0x55,0x55,0x55), // dark grey
        };

        public GraphColorDialog(Color nodeColor, Color hubColor, Color lineColor)
        {
            InitializeComponent();
            _selNode = nodeColor; _selHub = hubColor; _selLine = lineColor;

            BuildPalette(NodeColorPanel, Palette, c => { _selNode = c; UpdatePreview(); }, _selNode);
            BuildPalette(HubColorPanel,  Palette, c => { _selHub  = c; UpdatePreview(); }, _selHub);
            BuildPalette(LineColorPanel, Palette, c => { _selLine = c; UpdatePreview(); }, _selLine);
            UpdatePreview();
        }

        private void BuildPalette(WrapPanel panel, List<Color> colors, System.Action<Color> onSelect, Color current)
        {
            foreach (var color in colors)
            {
                var swatch = new Border
                {
                    Width = 28, Height = 28, CornerRadius = new CornerRadius(6),
                    Background = new SolidColorBrush(color),
                    Margin = new Thickness(3),
                    Cursor = Cursors.Hand,
                    BorderThickness = new Thickness(SameRgb(color, current) ? 2 : 0),
                    BorderBrush = new SolidColorBrush(Colors.White),
                    ToolTip = $"#{color.R:X2}{color.G:X2}{color.B:X2}"
                };
                var captured = color;
                swatch.MouseLeftButtonDown += (s, _) =>
                {
                    // Clear selection borders in this panel
                    foreach (Border b in panel.Children) b.BorderThickness = new Thickness(0);
                    ((Border)s).BorderThickness = new Thickness(2);
                    onSelect(captured);
                };
                panel.Children.Add(swatch);
            }
        }

        private void UpdatePreview()
        {
            PreviewCanvas.Children.Clear();

            // Draw preview: two nodes + line
            double cx1 = 80, cy = 35, cx2 = 250, cx3 = 350;

            var line1 = new Line { X1=cx1, Y1=cy, X2=cx2, Y2=cy, Stroke=new SolidColorBrush(_selLine), StrokeThickness=1.4 };
            var line2 = new Line { X1=cx2, Y1=cy, X2=cx3, Y2=cy, Stroke=new SolidColorBrush(_selLine), StrokeThickness=1.4 };
            PreviewCanvas.Children.Add(line1);
            PreviewCanvas.Children.Add(line2);

            AddPreviewNode(cx1, cy, 8, _selNode);
            AddPreviewNode(cx2, cy, 14, _selHub);
            AddPreviewNode(cx3, cy, 8, _selNode);
        }

        private void AddPreviewNode(double x, double y, double r, Color fill)
        {
            var el = new Ellipse
            {
                Width=r*2, Height=r*2, Fill=new SolidColorBrush(fill),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                    { Color=fill, BlurRadius=8, ShadowDepth=0, Opacity=0.6 }
            };
            Canvas.SetLeft(el, x-r); Canvas.SetTop(el, y-r);
            PreviewCanvas.Children.Add(el);
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        { if (e.ChangedButton == MouseButton.Left) DragMove(); }

        private void Apply_Click(object sender, RoutedEventArgs e)
        { NodeColor = _selNode; HubColor = _selHub; LineColor = _selLine; DialogResult = true; }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        { NodeColor = _selNode; HubColor = _selHub; LineColor = _selLine; DialogResult = true; }

        private static bool SameRgb(Color a, Color b) => a.R == b.R && a.G == b.G && a.B == b.B;
    }
}
