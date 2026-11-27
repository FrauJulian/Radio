# Blockwoche 3.1: Radiosender

WPF desktop app that plans and plays a radio program for a chosen number of full hours: random music from a SQLite library, news at :00 and :30, and ad and moderator blocks in between. Listeners can request a song, which replaces an upcoming item while the broadcast runs.

## Requirements

- Windows
- .NET SDK 10
- An audio output device for playback
- A microphone, if you want to talk on air

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
   - **Online**: one search runs on Openverse and the Internet Archive at the same time; each source's results appear as soon as it answers, and the **Quelle** column shows where a result comes from. If one source fails, the others' results stay and a warning names the problem. Untick licenses in the **Lizenzen** dropdown next to the search box to leave them out of the results (Openverse filters on its server, the Internet Archive after the search, so it may return fewer results). Search the catalog, preview a result and import it with **In Bibliothek importieren** or a double-click. Select several results with Ctrl or Shift to import them in one go; they are downloaded one after the other. The app downloads the file and stores its license and attribution with the song. Results that are already in the library or have no duration cannot be imported; the tooltip of the disabled button says why.
2. **Bibliothek**: lists and searches all songs. Edit title, artist, license and attribution, or delete a song.
3. **Sendeplan** (the start page):
   1. Choose the start and the number of hours (1 to 24), then click **Plan erstellen**.
      - **Unbegrenzt** plans without an end: the timeline shows the next 5 hours, and while the broadcast runs one more hour is added whenever less than 5 hours are left.
      - The start field defaults to **Jetzt**. It also lists the next twelve full hours, and you can type your own time such as `14:30`. A typed time that has already passed today means tomorrow. A **Jetzt** plan begins with music up to the first news at the next :00 or :30; the chosen hours count from there.
      - **Jetzt starten** starts the plan you created. A **Jetzt** plan moves to the moment you press it, so nothing is skipped while you looked it over. It stays disabled until a plan exists. While laut.fm is switched on the button reads **Live starten** and also starts the live stream; if laut.fm cannot be reached, the music still plays and a warning says why. While on air the button turns red and reads **Sendung beenden**, which stops playback and the live stream together.
   2. The timeline shows every item with start time, type (song, news, ad, moderator), title and duration.
   3. The now-playing card shows the current item and its progress. Before the planned start it shows when the broadcast begins; if the plan has already begun, playback joins at the next item.
   4. **Edit the timeline**: select an upcoming row and use the buttons above the list to move it up or down, edit it, or remove it (trash icon), or drag it to a new place with the mouse: a label with type and title follows the cursor, a line shows where it lands, and the row fades in at its new place. The grip on the left of a row shows it can be dragged. **Hinzufügen** inserts a song (the one chosen under **Songwunsch**), news, an ad or moderator talk as the item after next: two places after the item on air, or at position 3 of the list when nothing is on air; for everything but songs you set title and length first. News, ad and moderator items have no title until you give them one; news and moderator items can also change their length. A double-click opens the editor too. Every item after a change follows on without a gap. The item on air can be edited too, but not moved or removed; **Aktuelles Element stoppen** in the now-playing card ends it at once: its length becomes the time from its start until now, and the next item starts.
   5. **Speaking reminder**: 30 seconds before a news or moderator item goes on air, a dialog in the middle of the screen reminds you on any page and stays until you click **OK**.
   6. **Microphone**: **Mikro ein** in the now-playing card puts the default microphone on air, also over running music; the button turns red and reads **Mikro aus**. It works with or without a broadcast and stays on while you visit other pages.
   7. **Mix levels**, below the now-playing row: under **Was die Hörer hören**, **Musik** and **Mikrofon** set how loud the music and the microphone are for the listeners. Under **Was du hörst**, **Musik** sets how loud the music is on your own device; you never hear your own microphone there, because the delay is distracting. **Genau wie die Hörer** lets you hear exactly what the listeners hear, microphone included.
   8. **Songwunsch**: choose a song, select an upcoming row in the timeline and click **Auswahl ersetzen**. This also works while the broadcast is running.

