DotP 2014: Deck Builder is written by Riiak Shi Nal

[See author's wiki](http://mtg.dragonanime.org/index.php?title=DotP_2014:_Deck_Builder)

[See Forum page](https://www.slightlymagic.net/forum/viewtopic.php?f=99&t=10999)

## Modernized editor

The editor keeps the original DotP 2014 deck and WAD formats while improving the application itself:

- readable light WinForms theme with black text;
- instant multi-word card search by name, filename, type, expansion, or artist (`Ctrl+F`, `Esc` to clear);
- multi-select cards with `Ctrl`/`Shift`, then drag the whole selection into the deck or either unlock list;
- drag cards between deck sections and reorder one or several unlocks with a visible drop marker;
- remove selected deck or unlock entries with `Delete`;
- indexed card/image lookup during game-data loading;
- safe ownership and cleanup of cached preview/casting-cost images;
- indexed Forge `.dck` imports.

The released application still targets .NET Framework 4.0 Client Profile and builds with the original x86/x64 configurations in Visual Studio.

## .NET 8 rewrite

The replacement is being developed incrementally in `main` while the released WinForms application remains usable. The first checkpoint lives in `src/DeckBuilder.Core` and contains UI-independent deck behavior:

- DotP card-reference parsing and formatting (`CARD`, `CARD#`, `CARD@2`, `CARD#@2`);
- main-deck quantity merging and separate ordered unlock entries;
- the ten-card promo-unlock limit;
- add, remove, move, and reorder operations;
- pre-indexed multi-word card search.

`tests/DeckBuilder.Core.Checks` is a dependency-free compatibility harness. GitHub Actions runs it with .NET 8 before publishing all Windows archives.

### Modern x64 preview

The `continuous` release also contains `dotp-2014-deck-builder-modern-x64.zip`, a self-contained .NET 8 WPF application. It does not require a separate .NET installation and currently provides:

- direct card-catalog loading from Magic 2014 packed `.wad` files and unpacked WAD directories;
- lazy card-art preview directly from packed or unpacked TDX images, with an in-memory cache;
- a light, high-contrast interface with black text at every control level;
- indexed multi-word card search;
- main deck, ordered regular unlocks, and ordered promo unlocks;
- multi-selection, buttons, keyboard deletion, and drag-and-drop between sections;
- portable `.dotpdeck` project save/open;
- DotP deck XML import/export, including case-insensitive and namespaced legacy XML;
- full card preview with game art, frames, mana, rules text, rarity, artist, and power/toughness;
- creation of a new project from any installed game deck, including regular and promo unlocks;
- verified atomic game WAD export with backup and content-pack enabler generation;
- deck-information editing for name, description, personality ID, deck-box image references, three-state availability, deck statistics, land configuration, and in-game colour overrides;
- searchable deck-box image selection with live previews loaded directly from installed packed or unpacked game data;
- searchable selection of installed AI personality XMLs with source information and avatar preview;
- non-destructive custom AI personality editing: copy an installed personality, change its name/avatar references/music, keep it inside `.dotpdeck`/unfinished XML, and bundle its separate personality XML plus name string into the exported WAD;
- searchable music-mix selection from the installed `Audio/Music/*.mp3` files.

The original x86/x64 archives remain available while custom bitmap/TDX personality and deck-box image building and the remaining advanced legacy dialogs are moved to the new engine.
