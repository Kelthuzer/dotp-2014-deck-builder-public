# DotP 2014 Deck Builder — Modern

Unofficial community deck builder and modding utility for **Magic: The Gathering — Duels of the Planeswalkers 2014**.

This repository contains the original legacy builder together with the newer **Modern .NET 8 / WPF** implementation.

## Current Modern features

- Load cards and installed decks from an unpacked DotP 2014 workspace.
- Fast searchable/sortable card catalog.
- Multi-card preview and drag/drop deck editing.
- Create a deck from scratch or copy an installed deck.
- Deck Library with game names, technical names and deck-box previews.
- Deck information / unlock editing.
- Russian card text editing in unpacked XML.
- WAD unpacking and rebuilding tools.
- Build a custom deck WAD with the required support WADs.
- Custom deck-box artwork editor.
- Workspace variant/conflict tools.
- Manual exact-duplicate cleanup for unpacked deck XML and TDX artwork.

## Recommended workflow

1. Unpack your DotP 2014 game/content WADs into a workspace.
2. Open that workspace in the Modern builder.
3. Edit or create a deck.
4. Use **Package deck** to generate the deck and required support WADs.
5. Copy the generated files to the game directory and test them in-game.

The builder is designed around loose/unpacked game resources so changes can be inspected before they are packed back into WAD files.

## Building

The repository includes a GitHub Actions Windows build.

For the Modern application locally:

```powershell
dotnet publish "src\DeckBuilder.Modern\DeckBuilder.Modern.csproj" `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true
```

The Modern project targets .NET 8 and Windows/WPF.

## Legacy builder

The original .NET Framework builder remains under:

```text
DotP 2014 Deck Builder/
```

It is kept both for compatibility and as a reference for game-format behaviour while the Modern implementation replaces older workflows.

## Third-party and game resources

This project includes or references third-party components used by the original builder, including Gibbed libraries, SharpZipLib and Squish binaries. Rights and licenses for those components remain with their respective authors.

A small number of game-derived UI/reference resources are present where required for compatibility or editor rendering. Magic: The Gathering and related assets are property of their respective rights holders.

This is an unofficial community project and is not affiliated with or endorsed by Wizards of the Coast or Stainless Games.

## Repository history

This public repository intentionally begins from a clean snapshot. Private development history, local settings, credentials and build outputs are not included.

## Status

The Modern builder is actively being developed. Keep backups of your unpacked workspace and generated WADs while testing changes.
