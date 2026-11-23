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
- `settings.json`: the values from the Settings page
- `Music\`: copies of uploaded files and downloaded Openverse tracks

## Usage

The interface is in German. This section names the controls as they appear on screen.

1. **Import**: one page for both ways of adding music. Every result, added or failed, is listed under "Importiert" as soon as there is one, with a link to the library.
   - **Vom Computer**: drop MP3 or WAV files on the page or click **Dateien auswählen...**; several files at once are fine. The app copies each file into the music folder, reads title and artist from the file tags and measures the duration. A file that fails is reported and the others still get imported.
   - **Openverse**: search the [Openverse](https://openverse.org) audio catalog, preview a result and import it with **In Bibliothek importieren** or a double-click. Select several results with Ctrl or Shift to import them in one go; they are downloaded one after the other. The app downloads the file and stores its license and attribution with the song. Results that are already in the library or have no duration cannot be imported; the tooltip of the disabled button says why.
2. **Bibliothek**: lists and searches all songs. Edit title, artist, license and attribution, or delete a song.
3. **Sendeplan** (the start page):
   1. Choose the start (one of the next 12 full hours) and the number of hours (1 to 24), then click **Plan erstellen**.
      - **Unbegrenzt** plans without an end: the timeline shows the next 5 hours, and while the broadcast runs one more hour is added whenever less than 5 hours are left.
      - **Jetzt starten** ignores the start time, plans from this moment and goes on air at once. Music leads up to the first news at the next :00 or :30; the chosen hours count from there.
   2. The timeline shows every item with start time, type (song, news, ad, moderator), title and duration.
   3. **Start** puts the plan on air on the real clock. Before the planned start the now-playing card shows when the broadcast begins; if the plan has already begun, playback joins at the next item. The same button then reads **Stopp** and ends it. The now-playing card shows the current item, its progress and the volume.
   4. **Edit the timeline**: select an upcoming row and use the buttons above the list to move it up or down, edit it, or remove it (trash icon). News, ad and moderator items have no title until you give them one; news and moderator items can also change their length. A double-click opens the editor too. Every item after a change follows on without a gap. The item on air can be edited too, but not moved or removed; **Jetzt beenden** in the now-playing card ends it at once and starts the next item.
   5. **Speaking reminder**: 30 seconds before a news or moderator item goes on air, a dialog in the middle of the screen reminds you on any page and stays until you click **OK**.
   6. **Songwunsch**: choose a song, select an upcoming row in the timeline and click **Auswahl ersetzen**. This also works while the broadcast is running.

4. **Einstellungen** (bottom left): switch the speaking alert on or off for news and for moderator talk, set how many seconds before the start it appears and whether it plays a bing sound. Set the minutes per hour for news, moderator talk and ads (defaults 10, 10 and 20); together they can take at most 60 minutes. **Zurücksetzen** puts the default values into the form, **Speichern** stores them in `settings.json` next to the database. Alerts change at once; minutes per hour apply to the next schedule.

News, ads and moderator talk are placeholders: the app shows them in the timeline and keeps silent for their duration, so the speaker can talk live.

## How the schedule is built

`ScheduleGenerator` plans in half-hour slots:

- Each slot starts with news at :00 or :30; the news minutes per hour are split over the two.
- The rest of the slot is random songs plus ads and moderator talk. The minutes per hour from the settings are targets: the last song has to end exactly when the next news starts and songs are never cut, so the ad and moderator time is what the songs leave. The generator tries several hundred random song picks per slot and keeps the one that leaves closest to the target. What the :00 slot misses, the :30 slot makes up for, so the hour comes out within a few seconds of the targets with a normal library.
- Ads are cut into items of at most 5 minutes. Each ad item follows a moderator item, and the breaks sit between the songs, so ads never run back to back as long as there is moderator time or a song to put between them.
- The slot always ends with a song. If the targets take the whole hour, the slot has no songs.

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

For each downloaded track the app stores the license (for example `CC BY 4.0`) and the attribution text from Openverse. Both are shown in the edit dialog of a song (**Bearbeiten** on the library page). Anyone who plays or shares these tracks must keep this attribution, as the licenses require.

## Assignment checklist

| Requirement | Where | Status |
| --- | --- | --- |
| Plays random music for X hours; only full hours, entered by the user | Schedule page, hours 1 to 24 or unlimited; `ScheduleGenerator.PickSongs` | Done |
| 2 × 5 minutes of news per hour, at :00 and :30 | `ScheduleGenerator.Generate`; `PlaybackEngine` waits for each planned start | Done |
| News comes after a song, no song is cut | Slot ends with a song that ends at the news start; tests in `ScheduleGeneratorTests` | Done |
| Up to 20 minutes of ads and moderator talk per hour | Targets in the settings. The defaults are 20 minutes of ads plus 10 of moderator talk, which is 30; set them to 20 together to meet this rule | Configurable |
| Ads never 20 minutes in a row | One ad item lasts at most 5 minutes and follows a moderator item | Done |
| Remaining time filled with music | Songs fill each slot | Done |
| Music comes from a SQLite database | `SongRepository`, `radiosender.db` | Done |
| Library can be extended at any time: add, edit, delete | Library page and `SongEditWindow` | Done |
| Add by simple upload | Import page, "Vom Computer" (MP3, WAV, several files, drag and drop) | Done |
| Add through the Openverse API | Import page, "Openverse" | Done |
| Music player is a singleton class | `MusicPlayer.Instance` | Done |
| Hot swap: a requested song replaces an item during the planned run, everything after adapts | Song request on the Schedule page; `ScheduleGenerator.Replace`, `PlaybackEngine.ReplaceUpcoming` | Done |
| News and ad blocks are shown | Timeline with type labels, now-playing card | Done |
| The player really plays music | `MusicPlayer` with NAudio | Done |
| C#, WPF with MVC, WPF UI | .NET 10, folders above, WPF UI 4.3 | Done |

## Known limitations

- **Waiting for the start**: a plan made with **Plan erstellen** starts at a full hour, so after **Start** the app stays silent until then. **Jetzt starten** avoids the wait.
- **Unlimited plans** keep every played item in the timeline, so the list grows by about 20 rows per hour.
- **Manual edits** shift every later item, so after moving, removing or resizing an item the news can leave :00 and :30 and the plan can end earlier or later.
- **Late items**: if a song plays longer than its stored duration, the following items start late by the same amount. Nothing is cut to catch up; the delay only disappears when a later item ends early.
- **Skipped songs**: a song that cannot be played (missing file, unsupported format) is skipped with a warning, and the station stays silent until the next planned item.
- **Deleting a song** removes it from the library but keeps its file in the `Music` folder.
- **Placeholders** have no audio.
- **Targets** for ads and moderator talk are missed by the time the songs cannot fill, usually a few seconds per hour; a small library or only long songs make that more.
