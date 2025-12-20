using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace FieldScanNew.Infrastructure
{
    public class BoolToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isConnected && isConnected)
            {
                return new SolidColorBrush(Colors.LightGreen); // 连接成功：绿色
            }
            return new SolidColorBrush(Colors.LightGray); // 未连接：灰色
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}