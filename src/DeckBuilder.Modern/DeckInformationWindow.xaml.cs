using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DeckBuilder.Core.Models;
using DeckBuilder.Core.Services;
using DeckBuilder.GameData;

namespace DeckBuilder.Modern;

public partial class DeckInformationWindow : Window
{
    private readonly GameCardImageLoader? _imageLoader;
    private readonly string? _gameDirectory;
    private readonly GamePersonalityCatalogLoader _personalityLoader = new();
    private IReadOnlyList<InstalledPersonalityRecord>? _personalities;
    private AiPersonalityDefinition? _workingCustomPersonality;

    public string SelectedName { get; private set; } = string.Empty;
    public string SelectedDescription { get; private set; } = string.Empty;
    public string SelectedPersonality { get; private set; } = string.Empty;
    public string SelectedDeckBoxImage { get; private set; } = string.Empty;
    public string SelectedDeckBoxImageLocked { get; private set; } = "locked";
    public DeckAvailability SelectedAvailability { get; private set; } = DeckAvailability.AlwaysAvailable;
    public bool SelectedOverrideColours { get; private set; }
    public DeckColourFlags SelectedOverrideColour { get; private set; } = DeckColourFlags.NotDefined;
    public string SelectedCreatureSize { get; private set; } = "?";
    public string SelectedDeckSpeed { get; private set; } = "?";
    public string SelectedFlexibility { get; private set; } = "?";
    public string SelectedSynergy { get; private set; } = "?";
    public int SelectedIgnoreCmcOver { get; private set; } = -1;
    public int SelectedMinForests { get; private set; }
    public int SelectedMinIslands { get; private set; }
    public int SelectedMinMountains { get; private set; }
    public int SelectedMinPlains { get; private set; }
    public int SelectedMinSwamps { get; private set; }
    public int SelectedSpellsAsLand { get; private set; }

    public DeckInformationWindow(
        DeckDocument deck,
        GameCardImageLoader? imageLoader = null,
        string? gameDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(deck);
        _imageLoader = imageLoader;
        _gameDirectory = gameDirectory;
        _workingCustomPersonality = deck.CustomPersonality?.Clone();
        InitializeComponent();

        NameBox.Text = deck.Name;
        DescriptionBox.Text = deck.Description;
        PersonalityBox.Text = _workingCustomPersonality?.FileName ?? deck.Personality;
        DeckBoxImageBox.Text = deck.DeckBoxImage;
        DeckBoxImageLockedBox.Text = deck.DeckBoxImageLocked;
        PersonalityBrowseButton.IsEnabled = !string.IsNullOrWhiteSpace(_gameDirectory) && Directory.Exists(_gameDirectory);
        DeckBoxImageBrowseButton.IsEnabled = _imageLoader is not null;
        DeckBoxImageLockedBrowseButton.IsEnabled = _imageLoader is not null;
        AvailabilityBox.SelectedIndex = deck.Availability switch
        {
            DeckAvailability.AlwaysAvailable => 0,
            DeckAvailability.Locked => 1,
            DeckAvailability.NeverAvailable => 2,
            _ => 0
        };

        DeckColourFlags detectedColour = DeckColourCalculator.Calculate(deck);
        DeckColourFlags displayedColour = deck.OverrideColours ? deck.OverrideColour : detectedColour;
        ColourOverrideBox.IsChecked = deck.OverrideColours;
        OverrideBlackBox.IsChecked = DeckColourCalculator.Has(displayedColour, DeckColourFlags.Black);
        OverrideBlueBox.IsChecked = DeckColourCalculator.Has(displayedColour, DeckColourFlags.Blue);
        OverrideGreenBox.IsChecked = DeckColourCalculator.Has(displayedColour, DeckColourFlags.Green);
        OverrideRedBox.IsChecked = DeckColourCalculator.Has(displayedColour, DeckColourFlags.Red);
        OverrideWhiteBox.IsChecked = DeckColourCalculator.Has(displayedColour, DeckColourFlags.White);
        DetectedColoursText.Text = $"Detected from main deck and unlocks: {FormatColours(detectedColour)}.";
        UpdateColourControls();

        CreatureSizeBox.Text = deck.CreatureSize;
        DeckSpeedBox.Text = deck.DeckSpeed;
        FlexibilityBox.Text = deck.Flexibility;
        SynergyBox.Text = deck.Synergy;
        IgnoreCmcOverBox.Text = deck.IgnoreCmcOver.ToString(CultureInfo.InvariantCulture);
        MinForestsBox.Text = deck.MinForests.ToString(CultureInfo.InvariantCulture);
        MinIslandsBox.Text = deck.MinIslands.ToString(CultureInfo.InvariantCulture);
        MinMountainsBox.Text = deck.MinMountains.ToString(CultureInfo.InvariantCulture);
        MinPlainsBox.Text = deck.MinPlains.ToString(CultureInfo.InvariantCulture);
        MinSwampsBox.Text = deck.MinSwamps.ToString(CultureInfo.InvariantCulture);
        SpellsAsLandBox.Text = deck.NumberOfSpellsThatCountAsLand.ToString(CultureInfo.InvariantCulture);
        UidText.Text = deck.Uid < 0
            ? "Not assigned yet; game WAD export will choose a free custom slot."
            : deck.Uid.ToString(CultureInfo.InvariantCulture);
        ContentPackText.Text = deck.ContentPack <= 0
            ? "Not assigned yet; custom game WAD export currently uses content pack 1000."
            : deck.ContentPack.ToString(CultureInfo.InvariantCulture);
    }

