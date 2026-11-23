using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace StemCode.Desktop.Converters;

// Maps a FileEditSummary.Action ("Created"/"Edited"/"Deleted") to its badge color.
public sealed class EditActionToBrushConverter : IValueConverter
{
    private static readonly IBrush Created = new SolidColorBrush(Color.Parse("#86EFAC"));
    private static readonly IBrush Deleted = new SolidColorBrush(Color.Parse("#F4A6A6"));
    private static readonly IBrush Edited = new SolidColorBrush(Color.Parse("#E2C08D"));

    public static readonly EditActionToBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value as string) switch
        {
            "Created" => Created,
            "Deleted" => Deleted,
            _ => Edited,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
