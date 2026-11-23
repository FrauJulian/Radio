# Blockwoche 3.1: Radiosender

WPF desktop app that plans and plays a radio program: random music from a SQLite library, news at :00 and :30, and ad and moderator blocks.

## Requirements

- Windows
- .NET SDK 10

## Build and run

```powershell
dotnet build Radiosender.slnx
dotnet run --project src/Radiosender
```

## Structure

The app uses the MVC pattern and [WPF UI](https://github.com/lepoco/wpfui) for the interface.

- `src/Radiosender/Models`: data and domain types
- `src/Radiosender/Views`: windows and pages (XAML)
- `src/Radiosender/Controllers`: logic that connects views and models
- `src/Radiosender/Services`: database, audio player and API access
