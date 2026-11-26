namespace Radiosender.Models;

// Code = Openverse "license" value.
public sealed record LicenseType(string Code, string Label)
{
    public static IReadOnlyList<LicenseType> All { get; } =
    [
        new("cc0", "CC0"),
        new("pdm", "Public Domain"),
        new("by", "CC BY"),
        new("by-sa", "CC BY-SA"),
        new("by-nd", "CC BY-ND"),
        new("by-nc", "CC BY-NC"),
        new("by-nc-sa", "CC BY-NC-SA"),
        new("by-nc-nd", "CC BY-NC-ND"),
    ];

    // "CC BY-SA 4.0" -> "by-sa", "CC0 1.0" -> "cc0", "Public Domain Mark 1.0" -> "pdm"; null if not in All.
    public static string? CodeOf(string license)
    {
        var name = license.Trim();
        if (name.StartsWith("CC0", StringComparison.OrdinalIgnoreCase))
            return "cc0";
        if (name.StartsWith("Public Domain", StringComparison.OrdinalIgnoreCase))
            return "pdm";
        if (!name.StartsWith("CC ", StringComparison.OrdinalIgnoreCase))
            return null;
        var code = name[3..].Split(' ')[0].ToLowerInvariant();
        return All.Any(type => type.Code == code) ? code : null;
    }
}
