namespace OsuStoryBoardsEditor.Commands
{
    public interface IEditorCommand
    {
        // Texto corto que se muestra en la UI ("Mover sprite", "Eliminar keyframe"...)
        string Description { get; }
        void Execute();
        void Undo();
    }
}