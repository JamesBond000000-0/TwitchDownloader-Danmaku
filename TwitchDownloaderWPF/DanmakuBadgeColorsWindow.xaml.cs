using SkiaSharp;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Xceed.Wpf.Toolkit;

namespace TwitchDownloaderWPF
{
    /// <summary>
    /// Lets the user give individual badge ids (broadcaster, moderator, vip,
    /// ...) their own danmaku highlight color. Row order is the priority
    /// order used by the renderers when a comment carries several of the
    /// badges. The result only contains the checked rows.
    /// </summary>
    public partial class DanmakuBadgeColorsWindow : Window
    {
        private static readonly (string Badge, string DefaultColor)[] BadgeRows =
        [
            ("broadcaster", "#CCE91916"),
            ("moderator", "#CC00AD03"),
            ("vip", "#CCE005B9"),
            ("partner", "#CC9146FF"),
            ("verified", "#CC808080"),
            ("subscriber", "#CC0057B8"),
        ];

        private readonly List<(string Badge, CheckBox Check, ColorPicker Picker)> _rows = [];

        public Dictionary<string, SKColor> Result { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

        public DanmakuBadgeColorsWindow(IReadOnlyDictionary<string, SKColor> current)
        {
            InitializeComponent();

            foreach (var (badge, defaultColor) in BadgeRows)
            {
                var check = new CheckBox
                {
                    Content = badge,
                    Width = 110,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)FindResource("AppText"),
                };
                var picker = new ColorPicker
                {
                    Width = 50,
                    Height = 22,
                    Margin = new Thickness(5, 0, 0, 0),
                    Background = Brushes.White,
                    SelectedColor = ColorConverter.ConvertFromString(defaultColor) as Color?,
                };

                if (current.TryGetValue(badge, out var existing))
                {
                    check.IsChecked = true;
                    picker.SelectedColor = Color.FromArgb(existing.Alpha, existing.Red, existing.Green, existing.Blue);
                }

                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
                row.Children.Add(check);
                row.Children.Add(picker);
                rowsPanel.Children.Add(row);
                _rows.Add((badge, check, picker));
            }
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            var result = new Dictionary<string, SKColor>(StringComparer.OrdinalIgnoreCase);
            foreach (var (badge, check, picker) in _rows)
            {
                if (check.IsChecked == true && picker.SelectedColor is { } color)
                {
                    result[badge] = new SKColor(color.R, color.G, color.B, color.A);
                }
            }

            Result = result;
            DialogResult = true;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
