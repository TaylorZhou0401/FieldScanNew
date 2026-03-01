using FieldScanNew.Infrastructure;
using FieldScanNew.Models;
using FieldScanNew.Services;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using Point = System.Windows.Point;

namespace FieldScanNew.ViewModels
{
    public class XYCalibViewModel : ViewModelBase, IStepViewModel
    {
        public string DisplayName => "4. 机械臂校准";

        private readonly ProjectData _projectData;
        private readonly string _projectFolderPath;
        private readonly CameraService _cameraService;
        private readonly HardwareService _hardwareService;

        private BitmapSource? _dutImageSource;
        public BitmapSource? DutImageSource { get => _dutImageSource; set { _dutImageSource = value; OnPropertyChanged(); } }

        private BitmapSource? _cameraPreviewSource;
        public BitmapSource? CameraPreviewSource { get => _cameraPreviewSource; set { _cameraPreviewSource = value; OnPropertyChanged(); } }

        private bool _isCameraMode = false;
        public bool IsCameraMode { get => _isCameraMode; set { _isCameraMode = value; OnPropertyChanged(); } }

        // [已删除] 拖动示教相关的属性 (IsDragMode, DragButtonText, DragButtonColor)

        // 机械臂 ID 属性 (绑定到 ScaraRobotArm)
        public int RobotId
        {
            get => (_hardwareService.ActiveRobot as ScaraRobotArm)?.RobotId ?? 19;
            set
            {
                if (_hardwareService.ActiveRobot is ScaraRobotArm robot)
                {
                    robot.RobotId = value;
                    OnPropertyChanged();
                }
            }
        }

        public ObservableCollection<string> CameraList { get; }
        private int _selectedCameraIndex = 0;
        public int SelectedCameraIndex { get => _selectedCameraIndex; set { _selectedCameraIndex = value; OnPropertyChanged(); } }

        private float _jogStep = 10.0f;
        public float JogStep { get => _jogStep; set { _jogStep = value; OnPropertyChanged(); } }

        private string _instructionText = "步骤1：打开摄像头，点击【拍照】获取基准图。";
        public string InstructionText { get => _instructionText; set { _instructionText = value; OnPropertyChanged(); } }

        private Point _pixelP1; private Point _pixelP2;
        public string PixelP1Text => $"像素 P1: ({_pixelP1.X:F0}, {_pixelP1.Y:F0})";
        public string PixelP2Text => $"像素 P2: ({_pixelP2.X:F0}, {_pixelP2.Y:F0})";

        private double _physicalX1; public double PhysicalX1 { get => _physicalX1; set { _physicalX1 = value; OnPropertyChanged(); } }
        private double _physicalY1; public double PhysicalY1 { get => _physicalY1; set { _physicalY1 = value; OnPropertyChanged(); } }
        private double _physicalX2; public double PhysicalX2 { get => _physicalX2; set { _physicalX2 = value; OnPropertyChanged(); } }
        private double _physicalY2; public double PhysicalY2 { get => _physicalY2; set { _physicalY2 = value; OnPropertyChanged(); } }

        private int _clickStep = 0;
        private bool _showP1 = false; public bool ShowP1 { get => _showP1; set { _showP1 = value; OnPropertyChanged(); } }
        private bool _showP2 = false; public bool ShowP2 { get => _showP2; set { _showP2 = value; OnPropertyChanged(); } }

        public double P1Left => _pixelP1.X - 5; public double P1Top => _pixelP1.Y - 5;
        public double P2Left => _pixelP2.X - 5; public double P2Top => _pixelP2.Y - 5;

        public ICommand ToggleCameraCommand { get; }
        public ICommand CaptureCommand { get; }
        public ICommand ResetCommand { get; }
        public ICommand CalibrateCommand { get; }
        public ICommand JogCommand { get; }
        public ICommand ReadRobotPosCommand { get; }
        public ICommand ResetRAxisCommand { get; }
        // [已删除] ToggleDragCommand

