namespace DeckBuilder.Core.Models;

public sealed class AiPersonalityDefinition
{
    public string FileName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = "New Personality";

    public string NameTag { get; set; } = string.Empty;

    public string LargeAvatarImage { get; set; } = string.Empty;

    public string SmallAvatarImage { get; set; } = string.Empty;

    public string SmallAvatarLockedImage { get; set; } = string.Empty;

    public string LobbyImage { get; set; } = string.Empty;

    public string Music { get; set; } = string.Empty;

    public AiPersonalityDefinition Clone() => new()
    {
        FileName = FileName,
        DisplayName = DisplayName,
        NameTag = NameTag,
        LargeAvatarImage = LargeAvatarImage,
        SmallAvatarImage = SmallAvatarImage,
        SmallAvatarLockedImage = SmallAvatarLockedImage,
        LobbyImage = LobbyImage,
        Music = Music
    };
}
