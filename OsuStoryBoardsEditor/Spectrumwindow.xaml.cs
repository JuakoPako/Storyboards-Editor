using OsuStoryBoardsEditor.Services;
using System;
using System.Globalization;
using System.Windows;

namespace OsuStoryBoardsEditor
{
    public partial class SpectrumWindow : Window
    {
        public SpectrumBarsOptions Options { get; private set; } = new();

        public SpectrumWindow(int totalMs, int startMs = 0)
        {
            InitializeComponent();
            int total = totalMs > 0 ? totalMs : 30000;
            int from = Math.Clamp(startMs, 0, Math.Max(0, total - 1000));
            TxtFrom.Text = from.ToString();
            TxtTo.Text = Math.Min(total, from + 20000).ToString();   // 20 s por defecto, como sugiere el tip
        }

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

        private void BtnAccept_Click(object sender, RoutedEventArgs e)
        {
            int from = Math.Max(0, (int)D(TxtFrom.Text, 0));
            int to = (int)D(TxtTo.Text, from + 20000);
            if (ChkKiai.IsChecked != true && to - from < 1000) { MessageBox.Show("El rango tiene que durar al menos 1 segundo."); return; }
            if (!TryColor(TxtColor.Text, out var r, out var g, out var b))
            { MessageBox.Show("Color inválido. Usá el formato #RRGGBB."); return; }

            Options = new SpectrumBarsOptions
            {
                KiaiOnly = ChkKiai.IsChecked == true,
                RangeStart = from,
                RangeEnd = to,
                Bars = Math.Clamp((int)D(TxtBars.Text, 32), 2, 128),
                Fps = Math.Clamp((int)D(TxtFps.Text, 20), 5, 60),
                TotalWidth = Math.Clamp(D(TxtWidth.Text, 854), 10, 2000),
                Gap = Math.Clamp(D(TxtGap.Text, 2), 0, 200),
                MaxHeight = Math.Clamp(D(TxtMaxH.Text, 120), 2, 2000),
                BaseY = D(TxtBaseY.Text, 470),
                Contrast = Math.Clamp(D(TxtContrast.Text, 2), 0.2, 6),
                MinDeltaPx = Math.Clamp(D(TxtMinDelta.Text, 2), 0, 50),
                R = r,
                G = g,
                B = b
            };
            DialogResult = true;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}