using System.Runtime.CompilerServices;
using DeckBuilder.Core.Models;

internal static class DeckDefaultPersonalityChecks
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (!DeckDocument.DefaultPersonality.Equals("D14_DEFAULT_PERSONALITY.XML", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The built-in fallback must reference the stock D14 default personality that exists in the game data.");

        DeckDocument deck = new();
        if (!deck.Personality.Equals(DeckDocument.DefaultPersonality, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A new deck must always have a built-in AI personality.");

        deck.Personality = string.Empty;
        if (!deck.Personality.Equals(DeckDocument.DefaultPersonality, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A restored/exported blank personality must fall back to the built-in AI personality.");

        deck.Personality = "CUSTOM_PERSONALITY.XML";
        if (!deck.Personality.Equals("CUSTOM_PERSONALITY.XML", StringComparison.Ordinal))
            throw new InvalidOperationException("An explicitly selected AI personality must override the default fallback.");

        Console.WriteLine("PASS: default AI personality fallback");
    }
}