        public XYCalibViewModel(ProjectData projectData, string projectFolderPath)
        {
            _projectData = projectData;
            _projectFolderPath = projectFolderPath;
            _hardwareService = HardwareService.Instance;
            _cameraService = CameraService.Instance;
            CameraList = new ObservableCollection<string>(_cameraService.GetCameraList());
            if (CameraList.Count > 0) SelectedCameraIndex = 0;

            ToggleCameraCommand = new RelayCommand(ExecuteToggleCamera);
            CaptureCommand = new RelayCommand(ExecuteCapture);
            ResetCommand = new RelayCommand(ExecuteReset);
            CalibrateCommand = new RelayCommand(ExecuteCalibrate);
            JogCommand = new RelayCommand(async (param) => await ExecuteJog(param));
            ReadRobotPosCommand = new RelayCommand(async (param) => await ExecuteReadRobotPos(param));
            ResetRAxisCommand = new RelayCommand(async (_) => await ExecuteResetRAxis());

            // [已删除] ToggleDragCommand 初始化

            _cameraService.NewFrameReceived += OnNewFrameReceived;

            if (!string.IsNullOrEmpty(_projectData.DutImagePath) && System.IO.File.Exists(_projectData.DutImagePath))
            {
                LoadImageFromPath(_projectData.DutImagePath);
            }
        }

        public async Task InitializeRobotStateAsync()
        {
            if (_hardwareService.ActiveRobot != null && _hardwareService.ActiveRobot.IsConnected)
            {
                try
                {
                    var pos = await _hardwareService.ActiveRobot.GetPositionAsync();
                    if (Math.Abs(pos.R - 90f) > 0.1)
                    {
                        await _hardwareService.ActiveRobot.MoveToNoWaitAsync(pos.X, pos.Y, pos.Z, 90f);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"归位失败: {ex.Message}");
                }
            }
        }

        private async Task ExecuteResetRAxis()
        {
            if (_hardwareService.ActiveRobot == null || !_hardwareService.ActiveRobot.IsConnected)
            {
                MessageBox.Show("机械臂未连接！", "错误");
                return;
            }

            try
            {
                var currentPos = await _hardwareService.ActiveRobot.GetPositionAsync();
                await _hardwareService.ActiveRobot.MoveToAsync(currentPos.X, currentPos.Y, currentPos.Z, 90f);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"重置 R 轴失败: {ex.Message}", "错误");
            }
        }

        // [已删除] ExecuteToggleDrag 方法

        private async Task ExecuteJog(object? parameter)
        {
            if (_hardwareService.ActiveRobot == null || !_hardwareService.ActiveRobot.IsConnected)
            {
                MessageBox.Show("机械臂未连接！", "错误");
                return;
            }

            string direction = parameter as string ?? "";

            // [已删除] 拖动模式下的阻挡逻辑

            float x = 0, y = 0, z = 0;
            switch (direction)
            {
                case "X+": x = JogStep; break;
                case "X-": x = -JogStep; break;
                case "Y+": y = JogStep; break;
                case "Y-": y = -JogStep; break;
                case "Z+": z = JogStep; break;
                case "Z-": z = -JogStep; break;
            }

            try
            {
                // [修改] 移除了拖动模式判断，直接执行普通的 MoveJogAsync
                // 注意：R 增量为 0，底层逻辑会维持当前角度（即 90 度）
                await _hardwareService.ActiveRobot.MoveJogAsync(x, y, z, 0);
            }
            catch (Exception ex) { MessageBox.Show("移动失败: " + ex.Message); }
        }

        private async Task ExecuteReadRobotPos(object? parameter)
        {
            if (_hardwareService.ActiveRobot == null || !_hardwareService.ActiveRobot.IsConnected)
            {
                MessageBox.Show("未连接！", "错误");
                return;
            }
            try
            {
                var pos = await _hardwareService.ActiveRobot.GetPositionAsync();

                // 强制记录 R=90
                _projectData.ScanConfig.ScanHeightZ = pos.Z;
                _projectData.ScanConfig.ScanAngleR = 90f;

                string target = parameter as string ?? "1";
                if (target == "1") { PhysicalX1 = pos.X; PhysicalY1 = pos.Y; }
                else if (target == "2") { PhysicalX2 = pos.X; PhysicalY2 = pos.Y; }
            }
            catch (Exception ex) { MessageBox.Show("读取失败: " + ex.Message); }
        }

