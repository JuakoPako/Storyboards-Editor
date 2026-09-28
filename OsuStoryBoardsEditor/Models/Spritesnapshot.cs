using OsuStoryBoardsEditor.Models;

namespace OsuStoryBoardsEditor.Commands
{
    // Foto del estado de UN OsuCommand. Guarda la referencia al objeto original (para que el timeline
    // y las selecciones sigan apuntando a la misma instancia después de un undo) y COPIAS de los arrays,
    // porque el editor muta los arrays en el lugar (cmd.StartValues[0] += dx).
    internal sealed class CommandMemento
    {
        public OsuCommand Ref { get; }
        private readonly CommandType _type;
        private readonly int _easing, _start, _end;
        private readonly double[] _startVals, _endVals;
        private readonly string? _param;

        public CommandMemento(OsuCommand c)
        {
            Ref = c;
            _type = c.Type;
            _easing = c.Easing;
            _start = c.StartTime;
            _end = c.EndTime;
            _startVals = (double[])c.StartValues.Clone();
            _endVals = (double[])c.EndValues.Clone();
            _param = c.Parameter;
        }

        public void Restore()
        {
            Ref.Type = _type;
            Ref.Easing = _easing;
            Ref.StartTime = _start;
            Ref.EndTime = _end;
            Ref.StartValues = (double[])_startVals.Clone();
            Ref.EndValues = (double[])_endVals.Clone();
            Ref.Parameter = _param;
        }

        public bool SameAs(CommandMemento o) =>
            ReferenceEquals(Ref, o.Ref) && _type == o._type && _easing == o._easing &&
            _start == o._start && _end == o._end && _param == o._param &&
            _startVals.SequenceEqual(o._startVals) && _endVals.SequenceEqual(o._endVals);
    }

    internal sealed class LoopMemento
    {
        public OsuSpriteLoop Ref { get; }
        private readonly int _start, _count;
        private readonly List<CommandMemento> _cmds;

        public LoopMemento(OsuSpriteLoop l)
        {
            Ref = l;
            _start = l.StartTime;
            _count = l.LoopCount;
            _cmds = l.Commands.Select(c => new CommandMemento(c)).ToList();
        }

        public void Restore()
        {
            Ref.StartTime = _start;
            Ref.LoopCount = _count;
            SpriteSnapshot.RestoreCommandList(Ref.Commands, _cmds);
        }

        public bool SameAs(LoopMemento o) =>
            ReferenceEquals(Ref, o.Ref) && _start == o._start && _count == o._count &&
            SpriteSnapshot.SameCommands(_cmds, o._cmds);
    }

    internal sealed class TriggerMemento
    {
        public OsuSpriteTrigger Ref { get; }
        private readonly string _name;
        private readonly int _start, _end;
        private readonly int? _group;
        private readonly List<CommandMemento> _cmds;

        public TriggerMemento(OsuSpriteTrigger t)
        {
            Ref = t;
            _name = t.TriggerName;
            _start = t.StartTime;
            _end = t.EndTime;
            _group = t.Group;
            _cmds = t.Commands.Select(c => new CommandMemento(c)).ToList();
        }

        public void Restore()
        {
            Ref.TriggerName = _name;
            Ref.StartTime = _start;
            Ref.EndTime = _end;
            Ref.Group = _group;
            SpriteSnapshot.RestoreCommandList(Ref.Commands, _cmds);
        }

        public bool SameAs(TriggerMemento o) =>
            ReferenceEquals(Ref, o.Ref) && _name == o._name && _start == o._start &&
            _end == o._end && _group == o._group && SpriteSnapshot.SameCommands(_cmds, o._cmds);
    }

    // Foto completa de un sprite: propiedades, comandos, loops y triggers.
    // Es la pieza que hace "global" al undo: cualquier cosa que le hagas a UN sprite queda cubierta
    // sin tener que escribir un comando específico para cada operación.
    public sealed class SpriteSnapshot
    {
        public OsuSprite Sprite { get; }

