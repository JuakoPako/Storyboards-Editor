namespace OsuStoryBoardsEditor.Models
{
    // Inmutable a propósito: para el undo basta guardar la referencia
    public sealed record TextSpec(
        string Text,
        string FontFamily,
        float FontSize = 48f,
        bool Bold = false,
        bool Italic = false,
        byte R = 255, byte G = 255, byte B = 255);
}