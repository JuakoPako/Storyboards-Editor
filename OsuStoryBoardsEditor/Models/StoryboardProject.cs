using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OsuStoryBoardsEditor.Models
{
    public class StoryboardProject : INotifyPropertyChanged
    {
        private string _audioPath = "";
        private int _totalDuration = 0;   // ms
        private OsuSprite? _selectedSprite;
        private int _beatDivisor = 4; // 1, 2, 3, 4, 5...

        // Timing importado de un .osu (líneas rojas = BPM real)
        public List<OsuTimingPoint> TimingPoints { get; set; } = new();

        public int BeatDivisor
        {
            get => _beatDivisor;
            set { _beatDivisor = value; OnPropertyChanged(); }
        }

        public string AudioPath
        {
            get => _audioPath;
            set { _audioPath = value; OnPropertyChanged(); }
        }

        public int TotalDuration
        {
            get => _totalDuration;
            set { _totalDuration = value; OnPropertyChanged(); }
        }

        public OsuSprite? SelectedSprite
        {
            get => _selectedSprite;
            set { _selectedSprite = value; OnPropertyChanged(); }
        }

        public ObservableCollection<OsuSprite> Sprites { get; set; } = new();

        // Agrega un sprite desde un path de imagen
        public OsuSprite AddSprite(string filePath)
        {
            var sprite = new OsuSprite
            {
                FilePath = filePath,
                Name = System.IO.Path.GetFileNameWithoutExtension(filePath),
                StartTime = 0,
                EndTime = TotalDuration > 0 ? TotalDuration : 5000,
                X = 320,  // ← centro real del canvas 854×480
                Y = 240,
                Origin = SpriteOrigin.Centre
            };
            Sprites.Add(sprite);
            SelectedSprite = sprite;
            return sprite;
        }

        public void RemoveSprite(OsuSprite sprite)
        {
            Sprites.Remove(sprite);
            if (SelectedSprite == sprite)
                SelectedSprite = null;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? p = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }
}