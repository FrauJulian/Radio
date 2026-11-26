using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Radiosender.Views;

public static class GridColumns
{
    private const double Slack = 28;

    private const double DefaultFontSize = 14;

    // XAML width = minimum.
    private static readonly ConditionalWeakTable<GridViewColumn, object> MinWidths = [];

    // Assumes one shared font. shortcut: never trimmed, clear if libraries reach 100k+ songs.
    private static readonly Dictionary<string, double> TextWidths = [];

    // Measures all rows: lists are virtualized, built-in auto width misses off-screen rows.
    public static void Fit(ListView list)
    {
        if (list.View is not GridView view)
            return;

        // Rows have own font size: ask realized row, not list.
        var row = list.ItemContainerGenerator.ContainerFromIndex(0) as Control;
        var typeface = new Typeface(row?.FontFamily ?? list.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var fontSize = row?.FontSize ?? DefaultFontSize;
        var pixelsPerDip = VisualTreeHelper.GetDpi(list).PixelsPerDip;

        foreach (var column in view.Columns)
        {
            if (column.DisplayMemberBinding is not Binding { StringFormat: null, Path.Path: { } path })
                continue;
            var min = (double)MinWidths.GetValue(column, c => double.IsNaN(c.Width) ? 0d : c.Width);
            var properties = path.Split('.');

            var widest = 0d;
            foreach (var item in list.Items)
            {
                var text = Text(item, properties);
                if (text.Length == 0)
                    continue;
                if (!TextWidths.TryGetValue(text, out var width))
                {
                    width = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                        typeface, fontSize, Brushes.Black, pixelsPerDip).WidthIncludingTrailingWhitespace;
                    TextWidths[text] = width;
                }
                widest = Math.Max(widest, width);
            }

            var fitted = Math.Max(min, Math.Ceiling(widest + Slack));
            if (column.Width != fitted) // set triggers layout pass
                column.Width = fitted;
        }
    }

    // Reflection, not probe binding: binding resolves only once in the visual tree.
    private static string Text(object? value, string[] properties)
    {
        foreach (var property in properties)
        {
            value = value?.GetType().GetProperty(property)?.GetValue(value);
            if (value is null)
                return "";
        }
        return value?.ToString() ?? "";
    }
}
