using FieldScanNew.Infrastructure;
using FieldScanNew.Models;
using FieldScanNew.Services;
using FieldScanNew.Views;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using OxyPlot.Annotations;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using MessageBox = System.Windows.MessageBox;

using MathNet.Numerics;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;
using Microsoft.Win32;
using Newtonsoft.Json; // 需要添加
using Newtonsoft.Json.Linq; // 需要自行添加
using System.Diagnostics;
using System.Collections.Generic;
using System.Windows;

namespace FieldScanNew.ViewModels
{
    public class MainViewModel : ViewModelBase
    {
        // ... (属性部分保持不变，请保留原有的属性定义) ...
        private readonly IDialogService _dialogService;
        private readonly HardwareService _hardwareService;
        public PlotModel HeatmapModel { get; set; }
        public PlotModel SpectrumModel { get; set; }
        public ObservableCollection<ProjectViewModel> Projects { get; }
        private BitmapSource? _dutImageSource;
        public BitmapSource? DutImageSource { get => _dutImageSource; set { _dutImageSource = value; OnPropertyChanged(); UpdatePlotBackground(); } }
        private IStepViewModel? _selectedStep;
        public IStepViewModel? SelectedStep
        {
            get => _selectedStep;
            set
            {
                if (Equals(value, _selectedStep)) return;
                _selectedStep = value;
                OnPropertyChanged();
                if (_selectedStep != null && !(_selectedStep is ProjectViewModel) && !(_selectedStep is MeasurementViewModel))
                {
                    _dialogService.ShowDialog(_selectedStep);
                    LoadDutImage();
                }
            }
        }
        private ScanSettings _currentScanSettings;
        public ScanSettings CurrentScanSettings
        {
            get => _currentScanSettings;
            set { if (_currentScanSettings != null) _currentScanSettings.PropertyChanged -= OnSettingsChanged; _currentScanSettings = value; if (_currentScanSettings != null) _currentScanSettings.PropertyChanged += OnSettingsChanged; OnPropertyChanged(); }
        }
        private InstrumentSettings _currentInstrumentSettings;
        public InstrumentSettings CurrentInstrumentSettings
        {
            get => _currentInstrumentSettings;
            set { if (_currentInstrumentSettings != null) _currentInstrumentSettings.PropertyChanged -= OnSettingsChanged; _currentInstrumentSettings = value; if (_currentInstrumentSettings != null) _currentInstrumentSettings.PropertyChanged += OnSettingsChanged; OnPropertyChanged(); }
        }
        private bool _isScanning = false;
        public bool IsScanning { get => _isScanning; set { _isScanning = value; OnPropertyChanged(); } }
        private CancellationTokenSource? _cancellationTokenSource;
        public ICommand AddNewProjectCommand { get; }
        public ICommand LoadProjectCommand { get; }
        public ICommand StartScanCommand { get; }
        public ICommand QBCStartScanCommand { get; }
        public ICommand StopScanCommand { get; }

        // QBC输入数据模型（传给计算函数）
        public class QbcInputData
        {
            public HyperParams HyperParams { get; set; }
            public List<SampledPoint> SampledPoints { get; set; }
        }

        // QBC超参数模型
        public class HyperParams
        {
            public double X_min { get; set; }
            public double X_max { get; set; }
            public double Y_min { get; set; }
            public double Y_max { get; set; }
            public int Nx { get; set; }
            public int Ny { get; set; }
            public int Uncertainty_size { get; set; }
        }

        // QBC输出数据模型（计算函数返回）
        public class QbcOutputData
        {
            public string Status { get; set; } // "success" / "failed"
            public string Message { get; set; }
            public double Next_x { get; set; }
            public double Next_y { get; set; }
        }

        // 采样点模型（存储坐标+幅值）
        public class SampledPoint
        {
            public float X { get; set; }
            public float Y { get; set; }
            public double Magnitude { get; set; } // 校准后的幅值（dBuV/m）
        }

        public enum RbfKernel
        {
            Linear,
            Cubic,
            ThinPlateSpline,
            Quintic
        }

        public MainViewModel()
        {
            _dialogService = new DialogService();
            _hardwareService = HardwareService.Instance;

            HeatmapModel = new PlotModel { Title = "近场热力图" };
            HeatmapModel.PlotType = PlotType.Cartesian;
            HeatmapModel.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, IsAxisVisible = false });
            HeatmapModel.Axes.Add(new LinearAxis { Position = AxisPosition.Left, IsAxisVisible = false });
            var palette = OxyPalettes.Jet(100);
            var transparentColors = palette.Colors.Select(c => OxyColor.FromAColor(180, c));
            HeatmapModel.Axes.Add(new LinearColorAxis { Position = AxisPosition.Right, Palette = new OxyPalette(transparentColors), Title = "信号强度 (dBuV/m)" });

