using OsuStoryBoardsEditor.Commands;
using OsuStoryBoardsEditor.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace OsuStoryBoardsEditor.Controls
{
    public partial class LayerPanelControl : UserControl
    {
        // Evento que MainWindow escucha para saber qué sprite se seleccionó
        public event Action<OsuSprite>? SpriteSelected;

        public LayerPanelControl()
        {
            InitializeComponent();
        }

        private void Layer_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is OsuSprite sprite)
            {
                SpriteSelected?.Invoke(sprite);
            }
        }

        private void Visibility_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true; // que no dispare Layer_Click también
            if (sender is FrameworkElement fe && fe.DataContext is OsuSprite sprite)
            {
                using (_undo?.BeginTransaction(sprite.Visible ? "Ocultar capa" : "Mostrar capa", sprite))
                    sprite.Visible = !sprite.Visible;
            }
        }

        private void AddLayer_Click(object sender, MouseButtonEventArgs e)
        {
            // MainWindow lo maneja abriendo un file dialog
            // Por ahora disparamos un evento hacia arriba
            AddLayerRequested?.Invoke();
        }

        public event Action? AddLayerRequested;

        // ── Undo/redo global ──
        private UndoRedoManager? _undo;
        public void SetUndoManager(UndoRedoManager undo) => _undo = undo;

        private OsuSprite? _selected;

        // MainWindow lo llama cada vez que cambia el sprite activo (canvas, capas o timeline)
        public void SetSelectedSprite(OsuSprite? sprite)
        {
            _selected = sprite;
            for (int i = 0; i < LayerList.Items.Count; i++)
            {
                if (LayerList.ItemContainerGenerator.ContainerFromIndex(i) is not ContentPresenter cp) continue;
                cp.ApplyTemplate();
                if (cp.ContentTemplate?.FindName("ItemBorder", cp) is Border b)
                    ApplyHighlight(b);
            }
        }

        // Items recién generados (sprite importado) se pintan bien apenas cargan
        private void ItemBorder_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is Border b) ApplyHighlight(b);
        }

        private void ApplyHighlight(Border b)
        {
            bool selected = b.DataContext == _selected;
            b.Background = selected ? (Brush)FindResource("AccentDim") : Brushes.Transparent;
            b.BorderBrush = selected ? (Brush)FindResource("Accent") : Brushes.Transparent;
            b.BorderThickness = new Thickness(selected ? 1 : 0.5);
            if (b.Child is Grid g && g.Children.Count > 1 && g.Children[1] is TextBlock name)
            {
                name.Foreground = selected ? (Brush)FindResource("TextPrimary") : (Brush)FindResource("TextMuted");
                name.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }
    }
}