# Blockwoche 3.1: Radiosender

WPF desktop app that plans and plays a radio program for a chosen number of full hours: random music from a SQLite library, news at :00 and :30, and ad and moderator blocks in between. Listeners can request a song, which replaces an upcoming item while the broadcast runs.

## Requirements

- Windows
- .NET SDK 10
- An audio output device for playback

## Build and run

```powershell
dotnet build Radiosender.slnx
dotnet run --project src/Radiosender
dotnet test Radiosender.slnx
```

On first start the app creates its data under `%LOCALAPPDATA%\Radiosender`:

- `radiosender.db`: the SQLite music library
- `Music\`: copies of uploaded files and downloaded Openverse tracks

## Usage

1. **Import**: one page for both ways of adding music. Every result, added or failed, is listed under "Added in this session", with a link to the Library.
   - **From computer**: drop MP3 or WAV files on the page or click **Choose files...**; several files at once are fine. The app copies each file into the music folder, reads title and artist from the file tags and measures the duration. A file that fails is reported and the others still get imported.
   - **Openverse**: search the [Openverse](https://openverse.org) audio catalog, preview a result and import it with **Import to library** or a double-click. The app downloads the file and stores its license and attribution with the song. Results that are already in the library or have no duration are marked and cannot be imported.
2. **Library**: lists and searches all songs. Edit title, artist, license and attribution, or delete a song.
3. **Schedule**:
   1. Enter the number of hours (1 to 24) and click **Create schedule**. The plan starts at the next full hour.
   2. The timeline shows every item with start time, type (song, news, ad, moderator), title and duration.
   3. **Start** puts the plan on air on the real clock. Before the planned start the now-playing card shows when the broadcast begins; if the plan has already begun, playback joins at the next item. **Stop** ends it. The now-playing card shows the current item, its progress and the volume.
   4. **Song request**: choose a song, select an upcoming row in the timeline and click **Play instead of selected**. This also works while the broadcast is running.

News, ads and moderator talk are placeholders: the app shows them in the timeline and keeps silent for their duration.

## How the schedule is built

`ScheduleGenerator` plans in half-hour slots:

- Each slot starts with 5 minutes of news at :00 or :30.
- The rest of the slot is random songs plus 2 to 10 minutes of ads and moderator talk. Ten minutes per half hour keeps every hour at 20 minutes or less.
- Ads and moderator items alternate, and one ad item lasts at most 5 minutes, so there is never a long block of ads.
- The filler sits between the songs and the slot always ends with a song, so that song ends exactly when the next news starts. Songs are never cut. When the filler does not add up, the last placeholder is shortened.

During playback `PlaybackEngine` starts every item at its planned time. If the previous item ended early, it waits; if it ran late, the next item starts right after it and is never cut. The news therefore comes at the real :00 and :30.

A song request (`ScheduleGenerator.Replace`) puts the song at the selected position and rebuilds only the rest of that half hour with the same rules. Earlier items and later half hours stay as they are, so the next news keeps its time. News items cannot be replaced, and a song that is longer than the time left before the next news is rejected.

## Architecture

The app follows the MVC pattern and uses [WPF UI](https://github.com/lepoco/wpfui) (lepoco/wpfui) for the Fluent interface.

| Layer | Folder | Content |
| --- | --- | --- |
| Model | `src/Radiosender/Models` | `Song`, `ScheduleItem`, `AdSpot`, `ModeratorSegment`, `OpenverseTrack` |
| View | `src/Radiosender/Views` | `MainWindow` with navigation, one page per feature, `SongEditWindow` |
| Controller | `src/Radiosender/Controllers` | `LibraryController`, `UploadController`, `OpenverseController`, `ScheduleController` |
| Services | `src/Radiosender/Services` | database, audio, schedule logic and API access used by the controllers |

Views only handle input and display. They call a controller, and the controller works with the models and services. `App` creates the shared `SongRepository` and one `ScheduleController` at startup. A running broadcast therefore keeps playing when the user switches pages.

Main services:

- `SongRepository`: CRUD for songs in SQLite, with parameterized queries
- `SongImportService`: copies an uploaded file into the music folder, reads its tags with TagLibSharp and its duration with NAudio
- `OpenverseClient`: anonymous search and download through the Openverse API
- `MusicPlayer`: the audio player as a singleton (`MusicPlayer.Instance`, private constructor). It plays files with NAudio.
- `ScheduleGenerator`: builds the plan and handles song requests
- `PlaybackEngine`: runs the plan item by item on a background thread and plays songs through `MusicPlayer`

Tests are in `tests/Radiosender.Tests` (xUnit). They cover the repository, import, Openverse parsing, the generator rules, the playback engine and the schedule controller.

## Openverse attribution

Music search uses the [Openverse API](https://api.openverse.org/v1/) without an API key. Openverse lists openly licensed works (Creative Commons and public domain) from many sources. The rights stay with the creators.

For each downloaded track the app stores the license (for example `CC BY 4.0`) and the attribution text from Openverse. The Library page shows both. Anyone who plays or shares these tracks must keep this attribution, as the licenses require.

## Assignment checklist

| Requirement | Where | Status |
| --- | --- | --- |
| Plays random music for X hours; only full hours, entered by the user | Schedule page, hours 1 to 24; `ScheduleGenerator.PickSongs` | Done |
| 2 × 5 minutes of news per hour, at :00 and :30 | `ScheduleGenerator.Generate`; `PlaybackEngine` waits for each planned start | Done |
| News comes after a song, no song is cut | Slot ends with a song that ends at the news start; tests in `ScheduleGeneratorTests` | Done |
| Up to 20 minutes of ads and moderator talk per hour | At most 10 minutes per half hour | Done |
| Ads never 20 minutes in a row | Ads alternate with moderator items, one ad item lasts at most 5 minutes | Done |
| Remaining time filled with music | Songs fill each slot | Done |
| Music comes from a SQLite database | `SongRepository`, `radiosender.db` | Done |
| Library can be extended at any time: add, edit, delete | Library page and `SongEditWindow` | Done |
| Add by simple upload | Import page, "From computer" (MP3, WAV, several files, drag and drop) | Done |
| Add through the Openverse API | Import page, "Openverse" | Done |
| Music player is a singleton class | `MusicPlayer.Instance` | Done |
| Hot swap: a requested song replaces an item during the planned run, everything after adapts | Song request on the Schedule page; `ScheduleGenerator.Replace`, `PlaybackEngine.ReplaceUpcoming` | Done |
| News and ad blocks are shown | Timeline with type labels, now-playing card | Done |
| The player really plays music | `MusicPlayer` with NAudio | Done |
| C#, WPF with MVC, WPF UI | .NET 10, folders above, WPF UI 4.3 | Done |

## Known limitations

- **Waiting for the start**: a new plan starts at the next full hour, so after **Start** the app can stay silent for up to an hour.
- **Late items**: if a song plays longer than its stored duration, the following items start late by the same amount. Nothing is cut to catch up; the delay only disappears when a later item ends early.
- **Skipped songs**: a song that cannot be played (missing file, unsupported format) is skipped with a warning, and the station stays silent until the next planned item.
- **Deleting a song** removes it from the library but keeps its file in the `Music` folder.
- **Placeholders** have no audio. The last filler of a half hour can be very short (for example 2 seconds) because it is cut to fit.
