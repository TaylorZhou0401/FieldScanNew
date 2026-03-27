using FieldScanNew.Infrastructure;
using FieldScanNew.Models;
using FieldScanNew.Services;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows; // 引用 Point, Rect
using System.Windows.Input;
using System.Windows.Media.Imaging;

using MessageBox = System.Windows.MessageBox;

namespace FieldScanNew.ViewModels
{
    public class ScanAreaViewModel : ViewModelBase, IStepViewModel
    {
        // **核心修正：改名为 "5. 扫描区域配置"**
        public string DisplayName => "5. 扫描区域配置";

        private readonly ProjectData _projectData;

        public ScanSettings Settings
        {
            get => _projectData.ScanConfig;
            set
            {
                if (_projectData.ScanConfig != value)
                {
                    _projectData.ScanConfig = value;
                    OnPropertyChanged();
                }
            }
        }

        private BitmapSource? _dutImageSource;
        public BitmapSource? DutImageSource { get => _dutImageSource; set { _dutImageSource = value; OnPropertyChanged(); } }

        private string _statusText = "请在图片上【按住鼠标左键拖拽】以框选扫描区域。";
        public string StatusText { get => _statusText; set { _statusText = value; OnPropertyChanged(); } }

        private string _previewStatusText = "";
        public string PreviewStatusText { get => _previewStatusText; set { _previewStatusText = value; OnPropertyChanged(); } }

        private string _previewButtonText = "预览扫描边界";
        public string PreviewButtonText { get => _previewButtonText; set { _previewButtonText = value; OnPropertyChanged(); } }

        private bool _isPreviewing = false;
        public bool IsPreviewing
        {
            get => _isPreviewing;
            set
            {
                _isPreviewing = value;
                OnPropertyChanged();
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public ICommand PreviewBoundaryCommand { get; }

        private CancellationTokenSource? _previewCts;

        public ScanAreaViewModel(ProjectData projectData)
        {
            _projectData = projectData;
            PreviewBoundaryCommand = new RelayCommand(async _ => await ExecutePreviewBoundary());
            ReloadImage();
        }

        private async Task ExecutePreviewBoundary()
        {
            if (IsPreviewing)
            {
                // 如果正在预览中，触发取消
                _previewCts?.Cancel();
                PreviewStatusText = "正在取消预览...";
                PreviewButtonText = "正在取消...";
                return;
            }

            if (HardwareService.Instance.ActiveRobot == null || !HardwareService.Instance.ActiveRobot.IsConnected)
            {
                MessageBox.Show("请先连接机械臂！", "提示");
                return;
            }

            IsPreviewing = true;
            PreviewButtonText = "取消预览";
            PreviewStatusText = "正在进行边界预览...";
            _previewCts = new CancellationTokenSource();

            try
            {
                float minX = Math.Min(Settings.StartX, Settings.StopX);
                float maxX = Math.Max(Settings.StartX, Settings.StopX);
                float minY = Math.Min(Settings.StartY, Settings.StopY);
                float maxY = Math.Max(Settings.StartY, Settings.StopY);
                float z = Settings.ScanHeightZ;
                float r = Settings.ScanAngleR;

                var robot = HardwareService.Instance.ActiveRobot;

                var points = new[]
                {
                    (minX, minY),
                    (maxX, minY),
                    (maxX, maxY),
                    (minX, maxY),
                    (minX, minY)
                };

                foreach (var point in points)
                {
                    if (_previewCts.Token.IsCancellationRequested)
                    {
                        PreviewStatusText = "边界预览已取消。";
                        return;
                    }
                    await robot.MoveToAsync(point.Item1, point.Item2, z, r);
                }

                PreviewStatusText = "边界预览结束。";
            }
            catch (Exception ex)
            {
                PreviewStatusText = "预览发生异常结束。";
                MessageBox.Show("预览过程中发生错误: " + ex.Message, "错误");
            }
            finally
            {
                PreviewButtonText = "预览扫描边界";
                IsPreviewing = false;
                _previewCts?.Dispose();
                _previewCts = null;
            }
        }

        public void ReloadImage()
        {
            if (!string.IsNullOrEmpty(_projectData.DutImagePath) && System.IO.File.Exists(_projectData.DutImagePath))
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(_projectData.DutImagePath);
                    bitmap.EndInit();
                    bitmap.Freeze();
                    DutImageSource = bitmap;
                }
                catch { }
            }
            else
            {
                DutImageSource = null;
                StatusText = "未找到校准图片，请先在“4. 机械臂校准”中加载或拍摄图片。";
            }
        }

        public void UpdateScanAreaFromSelection(Rect rectPixel)
        {
            if (!_projectData.IsCalibrated)
            {
                MessageBox.Show("系统尚未校准！\n请先完成“4. 机械臂校准”，否则无法自动计算物理坐标。", "警告");
                return;
            }

            // ================================================================
            // **核心修正：使用独立缩放公式计算物理坐标**
            // ================================================================
            double scaleX = _projectData.MatrixM11;
            double scaleY = _projectData.MatrixM22;
            double offX = _projectData.OffsetX;
            double offY = _projectData.OffsetY;

            // 计算矩形对角线两个点的物理坐标 (P1:左上, P2:右下)
            // 注意：因为可能存在翻转，P1转换后不一定还是“左上”，可能是“右下”

            double physX_1 = (scaleX * rectPixel.X) + offX;
            double physY_1 = (scaleY * rectPixel.Y) + offY;

            double physX_2 = (scaleX * (rectPixel.X + rectPixel.Width)) + offX;
            double physY_2 = (scaleY * (rectPixel.Y + rectPixel.Height)) + offY;

            // 自动判断大小，填入 Start/Stop
            // Start 总是放较小值，Stop 总是放较大值 (或者根据扫描习惯)
            // 这里我们遵循：Start < Stop
            Settings.StartX = (float)Math.Min(physX_1, physX_2);
            Settings.StopX = (float)Math.Max(physX_1, physX_2);
            Settings.StartY = (float)Math.Min(physY_1, physY_2);
            Settings.StopY = (float)Math.Max(physY_1, physY_2);

            StatusText = $"区域已更新：X[{Settings.StartX:F1} ~ {Settings.StopX:F1}], Y[{Settings.StartY:F1} ~ {Settings.StopY:F1}]";
        }
    }
}