        private readonly string _name, _filePath;
        private readonly double _x, _y, _scale, _rotation, _opacity;
        private readonly bool _visible;
        private readonly SpriteLayer _layer;
        private readonly SpriteOrigin _origin;
        private readonly int _startTime, _endTime;
        private readonly List<CommandMemento> _commands;
        private readonly List<LoopMemento> _loops;
        private readonly List<TriggerMemento> _triggers;

        private SpriteSnapshot(OsuSprite s)
        {
            Sprite = s;
            _name = s.Name; _filePath = s.FilePath;
            _x = s.X; _y = s.Y; _scale = s.Scale; _rotation = s.Rotation; _opacity = s.Opacity;
            _visible = s.Visible; _layer = s.Layer; _origin = s.Origin;
            _startTime = s.StartTime; _endTime = s.EndTime;
            _commands = s.Commands.Select(c => new CommandMemento(c)).ToList();
            _loops = s.Loops.Select(l => new LoopMemento(l)).ToList();
            _triggers = s.Triggers.Select(t => new TriggerMemento(t)).ToList();
        }

        public static SpriteSnapshot Capture(OsuSprite s) => new(s);

        public void Restore()
        {
            var s = Sprite;

            // Solo tocamos las propiedades que cambiaron, para no disparar PropertyChanged de más
            if (s.Name != _name) s.Name = _name;
            if (s.X != _x) s.X = _x;
            if (s.Y != _y) s.Y = _y;
            if (s.Scale != _scale) s.Scale = _scale;
            if (s.Rotation != _rotation) s.Rotation = _rotation;
            if (s.Opacity != _opacity) s.Opacity = _opacity;
            if (s.Visible != _visible) s.Visible = _visible;
            s.FilePath = _filePath;
            s.Layer = _layer;
            s.Origin = _origin;
            s.StartTime = _startTime;
            s.EndTime = _endTime;

            RestoreCommandList(s.Commands, _commands);

            foreach (var l in _loops) l.Restore();
            if (!s.Loops.SequenceEqual(_loops.Select(l => l.Ref)))
            {
                s.Loops.Clear();
                s.Loops.AddRange(_loops.Select(l => l.Ref));
            }

            foreach (var t in _triggers) t.Restore();
            if (!s.Triggers.SequenceEqual(_triggers.Select(t => t.Ref)))
            {
                s.Triggers.Clear();
                s.Triggers.AddRange(_triggers.Select(t => t.Ref));
            }
        }

        public bool SameAs(SpriteSnapshot o) =>
            ReferenceEquals(Sprite, o.Sprite) &&
            _name == o._name && _filePath == o._filePath &&
            _x == o._x && _y == o._y && _scale == o._scale && _rotation == o._rotation && _opacity == o._opacity &&
            _visible == o._visible && _layer == o._layer && _origin == o._origin &&
            _startTime == o._startTime && _endTime == o._endTime &&
            SameCommands(_commands, o._commands) &&
            _loops.Count == o._loops.Count && _loops.Zip(o._loops, (a, b) => a.SameAs(b)).All(b => b) &&
            _triggers.Count == o._triggers.Count && _triggers.Zip(o._triggers, (a, b) => a.SameAs(b)).All(b => b);

        // ── helpers compartidos ──
        internal static bool SameCommands(List<CommandMemento> a, List<CommandMemento> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!a[i].SameAs(b[i])) return false;
            return true;
        }

        // Restaura valores de cada comando y, solo si cambió el orden/contenido de la lista,
        // la reconstruye (así no se disparan CollectionChanged inútiles en ObservableCollection).
        internal static void RestoreCommandList(IList<OsuCommand> target, List<CommandMemento> mementos)
        {
            foreach (var m in mementos) m.Restore();

            bool same = target.Count == mementos.Count;
            if (same)
                for (int i = 0; i < mementos.Count; i++)
                    if (!ReferenceEquals(target[i], mementos[i].Ref)) { same = false; break; }
            if (same) return;

            target.Clear();
            foreach (var m in mementos) target.Add(m.Ref);
        }
    }
}