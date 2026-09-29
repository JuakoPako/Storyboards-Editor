using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OsuStoryBoardsEditor.Models;

namespace OsuStoryBoardsEditor
{
    public partial class ShakeWindow : Window
    {
        public ShakeOptions Options { get; private set; } = new();

        public ShakeWindow(OsuSprite sprite)
        {
            InitializeComponent();

            TxtFrom.Text = sprite.StartTime.ToString();
            TxtTo.Text = sprite.EndTime.ToString();
            TxtEnd.Text = "100";
            TxtSeed.Text = new Random().Next(1, 99999).ToString();
            CmbMode.SelectedIndex = 0;      // dispara ApplyModeDefaults
            ApplyModeDefaults();
            UpdateEstimate();
        }

        private static double D(string s, double def) =>
            double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;

        // Valores de partida según el modo: suave = lento y chico, brusco = rápido y más grande
        private void ApplyModeDefaults()
        {
            if (TxtAmpX == null || TxtAmpY == null || TxtFreq == null || TxtRotDeg == null || CmbMode == null) return;
            bool harsh = CmbMode.SelectedIndex == 1;
            TxtAmpX.Text = harsh ? "10" : "6";
            TxtAmpY.Text = harsh ? "10" : "6";
            TxtFreq.Text = harsh ? "20" : "5";
            TxtRotDeg.Text = harsh ? "3" : "1.5";
        }

        private void CmbMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyModeDefaults();
            UpdateEstimate();
        }

        private void Field_Changed(object sender, RoutedEventArgs e) => UpdateEstimate();

        private void BtnSeed_Click(object sender, RoutedEventArgs e)
            => TxtSeed.Text = new Random().Next(1, 99999).ToString();

        private ShakeOptions Read()
        {
            int from = (int)D(TxtFrom.Text, 0);
            int to = (int)D(TxtTo.Text, from + 1000);
            return new ShakeOptions
            {
                Mode = CmbMode.SelectedIndex == 1 ? ShakeMode.Harsh : ShakeMode.Smooth,
                RangeStart = from,
                RangeEnd = to,
                AmpX = Math.Max(0, D(TxtAmpX.Text, 6)),
                AmpY = Math.Max(0, D(TxtAmpY.Text, 6)),
                Frequency = Math.Clamp(D(TxtFreq.Text, 5), 0.5, 60),
                Rotate = ChkRot.IsChecked == true,
                RotDeg = Math.Max(0, D(TxtRotDeg.Text, 1.5)),
                EndPercent = Math.Clamp(D(TxtEnd.Text, 100), 0, 100),
                Seed = (int)D(TxtSeed.Text, 1)
            };
        }

        // Muestra cuántos comandos va a generar (en rojo si se pasa del límite)
        private void UpdateEstimate()
        {
            if (TxtEstimate == null || TxtFrom == null || TxtTo == null || TxtFreq == null
                || ChkRot == null || CmbMode == null) return;

            int n = new ShakeService().EstimateCommands(Read());
            bool tooMany = n > ShakeService.MaxCommands;
            TxtEstimate.Text = tooMany
                ? $"≈ {n:N0} comandos: demasiados (máx. {ShakeService.MaxCommands:N0}). Baja la frecuencia o acorta el rango."
                : $"≈ {n:N0} comandos";
            TxtEstimate.Foreground = tooMany ? Brushes.IndianRed : Brushes.Gray;
        }

        private void BtnAccept_Click(object sender, RoutedEventArgs e)
        {
            var o = Read();
            if (o.RangeEnd - o.RangeStart < 20)
            {
                MessageBox.Show("El rango es demasiado corto (mínimo 20 ms).", "Shake");
                return;
            }
            if (new ShakeService().EstimateCommands(o) > ShakeService.MaxCommands)
            {
                MessageBox.Show("Se generarían demasiados comandos. Baja la frecuencia o acorta el rango.", "Shake");
                return;
            }
            Options = o;
            DialogResult = true;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
