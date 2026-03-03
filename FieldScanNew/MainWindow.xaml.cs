using FieldScanNew.Services; // 引用服务以便断开硬件
using FieldScanNew.ViewModels;
using System; // 引用 Environment
using System.Windows;

namespace FieldScanNew
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            this.DataContext = new MainViewModel();
            TaskTreeView.SelectedItemChanged += TaskTreeView_SelectedItemChanged;

            // 新增：监听鼠标点击，如果是重复点击已选中的项，手动触发逻辑
            TaskTreeView.PreviewMouseLeftButtonUp += (s, e) => {
                if (TaskTreeView.SelectedItem is IStepViewModel step)
                {
                    if (this.DataContext is MainViewModel vm)
                    {
                        // 强制调用一次逻辑
                        vm.TriggerStepDialog(step);
                    }
                }
            };
        }

        private void TaskTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (this.DataContext is MainViewModel vm && e.NewValue is IStepViewModel step)
            {
                vm.SelectedStep = step;
            }
        }

        public void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        public void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = this.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        // **核心修正 1：点击关闭按钮时，调用 Close()**
        public void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        // **核心修正 2：重写 OnClosed 方法，强制退出整个应用程序**
        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            // 1. 尝试优雅地断开硬件连接 (可选，但推荐)
            try
            {
                HardwareService.Instance.ActiveRobot?.Disconnect();
                HardwareService.Instance.ActiveDevice?.Disconnect();
                // 如果有摄像头服务，最好也在这里停止，但在 ViewModel 里比较难拿，
                // 直接用下面的 Environment.Exit(0) 强杀通常也没问题。
            }
            catch { }

            // 2. 通知 WPF 应用退出
            Application.Current.Shutdown();

            // 3. 【最关键一步】强制终止所有线程（包括摄像头和后台通信线程）
            // 这能确保进程彻底消失，不会再占用 exe 文件
            Environment.Exit(0);
        }
    }
}