4. **Einstellungen** (bottom left): choose the language, **Deutsch** (default) or **English**; after **Speichern** the whole interface switches at once, without a restart. Switch the speaking alert on or off for news and for moderator talk, set how many seconds before the start it appears and whether it plays a bing sound. Choose the playback device for the broadcast (for the listeners) and, if you have a second one such as headphones, the device for yourself. Set the minutes per hour for news, moderator talk and ads (defaults 10, 10 and 20); together they can take at most 60 minutes. **Zurücksetzen** puts the default values into the form, **Speichern** stores them in `settings.json` next to the database. Alerts change at once; minutes per hour apply to the next schedule. The **laut.fm** switch turns on live streaming; its fields are described under [laut.fm](#lautfm).

News, ads and moderator talk are placeholders: the app shows them in the timeline and keeps silent for their duration, so the speaker can talk live.

Errors, such as invalid input or a failed save, search or import, open a popup with the message. Hints and warnings stay in the bar at the top of the page.

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
| Model | `src/Radiosender/Models` | `Song`, `ScheduleItem`, `MusicTrack`, `OpenverseTrack` |
| View | `src/Radiosender/Views` | `MainWindow` with navigation, one page per feature, `SongEditWindow` |
| Controller | `src/Radiosender/Controllers` | `LibraryController`, `UploadController`, `OnlineImportController`, `ScheduleController`, `LiveController` |
| Services | `src/Radiosender/Services` | database, audio, schedule logic and API access used by the controllers |

Views only handle input and display. They call a controller, and the controller works with the models and services. `App` is the composition root: it registers every service, controller and page in a `Microsoft.Extensions.DependencyInjection` container, and the navigation view asks that container for each page, so pages get their controllers through the constructor. The `SongRepository` and the `ScheduleController` are singletons. A running broadcast therefore keeps playing when the user switches pages.

Main services:

- `SongRepository`: CRUD for songs in SQLite through Entity Framework Core, without SQL in the code
- `SongImportService`: copies an uploaded file into the music folder, reads its tags with TagLibSharp and its duration with NAudio
- `IMusicProvider`: an online catalog that can be searched. `OpenverseProvider` uses the anonymous Openverse API and `InternetArchiveProvider` the Internet Archive search, limited to Creative Commons licensed MP3 files.
- `TrackDownloader`: downloads a search result into the music folder, the same way for every provider
- `ProgramMixer`: mixes songs and the microphone twice in one pass, once for the listeners and once for the person at the desk, each with its own levels
- `ProgramOutput`: the station's audio channel. It plays the broadcast mix on one device and, if a second device is chosen, the monitor mix on that one
- `MusicPlayer`: the audio player as a singleton (`MusicPlayer.Instance`, private constructor). It reads files with NAudio and plays them on the `ProgramOutput`
- `Microphone`: captures the default recording device and puts it on the `ProgramOutput`
- `ScheduleGenerator`: builds the plan and handles song requests
- `PlaybackEngine`: runs the plan item by item on a background thread and plays songs through `MusicPlayer`
- `Loc`: the interface texts in German (`Strings.resx`, default) and English (`Strings.en.resx`) in `Services/Localization`. XAML uses `{l:Tr Key}`, which binds to `Loc` and follows a language switch; code uses `Loc.T` and `Loc.F` and refreshes its texts on `Loc.Changed`
- `IcecastStream`: logs in at an Icecast server as a live source, encodes a copy of the broadcast mix to MP3 with Windows Media Foundation and sends it. `LiveController` starts and stops it and sets the stream title for each item through `StreamTitles`

Tests are in `tests/Radiosender.Tests` (xUnit). They cover the repository, import, Openverse parsing, the generator rules, the playback engine, the schedule controller, laut.fm (settings, Icecast login, ad trigger) and the translations (same keys in both languages, every key used in code exists).

## Adding a music provider

Online sources sit behind `IMusicProvider`, so a new one needs no change to the import, the download or the page:

1. Write a class that implements `IMusicProvider`: a `Name` for the interface and `SearchAsync`, which returns `MusicTrack` items (title, creator, https link to the audio file, license, attribution, duration).
2. Give every track an `Id` that no other provider uses, for example prefixed with the provider name. Openverse ids are stored as they come.
3. Register the class in `App.BuildServices` with `services.AddSingleton<IMusicProvider, YourProvider>()`.

The import page searches all registered providers at the same time and shows each one's results as they arrive.

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
| Music comes from a SQLite database | `SongRepository` with Entity Framework Core, `radiosender.db` | Done |
| Library can be extended at any time: add, edit, delete | Library page and `SongEditWindow` | Done |
| Add by simple upload | Import page, "Vom Computer" (MP3, WAV, several files, drag and drop) | Done |
| Add through the Openverse API | Import page, "Online", provider Openverse; the Internet Archive is a second provider | Done |
| Music player is a singleton class | `MusicPlayer.Instance` | Done |
| Hot swap: a requested song replaces an item during the planned run, everything after adapts | Song request on the Schedule page; `ScheduleGenerator.Replace`, `PlaybackEngine.ReplaceUpcoming` | Done |
| News and ad blocks are shown | Timeline with type labels, now-playing card | Done |
| The player really plays music | `MusicPlayer` with NAudio | Done |
| C#, WPF with MVC, WPF UI | .NET 10, folders above, WPF UI 4.3 | Done |

## laut.fm

With laut.fm switched on in the settings, the app streams live to a [laut.fm](https://laut.fm) station.

1. In Radioadmin, open **Live senden** and copy server, port, mountpoint, user and password into the laut.fm card on the Settings page. Save.
2. On the Sendeplan page, **Live starten** connects to laut.fm and sends the broadcast mix as MP3 at 128 kbit/s. laut.fm then plays the live stream instead of the station's regular program.

The app connects as an Icecast source (`PUT` to the mountpoint with the user and password). Each item that starts sets the stream title: `Artist - Title` for songs and the item title otherwise. Ad items send `START_AD_BREAK`, which laut.fm needs to start its ad break; the app sends it at most once every 15 minutes, as laut.fm requires.

The password is stored in `settings.json` encrypted with Windows DPAPI for the current Windows user. Another user or computer cannot read it and has to enter it again.

Risks:

- Nothing here has been tested against a real laut.fm account. The tests run against a local fake server. If laut.fm expects a different format or login method, the Sendeplan page shows the error but does not adapt.
- The source connection and the title updates use plain HTTP, like every Icecast source client, so the live password crosses the network unencrypted.
- The MP3 encoder comes with Windows. Windows "N" editions lack it until the Media Feature Pack is installed; going live then reports the missing encoder.
- laut.fm wants the ad trigger at least once every half hour. With 0 ad minutes per hour the plan has no ad items and the app never sends it.

## Known limitations

- **Waiting for the start**: a plan with a later start time stays silent after **Jetzt starten** until that time. Pick **Jetzt** to avoid the wait. News times in a **Jetzt** plan shift by however long you wait before starting it.
- **Unlimited plans** keep every played item in the timeline, so the list grows by about 20 rows per hour.
- **Manual edits** shift every later item, so after moving, removing or resizing an item the news can leave :00 and :30 and the plan can end earlier or later.
- **Late items**: if a song plays longer than its stored duration, the following items start late by the same amount. Nothing is cut to catch up; the delay only disappears when a later item ends early.
- **Skipped songs**: a song that cannot be played (missing file, unsupported format) is skipped with a warning, and the station stays silent until the next planned item.
- **Internet Archive**: one search asks for up to 8 items and then for the file list of each, and shows at most 5 MP3 files per item. Results can include long recordings; the 50 MB download limit applies.
- **Very old libraries**: a `radiosender.db` from before the Openverse import has no `OpenverseId` column and is not upgraded. Delete the file and import the music again.
- **Deleting a song** removes it from the library but keeps its file in the `Music` folder.
- **Placeholders** have no audio; news and moderator talk are spoken live through the microphone.
- **Microphone**: uses the default Windows recording device. With loudspeakers next to the microphone the room hears itself and can start to howl, so use headphones. Speech arrives about a fifth of a second late. The alert tone and the import preview are not part of the program channel.
- **Two mixes need two devices**: with one playback device you hear your own mix, or the broadcast mix while **Sendung 1:1 hören** is on, and nobody else hears anything different. Your own mix never contains the microphone. With two devices their clocks can drift, which shows as a rare tiny dropout on the second device. The mix levels are not stored and start at full on every launch.
- **Targets** for ads and moderator talk are missed by the time the songs cannot fill, usually a few seconds per hour; a small library or only long songs make that more.
