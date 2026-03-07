using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FieldScanNew.ViewModels;

namespace FieldScanNew.Views
{
    public partial class XYCalibView : UserControl
    {
        public XYCalibView()
        {
            InitializeComponent();
            // 订阅加载事件
            this.Loaded += XYCalibView_Loaded;
        }

        private void XYCalibView_Loaded(object sender, RoutedEventArgs e)
        {
            // 当界面加载时，调用 ViewModel 的初始化归位逻辑
            if (this.DataContext is XYCalibViewModel vm)
            {
                Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    await vm.InitializeRobotStateAsync();
                });
            }
        }

        private void Image_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var image = sender as Image;
            if (image == null || this.DataContext == null) return;
            var vm = this.DataContext as XYCalibViewModel;
            if (vm == null) return;

            Point clickPoint = e.GetPosition(image);
            double currentWidth = image.ActualWidth;
            double currentHeight = image.ActualHeight;
            double originalWidth = vm.DutImageSource?.PixelWidth ?? 0;
            double originalHeight = vm.DutImageSource?.PixelHeight ?? 0;

            if (originalWidth > 0 && originalHeight > 0)
            {
                double scaleX = originalWidth / currentWidth;
                double scaleY = originalHeight / currentHeight;
                Point pixelPoint = new Point(clickPoint.X * scaleX, clickPoint.Y * scaleY);
                vm.HandleImageClick(pixelPoint);
            }
        }
    }
}