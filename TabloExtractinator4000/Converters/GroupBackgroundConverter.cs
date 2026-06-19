using System.Globalization;
using System.Windows.Data;
using TabloExtractinator4000.ViewModels;
using WpfColor = System.Windows.Media.Color;
using WpfBrush = System.Windows.Media.SolidColorBrush;

namespace TabloExtractinator4000.Converters;

// Returns a background brush for a DataGrid group header based on the first item's RecordingType.
public class GroupBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is CollectionViewGroup group && group.Items.Count > 0)
        {
            var first = group.Items[0] as RecordingRowViewModel;
            return (first?.RecordingType) switch
            {
                "Movie" => new WpfBrush(WpfColor.FromRgb(0x1A, 0x2E, 0x1A)), // dark green
                "Sport" => new WpfBrush(WpfColor.FromRgb(0x2E, 0x28, 0x10)), // dark amber
                _       => new WpfBrush(WpfColor.FromRgb(0x1A, 0x24, 0x38)), // dark blue-grey
            };
        }
        return new WpfBrush(WpfColor.FromRgb(0x1A, 0x24, 0x38));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}