            SpectrumModel = new PlotModel { Title = "实时频谱 (Trace)" };
            SpectrumModel.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Title = "Index" });
            SpectrumModel.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Title = "Amplitude (dBm)" });

            Projects = new ObservableCollection<ProjectViewModel>();
            _currentScanSettings = new ScanSettings();
            _currentInstrumentSettings = new InstrumentSettings();

            AddNewProjectCommand = new RelayCommand(ExecuteAddNewProject);
            LoadProjectCommand = new RelayCommand(ExecuteLoadProject);
            StartScanCommand = new RelayCommand(async _ => await ExecuteStartScan(), _ => !IsScanning);
            QBCStartScanCommand = new RelayCommand(async _ => await QBCExecuteStartScan(), _ => !IsScanning);
            StopScanCommand = new RelayCommand(_ => ExecuteStopScan(), _ => IsScanning);

            CurrentScanSettings.PropertyChanged += OnSettingsChanged;
            CurrentInstrumentSettings.PropertyChanged += OnSettingsChanged;
        }

        // ... (LoadDutImage, UpdatePlotBackground, OnSettingsChanged... 保持不变，为了节省篇幅已省略) ...
        // 请务必保留您原文件中的这些辅助方法
        private void LoadDutImage() { var project = Projects.FirstOrDefault(p => p.IsSelected); if (project != null && !string.IsNullOrEmpty(project.ProjectData.DutImagePath) && File.Exists(project.ProjectData.DutImagePath)) { try { var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.UriSource = new Uri(project.ProjectData.DutImagePath); bitmap.EndInit(); bitmap.Freeze(); DutImageSource = bitmap; } catch { DutImageSource = null; } } else { DutImageSource = null; } }
        private void UpdatePlotBackground() { HeatmapModel.Annotations.Clear(); if (DutImageSource == null) { HeatmapModel.InvalidatePlot(true); return; } var project = Projects.FirstOrDefault(p => p.IsSelected); double xMin, xMax, yMin, yMax; BitmapSource displayBitmap = DutImageSource; if (project == null || !project.ProjectData.IsCalibrated) { xMin = 0; xMax = DutImageSource.PixelWidth; yMin = 0; yMax = DutImageSource.PixelHeight; } else { var pd = project.ProjectData; double pixW = DutImageSource.PixelWidth; double pixH = DutImageSource.PixelHeight; double physX_Left = pd.OffsetX; double physX_Right = pixW * pd.MatrixM11 + pd.OffsetX; double physY_Top = pd.OffsetY; double physY_Bottom = pixH * pd.MatrixM22 + pd.OffsetY; xMin = Math.Min(physX_Left, physX_Right); xMax = Math.Max(physX_Left, physX_Right); yMin = Math.Min(physY_Top, physY_Bottom); yMax = Math.Max(physY_Top, physY_Bottom); bool flipX = pd.MatrixM11 < 0; bool flipY = pd.MatrixM22 > 0; if (flipX || flipY) { try { var transformGroup = new TransformGroup(); transformGroup.Children.Add(new ScaleTransform(flipX ? -1 : 1, flipY ? -1 : 1)); double tx = flipX ? pixW : 0; double ty = flipY ? pixH : 0; transformGroup.Children.Add(new TranslateTransform(tx, ty)); displayBitmap = new TransformedBitmap(DutImageSource, new MatrixTransform(flipX ? -1 : 1, 0, 0, flipY ? -1 : 1, tx, ty)); } catch { displayBitmap = DutImageSource; } } } try { using (var stream = new MemoryStream()) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(displayBitmap)); encoder.Save(stream); var imageAnnotation = new ImageAnnotation { ImageSource = new OxyImage(stream.ToArray()), X = new PlotLength((xMin + xMax) / 2, PlotLengthUnit.Data), Y = new PlotLength((yMin + yMax) / 2, PlotLengthUnit.Data), Width = new PlotLength(xMax - xMin, PlotLengthUnit.Data), Height = new PlotLength(yMax - yMin, PlotLengthUnit.Data), Layer = AnnotationLayer.BelowSeries, Interpolate = true }; HeatmapModel.Annotations.Add(imageAnnotation); } } catch (Exception ex) { Console.WriteLine("BG Error: " + ex.Message); } HeatmapModel.ResetAllAxes(); HeatmapModel.InvalidatePlot(true); }
        private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e) { var selectedProject = Projects.FirstOrDefault(p => p.IsSelected); if (selectedProject != null) AutoSaveCurrentProject(selectedProject); }
        private void ExecuteAddNewProject(object? parameter) { try { var inputDialog = new InputDialog("请输入新项目的名称:", "新项目"); if (inputDialog.ShowDialog() != true) return; string projectName = inputDialog.Answer; if (string.IsNullOrWhiteSpace(projectName)) return; var folderDialog = new System.Windows.Forms.FolderBrowserDialog { Description = "请选择项目的存放路径" }; if (folderDialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return; string projectPath = Path.Combine(folderDialog.SelectedPath, projectName); if (Directory.Exists(projectPath)) { MessageBox.Show("同名项目文件夹已存在！", "错误"); return; } Directory.CreateDirectory(projectPath); var newProject = new ProjectViewModel(projectName, projectPath, this); Projects.Add(newProject); foreach (var proj in Projects.Where(p => p != newProject)) proj.IsSelected = false; newProject.IsSelected = true; LoadProjectDataIntoViewModel(newProject); AutoSaveCurrentProject(newProject); } catch (Exception ex) { MessageBox.Show("创建新项目时发生严重错误: " + ex.Message, "错误"); } }
        private void ExecuteLoadProject(object? parameter) { try { var openFileDialog = new OpenFileDialog { Filter = "项目文件 (*.json)|*.json" }; if (openFileDialog.ShowDialog() == true) { string filePath = openFileDialog.FileName; string fileContent = File.ReadAllText(filePath); if (string.IsNullOrWhiteSpace(fileContent)) { MessageBox.Show("项目文件为空或已损坏。", "错误"); return; } var projectData = System.Text.Json.JsonSerializer.Deserialize<ProjectData>(fileContent); if (projectData == null) { MessageBox.Show("无法解析项目文件。", "错误"); return; } string projectFolder = Path.GetDirectoryName(filePath) ?? string.Empty; if (string.IsNullOrEmpty(projectFolder)) { MessageBox.Show("无法获取项目文件夹路径。", "错误"); return; } var loadedProject = new ProjectViewModel(projectData.ProjectName, projectFolder, this) { ProjectData = projectData }; if (projectData.MeasurementNames != null) { foreach (var name in projectData.MeasurementNames) loadedProject.Measurements.Add(new MeasurementViewModel(name, loadedProject)); } Projects.Add(loadedProject); foreach (var proj in Projects.Where(p => p != loadedProject)) proj.IsSelected = false; loadedProject.IsSelected = true; LoadProjectDataIntoViewModel(loadedProject); } } catch (Exception ex) { MessageBox.Show("加载项目时发生严重错误: " + ex.Message, "错误"); } }
        public void AutoSaveCurrentProject(ProjectViewModel project) { if (project?.ProjectData == null) return; project.ProjectData.ScanConfig = this.CurrentScanSettings; project.ProjectData.InstrumentConfig = this.CurrentInstrumentSettings; project.ProjectData.MeasurementNames = project.Measurements.Select(m => m.DisplayName).ToList(); try { string filePath = Path.Combine(project.ProjectFolderPath, "project.json"); var options = new JsonSerializerOptions { WriteIndented = true }; string jsonString = System.Text.Json.JsonSerializer.Serialize(project.ProjectData, options); File.WriteAllText(filePath, jsonString); } catch (Exception ex) { Console.WriteLine($"自动保存失败: {ex.Message}"); } }

        // **确保同步设置对象**
        internal void LoadProjectDataIntoViewModel(ProjectViewModel? project)
        {
            if (project?.ProjectData == null) { CurrentScanSettings = new ScanSettings(); CurrentInstrumentSettings = new InstrumentSettings(); return; }
            CurrentScanSettings = project.ProjectData.ScanConfig ?? new ScanSettings();
            CurrentInstrumentSettings = project.ProjectData.InstrumentConfig ?? new InstrumentSettings();
            foreach (var measurement in project.Measurements)
            {
                var instVm = measurement.Steps.OfType<InstrumentSetupViewModel>().FirstOrDefault();
                if (instVm != null) instVm.InstrumentSettings = CurrentInstrumentSettings;

                // **关键：同步给高级设置界面**
                var settingVm = measurement.Steps.OfType<ScanSettingsViewModel>().FirstOrDefault();
                if (settingVm != null) settingVm.Settings = CurrentInstrumentSettings;

                var scanVm = measurement.Steps.OfType<ScanAreaViewModel>().FirstOrDefault();
                if (scanVm != null) scanVm.Settings = CurrentScanSettings;
            }
            LoadDutImage();
        }

        private async Task ExecuteStartScan()
        {
            if (_hardwareService.ActiveRobot == null || !_hardwareService.ActiveRobot.IsConnected ||
               _hardwareService.ActiveDevice == null || !_hardwareService.ActiveDevice.IsConnected)
            { MessageBox.Show("请先连接机械臂和测量仪器！", "提示"); return; }

            var selectedProject = Projects.FirstOrDefault(p => p.IsSelected);
            if (selectedProject == null) { MessageBox.Show("请先选择一个项目！", "提示"); return; }

            UpdatePlotBackground();

            // =========================================================
            // **核心修正：扫描前强制下发参数**
            // 这一步会把最新的 RBW/VBW 发送给仪器
            // =========================================================
            try
            {
                await _hardwareService.ActiveDevice.ConnectAsync(CurrentInstrumentSettings);
            }
            catch (Exception ex) { MessageBox.Show($"更新配置失败: {ex.Message}", "警告"); }
            // =========================================================

            var scanSettings = selectedProject.ProjectData.ScanConfig;
            if (scanSettings.NumX < 2 || scanSettings.NumY < 2) { MessageBox.Show("扫描点数必须大于等于2！", "错误"); return; }

            IsScanning = true;
            _cancellationTokenSource = new CancellationTokenSource();

            // 热力图和频谱初始化

            try
            {
                double xMin = Math.Min(scanSettings.StartX, scanSettings.StopX);
                double xMax = Math.Max(scanSettings.StartX, scanSettings.StopX);
                double yMin = Math.Min(scanSettings.StartY, scanSettings.StopY);
                double yMax = Math.Max(scanSettings.StartY, scanSettings.StopY);

                var heatMapData = new double[scanSettings.NumX, scanSettings.NumY];
                var heatMapSeries = new HeatMapSeries
                {
                    X0 = xMin,
                    X1 = xMax,
                    Y0 = yMin,
                    Y1 = yMax,
                    Interpolate = true,
                    RenderMethod = HeatMapRenderMethod.Bitmap,
                    Data = heatMapData,
                    // **核心修正：改为边缘对齐，强制热力图不超出 [xMin, xMax] 范围**
                    CoordinateDefinition = HeatMapCoordinateDefinition.Edge
                };

                HeatmapModel.Series.Clear(); HeatmapModel.Series.Add(heatMapSeries); HeatmapModel.ResetAllAxes(); HeatmapModel.InvalidatePlot(true);

                var spectrumSeries = new LineSeries { Title = "Live Trace", Color = OxyColors.Blue, StrokeThickness = 1 };
                SpectrumModel.Series.Clear(); SpectrumModel.Series.Add(spectrumSeries); SpectrumModel.InvalidatePlot(true);

                var sbPeak = new StringBuilder(); sbPeak.AppendLine("PhysicalX(mm),PhysicalY(mm),MaxAmplitude(dBm)");
                var sbFull = new StringBuilder(); bool isFullHeaderWritten = false;

                //核心扫描循环

                for (int j = 0; j < scanSettings.NumY; j++)
                {
                    for (int i = 0; i < scanSettings.NumX; i++)
                    {
                        if (_cancellationTokenSource.Token.IsCancellationRequested) { MessageBox.Show("扫描已停止。", "提示"); IsScanning = false; return; }

                        //步骤2：计算当前点位的目标坐标
                        float targetX = scanSettings.StartX + i * (scanSettings.StopX - scanSettings.StartX) / (scanSettings.NumX - 1);
                        float targetY = scanSettings.StartY + j * (scanSettings.StopY - scanSettings.StartY) / (scanSettings.NumY - 1);

                        //步骤3：移动机械臂到目标点位
                        await _hardwareService.ActiveRobot.MoveToAsync(targetX, targetY, scanSettings.ScanHeightZ, scanSettings.ScanAngleR);

                        // 使用 0ms 延迟，因为底层驱动已包含 *WAI 等待
                        double[] traceData = await _hardwareService.ActiveDevice.GetTraceDataAsync(0);

                        if (traceData.Length > 0)
                        {
                            double maxVal = traceData.Max();
                            double ratioX = (targetX - xMin) / (xMax - xMin);
                            double ratioY = (targetY - yMin) / (yMax - yMin);
                            int arrayX = (int)Math.Round(ratioX * (scanSettings.NumX - 1));
                            int arrayY = (int)Math.Round(ratioY * (scanSettings.NumY - 1));
                            arrayX = Math.Max(0, Math.Min(arrayX, scanSettings.NumX - 1));
                            arrayY = Math.Max(0, Math.Min(arrayY, scanSettings.NumY - 1));

                            heatMapData[arrayX, arrayY] = maxVal;
                            HeatmapModel.InvalidatePlot(true);

                            spectrumSeries.Points.Clear();
                            for (int k = 0; k < traceData.Length; k++) spectrumSeries.Points.Add(new DataPoint(k, traceData[k]));
                            SpectrumModel.InvalidatePlot(true);

                            sbPeak.AppendLine($"{targetX:F3},{targetY:F3},{maxVal:F3}");

                            if (!isFullHeaderWritten)
                            {
                                sbFull.Append("PhysicalX(mm),PhysicalY(mm)");
                                for (int k = 0; k < traceData.Length; k++) sbFull.Append($",P{k}");
                                sbFull.AppendLine();
                                isFullHeaderWritten = true;
                            }
                            sbFull.Append($"{targetX:F3},{targetY:F3}");
                            foreach (var val in traceData) sbFull.Append($",{val:F3}");
                            sbFull.AppendLine();
                        }
                    }
                }

                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                SaveScanDataToCsv(selectedProject, sbPeak.ToString(), $"ScanResult_Peak_{timestamp}.csv");
                SaveScanDataToCsv(selectedProject, sbFull.ToString(), $"ScanResult_FullTrace_{timestamp}.csv");

                MessageBox.Show("扫描完成！", "成功");
            }
            catch (Exception ex) { MessageBox.Show("扫描错误: " + ex.Message, "错误"); }
            finally { IsScanning = false; }
        }

            private async Task QBCExecuteStartScan()
        {
            // ===================== 原有逻辑：前置校验（完全保留） =====================
            if (_hardwareService.ActiveRobot == null || !_hardwareService.ActiveRobot.IsConnected ||
            _hardwareService.ActiveDevice == null || !_hardwareService.ActiveDevice.IsConnected)
            { MessageBox.Show("请先连接机械臂和测量仪器！", "提示"); return; }

            var selectedProject = Projects.FirstOrDefault(p => p.IsSelected);
            if (selectedProject == null) { MessageBox.Show("请先选择一个项目！", "提示"); return; }

            UpdatePlotBackground();

            // 核心修正：扫描前强制下发参数（完全保留）
            try
            {
                await _hardwareService.ActiveDevice.ConnectAsync(CurrentInstrumentSettings);
            }
            catch (Exception ex) { MessageBox.Show($"更新配置失败: {ex.Message}", "警告"); }

            var scanSettings = selectedProject.ProjectData.ScanConfig;
            if (scanSettings.NumX < 2 || scanSettings.NumY < 2) { MessageBox.Show("扫描点数必须大于等于2！", "错误"); return; }

            IsScanning = true;
            _cancellationTokenSource = new CancellationTokenSource();

            // ===================== 原有逻辑：可视化初始化（完全保留） =====================
            try
            {
                double xMin = Math.Min(scanSettings.StartX, scanSettings.StopX);
                double xMax = Math.Max(scanSettings.StartX, scanSettings.StopX);
                double yMin = Math.Min(scanSettings.StartY, scanSettings.StopY);
                double yMax = Math.Max(scanSettings.StartY, scanSettings.StopY);

                var heatMapData = new double[scanSettings.NumX, scanSettings.NumY];
                var heatMapSeries = new HeatMapSeries
                {
                    X0 = xMin,
                    X1 = xMax,
                    Y0 = yMin,
                    Y1 = yMax,
                    Interpolate = true,
                    RenderMethod = HeatMapRenderMethod.Bitmap,
                    Data = heatMapData,
                    CoordinateDefinition = HeatMapCoordinateDefinition.Edge
                };

                HeatmapModel.Series.Clear(); HeatmapModel.Series.Add(heatMapSeries); HeatmapModel.ResetAllAxes(); HeatmapModel.InvalidatePlot(true);

                var spectrumSeries = new LineSeries { Title = "Live Trace", Color = OxyColors.Blue, StrokeThickness = 1 };
                SpectrumModel.Series.Clear(); SpectrumModel.Series.Add(spectrumSeries); SpectrumModel.InvalidatePlot(true);

                var sbPeak = new StringBuilder(); sbPeak.AppendLine("PhysicalX(mm),PhysicalY(mm),MaxAmplitude(dBm)");
                var sbFull = new StringBuilder(); bool isFullHeaderWritten = false;

                // ===================== 替换逻辑：QBC自适应采样（核心） =====================
                // 1. QBC参数初始化（复用scanSettings中的变量）
                int sumSampleCount = scanSettings.NumX * scanSettings.NumY;
                const double a = 3.13;
                const double b = 0.602;
                const int minPoints = 4;
                int targetSampleCount = (int)Math.Round(a * Math.Pow(sumSampleCount, b));
                targetSampleCount = Math.Max(minPoints, Math.Min(targetSampleCount, sumSampleCount));

                // 2. 生成均匀初始采样点
                int initMaxCount = targetSampleCount - 1;
                int initPointCount = Math.Max(4, (int)Math.Round(initMaxCount * 0.2));
                initPointCount = Math.Min(initPointCount, initMaxCount);

                // 计算初始采样点的行列间隔（均匀覆盖）
                int gridCols = (int)Math.Round(Math.Sqrt(initPointCount * (double)scanSettings.NumX / scanSettings.NumY));
                int gridRows = (int)Math.Round((double)initPointCount / gridCols);
                gridCols = Math.Max(2, Math.Min(gridCols, scanSettings.NumX));
                gridRows = Math.Max(2, Math.Min(gridRows, scanSettings.NumY));
                initPointCount = gridCols * gridRows;

                int xStepIndex = (scanSettings.NumX - 1) / (gridCols - 1);
                int yStepIndex = (scanSettings.NumY - 1) / (gridRows - 1);

                // 初始化采样点列表
                List<SampledPoint> sampledPoints = new List<SampledPoint>();

                // 3. 初始点采样（替换原有第一层循环）
                for (int row = 0; row < gridRows; row++)
                {
                    // 取消检查（保留原有取消机制）
                    if (_cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        MessageBox.Show("扫描已停止。", "提示");
                        IsScanning = false;
                        return;
                    }

                    int yIndex = row * yStepIndex;
                    yIndex = Math.Min(yIndex, scanSettings.NumY - 1);
                    // 计算初始点Y坐标（复用原有坐标计算逻辑）
                    float targetY = scanSettings.StartY + yIndex * (scanSettings.StopY - scanSettings.StartY) / (scanSettings.NumY - 1);

                    for (int col = 0; col < gridCols; col++)
                    {
                        // 取消检查
                        if (_cancellationTokenSource.Token.IsCancellationRequested)
                        {
                            MessageBox.Show("扫描已停止。", "提示");
                            IsScanning = false;
                            return;
                        }

                        int xIndex = col * xStepIndex;
                        xIndex = Math.Min(xIndex, scanSettings.NumX - 1);
                        // 计算初始点X坐标
                        float targetX = scanSettings.StartX + xIndex * (scanSettings.StopX - scanSettings.StartX) / (scanSettings.NumX - 1);

                        // 机械臂移动（替换为原有_hardwareService调用）
                        await _hardwareService.ActiveRobot.MoveToAsync(targetX, targetY, scanSettings.ScanHeightZ, scanSettings.ScanAngleR);

                        // 读取仪器数据（保留原有逻辑）
                        double[] traceData = await _hardwareService.ActiveDevice.GetTraceDataAsync(0);
                        if (traceData.Length == 0) continue;

                        double maxVal = traceData.Max();
                        // 计算热力图索引（保留原有逻辑）
                        double ratioX = (targetX - xMin) / (xMax - xMin);
                        double ratioY = (targetY - yMin) / (yMax - yMin);
                        int arrayX = (int)Math.Round(ratioX * (scanSettings.NumX - 1));
                        int arrayY = (int)Math.Round(ratioY * (scanSettings.NumY - 1));
                        arrayX = Math.Max(0, Math.Min(arrayX, scanSettings.NumX - 1));
                        arrayY = Math.Max(0, Math.Min(arrayY, scanSettings.NumY - 1));

                        // 更新热力图和频谱（保留原有逻辑）
                        heatMapData[arrayX, arrayY] = maxVal;
                        HeatmapModel.InvalidatePlot(true);

                        spectrumSeries.Points.Clear();
                        for (int k = 0; k < traceData.Length; k++) spectrumSeries.Points.Add(new DataPoint(k, traceData[k]));
                        SpectrumModel.InvalidatePlot(true);

                        // 缓存CSV数据（保留原有逻辑）
                        sbPeak.AppendLine($"{targetX:F3},{targetY:F3},{maxVal:F3}");
                        if (!isFullHeaderWritten)
                        {
                            sbFull.Append("PhysicalX(mm),PhysicalY(mm)");
                            for (int k = 0; k < traceData.Length; k++) sbFull.Append($",P{k}");
                            sbFull.AppendLine();
                            isFullHeaderWritten = true;
                        }
                        sbFull.Append($"{targetX:F3},{targetY:F3}");
                        foreach (var val in traceData) sbFull.Append($",{val:F3}");
                        sbFull.AppendLine();

                        // // 校准计算（复用你QBC代码中的逻辑，幅值转换）
                        // double correctedMagnitude = 0;
                        // // 假设你原有仪器参数中包含中心频率/跨度（需替换为实际变量）
                        // double startFreq = CurrentInstrumentSettings.CenterFrequencyHz - (CurrentInstrumentSettings.SpanHz / 2);
                        // double stepFreq = CurrentInstrumentSettings.SpanHz / (traceData.Length - 1);
                        // for (int j = 0; j < traceData.Length; j++)
                        // {
                        //     double freq = startFreq + j * stepFreq;
                        //     double powerDbm = traceData[j];
                        //     double powerDbuV = powerDbm + 107; // 50欧姆转换
                        //     // 替换为你原有ActiveProbe的调用逻辑
                        //     double antennaFactor = ActiveProbe?.GetFactorAtFrequency(freq) ?? 0;
                        //     correctedMagnitude = powerDbuV + antennaFactor;
                        // }

                        // 添加到采样点列表（用于QBC迭代）
                        sampledPoints.Add(new SampledPoint
                        {
                            X = targetX,
                            Y = targetY,
                            Magnitude = maxVal
                        });
                    }
                }

                // 4. QBC迭代采样（核心逻辑）
                while (sampledPoints.Count < targetSampleCount)
                {
                    // 取消检查
                    if (_cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        MessageBox.Show("扫描已停止。", "提示");
                        IsScanning = false;
                        return;
                    }

                    // 4.1 构建QBC输入数据
                    var inputData = new QbcInputData
                    {
                        HyperParams = new HyperParams
                        {
                            X_min = xMin,
                            X_max = xMax,
                            Y_min = yMin,
                            Y_max = yMax,
                            Nx = scanSettings.NumX,
                            Ny = scanSettings.NumY,
                            Uncertainty_size = targetSampleCount
                        },
                        SampledPoints = sampledPoints
                    };

                    // 4.2 调用QBC计算函数获取下一个点（你原有CalculateNextSamplePoint方法）
                    QbcOutputData nextPointData;
                    try
                    {
                        nextPointData = CalculateNextSamplePoint(inputData); // 需确保该方法已实现
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"计算下一个采样点失败：{ex.Message}", "警告");
                        break;
                    }

                    if (nextPointData.Status != "success")
                    {
                        MessageBox.Show($"QBC计算失败：{nextPointData.Message}", "警告");
                        break;
                    }

                    float nextX = (float)nextPointData.Next_x;
                    float nextY = (float)nextPointData.Next_y;

                    // 4.3 机械臂移动到新点（保留原有逻辑）
                    try
                    {
                        await _hardwareService.ActiveRobot.MoveToAsync(nextX, nextY, scanSettings.ScanHeightZ, scanSettings.ScanAngleR);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"机械臂移动失败：{ex.Message}", "警告");
                        break;
                    }

                    // 4.4 读取新点数据（保留原有逻辑）
                    double[] newTraceData = await _hardwareService.ActiveDevice.GetTraceDataAsync(0);
                    if (newTraceData.Length == 0) continue;

                    double newMaxVal = newTraceData.Max();
                    // 计算新点热力图索引
                    double newRatioX = (nextX - xMin) / (xMax - xMin);
                    double newRatioY = (nextY - yMin) / (yMax - yMin);
                    int newArrayX = (int)Math.Round(newRatioX * (scanSettings.NumX - 1));
                    int newArrayY = (int)Math.Round(newRatioY * (scanSettings.NumY - 1));
                    newArrayX = Math.Max(0, Math.Min(newArrayX, scanSettings.NumX - 1));
                    newArrayY = Math.Max(0, Math.Min(newArrayY, scanSettings.NumY - 1));

                    // 更新可视化（保留原有逻辑）
                    heatMapData[newArrayX, newArrayY] = newMaxVal;
                    HeatmapModel.InvalidatePlot(true);

                    spectrumSeries.Points.Clear();
                    for (int k = 0; k < newTraceData.Length; k++) spectrumSeries.Points.Add(new DataPoint(k, newTraceData[k]));
                    SpectrumModel.InvalidatePlot(true);

                    // 缓存CSV数据（保留原有逻辑）
                    sbPeak.AppendLine($"{nextX:F3},{nextY:F3},{newMaxVal:F3}");
                    sbFull.Append($"{nextX:F3},{nextY:F3}");
                    foreach (var val in newTraceData) sbFull.Append($",{val:F3}");
                    sbFull.AppendLine();

                    // 4.5 校准计算（复用原有逻辑）
                    // double newCorrectedMagnitude = 0;
                    // double newStartFreq = CurrentInstrumentSettings.CenterFrequencyHz - (CurrentInstrumentSettings.SpanHz / 2);
                    // double newStepFreq = CurrentInstrumentSettings.SpanHz / (newTraceData.Length - 1);
                    // for (int j = 0; j < newTraceData.Length; j++)
                    // {
                    //     double freq = newStartFreq + j * newStepFreq;
                    //     double powerDbm = newTraceData[j];
                    //     double powerDbuV = powerDbm + 107;
                    //     double antennaFactor = ActiveProbe?.GetFactorAtFrequency(freq) ?? 0;
                    //     newCorrectedMagnitude = powerDbuV + antennaFactor;
                    // }

                    // 4.6 更新采样点列表
                    sampledPoints.Add(new SampledPoint
                    {
                        X = nextX,
                        Y = nextY,
                        Magnitude = newMaxVal
                    });

                    // 打印进度（可选）
                    Console.WriteLine($"QBC采样进度：{sampledPoints.Count}/{targetSampleCount}");
                }

                                // 1. 调用填充方法（复用原有变量，仅新增临时变量）
                var (filledHeatMapData, fullPointMap) = FillUnsampledPointsWithRbfMean(sampledPoints, scanSettings);
                heatMapSeries = HeatmapModel.Series.OfType<HeatMapSeries>().FirstOrDefault();
                if (heatMapSeries != null)
                {
                    heatMapSeries.Data = filledHeatMapData; // 替换为填充后的完整数据
                    HeatmapModel.InvalidatePlot(true); // 强制刷新热力图
                }
                // 2. 复用原有sbPeak/sbFull变量，覆盖为填充后的数据（保留原有变量名）
                // 重新生成Peak CSV（全场所有点，包括填充的）
                sbPeak.Clear(); // 清空原有稀疏数据
                sbPeak.AppendLine("PhysicalX(mm),PhysicalY(mm),MaxAmplitude(dBm)"); // 保留原表头，不修改

                // 生成全场网格坐标（复用原有扫描范围计算逻辑）
                var xCoor = GenerateLinspace(xMin, xMax, scanSettings.NumX);
                var yCoor = GenerateLinspace(yMin, yMax, scanSettings.NumY);

                // 临时变量：记录原Full CSV的列数（用于补全未采样点的traceData）
                int fullTraceColCount = 0;
                if (!string.IsNullOrEmpty(sbFull.ToString()))
                {
                    // 分割换行符（兼容Windows/Linux），并过滤空行
                    var lines = sbFull.ToString().Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length > 0)
                    {
                        var firstLine = lines[0]; // 取第一行表头
                        fullTraceColCount = firstLine.Split(',').Length; // 计算列数
                    }
                }

                // 遍历全场网格，重新填充sbPeak/sbFull（保留原有变量名）
                for (int j = 0; j < scanSettings.NumY; j++)
                {
                    float targetY = (float)yCoor[j];
                    for (int i = 0; i < scanSettings.NumX; i++)
                    {
                        float targetX = (float)xCoor[i];
                        var key = ((float)Math.Round(targetX, 3), (float)Math.Round(targetY, 3));
                        
                        // 获取填充后的幅值（已采样点=原始值，未采样点=RBF均值）
                        double filledVal = fullPointMap.TryGetValue(key, out var val) ? val : 0;
                        
                        // 复用sbPeak：写入填充后的数据（保留原格式）
                        sbPeak.AppendLine($"{targetX:F3},{targetY:F3},{filledVal:F3}");

                        // 复用sbFull：补全未采样点的traceData（保留原格式）
                        if (fullTraceColCount == 0) continue; // 无原始数据时跳过
                        if (!sbFull.ToString().StartsWith("PhysicalX(mm)")) // 保留原表头逻辑
                        {
                            sbFull.Clear();
                            sbFull.Append("PhysicalX(mm),PhysicalY(mm)");
                            for (int k = 0; k < fullTraceColCount - 2; k++) sbFull.Append($",P{k}");
                            sbFull.AppendLine();
                        }
                        // 写入填充后的FullTrace数据（未采样点所有维度用均值填充）
                        sbFull.Append($"{targetX:F3},{targetY:F3}");
                        for (int k = 0; k < fullTraceColCount - 2; k++) sbFull.Append($",{filledVal:F3}");
                        sbFull.AppendLine();
                    }
                }

                // ===================== 原有逻辑：数据导出+提示（完全保留） =====================
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                SaveScanDataToCsv(selectedProject, sbPeak.ToString(), $"ScanResult_Peak_{timestamp}.csv");
                SaveScanDataToCsv(selectedProject, sbFull.ToString(), $"ScanResult_FullTrace_{timestamp}.csv");

                MessageBox.Show($"QBC自适应采样完成！共采样 {sampledPoints.Count} 个点", "成功");
            }
            catch (Exception ex) { MessageBox.Show("扫描错误: " + ex.Message, "错误"); }
            finally { IsScanning = false; }
        }

        // 辅助方法：根据坐标值获取数组索引（带容差和最近邻逻辑）
        private int GetIndexFromCoordinate(float[] array, float value, float tolerance = 0.001f)
        {
            // 第一步：尝试精确匹配
            for (int i = 0; i < array.Length; i++)
            {
                if (Math.Abs(array[i] - value) < tolerance)
                {
                    return i;
                }
            }

            // 第二步：精确匹配失败时，找最近邻索引
            int nearestIdx = 0;
            float minDistance = float.MaxValue;
            for (int i = 0; i < array.Length; i++)
            {
                float distance = Math.Abs(array[i] - value);
                if (distance < minDistance)
                {
                    minDistance = distance;
                    nearestIdx = i;
                }
            }
            Debug.WriteLine($"坐标{value}未精确匹配，使用最近邻索引{nearestIdx}（对应值{array[nearestIdx]}）");
            return nearestIdx;
        }

        private static QbcOutputData CalculateNextSamplePoint(QbcInputData inputData)
        {
            try
            {
                var hyperParams = inputData.HyperParams;
                var sampledPoints = inputData.SampledPoints;

                if (sampledPoints == null || sampledPoints.Count == 0)
                {
                    return new QbcOutputData
                    {
                        Status = "error",
                        Message = "已采样点为空",
                        Next_x = 0.0,
                        Next_y = 0.0
                    };
                }

                double[][] xObs = sampledPoints.Select(p => new[] { (double)p.X, (double)p.Y }).ToArray();
                double[] yObs = sampledPoints.Select(p => p.Magnitude).ToArray();

                var xCoor = GenerateLinspace(hyperParams.X_min, hyperParams.X_max, hyperParams.Nx);
                var yCoor = GenerateLinspace(hyperParams.Y_min, hyperParams.Y_max, hyperParams.Ny);

                var gridPoints = new List<double[]>();
                foreach (var y in yCoor)
                {
                    foreach (var x in xCoor)
                    {
                        gridPoints.Add(new[] { x, y });
                    }
                }

                var unselectedPoints = new List<double[]>();
                foreach (var point in gridPoints)
                {
                    bool isSampled = false;
                    foreach (var sampled in xObs)
                    {
                        double distance = Math.Sqrt(Math.Pow(point[0] - sampled[0], 2) + Math.Pow(point[1] - sampled[1], 2));
                        if (distance <= 1e-3)
                        {
                            isSampled = true;
                            break;
                        }
                    }
                    if (!isSampled)
                    {
                        unselectedPoints.Add(point);
                    }
                }

                if (unselectedPoints.Count == 0)
                {
                    return new QbcOutputData
                    {
                        Status = "error",
                        Message = "没有可采样的新点了",
                        Next_x = 0.0,
                        Next_y = 0.0
                    };
                }

                var kernels = new List<RbfKernel>
                {
                    RbfKernel.Linear,
                    RbfKernel.Cubic,
                    RbfKernel.ThinPlateSpline,
                    RbfKernel.Quintic
                };

                var predictions = new double[unselectedPoints.Count][];
                for (int i = 0; i < unselectedPoints.Count; i++)
                {
                    predictions[i] = new double[kernels.Count];
                }

                for (int k = 0; k < kernels.Count; k++)
                {
                    var model = new RbfInterpolator(xObs, yObs, kernels[k], 5);
                    for (int i = 0; i < unselectedPoints.Count; i++)
                    {
                        predictions[i][k] = model.Predict(unselectedPoints[i]);
                    }
                }

                var variances = new double[unselectedPoints.Count];
                for (int i = 0; i < unselectedPoints.Count; i++)
                {
                    var preds = predictions[i];
                    double mean = preds.Average();
                    variances[i] = preds.Sum(p => Math.Pow(p - mean, 2)) / preds.Length;
                }

                int maxVarIndex = 0;
                double maxVariance = variances[0];
                for (int i = 1; i < variances.Length; i++)
                {
                    if (variances[i] > maxVariance)
                    {
                        maxVariance = variances[i];
                        maxVarIndex = i;
                    }
                }

                var nextPoint = unselectedPoints[maxVarIndex];
                return new QbcOutputData
                {
                    Status = "success",
                    Message = "计算成功，已选不确定性最大的点",
                    Next_x = Math.Round(nextPoint[0], 2),
                    Next_y = Math.Round(nextPoint[1], 2)
                };
            }
            catch (Exception ex)
            {
                return new QbcOutputData
                {
                    Status = "error",
                    Message = $"计算过程出错：{ex.Message}",
                    Next_x = 0.0,
                    Next_y = 0.0
                };
            }
        }

        // 3. GenerateLinspace
        private static double[] GenerateLinspace(double start, double end, int count)
        {
            if (count <= 0)
                throw new ArgumentException("count必须大于0");

            if (count == 1)
                return new[] { start };

            double[] result = new double[count];
            double step = (end - start) / (count - 1);

            for (int i = 0; i < count; i++)
            {
                result[i] = start + step * i;
            }

            return result;
        }

        // 4. RbfInterpolator类（完整复用，需确保MathNet.Numerics已引入）
        public class RbfInterpolator
        {
            private double[][] _xObs;
            private double[] _yObs;
            private RbfKernel _kernel;
            private int _degree;
            private Vector<double> _weights;
            private int _polySize;

            public RbfInterpolator(double[][] xObs, double[] yObs, RbfKernel kernel, int degree)
            {
                _xObs = xObs ?? throw new ArgumentNullException(nameof(xObs));
                _yObs = yObs ?? throw new ArgumentNullException(nameof(yObs));
                _kernel = kernel;
                _degree = degree;

                if (_xObs.Length != _yObs.Length)
                    throw new ArgumentException("采样点数量与值数量不匹配");

                Train();
            }

            private void Train()
            {
                int n = _xObs.Length;
                int dim = _xObs[0].Length;

                var polyBasisExample = GetPolynomialBasis(_xObs[0]);
                _polySize = polyBasisExample.Length;

                var phi = DenseMatrix.Create(n + _polySize, n + _polySize, 0.0);

                for (int i = 0; i < n; i++)
                {
                    for (int j = 0; j < n; j++)
                    {
                        double r = CalculateDistance(_xObs[i], _xObs[j]);
                        phi[i, j] = EvaluateKernel(r);
                    }
                }

                for (int i = 0; i < n; i++)
                {
                    double[] polys = GetPolynomialBasis(_xObs[i]);
                    for (int j = 0; j < _polySize; j++)
                    {
                        phi[i, n + j] = polys[j];
                        phi[n + j, i] = polys[j];
                    }
                }

                var yVector = DenseVector.OfArray(_yObs);
                var zeros = DenseVector.Create(_polySize, 0.0);

                double[] yArray = yVector.ToArray();
                double[] zerosArray = zeros.ToArray();
                double[] combinedData = new double[yArray.Length + zerosArray.Length];
                yArray.CopyTo(combinedData, 0);
                zerosArray.CopyTo(combinedData, yArray.Length);
                var b = DenseVector.OfArray(combinedData);

                var regularization = DenseMatrix.CreateIdentity(phi.RowCount) * 1e-10;
                phi += regularization;

                _weights = phi.Solve(b);
            }

            public double Predict(double[] x)
            {
                int n = _xObs.Length;
                double result = 0.0;

                for (int i = 0; i < n; i++)
                {
                    double r = CalculateDistance(x, _xObs[i]);
                    result += _weights[i] * EvaluateKernel(r);
                }

                double[] polys = GetPolynomialBasis(x);
                for (int i = 0; i < _polySize; i++)
                {
                    result += _weights[n + i] * polys[i];
                }

                return result;
            }

            private double CalculateDistance(double[] x1, double[] x2)
            {
                double sum = 0;
                for (int i = 0; i < x1.Length; i++)
                {
                    sum += Math.Pow(x1[i] - x2[i], 2);
                }
                return Math.Sqrt(sum);
            }

            private double EvaluateKernel(double r)
            {
                if (r < 1e-12)
                    return 0.0;

                switch (_kernel)
                {
                    case RbfKernel.Linear:
                        return r;
                    case RbfKernel.Cubic:
                        return r * r * r;
                    case RbfKernel.ThinPlateSpline:
                        return r * r * Math.Log(r);
                    case RbfKernel.Quintic:
                        return Math.Pow(r, 5);
                    default:
                        throw new NotImplementedException($"未实现的核函数: {_kernel}");
                }
            }

            private double[] GetPolynomialBasis(double[] x)
            {
                var basis = new List<double>();
                int dim = x.Length;

                basis.Add(1.0);

                if (_degree >= 1)
                {
                    basis.AddRange(x);
                }

                if (_degree >= 2)
                {
                    basis.Add(x[0] * x[0]);
                    basis.Add(x[0] * x[1]);
                    basis.Add(x[1] * x[1]);
                }

                if (_degree >= 3)
                {
                    basis.Add(x[0] * x[0] * x[0]);
                    basis.Add(x[0] * x[0] * x[1]);
                    basis.Add(x[0] * x[1] * x[1]);
                    basis.Add(x[1] * x[1] * x[1]);
                }

                if (_degree >= 4)
                {
                    basis.Add(Math.Pow(x[0], 4));
                    basis.Add(Math.Pow(x[0], 3) * x[1]);
                    basis.Add(Math.Pow(x[0], 2) * Math.Pow(x[1], 2));
                    basis.Add(x[0] * Math.Pow(x[1], 3));
                    basis.Add(Math.Pow(x[1], 4));
                }

                if (_degree >= 5)
                {
                    basis.Add(Math.Pow(x[0], 5));
                    basis.Add(Math.Pow(x[0], 4) * x[1]);
                    basis.Add(Math.Pow(x[0], 3) * Math.Pow(x[1], 2));
                    basis.Add(Math.Pow(x[0], 2) * Math.Pow(x[1], 3));
                    basis.Add(x[0] * Math.Pow(x[1], 4));
                    basis.Add(Math.Pow(x[1], 5));
                }

                return basis.ToArray();
            }
        }

        /// <summary>
        /// 用四种RBF插值的平均值填充全场未采样点
        /// </summary>
        /// <param name="sampledPoints">已采样点列表</param>
        /// <param name="scanSettings">扫描配置（坐标范围/分辨率）</param>
        /// <returns>填充后的全场幅值矩阵 + 全场坐标-幅值映射（用于生成CSV）</returns>
        private (double[,] filledHeatMapData, Dictionary<(float X, float Y), double> fullPointMap) FillUnsampledPointsWithRbfMean(
            List<SampledPoint> sampledPoints, ScanSettings scanSettings)
        {
            // 1. 提取扫描范围
            double xMin = Math.Min(scanSettings.StartX, scanSettings.StopX);
            double xMax = Math.Max(scanSettings.StartX, scanSettings.StopX);
            double yMin = Math.Min(scanSettings.StartY, scanSettings.StopY);
            double yMax = Math.Max(scanSettings.StartY, scanSettings.StopY);

            // 2. 生成全场网格坐标（和原始扫描分辨率一致）
            var xCoor = GenerateLinspace(xMin, xMax, scanSettings.NumX);
            var yCoor = GenerateLinspace(yMin, yMax, scanSettings.NumY);
            
            // 3. 构建已采样点的坐标-幅值映射（快速查询）
            var sampledPointMap = new Dictionary<(float X, float Y), double>();
            foreach (var p in sampledPoints)
            {
                // 四舍五入到3位小数，避免浮点精度问题
                float x = (float)Math.Round(p.X, 3);
                float y = (float)Math.Round(p.Y, 3);
                sampledPointMap[(x, y)] = p.Magnitude;
            }

            // 4. 提取已采样点的X/Y和幅值（用于RBF训练）
            double[][] xObs = sampledPoints.Select(p => new[] { (double)p.X, (double)p.Y }).ToArray();
            double[] yObs = sampledPoints.Select(p => p.Magnitude).ToArray();

            // 5. 初始化四种RBF插值器
            var kernels = new List<RbfKernel> { RbfKernel.Linear, RbfKernel.Cubic, RbfKernel.ThinPlateSpline, RbfKernel.Quintic };
            var rbfModels = new List<RbfInterpolator>();
            foreach (var kernel in kernels)
            {
                rbfModels.Add(new RbfInterpolator(xObs, yObs, kernel, 5));
            }

            // 6. 初始化填充后的矩阵和全场坐标映射
            double[,] filledData = new double[scanSettings.NumX, scanSettings.NumY];
            var fullPointMap = new Dictionary<(float X, float Y), double>();

            // 7. 遍历全场网格，填充值
            for (int j = 0; j < scanSettings.NumY; j++) // Y轴（行）
            {
                float targetY = (float)Math.Round((float)yCoor[j], 3);
                for (int i = 0; i < scanSettings.NumX; i++) // X轴（列）
                {
                    float targetX = (float)Math.Round((float)xCoor[i], 3);
                    var key = (targetX, targetY);

                    // 7.1 已采样点：保留原始值
                    if (sampledPointMap.ContainsKey(key))
                    {
                        double val = sampledPointMap[key];
                        filledData[i, j] = val;
                        fullPointMap[key] = val;
                    }
                    // 7.2 未采样点：四种RBF插值的平均值
                    else
                    {
                        double[] predictions = new double[rbfModels.Count];
                        for (int k = 0; k < rbfModels.Count; k++)
                        {
                            predictions[k] = rbfModels[k].Predict(new[] { (double)targetX, targetY });
                        }
                        double meanVal = predictions.Average(); // 四种插值的平均值
                        filledData[i, j] = meanVal;
                        fullPointMap[key] = meanVal;
                    }
                }
            }

            return (filledData, fullPointMap);
        }

        private void SaveScanDataToCsv(ProjectViewModel project, string csvContent, string fileName)
        {
            try
            {
                string dataFolder = Path.Combine(project.ProjectFolderPath, "Data");
                if (!Directory.Exists(dataFolder)) Directory.CreateDirectory(dataFolder);
                File.WriteAllText(Path.Combine(dataFolder, fileName), csvContent, Encoding.UTF8);
            }
            catch (Exception ex) { MessageBox.Show($"保存失败: {ex.Message}"); }
        }

        private void ExecuteStopScan() { _cancellationTokenSource?.Cancel(); }
    }
}