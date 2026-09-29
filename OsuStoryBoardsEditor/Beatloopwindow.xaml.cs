using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using OsuStoryBoardsEditor.Models;

namespace OsuStoryBoardsEditor
{
    public partial class BeatLoopWindow : Window
    {
        public BeatEffectOptions Options { get; private set; } = new();

        public BeatLoopWindow(OsuSprite sprite)
        {
            InitializeComponent();

            CmbEasingIn.ItemsSource = OsuCommand.EasingNames;
            CmbEasingOut.ItemsSource = OsuCommand.EasingNames;
            CmbMoveEasing.ItemsSource = OsuCommand.EasingNames;
            CmbStepEasing.ItemsSource = OsuCommand.EasingNames;
            CmbStepEasing.SelectedIndex = 19;   // Expo Out
            CmbEasingIn.SelectedIndex = 4;
            CmbEasingOut.SelectedIndex = 2;
            CmbMoveEasing.SelectedIndex = 0;
            CmbType.SelectedIndex = 0;

            TxtFrom.Text = sprite.StartTime.ToString();
            TxtTo.Text = sprite.EndTime.ToString();
        }

        private void CmbType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PanelPulse == null || PanelWrap == null || PanelStep == null) return;
            int t = CmbType.SelectedIndex;
            PanelPulse.Visibility = (t == 0 || t == 3) ? Visibility.Visible : Visibility.Collapsed;
            if (t == 0) TxtIntensity.Text = "15";
            else if (t == 3) TxtIntensity.Text = "60";   // cuánto baja el brillo entre beats (%)
            PanelWrap.Visibility = t == 1 ? Visibility.Visible : Visibility.Collapsed;
            PanelStep.Visibility = t == 2 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static double D(string s, double def) =>
            double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;

        private void BtnAccept_Click(object sender, RoutedEventArgs e)
        {
            int from = (int)D(TxtFrom.Text, 0);
            int to = (int)D(TxtTo.Text, from + 1000);

            bool step = CmbType.SelectedIndex == 2;

            Options = new BeatEffectOptions
            {
                Type = CmbType.SelectedIndex switch
                {
                    1 => BeatEffectType.WrapMove,
                    2 => BeatEffectType.StepMove,
                    3 => BeatEffectType.GlowPulse,
                    _ => BeatEffectType.ScalePulse
                },
                RangeStart = from,
                RangeEnd = to,
                Intensity = D(TxtIntensity.Text, 15) / 100.0,
                EasingIn = Math.Max(0, CmbEasingIn.SelectedIndex),
                EasingOut = Math.Max(0, CmbEasingOut.SelectedIndex),
                BeatsPerCross = Math.Max(1, (int)D(TxtBeats.Text, 4)),
                MoveEasing = Math.Max(0, CmbMoveEasing.SelectedIndex),

                // compartidos entre wrap y saltos: se lee el panel que esté activo
                Axis = (step ? CmbStepAxis : CmbAxis).SelectedIndex == 1 ? WrapAxis.Vertical : WrapAxis.Horizontal,
                Reverse = (step ? ChkStepReverse : ChkReverse).IsChecked == true,
                Margin = D((step ? TxtStepMargin : TxtMargin).Text, 100),

                Jumps = Math.Max(1, (int)D(TxtJumps.Text, 8)),
                BeatsPerJump = Math.Max(1, (int)D(TxtBeatsPerJump.Text, 1)),
                JumpMs = Math.Max(1, (int)D(TxtJumpMs.Text, 80)),
                ShakePos = ChkShakePos.IsChecked == true,
                ShakePosPx = D(TxtShakePx.Text, 3),
                ShakeRot = ChkShakeRot.IsChecked == true,
                ShakeRotDeg = D(TxtShakeDeg.Text, 4),
                ShakeCount = Math.Max(1, (int)D(TxtShakeCount.Text, 3)),
                ShakeMs = Math.Max(10, (int)D(TxtShakeMs.Text, 150)),
                JumpEasing = Math.Max(0, CmbStepEasing.SelectedIndex)
            };

            DialogResult = true;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}