        private void ExecuteCalibrate(object? obj)
        {
            if (_clickStep < 2) { MessageBox.Show("请先选两个点。", "提示"); return; }
            double dxPix = _pixelP2.X - _pixelP1.X;
            double dyPix = _pixelP2.Y - _pixelP1.Y;
            double dxPhy = PhysicalX2 - PhysicalX1;
            double dyPhy = PhysicalY2 - PhysicalY1;

            if (Math.Abs(dxPix) < 1.0 && Math.Abs(dyPix) < 1.0) { MessageBox.Show("两点重合！", "错误"); return; }

            double scaleX = (Math.Abs(dxPix) > 10) ? (dxPhy / dxPix) : 1.0;
            double scaleY = (Math.Abs(dyPix) > 10) ? (dyPhy / dyPix) : 1.0;
            double offsetX = PhysicalX1 - scaleX * _pixelP1.X;
            double offsetY = PhysicalY1 - scaleY * _pixelP1.Y;

            _projectData.MatrixM11 = scaleX;
            _projectData.MatrixM22 = scaleY;
            _projectData.MatrixM12 = 0;
            _projectData.MatrixM21 = 0;
            _projectData.OffsetX = offsetX;
            _projectData.OffsetY = offsetY;
            _projectData.IsCalibrated = true;

            _cameraService.StopCamera();
            CameraPreviewSource = null;
            IsCameraMode = false;

            MessageBox.Show($"校准成功！\n高度(Z): {_projectData.ScanConfig.ScanHeightZ:F2} mm\n角度(R): 90.00 °", "成功");
        }

        public void HandleImageClick(Point pixelPoint)
        {
            if (DutImageSource == null) return;
            if (_clickStep == 0) { _pixelP1 = pixelPoint; ShowP1 = true; OnPropertyChanged(nameof(P1Left)); OnPropertyChanged(nameof(P1Top)); OnPropertyChanged(nameof(PixelP1Text)); _clickStep = 1; InstructionText = "步骤2：请看下方实时画面，移动机械臂到该点上方，记录物理坐标。\n然后点击上方图片【特征点2】。"; }
            else if (_clickStep == 1) { _pixelP2 = pixelPoint; ShowP2 = true; OnPropertyChanged(nameof(P2Left)); OnPropertyChanged(nameof(P2Top)); OnPropertyChanged(nameof(PixelP2Text)); _clickStep = 2; InstructionText = "步骤3：移动机械臂到点2记录坐标，最后点击“计算校准”。"; }
        }

        private void ExecuteReset(object? obj) { _clickStep = 0; ShowP1 = false; ShowP2 = false; InstructionText = "步骤1：打开摄像头，点击【拍照】获取基准图。"; }
        private void OnNewFrameReceived(BitmapSource frame) { Application.Current.Dispatcher.Invoke(() => { CameraPreviewSource = frame; }); }
        private void ExecuteToggleCamera(object? obj) { if (!IsCameraMode) { if (CameraList.Count == 0) { MessageBox.Show("未检测到摄像头！", "提示"); return; } _cameraService.StartCamera(SelectedCameraIndex); IsCameraMode = true; } else { _cameraService.StopCamera(); IsCameraMode = false; CameraPreviewSource = null; } }
        private void ExecuteCapture(object? obj) { if (CameraPreviewSource != null) { DutImageSource = CameraPreviewSource; SaveCaptureToFile(DutImageSource); } }
        private void SaveCaptureToFile(BitmapSource image) { try { string imagesFolder = System.IO.Path.Combine(_projectFolderPath, "Images"); if (!System.IO.Directory.Exists(imagesFolder)) System.IO.Directory.CreateDirectory(imagesFolder); string fileName = $"Capture_{DateTime.Now:yyyyMMdd_HHmmss}.jpg"; string fullPath = System.IO.Path.Combine(imagesFolder, fileName); var encoder = new JpegBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using (var stream = new System.IO.FileStream(fullPath, System.IO.FileMode.Create)) { encoder.Save(stream); } _projectData.DutImagePath = fullPath; } catch (Exception ex) { MessageBox.Show($"保存图片失败: {ex.Message}", "错误"); } }
        private void LoadImageFromPath(string path) { try { var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.UriSource = new Uri(path); bitmap.EndInit(); bitmap.Freeze(); DutImageSource = bitmap; } catch { } }
        ~XYCalibViewModel() { _cameraService.StopCamera(); }
    }
}