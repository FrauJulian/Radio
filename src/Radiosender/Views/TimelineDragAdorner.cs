using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Radiosender.Views;

// Drag feedback over a list: small label at the cursor, thin accent line where the row lands.
public sealed class TimelineDragAdorner : Adorner
{
    private const double MaxLabelWidth = 320;
    private static readonly Brush ShadowBrush = Freeze(new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)));
    private readonly string _label;
    private readonly Brush _accent;
    private readonly Brush _card;
    private readonly Brush _text;
    private readonly Pen _border;
    private Point _cursor;
    private double? _lineY;

    public TimelineDragAdorner(Control list, string label, Point cursor, Brush accent, Brush card, Brush text, Brush border) : base(list)
    {
        _label = label;
        _cursor = cursor;
        _accent = accent;
        _card = card;
        _text = text;
        _border = new Pen(border, 1);
        IsHitTestVisible = false;
    }

    public void Update(Point cursor, double? lineY)
    {
        _cursor = cursor;
        _lineY = lineY;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = AdornedElement.RenderSize.Width;
        if (_lineY is { } y)
        {
            dc.DrawRoundedRectangle(_accent, null, new Rect(12, y - 1, width - 28, 2), 1, 1);
            dc.DrawEllipse(null, new Pen(_accent, 2), new Point(8, y), 4, 4);
        }

        var list = (Control)AdornedElement;
        var text = new FormattedText(_label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(list.FontFamily, list.FontStyle, FontWeights.SemiBold, list.FontStretch), 13, _text,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = MaxLabelWidth,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        var card = new Rect(_cursor.X + 14, _cursor.Y - text.Height / 2 - 6, Math.Min(text.WidthIncludingTrailingWhitespace, MaxLabelWidth) + 20, text.Height + 12);
        // Keep the label inside the list.
        if (card.Right > width - 4)
            card.X = Math.Max(4, _cursor.X - card.Width - 14);
        // Soft shadow: offset copy, cheaper than an effect on the whole adorner (that would shade the line too).
        var shadow = card;
        shadow.Offset(0, 2);
        dc.DrawRoundedRectangle(ShadowBrush, null, shadow, 7, 7);
        dc.DrawRoundedRectangle(_card, _border, card, 6, 6);
        dc.DrawText(text, new Point(card.X + 10, card.Y + 6));
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
