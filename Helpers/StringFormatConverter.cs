using System;
using System.Windows.Data;
using System.Globalization;

namespace Kil0bitSystemMonitor.Helpers
{
    public class StringFormatConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (parameter is string format)
            {
                return string.Format(culture, format, value);
            }
            return value?.ToString() ?? "";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
