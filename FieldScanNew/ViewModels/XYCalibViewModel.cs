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

        private bool _isDragMode = false;
        public bool IsDragMode { get => _isDragMode; set { _isDragMode = value; OnPropertyChanged(); OnPropertyChanged(nameof(DragButtonText)); OnPropertyChanged(nameof(DragButtonColor)); } }
        public string DragButtonText => IsDragMode ? "🔓 已松开 (点击锁止)" : "🔒 拖动示教 (点击松开)";
        public string DragButtonColor => IsDragMode ? "#FFCCCC" : "#DDDDDD";

        public ObservableCollection<string> CameraList { get; }
        private int _selectedCameraIndex = 0;
        public int SelectedCameraIndex { get => _selectedCameraIndex; set { _selectedCameraIndex = value; OnPropertyChanged(); } }

        private float _jogStep = 10.0f;
        public float JogStep { get => _jogStep; set { _jogStep = value; OnPropertyChanged(); } }

        private float _angleStep = 5.0f;
        public float AngleStep { get => _angleStep; set { _angleStep = value; OnPropertyChanged(); } }

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
        public ICommand ToggleDragCommand { get; }

        public XYCalibViewModel(ProjectData projectData, string projectFolderPath)
        {
            _projectData = projectData;
            _projectFolderPath = projectFolderPath;
            _hardwareService = HardwareService.Instance;
            _cameraService = new CameraService();
            CameraList = new ObservableCollection<string>(_cameraService.GetCameraList());
            if (CameraList.Count > 0) SelectedCameraIndex = 0;

            ToggleCameraCommand = new RelayCommand(ExecuteToggleCamera);
            CaptureCommand = new RelayCommand(ExecuteCapture);
            ResetCommand = new RelayCommand(ExecuteReset);
            CalibrateCommand = new RelayCommand(ExecuteCalibrate);
            JogCommand = new RelayCommand(async (param) => await ExecuteJog(param));
            ReadRobotPosCommand = new RelayCommand(async (param) => await ExecuteReadRobotPos(param));
            ToggleDragCommand = new RelayCommand(async (_) => await ExecuteToggleDrag());

            _cameraService.NewFrameReceived += OnNewFrameReceived;

            if (!string.IsNullOrEmpty(_projectData.DutImagePath) && System.IO.File.Exists(_projectData.DutImagePath))
            {
                LoadImageFromPath(_projectData.DutImagePath);
            }
        }

        private async Task ExecuteToggleDrag()
        {
            if (_hardwareService.ActiveRobot == null || !_hardwareService.ActiveRobot.IsConnected)
            {
                MessageBox.Show("机械臂未连接，无法启用拖动示教！", "错误");
                return;
            }

            try
            {
                if (!IsDragMode)
                {
                    var currentPos = await _hardwareService.ActiveRobot.GetPositionAsync();
                    await _hardwareService.ActiveRobot.SetDragModeAsync(true);
                    IsDragMode = true;
                    await Task.Delay(500);
                    await _hardwareService.ActiveRobot.MoveToNoWaitAsync(currentPos.X, currentPos.Y, currentPos.Z, currentPos.R);
                }
                else
                {
                    await _hardwareService.ActiveRobot.SetDragModeAsync(false);
                    IsDragMode = false;
                }
            }
            catch (Exception ex) { MessageBox.Show($"切换拖动模式失败: {ex.Message}", "错误"); IsDragMode = false; }
        }

        private async Task ExecuteJog(object? parameter)
        {
            if (_hardwareService.ActiveRobot == null || !_hardwareService.ActiveRobot.IsConnected) { MessageBox.Show("机械臂未连接！", "错误"); return; }

            string direction = parameter as string ?? "";

            if (IsDragMode && (direction.StartsWith("X") || direction.StartsWith("Y")))
            {
                MessageBox.Show("XY轴已松开，请直接用手拖动。\n若要微调，请先点击锁止按钮。", "提示");
                return;
            }

            float x = 0, y = 0, z = 0, r = 0;
            switch (direction)
            {
                case "X+": x = JogStep; break;
                case "X-": x = -JogStep; break;
                case "Y+": y = JogStep; break;
                case "Y-": y = -JogStep; break;
                case "Z+": z = JogStep; break;
                case "Z-": z = -JogStep; break;
                case "R+": r = AngleStep; break;
                case "R-": r = -AngleStep; break;
            }

            try
            {
                if (IsDragMode)
                {
                    var currentPos = await _hardwareService.ActiveRobot.GetPositionAsync();
                    float targetX = currentPos.X + x;
                    float targetY = currentPos.Y + y;
                    float targetZ = currentPos.Z + z;
                    float targetR = currentPos.R + r;
                    await _hardwareService.ActiveRobot.MoveToNoWaitAsync(targetX, targetY, targetZ, targetR);
                }
                else
                {
                    await _hardwareService.ActiveRobot.MoveJogAsync(x, y, z, r);
                }
            }
            catch (Exception ex) { MessageBox.Show("移动失败: " + ex.Message); }
        }

        private async Task ExecuteReadRobotPos(object? parameter)
        {
            if (_hardwareService.ActiveRobot == null || !_hardwareService.ActiveRobot.IsConnected) { MessageBox.Show("未连接！", "错误"); return; }
            try { var pos = await _hardwareService.ActiveRobot.GetPositionAsync(); _projectData.ScanConfig.ScanHeightZ = pos.Z; _projectData.ScanConfig.ScanAngleR = pos.R; string target = parameter as string ?? "1"; if (target == "1") { PhysicalX1 = pos.X; PhysicalY1 = pos.Y; } else if (target == "2") { PhysicalX2 = pos.X; PhysicalY2 = pos.Y; } } catch (Exception ex) { MessageBox.Show("读取失败: " + ex.Message); }
        }

        private void ExecuteCalibrate(object? obj) { if (_clickStep < 2) { MessageBox.Show("请先选两个点。", "提示"); return; } double dxPix = _pixelP2.X - _pixelP1.X; double dyPix = _pixelP2.Y - _pixelP1.Y; double dxPhy = PhysicalX2 - PhysicalX1; double dyPhy = PhysicalY2 - PhysicalY1; if (Math.Abs(dxPix) < 1.0 && Math.Abs(dyPix) < 1.0) { MessageBox.Show("两点重合！", "错误"); return; } double scaleX = (Math.Abs(dxPix) > 10) ? (dxPhy / dxPix) : 1.0; double scaleY = (Math.Abs(dyPix) > 10) ? (dyPhy / dyPix) : 1.0; double offsetX = PhysicalX1 - scaleX * _pixelP1.X; double offsetY = PhysicalY1 - scaleY * _pixelP1.Y; _projectData.MatrixM11 = scaleX; _projectData.MatrixM22 = scaleY; _projectData.MatrixM12 = 0; _projectData.MatrixM21 = 0; _projectData.OffsetX = offsetX; _projectData.OffsetY = offsetY; _projectData.IsCalibrated = true; MessageBox.Show($"校准成功！\n高度(Z): {_projectData.ScanConfig.ScanHeightZ:F2} mm\n角度(R): {_projectData.ScanConfig.ScanAngleR:F2} °", "成功"); }

        // **修正1：允许随时选点**
        public void HandleImageClick(Point pixelPoint)
        {
            // 删除了 IsCameraMode 的拦截，只要有底图(DutImageSource)就可以选点
            if (DutImageSource == null) return;

            if (_clickStep == 0) { _pixelP1 = pixelPoint; ShowP1 = true; OnPropertyChanged(nameof(P1Left)); OnPropertyChanged(nameof(P1Top)); OnPropertyChanged(nameof(PixelP1Text)); _clickStep = 1; InstructionText = "步骤2：请看下方实时画面，移动机械臂到该点上方，记录物理坐标。\n然后点击上方图片【特征点2】。"; }
            else if (_clickStep == 1) { _pixelP2 = pixelPoint; ShowP2 = true; OnPropertyChanged(nameof(P2Left)); OnPropertyChanged(nameof(P2Top)); OnPropertyChanged(nameof(PixelP2Text)); _clickStep = 2; InstructionText = "步骤3：移动机械臂到点2记录坐标，最后点击“计算校准”。"; }
        }

        private void ExecuteReset(object? obj) { _clickStep = 0; ShowP1 = false; ShowP2 = false; InstructionText = "步骤1：打开摄像头，点击【拍照】获取基准图。"; }
        private void OnNewFrameReceived(BitmapSource frame) { Application.Current.Dispatcher.Invoke(() => { CameraPreviewSource = frame; }); }

        // **修正2：开关摄像头时不重置已选点**
        private void ExecuteToggleCamera(object? obj)
        {
            if (!IsCameraMode)
            {
                if (CameraList.Count == 0) { MessageBox.Show("未检测到摄像头！", "提示"); return; }
                _cameraService.StartCamera(SelectedCameraIndex);
                IsCameraMode = true;
                // ExecuteReset(null); // 删除此行，保留点位
            }
            else
            {
                _cameraService.StopCamera();
                IsCameraMode = false;
                CameraPreviewSource = null;
            }
        }

        // **修正3：拍照仅更新底图，不关闭摄像头**
        private void ExecuteCapture(object? obj)
        {
            if (CameraPreviewSource != null)
            {
                // 更新静态底图
                DutImageSource = CameraPreviewSource;

                // 不停止摄像头，不切换模式
                // _cameraService.StopCamera(); 
                // IsCameraMode = false; 

                SaveCaptureToFile(DutImageSource);
            }
        }

        private void SaveCaptureToFile(BitmapSource image) { try { string imagesFolder = System.IO.Path.Combine(_projectFolderPath, "Images"); if (!System.IO.Directory.Exists(imagesFolder)) System.IO.Directory.CreateDirectory(imagesFolder); string fileName = $"Capture_{DateTime.Now:yyyyMMdd_HHmmss}.jpg"; string fullPath = System.IO.Path.Combine(imagesFolder, fileName); var encoder = new JpegBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using (var stream = new System.IO.FileStream(fullPath, System.IO.FileMode.Create)) { encoder.Save(stream); } _projectData.DutImagePath = fullPath; } catch (Exception ex) { MessageBox.Show($"保存图片失败: {ex.Message}", "错误"); } }
        private void LoadImageFromPath(string path) { try { var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.UriSource = new Uri(path); bitmap.EndInit(); bitmap.Freeze(); DutImageSource = bitmap; } catch { } }
        ~XYCalibViewModel() { _cameraService.StopCamera(); }
    }
}