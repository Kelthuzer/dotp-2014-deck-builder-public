using System.Runtime.CompilerServices;
using DeckBuilder.Core.Models;

internal static class DeckDefaultPersonalityChecks
{
    [ModuleInitializer]
    internal static void Initialize()
    {
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
