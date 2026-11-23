using Radiosender.Models;
using Radiosender.Services;

namespace Radiosender.Tests;

public class ScheduleGeneratorTests
{
    private static readonly DateTime Start = new(2026, 11, 23, 14, 0, 0);

    // Realistic lengths with odd seconds, so exact fits only work through the filler.
    private static readonly IReadOnlyList<Song> Library =
    [
        Song(1, 3 * 60 + 12), Song(2, 4 * 60 + 5), Song(3, 2 * 60 + 47), Song(4, 5 * 60 + 31),
        Song(5, 3 * 60 + 58), Song(6, 3 * 60 + 3), Song(7, 6 * 60 + 9), Song(8, 2 * 60 + 21),
    ];

    private static Song Song(long id, int seconds) =>
        new() { Id = id, Title = $"Song {id}", Artist = "Artist", Duration = TimeSpan.FromSeconds(seconds) };

    private static IReadOnlyList<ScheduleItem> Generate(int seed, int hours = 3, ScheduleOptions? options = null) =>
        new ScheduleGenerator(options ?? new ScheduleOptions(), new Random(seed)).Generate(Start, hours, Library);

    public static TheoryData<int> Seeds => [1, 2, 3, 42, 1234];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Items_AreContiguousAndCoverExactlyTheHours(int seed)
    {
        var items = Generate(seed);

        Assert.Equal(Start, items[0].Start);
        for (var i = 1; i < items.Count; i++)
            Assert.Equal(items[i - 1].End, items[i].Start);
        Assert.Equal(Start.AddHours(3), items[^1].End);
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void News_RunsFiveMinutesAtFullAndHalfHour(int seed)
    {
        var news = Generate(seed).Where(i => i.Kind == ScheduleItemKind.News).ToList();

        Assert.Equal(6, news.Count);
        for (var n = 0; n < news.Count; n++)
        {
            Assert.Equal(Start.AddMinutes(30 * n), news[n].Start);
            Assert.Equal(TimeSpan.FromMinutes(5), news[n].Duration);
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void ASongEndsExactlyWhenEachNewsStartsAndAtTheEnd(int seed)
    {
        var items = Generate(seed);

        for (var i = 1; i < items.Count; i++)
        {
            if (items[i].Kind != ScheduleItemKind.News)
                continue;
            Assert.Equal(ScheduleItemKind.Song, items[i - 1].Kind);
            Assert.Equal(items[i].Start, items[i - 1].End);
        }
        Assert.Equal(ScheduleItemKind.Song, items[^1].Kind);
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void SongsAreNeverCutAndComeFromTheLibrary(int seed)
    {
        foreach (var song in Generate(seed).Where(i => i.Kind == ScheduleItemKind.Song))
        {
            Assert.Contains(song.Song!, Library);
            Assert.Equal(song.Song!.Duration, song.Duration);
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void AdsAndModeratorStayWithinTwentyMinutesPerHour(int seed)
    {
        var items = Generate(seed);

        for (var hour = 0; hour < 3; hour++)
        {
            var from = Start.AddHours(hour);
            var filler = items
                .Where(i => i.Start >= from && i.Start < from.AddHours(1))
                .Where(i => i.Kind is ScheduleItemKind.Ad or ScheduleItemKind.Moderator)
                .Aggregate(TimeSpan.Zero, (sum, i) => sum + i.Duration);

            Assert.InRange(filler, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(20));
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void NoAdBlockIsLongerThanTheConfiguredMaximum(int seed)
    {
        var options = new ScheduleOptions { MaxAdBlock = TimeSpan.FromMinutes(1) };
        var block = TimeSpan.Zero;

        foreach (var item in Generate(seed, options: options))
        {
            block = item.Kind == ScheduleItemKind.Ad ? block + item.Duration : TimeSpan.Zero;
            Assert.True(block <= options.MaxAdBlock, $"Ad block of {block} at {item.Start:HH:mm:ss}");
        }
    }

    [Fact]
    public void Generate_ProducesDifferentPlansForDifferentSeeds()
    {
        var a = Generate(1).Select(i => i.Title);
        var b = Generate(2).Select(i => i.Title);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Generate_ThrowsWhenSongsAreTooLongForAnySlot()
    {
        Song[] longSongs = [Song(1, 24 * 60), Song(2, 26 * 60)];
        var generator = new ScheduleGenerator(new ScheduleOptions(), new Random(1));

        Assert.Throws<ScheduleException>(() => generator.Generate(Start, 1, longSongs));
    }

    [Fact]
    public void Generate_ThrowsForEmptyLibrary()
    {
        var generator = new ScheduleGenerator(new ScheduleOptions(), new Random(1));

        Assert.Throws<ScheduleException>(() => generator.Generate(Start, 1, []));
    }

    [Fact]
    public void Generate_RejectsInvalidInput()
    {
        var generator = new ScheduleGenerator(new ScheduleOptions(), new Random(1));

        Assert.Throws<ArgumentOutOfRangeException>(() => generator.Generate(Start, 0, Library));
        Assert.Throws<ArgumentException>(() => generator.Generate(Start.AddMinutes(15), 1, Library));
        Assert.Throws<ArgumentException>(() =>
            new ScheduleGenerator(new ScheduleOptions { MaxAdBlock = TimeSpan.FromMinutes(20) }, new Random(1)).Generate(Start, 1, Library));
    }

    private static readonly Song Request = Song(99, 4 * 60 + 17);

    private static ScheduleGenerator Generator(int seed) => new(new ScheduleOptions(), new Random(seed));

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Replace_PutsTheSongInPlaceAndKeepsEverythingElseOnTime(int seed)
    {
        var plan = Generate(seed, hours: 2);
        var index = 1; // first item after the 14:00 news
        var nextNews = plan.Select((p, i) => (p, i)).First(x => x.i > index && x.p.Kind == ScheduleItemKind.News).i;

        var swapped = Generator(seed).Replace(plan, index, Request, Library);

        Assert.Equal(plan.Take(index), swapped.Take(index));
        Assert.Equal(Request, swapped[index].Song);
        Assert.Equal(plan[index].Start, swapped[index].Start);
        // Later slots are untouched, so the next news is still at 14:30.
        var swappedNews = swapped.Select((p, i) => (p, i)).First(x => x.i > index && x.p.Kind == ScheduleItemKind.News).i;
        Assert.Equal(plan.Skip(nextNews), swapped.Skip(swappedNews));
        Assert.Equal(ScheduleItemKind.Song, swapped[swappedNews - 1].Kind);
        for (var i = 1; i < swapped.Count; i++)
            Assert.Equal(swapped[i - 1].End, swapped[i].Start);
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Replace_KeepsFillerLimitsAndNoAdsBackToBack(int seed)
    {
        var plan = Generate(seed, hours: 1);
        // Every non-news item in the first half hour, so prefixes with filler are covered too.
        var last = plan.Select((p, i) => (p, i)).First(x => x.i > 0 && x.p.Kind == ScheduleItemKind.News).i;
        for (var index = 1; index < last; index++)
        {
            IReadOnlyList<ScheduleItem> swapped;
            try { swapped = Generator(seed).Replace(plan, index, Request, Library); }
            catch (ScheduleException) { continue; } // too little time left before the news

            var filler = swapped.Where(i => i.Kind is ScheduleItemKind.Ad or ScheduleItemKind.Moderator)
                .Aggregate(TimeSpan.Zero, (sum, i) => sum + i.Duration);
            Assert.True(filler <= TimeSpan.FromMinutes(20), $"{filler} filler after swap at {index}");
            for (var i = 1; i < swapped.Count; i++)
                Assert.False(swapped[i - 1].Kind == ScheduleItemKind.Ad && swapped[i].Kind == ScheduleItemKind.Ad,
                    $"Ads back to back at {swapped[i].Start:HH:mm:ss}");
        }
    }

    [Fact]
    public void Replace_RejectsSongThatDoesNotFitBeforeTheNews()
    {
        var plan = Generate(1, hours: 1);
        var beforeNews = plan.Select((p, i) => (p, i)).First(x => x.i > 0 && x.p.Kind == ScheduleItemKind.News).i - 1;
        var tooLong = Song(98, 25 * 60);

        var ex = Assert.Throws<ScheduleException>(() => Generator(1).Replace(plan, beforeNews, tooLong, Library));
        Assert.Contains("does not fit", ex.Message);
    }

    [Fact]
    public void Replace_RejectsNews()
    {
        var plan = Generate(1, hours: 1);

        Assert.Throws<ScheduleException>(() => Generator(1).Replace(plan, 0, Request, Library));
    }}