    public void ApplyTo(DeckDocument deck)
    {
        ArgumentNullException.ThrowIfNull(deck);
        deck.Name = SelectedName;
        deck.Description = SelectedDescription;
        deck.Personality = SelectedPersonality;
        deck.CustomPersonality = _workingCustomPersonality is not null
            && SelectedPersonality.Equals(_workingCustomPersonality.FileName, StringComparison.OrdinalIgnoreCase)
                ? _workingCustomPersonality.Clone()
                : null;
        deck.DeckBoxImage = SelectedDeckBoxImage;
        deck.DeckBoxImageLocked = SelectedDeckBoxImageLocked;
        deck.Availability = SelectedAvailability;
        deck.OverrideColours = SelectedOverrideColours;
        deck.OverrideColour = SelectedOverrideColour;
        deck.CreatureSize = SelectedCreatureSize;
        deck.DeckSpeed = SelectedDeckSpeed;
        deck.Flexibility = SelectedFlexibility;
        deck.Synergy = SelectedSynergy;
        deck.IgnoreCmcOver = SelectedIgnoreCmcOver;
        deck.MinForests = SelectedMinForests;
        deck.MinIslands = SelectedMinIslands;
        deck.MinMountains = SelectedMinMountains;
        deck.MinPlains = SelectedMinPlains;
        deck.MinSwamps = SelectedMinSwamps;
        deck.NumberOfSpellsThatCountAsLand = SelectedSpellsAsLand;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        string name = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, "Enter a deck name before applying the changes.", "Deck name required",
                MessageBoxButton.OK, MessageBoxImage.Information);
            NameBox.Focus();
            NameBox.SelectAll();
            return;
        }

        if (!TryReadInteger(IgnoreCmcOverBox, "Ignore CMC over", -1, out int ignoreCmcOver)
            || !TryReadInteger(MinForestsBox, "Minimum Forests", 0, out int minForests)
            || !TryReadInteger(MinIslandsBox, "Minimum Islands", 0, out int minIslands)
            || !TryReadInteger(MinMountainsBox, "Minimum Mountains", 0, out int minMountains)
            || !TryReadInteger(MinPlainsBox, "Minimum Plains", 0, out int minPlains)
            || !TryReadInteger(MinSwampsBox, "Minimum Swamps", 0, out int minSwamps)
            || !TryReadInteger(SpellsAsLandBox, "Spells that count as land", 0, out int spellsAsLand))
        {
            return;
        }

        SelectedName = name;
        SelectedDescription = DescriptionBox.Text.Trim();
        SelectedPersonality = PersonalityBox.Text.Trim();
        SelectedDeckBoxImage = DeckBoxImageBox.Text.Trim();
        SelectedDeckBoxImageLocked = DeckBoxImageLockedBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(SelectedDeckBoxImageLocked))
        {
            SelectedDeckBoxImageLocked = "locked";
        }

        SelectedAvailability = AvailabilityBox.SelectedIndex switch
        {
            1 => DeckAvailability.Locked,
            2 => DeckAvailability.NeverAvailable,
            _ => DeckAvailability.AlwaysAvailable
        };
        SelectedOverrideColours = ColourOverrideBox.IsChecked == true;
        SelectedOverrideColour = DeckColourCalculator.FromSelections(
            OverrideBlackBox.IsChecked == true,
            OverrideBlueBox.IsChecked == true,
            OverrideGreenBox.IsChecked == true,
            OverrideRedBox.IsChecked == true,
            OverrideWhiteBox.IsChecked == true);
        SelectedCreatureSize = NormalizeStatistic(CreatureSizeBox.Text);
        SelectedDeckSpeed = NormalizeStatistic(DeckSpeedBox.Text);
        SelectedFlexibility = NormalizeStatistic(FlexibilityBox.Text);
        SelectedSynergy = NormalizeStatistic(SynergyBox.Text);
        SelectedIgnoreCmcOver = ignoreCmcOver;
        SelectedMinForests = minForests;
        SelectedMinIslands = minIslands;
        SelectedMinMountains = minMountains;
        SelectedMinPlains = minPlains;
        SelectedMinSwamps = minSwamps;
        SelectedSpellsAsLand = spellsAsLand;
        DialogResult = true;
    }

    private async void PersonalityBrowse_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<InstalledPersonalityRecord>? personalities = await EnsurePersonalitiesAsync();
        if (personalities is null)
        {
            return;
        }

        PersonalityPickerWindow dialog = new(personalities, _imageLoader, PersonalityBox.Text)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true && dialog.SelectedPersonality is not null)
        {
            _workingCustomPersonality = null;
            PersonalityBox.Text = dialog.SelectedPersonality.FileName;
        }
    }

    private async void PersonalityEdit_Click(object sender, RoutedEventArgs e)
    {
        AiPersonalityDefinition seed;
        if (_workingCustomPersonality is not null)
        {
            seed = _workingCustomPersonality.Clone();
        }
        else
        {
            InstalledPersonalityRecord? installed = null;
            if (!string.IsNullOrWhiteSpace(_gameDirectory) && Directory.Exists(_gameDirectory))
            {
                IReadOnlyList<InstalledPersonalityRecord>? personalities = await EnsurePersonalitiesAsync(showEmptyMessage: false);
                installed = personalities?.FirstOrDefault(personality =>
                    personality.FileName.Equals(PersonalityBox.Text.Trim(), StringComparison.OrdinalIgnoreCase));
            }

            seed = installed is null
                ? CreateBlankCustomPersonality()
                : CreateCustomCopy(installed);
        }

        PersonalityEditorWindow dialog = new(seed, _imageLoader) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _workingCustomPersonality = dialog.Result.Clone();
            PersonalityBox.Text = _workingCustomPersonality.FileName;
        }
    }

    private async Task<IReadOnlyList<InstalledPersonalityRecord>?> EnsurePersonalitiesAsync(bool showEmptyMessage = true)
    {
        if (string.IsNullOrWhiteSpace(_gameDirectory) || !Directory.Exists(_gameDirectory))
        {
            if (showEmptyMessage)
            {
                MessageBox.Show(this,
                    "Load the Magic 2014 game folder before browsing AI personalities.",
                    "Game data required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            return null;
        }

        PersonalityBrowseButton.IsEnabled = false;
        try
        {
            if (_personalities is null)
            {
                GamePersonalityCatalogLoadResult result = await _personalityLoader.LoadAsync(_gameDirectory);
                _personalities = result.Personalities;
            }

            if (_personalities.Count == 0 && showEmptyMessage)
            {
                MessageBox.Show(this,
                    "No AI personalities were found in the loaded Magic 2014 data.",
                    "No personalities",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            return _personalities.Count == 0 ? null : _personalities;
        }
        catch (Exception exception)
        {
            MessageBox.Show(this,
                $"Could not load AI personalities.\n\n{exception.Message}",
                "Personality picker",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return null;
        }
        finally
        {
            PersonalityBrowseButton.IsEnabled = true;
        }
    }

    private AiPersonalityDefinition CreateBlankCustomPersonality()
    {
        string displayName = string.IsNullOrWhiteSpace(NameBox.Text)
            ? "New Personality"
            : $"{NameBox.Text.Trim()} AI";
        return CreateSeparateIdentifiers(new AiPersonalityDefinition { DisplayName = displayName });
    }

    private static AiPersonalityDefinition CreateCustomCopy(InstalledPersonalityRecord installed)
    {
        AiPersonalityDefinition definition = new()
        {
            DisplayName = string.IsNullOrWhiteSpace(installed.DisplayName)
                ? "New Personality"
                : installed.DisplayName,
            LargeAvatarImage = installed.LargeAvatarImage,
            SmallAvatarImage = installed.SmallAvatarImage,
            SmallAvatarLockedImage = installed.SmallAvatarLockedImage,
            LobbyImage = installed.LobbyImage,
            Music = installed.Music
        };
        return CreateSeparateIdentifiers(definition);
    }

    private static AiPersonalityDefinition CreateSeparateIdentifiers(AiPersonalityDefinition definition)
    {
        string code = AiPersonalityXmlSerializer.Codify(definition.DisplayName);
        definition.FileName = $"D14_PERSONALITY_{code}_CUSTOM.XML";
        definition.NameTag = $"PLAYER_NAME_{code}_CUSTOM";
        return AiPersonalityXmlSerializer.NormalizeIdentifiers(definition);
    }

    private void DeckBoxImageBrowse_Click(object sender, RoutedEventArgs e) =>
        PickDeckImage(DeckBoxImageBox, "Choose deck-box image");

    private void DeckBoxImageLockedBrowse_Click(object sender, RoutedEventArgs e) =>
        PickDeckImage(DeckBoxImageLockedBox, "Choose locked deck-box image");

    private void PickDeckImage(TextBox target, string title)
    {
        if (_imageLoader is null)
        {
            MessageBox.Show(this,
                "Load the Magic 2014 game folder before browsing built-in deck images.",
                "Game data required",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        GameImagePickerWindow dialog = new(_imageLoader, GameImageKind.Deck, title, target.Text)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
        {
            target.Text = dialog.SelectedImageId;
        }
    }

    private void ColourOverrideBox_Changed(object sender, RoutedEventArgs e) => UpdateColourControls();

    private void UpdateColourControls()
    {
        if (ColourOptionsPanel is not null)
        {
            ColourOptionsPanel.IsEnabled = ColourOverrideBox.IsChecked == true;
        }
    }

    private bool TryReadInteger(TextBox box, string fieldName, int minimum, out int value)
    {
        if (int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
            && value >= minimum)
        {
            return true;
        }

        MessageBox.Show(this, $"{fieldName} must be an integer greater than or equal to {minimum}.",
            "Invalid deck information", MessageBoxButton.OK, MessageBoxImage.Information);
        box.Focus();
        box.SelectAll();
        return false;
    }

    private static string NormalizeStatistic(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "?" : value.Trim();

    private static string FormatColours(DeckColourFlags colour)
    {
        List<string> values = new();
        if (DeckColourCalculator.Has(colour, DeckColourFlags.Black)) values.Add("Black");
        if (DeckColourCalculator.Has(colour, DeckColourFlags.Blue)) values.Add("Blue");
        if (DeckColourCalculator.Has(colour, DeckColourFlags.Green)) values.Add("Green");
        if (DeckColourCalculator.Has(colour, DeckColourFlags.Red)) values.Add("Red");
        if (DeckColourCalculator.Has(colour, DeckColourFlags.White)) values.Add("White");
        return values.Count == 0 ? "Colourless" : string.Join(", ", values);
    }
}
