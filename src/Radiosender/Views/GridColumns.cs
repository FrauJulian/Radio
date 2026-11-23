using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Radiosender.Views;

/// <summary>Sizes the text columns of a list so that every row can be read in full.</summary>
public static class GridColumns
{
    // Cell padding on both sides plus a little air, so the text never touches the next column.
    private const double Slack = 28;

    // Rows use this size when no row exists yet to ask.
    private const double DefaultFontSize = 14;

    // The width from the XAML stays the minimum, so an empty or short list keeps its designed look.
    private static readonly ConditionalWeakTable<GridViewColumn, object> MinWidths = [];

    // Laying out text is the slow part and the same titles come up on every refresh, so widths are remembered.
    // All lists share one font. shortcut: never trimmed; clear it if libraries reach hundreds of thousands of songs.
    private static readonly Dictionary<string, double> TextWidths = [];

    /// <summary>
    /// Widens each column with a <see cref="GridViewColumn.DisplayMemberBinding"/> to its longest text over
    /// all rows, not only the visible ones: the lists are virtualized, so the built-in auto width would miss
    /// rows that are scrolled out of view. Columns with a cell template keep their width, and so do columns
    /// with a string format: those show times, which always have the same length.
    /// </summary>
    public static void Fit(ListView list)
    {
        if (list.View is not GridView view)
            return;

        // The rows have their own font size, so a realized row is asked rather than the list.
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
            if (column.Width != fitted) // setting it triggers a layout pass
                column.Width = fitted;
        }
    }

    // Follows a simple property path such as "Item.Title". A binding on a probe element would not do:
    // it only resolves once the element is part of the window.
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
