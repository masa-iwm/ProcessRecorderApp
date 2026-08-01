using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;
using System.Collections.Generic;
using System.Text;

namespace ProcessRecorderApp.Converters;

public partial class GridLengthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is double d) return new GridLength(d, GridUnitType.Pixel);
        else return GridLength.Auto;
    }

    public object? ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is not GridLength d)
            return null;
        if (targetType == typeof(double))
            return d.Value;
        return null;
    }
}
