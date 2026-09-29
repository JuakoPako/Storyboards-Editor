using OsuStoryBoardsEditor.Models;
using OsuStoryBoardsEditor.Services;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace OsuStoryBoardsEditor
{
    public partial class TextSpriteWindow : Window
    {
        public TextSpec? Result { get; private set; }
        public bool PerLetter { get; private set; }
        private readonly bool _ready;

        public TextSpriteWindow()
        {
            InitializeComponent();
            var fonts = TextSpriteService.GetInstalledFonts();
            CmbFont.ItemsSource = fonts;
            CmbFont.SelectedItem =
                fonts.FirstOrDefault(f => f.Equals("Segoe UI", StringComparison.OrdinalIgnoreCase))
                ?? fonts.FirstOrDefault();
            _ready = true;   // antes de esto los TextChanged del XAML todavía no tienen controles
            UpdatePreview();
        }

        private void Input_Changed(object sender, RoutedEventArgs e) => UpdatePreview();

        private static double D(string s, double def) =>
            double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;

        private static bool TryColor(string s, out byte r, out byte g, out byte b)
        {
            r = g = b = 255;
            s = s.Trim().TrimStart('#');
            if (s.Length != 6) return false;
            return byte.TryParse(s[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)
                && byte.TryParse(s.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)
                && byte.TryParse(s.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b);
        }

        private void UpdatePreview()
        {
            if (!_ready) return;
            PreviewText.Text = TxtText.Text;
            if (CmbFont.SelectedItem is string f) PreviewText.FontFamily = new FontFamily(f);
            PreviewText.FontSize = Math.Clamp(D(TxtSize.Text, 48), 8, 72);   // el preview se limita
            PreviewText.FontWeight = ChkBold.IsChecked == true ? FontWeights.Bold : FontWeights.Normal;
            PreviewText.FontStyle = ChkItalic.IsChecked == true ? FontStyles.Italic : FontStyles.Normal;
            if (TryColor(TxtColor.Text, out var r, out var g, out var b))
                PreviewText.Foreground = new SolidColorBrush(Color.FromRgb(r, g, b));
        }

        private void BtnAccept_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtText.Text)) { MessageBox.Show("Escribí un texto."); return; }
            if (CmbFont.SelectedItem is not string family) return;
            if (!TryColor(TxtColor.Text, out var r, out var g, out var b))
            { MessageBox.Show("Color inválido. Usá el formato #RRGGBB."); return; }

            float size = (float)Math.Clamp(D(TxtSize.Text, 48), 6, 400);
            Result = new TextSpec(TxtText.Text, family, size,
                ChkBold.IsChecked == true, ChkItalic.IsChecked == true, r, g, b);
            PerLetter = ChkPerLetter.IsChecked == true;
            DialogResult = true;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}