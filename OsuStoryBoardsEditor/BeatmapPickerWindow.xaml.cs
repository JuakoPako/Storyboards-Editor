using OsuStoryBoardsEditor.Models;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace OsuStoryBoardsEditor
{
    public partial class BeatmapPickerWindow : Window
    {
        public BeatmapSetInfo? SelectedBeatmap { get; private set; }

        private readonly List<BeatmapSetInfo> _allBeatmaps;

        public BeatmapPickerWindow(List<BeatmapSetInfo> beatmaps, string? headerText = null)
        {
            InitializeComponent();
            _allBeatmaps = beatmaps;
            LstBeatmaps.ItemsSource = new ObservableCollection<BeatmapSetInfo>(_allBeatmaps);
            if (headerText != null) TxtHeader.Text = headerText;
        }

        private void TxtSearch_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            var query = TxtSearch.Text.Trim();

            var filtered = string.IsNullOrEmpty(query)
                ? _allBeatmaps
                : _allBeatmaps.Where(b =>
                    (b.Title?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (b.Artist?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (b.Creator?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                  .ToList();

            LstBeatmaps.ItemsSource = new ObservableCollection<BeatmapSetInfo>(filtered);
        }

        private void LstBeatmaps_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (LstBeatmaps.SelectedItem is BeatmapSetInfo info)
            {
                SelectedBeatmap = info;
                DialogResult = true;
            }
        }
    }
}