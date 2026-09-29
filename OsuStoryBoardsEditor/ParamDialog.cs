using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace OsuStoryBoardsEditor
{
    public class ParamDialog : Window
    {
        private readonly Dictionary<string, TextBox> _boxes = new();

        public ParamDialog(string title, params (string key, string label, string def)[] fields)
        {
            Title = title;
            Width = 320;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;

            var panel = new StackPanel { Margin = new Thickness(14) };
            foreach (var f in fields)
            {
                panel.Children.Add(new TextBlock { Text = f.label, Margin = new Thickness(0, 6, 0, 0) });
                var tb = new TextBox { Text = f.def, Margin = new Thickness(0, 2, 0, 0) };
                _boxes[f.key] = tb;
                panel.Children.Add(tb);
            }

            var ok = new Button { Content = "Generar", Height = 26, Margin = new Thickness(0, 12, 0, 0), IsDefault = true };
            ok.Click += (_, __) => DialogResult = true;
            panel.Children.Add(ok);
            Content = panel;
        }

        public double D(string key, double def) =>
            double.TryParse(_boxes[key].Text.Replace(',', '.'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var v) ? v : def;

        public string S(string key) => _boxes[key].Text;
    }
}