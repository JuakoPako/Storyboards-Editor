using OsuStoryBoardsEditor.Models;

namespace OsuStoryBoardsEditor.Commands
{
    // Historial global de la aplicación. Tres formas de registrar un cambio:
    //
    //  1) Transacción (la más usada): fotografía uno o más sprites, dejás que el código mute lo que quiera,
    //     y al cerrar compara. Si algo cambió registra UN paso deshacible; si no, no registra nada.
    //         using (undo.BeginTransaction("Mover sprite", sprite)) { sprite.X = 10; }
    //     También sirve para drags: abrir en MouseDown, Dispose() en MouseUp.
    //
    //  2) Execute(cmd): ejecuta un comando y lo registra (agregar/eliminar sprites, etc.).
    //
    //  3) Record(cmd): registra un comando cuyo efecto YA fue aplicado.
    //
    // BeginGroup(desc) agrupa todo lo que se registre adentro como un único paso.
    public sealed class UndoRedoManager
    {
        private readonly List<IEditorCommand> _undo = new();
        private readonly List<IEditorCommand> _redo = new();
        private readonly List<Transaction> _openTransactions = new();
        private readonly Stack<GroupScope> _groups = new();
        private IEditorCommand? _savedPoint;
        private int _replaying;

        // Cada paso guarda fotos de los sprites tocados; este tope evita que el historial crezca sin límite.
        public int MaxHistory { get; set; } = 300;

        // Cambió el historial por cualquier motivo (registrar, deshacer, rehacer, limpiar, marcar guardado).
        public event Action? StateChanged;
        // Se ejecutó un undo/redo: (descripción del paso, true si fue deshacer). Acá la UI se refresca entera.
        public event Action<string, bool>? Replayed;

        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;
        public string? UndoDescription => CanUndo ? _undo[^1].Description : null;
        public string? RedoDescription => CanRedo ? _redo[^1].Description : null;

        // Hay cambios desde el último MarkSaved()/Clear() (volver con undo al punto guardado lo deja limpio).
        public bool IsDirty => !ReferenceEquals(_undo.Count > 0 ? _undo[^1] : null, _savedPoint);

        // ── Registro ─────────────────────────────────────
        public void Execute(IEditorCommand command)
        {
            command.Execute();
            Record(command);
        }

        public void Record(IEditorCommand command)
        {
            if (_replaying > 0) return; // los setters disparados por un undo no generan pasos nuevos

            if (_groups.Count > 0)
            {
                _groups.Peek().Commands.Add(command);
                return;
            }

            _undo.Add(command);
            if (_undo.Count > MaxHistory) _undo.RemoveAt(0);
            _redo.Clear(); // una acción nueva invalida el redo
            StateChanged?.Invoke();
        }

        public Transaction BeginTransaction(string description, params OsuSprite?[] sprites)
        {
            if (_replaying > 0) return new Transaction(null, description, new List<SpriteSnapshot>());

            var before = sprites
                .Where(s => s != null)
                .Distinct()
                .Select(s => SpriteSnapshot.Capture(s!))
                .ToList();

            var tx = new Transaction(this, description, before);
            _openTransactions.Add(tx);
            return tx;
        }

        public IDisposable BeginGroup(string description)
        {
            var g = new GroupScope(this, description);
            _groups.Push(g);
            return g;
        }

        // ── Deshacer / rehacer ───────────────────────────
        public void Undo()
        {
            CommitOpenTransactions(); // Ctrl+Z en medio de un drag: primero cerramos el paso en curso
            if (!CanUndo) return;

            var cmd = _undo[^1];
            Replay(cmd.Undo);           // si lanza, las pilas quedan intactas
            _undo.RemoveAt(_undo.Count - 1);
            _redo.Add(cmd);

            StateChanged?.Invoke();
            Replayed?.Invoke(cmd.Description, true);
        }

        public void Redo()
        {
            CommitOpenTransactions();
            if (!CanRedo) return;

            var cmd = _redo[^1];
            Replay(cmd.Execute);
            _redo.RemoveAt(_redo.Count - 1);
            _undo.Add(cmd);

            StateChanged?.Invoke();
            Replayed?.Invoke(cmd.Description, false);
        }

        public void Clear()
        {
            _undo.Clear();
            _redo.Clear();
            _savedPoint = null;
            StateChanged?.Invoke();
        }

        public void MarkSaved()
        {
            _savedPoint = _undo.Count > 0 ? _undo[^1] : null;
            StateChanged?.Invoke();
        }

        // ── Internos ─────────────────────────────────────
        private void Replay(Action action)
        {
            _replaying++;
            try { action(); }
            finally { _replaying--; }
        }

        private void CommitOpenTransactions()
        {
            foreach (var t in _openTransactions.ToList()) t.Commit();
        }

        private void Finish(Transaction tx, bool commit)
        {
            _openTransactions.Remove(tx);

            if (!commit)
            {
                Replay(() => { foreach (var b in tx.Before) b.Restore(); });
                return;
            }

            var cmds = new List<IEditorCommand>();
            foreach (var before in tx.Before)
            {
                var after = SpriteSnapshot.Capture(before.Sprite);
                if (!before.SameAs(after))
                    cmds.Add(new SpriteStateCommand(tx.Description, before, after));
            }
            if (cmds.Count == 0) return; // no cambió nada: no ensuciamos el historial

            Record(cmds.Count == 1 ? cmds[0] : new CompositeCommand(tx.Description, cmds));
        }

        // ── Transacción ──────────────────────────────────
        public sealed class Transaction : IDisposable
        {
            private readonly UndoRedoManager? _owner;
            private bool _finished;

            internal string Description { get; }
            internal List<SpriteSnapshot> Before { get; }

            internal Transaction(UndoRedoManager? owner, string description, List<SpriteSnapshot> before)
            {
                _owner = owner;
                Description = description;
                Before = before;
            }

            // Cierra la transacción y registra el paso si hubo cambios.
            public void Commit()
            {
                if (_finished) return;
                _finished = true;
                _owner?.Finish(this, commit: true);
            }

            // Cierra la transacción, DEVUELVE los sprites a como estaban y no registra nada (p.ej. Esc en un drag).
            // La UI la tiene que refrescar quien llame.
            public void Rollback()
            {
                if (_finished) return;
                _finished = true;
                _owner?.Finish(this, commit: false);
            }

            public void Dispose() => Commit();
        }

        private sealed class GroupScope : IDisposable
        {
            private readonly UndoRedoManager _owner;
            private readonly string _description;
            private bool _disposed;
            public List<IEditorCommand> Commands { get; } = new();

            public GroupScope(UndoRedoManager owner, string description)
            {
                _owner = owner;
                _description = description;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                if (_owner._groups.Count > 0 && ReferenceEquals(_owner._groups.Peek(), this))
                    _owner._groups.Pop();
                if (Commands.Count == 0) return;
                _owner.Record(Commands.Count == 1 ? Commands[0] : new CompositeCommand(_description, Commands));
            }
        }
    }
}