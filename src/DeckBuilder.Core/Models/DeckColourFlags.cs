namespace DeckBuilder.Core.Models;

[Flags]
public enum DeckColourFlags
{
    NotDefined = 0,
    Colourless = 0x01,
    Black = 0x02,
    Blue = 0x04,
    Green = 0x08,
    Red = 0x10,
    White = 0x20,
    MultiColour = 0x40
}
