using OsuStoryBoardsEditor.Models;

namespace OsuStoryBoardsEditor.Commands
{
    // ── Cambio de estado de un sprite (lo que produce una transacción) ──
    // Cubre mover, escalar, rotar, opacidad, visibilidad, nombre, capa, tiempos y TODOS los comandos/keyframes,
    // loops y triggers del sprite. Al ejecutarse desde el manager el cambio ya está aplicado; Execute() solo
    // se usa al rehacer.
    public sealed class SpriteStateCommand : IEditorCommand
    {
        private readonly SpriteSnapshot _before, _after;
        public string Description { get; }

        public SpriteStateCommand(string description, SpriteSnapshot before, SpriteSnapshot after)
        {
            Description = description;
            _before = before;
            _after = after;
        }

        public void Execute() => _after.Restore();
        public void Undo() => _before.Restore();
    }

    // ── Varias acciones que se deshacen de una sola vez ──
    public sealed class CompositeCommand : IEditorCommand
    {
        private readonly IReadOnlyList<IEditorCommand> _commands;
        public string Description { get; }

        public CompositeCommand(string description, IReadOnlyList<IEditorCommand> commands)
        {
            Description = description;
            _commands = commands;
        }

        public void Execute() { foreach (var c in _commands) c.Execute(); }
        public void Undo() { for (int i = _commands.Count - 1; i >= 0; i--) _commands[i].Undo(); }
    }

    // ── Agregar sprites ──
    // Sirve tanto con Execute() (el sprite todavía no está en el proyecto) como con Record()
    // (el sprite ya fue agregado, p.ej. por StoryboardProject.AddSprite): el índice se captura al construir.
    public sealed class AddSpritesCommand : IEditorCommand
    {
        private readonly StoryboardProject _project;
        private readonly List<(OsuSprite Sprite, int Index)> _entries = new();
        public string Description { get; }

        public AddSpritesCommand(StoryboardProject project, IEnumerable<OsuSprite> sprites, string? description = null)
        {
            _project = project;
            int next = project.Sprites.Count;
            foreach (var s in sprites)
            {
                int idx = project.Sprites.IndexOf(s);
                _entries.Add((s, idx >= 0 ? idx : next++));
            }
            Description = description ?? (_entries.Count == 1
                ? $"Agregar sprite \"{_entries[0].Sprite.Name}\""
                : $"Agregar {_entries.Count} sprites");
        }

        public void Execute()
        {
            foreach (var (sprite, index) in _entries.OrderBy(e => e.Index))
                if (!_project.Sprites.Contains(sprite))
                    _project.Sprites.Insert(Math.Min(index, _project.Sprites.Count), sprite);
        }

        public void Undo()
        {
            foreach (var (sprite, _) in _entries)
            {
                _project.Sprites.Remove(sprite);
                if (_project.SelectedSprite == sprite) _project.SelectedSprite = null;
            }
        }
    }

    // ── Eliminar sprites ──
    // Usar con UndoRedoManager.Execute(): los índices se capturan al construir, mientras siguen en la lista.
    public sealed class RemoveSpritesCommand : IEditorCommand
    {
        private readonly StoryboardProject _project;
        private readonly List<(OsuSprite Sprite, int Index)> _entries = new();
        public string Description { get; }

        public RemoveSpritesCommand(StoryboardProject project, IEnumerable<OsuSprite> sprites, string? description = null)
        {
            _project = project;
            foreach (var s in sprites)
            {
                int idx = project.Sprites.IndexOf(s);
                if (idx >= 0) _entries.Add((s, idx));
            }
            Description = description ?? (_entries.Count == 1
                ? $"Eliminar sprite \"{_entries[0].Sprite.Name}\""
                : $"Eliminar {_entries.Count} sprites");
        }

        public void Execute()
        {
            foreach (var (sprite, _) in _entries)
            {
                _project.Sprites.Remove(sprite);
                if (_project.SelectedSprite == sprite) _project.SelectedSprite = null;
            }
        }

        public void Undo()
        {
            foreach (var (sprite, index) in _entries.OrderBy(e => e.Index))
                if (!_project.Sprites.Contains(sprite))
                    _project.Sprites.Insert(Math.Min(index, _project.Sprites.Count), sprite);
        }
    }

    // ── Estado a nivel proyecto (import .osb, nuevo SB desde mapa, cargar .osbs, cargar audio) ──
    // Los sprites se guardan por referencia (no se clonan), así que es barato aunque haya miles.
    public sealed record ProjectState(
        List<OsuSprite> Sprites,
        List<OsuTimingPoint> TimingPoints,
        string AudioPath,
        int TotalDuration,
        string? BgPath);

    public sealed class ProjectStateCommand : IEditorCommand
    {
        private readonly ProjectState _before, _after;
        private readonly Action<ProjectState> _apply;
        public string Description { get; }

        public ProjectStateCommand(string description, ProjectState before, ProjectState after,
            Action<ProjectState> apply)
        {
            Description = description;
            _before = before;
            _after = after;
            _apply = apply;
        }

        public void Execute() => _apply(_after);
        public void Undo() => _apply(_before);
    }
}