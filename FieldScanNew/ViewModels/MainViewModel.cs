using FieldScanNew.Infrastructure;
using FieldScanNew.Models; 
using FieldScanNew.Services;
using FieldScanNew.Views;
using MathNet.Numerics;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Series;
using OxyPlot.Wpf;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace FieldScanNew.ViewModels
{
    public class MainViewModel : ViewModelBase
    {
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
                if (_selectedStep != null) TriggerStepDialog(_selectedStep);
            }
        }

        public void TriggerStepDialog(IStepViewModel step)
        {
            if (step == null || step is ProjectViewModel || step is MeasurementViewModel) return;
            _dialogService.ShowDialog(step);
            LoadDutImage();
            _selectedStep = null;
            OnPropertyChanged(nameof(SelectedStep));
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

        public class QbcInputData
        {
            public HyperParams HyperParams { get; set; }
            public List<SampledPoint> SampledPoints { get; set; }
        }

        public class HyperParams
        {
            public double X_min { get; set; }
            public double X_max { get; set; }
            public double Y_min { get; set; }
            public double Y_max { get; set; }
            public int Nx { get; set; }
            public int Ny { get; set; }
            public int Uncertainty_size { get; set; }
            public double ConvergenceError { get; set; }
            public double StdDevCoef { get; set; }
        }

        public class QbcOutputData
        {
            public string Status { get; set; }
            public string Message { get; set; }
            public double Next_x { get; set; }
            public double Next_y { get; set; }
        }

        public class SampledPoint
        {
            public float X { get; set; }
            public float Y { get; set; }
            public double Magnitude { get; set; }
        }

        public enum RbfKernel { Linear, Cubic, ThinPlateSpline, Quintic }

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

            // 修改：横坐标显示频率，纵坐标由于加入了+107和探头因子，单位其实变成了 dBuV/m (或 dBuV)
            SpectrumModel = new PlotModel { Title = "实时频谱 (Trace)" };
            SpectrumModel.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Title = "Frequency (Hz)" });
            SpectrumModel.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Title = "Level (dBuV/m)" });

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

        private void LoadDutImage()
        {
            var selectedProject = Projects.FirstOrDefault(p => p.IsSelected);
            if (selectedProject != null && !string.IsNullOrEmpty(selectedProject.ProjectData.DutImagePath) && File.Exists(selectedProject.ProjectData.DutImagePath))
            {
                try { var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.UriSource = new Uri(selectedProject.ProjectData.DutImagePath); bitmap.EndInit(); bitmap.Freeze(); DutImageSource = bitmap; } catch { DutImageSource = null; }
            }
            else { DutImageSource = null; }
        }

        private void UpdatePlotBackground()
        {
            HeatmapModel.Annotations.Clear();
            if (DutImageSource == null) { HeatmapModel.InvalidatePlot(true); return; }

            var selectedProject = Projects.FirstOrDefault(p => p.IsSelected);
            double xMin, xMax, yMin, yMax;
            BitmapSource displayBitmap = DutImageSource;

            if (selectedProject == null || !selectedProject.ProjectData.IsCalibrated)
            { xMin = 0; xMax = DutImageSource.PixelWidth; yMin = 0; yMax = DutImageSource.PixelHeight; }
            else
            {
                var projectData = selectedProject.ProjectData;
                double physicalX_Right = DutImageSource.PixelWidth * projectData.MatrixM11 + projectData.OffsetX;
                double physicalY_Bottom = DutImageSource.PixelHeight * projectData.MatrixM22 + projectData.OffsetY;
                xMin = Math.Min(projectData.OffsetX, physicalX_Right); xMax = Math.Max(projectData.OffsetX, physicalX_Right);
                yMin = Math.Min(projectData.OffsetY, physicalY_Bottom); yMax = Math.Max(projectData.OffsetY, physicalY_Bottom);
            }

            try
            {
                using (var stream = new MemoryStream())
                {
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(displayBitmap)); encoder.Save(stream);
                    var imageAnnotation = new ImageAnnotation { ImageSource = new OxyImage(stream.ToArray()), X = new PlotLength((xMin + xMax) / 2, PlotLengthUnit.Data), Y = new PlotLength((yMin + yMax) / 2, PlotLengthUnit.Data), Width = new PlotLength(xMax - xMin, PlotLengthUnit.Data), Height = new PlotLength(yMax - yMin, PlotLengthUnit.Data), Layer = AnnotationLayer.BelowSeries, Interpolate = true };
                    HeatmapModel.Annotations.Add(imageAnnotation);
                }
            }
            catch (Exception ex) { Console.WriteLine("BG Error: " + ex.Message); }
            HeatmapModel.ResetAllAxes(); HeatmapModel.InvalidatePlot(true);
        }

        private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
        { var selectedProject = Projects.FirstOrDefault(p => p.IsSelected); if (selectedProject != null) AutoSaveCurrentProject(selectedProject); }

        private void ExecuteAddNewProject(object? parameter)
        {
            try { var inputDialog = new InputDialog("请输入新项目的名称:", "新项目"); if (inputDialog.ShowDialog() != true) return; string projectName = SanitizeFileName(inputDialog.Answer); if (string.IsNullOrWhiteSpace(projectName)) return; var folderDialog = new System.Windows.Forms.FolderBrowserDialog { Description = "请选择项目的存放路径" }; if (folderDialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return; string projectPath = Path.Combine(folderDialog.SelectedPath, projectName); if (Directory.Exists(projectPath)) { MessageBox.Show("同名项目文件夹已存在！", "错误"); return; } Directory.CreateDirectory(projectPath); var newProject = new ProjectViewModel(projectName, projectPath, this); Projects.Add(newProject); foreach (var proj in Projects.Where(p => p != newProject)) proj.IsSelected = false; newProject.IsSelected = true; LoadProjectDataIntoViewModel(newProject); AutoSaveCurrentProject(newProject); } catch (Exception ex) { MessageBox.Show("创建新项目时发生严重错误: " + ex.Message, "错误"); }
        }
        private void ExecuteLoadProject(object? parameter)
        {
            try { var openFileDialog = new OpenFileDialog { Filter = "项目文件 (*.json)|*.json" }; if (openFileDialog.ShowDialog() == true) { string filePath = openFileDialog.FileName; string fileContent = File.ReadAllText(filePath); if (string.IsNullOrWhiteSpace(fileContent)) { MessageBox.Show("项目文件为空或已损坏。", "错误"); return; } var projectData = System.Text.Json.JsonSerializer.Deserialize<ProjectData>(fileContent); if (projectData == null) { MessageBox.Show("无法解析项目文件。", "错误"); return; } string projectFolder = Path.GetDirectoryName(filePath) ?? string.Empty; var loadedProject = new ProjectViewModel(projectData.ProjectName, projectFolder, this) { ProjectData = projectData }; if (projectData.MeasurementNames != null) { foreach (var name in projectData.MeasurementNames) loadedProject.Measurements.Add(new MeasurementViewModel(name, loadedProject)); } Projects.Add(loadedProject); foreach (var proj in Projects.Where(p => p != loadedProject)) proj.IsSelected = false; loadedProject.IsSelected = true; LoadProjectDataIntoViewModel(loadedProject); } } catch (Exception ex) { MessageBox.Show("加载项目时发生严重错误: " + ex.Message, "错误"); }
        }
        public void AutoSaveCurrentProject(ProjectViewModel project)
        {
            if (project?.ProjectData == null) return; project.ProjectData.ScanConfig = this.CurrentScanSettings; project.ProjectData.InstrumentConfig = this.CurrentInstrumentSettings; project.ProjectData.MeasurementNames = project.Measurements.Select(m => m.DisplayName).ToList(); try { string filePath = Path.Combine(project.ProjectFolderPath, "project.json"); var options = new JsonSerializerOptions { WriteIndented = true }; string jsonString = System.Text.Json.JsonSerializer.Serialize(project.ProjectData, options); File.WriteAllText(filePath, jsonString); } catch (Exception ex) { Console.WriteLine($"自动保存失败: {ex.Message}"); }
        }
        internal void LoadProjectDataIntoViewModel(ProjectViewModel? project)
        {
            if (project?.ProjectData == null) { CurrentScanSettings = new ScanSettings(); CurrentInstrumentSettings = new InstrumentSettings(); return; }
            CurrentScanSettings = project.ProjectData.ScanConfig ?? new ScanSettings(); CurrentInstrumentSettings = project.ProjectData.InstrumentConfig ?? new InstrumentSettings(); foreach (var measurement in project.Measurements) { var instVm = measurement.Steps.OfType<InstrumentSetupViewModel>().FirstOrDefault(); if (instVm != null) instVm.InstrumentSettings = CurrentInstrumentSettings; var settingVm = measurement.Steps.OfType<ScanSettingsViewModel>().FirstOrDefault(); if (settingVm != null) settingVm.Settings = CurrentInstrumentSettings; var scanVm = measurement.Steps.OfType<ScanAreaViewModel>().FirstOrDefault(); if (scanVm != null) scanVm.Settings = CurrentScanSettings; }
            LoadDutImage();
        }
        private string GetCurrentMeasurementName(ProjectViewModel project) { if (SelectedStep != null) { foreach (var m in project.Measurements) { if (m == SelectedStep || m.Steps.Contains(SelectedStep)) return m.DisplayName; } } return project.Measurements.Count > 0 ? project.Measurements.Last().DisplayName : "General"; }
        private string SanitizeFileName(string name) { var invalidChars = Path.GetInvalidFileNameChars(); return new string(name.Where(ch => !invalidChars.Contains(ch)).ToArray()); }

        // =======================================================
        // 核心方法：根据频率获取线性插值的探头因子
        // 如果没有加载文件（Count=0），直接返回 0，确保默认逻辑正确
        // =======================================================
        private double GetInterpolatedFactor(double freqHz)
        {
            var points = CurrentInstrumentSettings?.ProbePoints;
            if (points == null || points.Count == 0) return 0.0; // 默认因子为 0

            // 如果只有一个点
            if (points.Count == 1) return points[0].CorrectionFactor;

            // 查找区间
            var left = points.LastOrDefault(p => p.Frequency <= freqHz);
            var right = points.FirstOrDefault(p => p.Frequency > freqHz);

            if (left == null) return points.First().CorrectionFactor; // 频率低于最小值，取第一个
            if (right == null) return points.Last().CorrectionFactor; // 频率高于最大值，取最后一个

            // 线性插值
            double ratio = (freqHz - left.Frequency) / (right.Frequency - left.Frequency);
            return left.CorrectionFactor + ratio * (right.CorrectionFactor - left.CorrectionFactor);
        }

        private async Task ExecuteStartScan()
        {
            if (_hardwareService.ActiveRobot == null || !_hardwareService.ActiveRobot.IsConnected ||
               _hardwareService.ActiveDevice == null || !_hardwareService.ActiveDevice.IsConnected)
            { MessageBox.Show("请先连接机械臂和测量仪器！", "提示"); return; }

            var selectedProject = Projects.FirstOrDefault(p => p.IsSelected);
            if (selectedProject == null) { MessageBox.Show("请先选择一个项目！", "提示"); return; }

            var scanSettings = selectedProject.ProjectData.ScanConfig;
            if (scanSettings.NumX < 2 || scanSettings.NumY < 2) { MessageBox.Show("扫描点数必须大于等于2！", "错误"); return; }
            if (!scanSettings.ScanHx && !scanSettings.ScanHy) { MessageBox.Show("请至少勾选一个扫描分量(Hx 或 Hy)！", "提示"); return; }

            UpdatePlotBackground();
            try { await _hardwareService.ActiveDevice.ConnectAsync(CurrentInstrumentSettings); }
            catch (Exception ex) { MessageBox.Show($"更新配置失败: {ex.Message}", "警告"); }

            IsScanning = true;
            _cancellationTokenSource = new CancellationTokenSource();

            var tasks = new List<(string Name, float Angle)>();
            if (scanSettings.ScanHx) tasks.Add(("Hx", 0f));
            if (scanSettings.ScanHy) tasks.Add(("Hy", 90f));

            string projectName = SanitizeFileName(selectedProject.ProjectData.ProjectName);
            string measurementName = SanitizeFileName(GetCurrentMeasurementName(selectedProject));

            try
            {
                double centerFreq = CurrentInstrumentSettings.CenterFrequencyHz;
                double span = CurrentInstrumentSettings.SpanHz;
                double startFreq = centerFreq - (span / 2.0);
                double stopFreq = centerFreq + (span / 2.0);

                foreach (var task in tasks)
                {
                    if (_cancellationTokenSource.Token.IsCancellationRequested) break;

                    string componentName = task.Name;
                    float robotAngle = task.Angle;

                    var currentPos = await _hardwareService.ActiveRobot.GetPositionAsync();
                    await _hardwareService.ActiveRobot.MoveToAsync(currentPos.X, currentPos.Y, currentPos.Z, robotAngle);

                    double xMin = Math.Min(scanSettings.StartX, scanSettings.StopX);
                    double xMax = Math.Max(scanSettings.StartX, scanSettings.StopX);
                    double yMin = Math.Min(scanSettings.StartY, scanSettings.StopY);
                    double yMax = Math.Max(scanSettings.StartY, scanSettings.StopY);

                    var heatMapData = new double[scanSettings.NumX, scanSettings.NumY];
                    var heatMapSeries = new HeatMapSeries { X0 = xMin, X1 = xMax, Y0 = yMin, Y1 = yMax, Interpolate = true, RenderMethod = HeatMapRenderMethod.Bitmap, Data = heatMapData, CoordinateDefinition = HeatMapCoordinateDefinition.Edge };

                    HeatmapModel.Series.Clear(); HeatmapModel.Series.Add(heatMapSeries);
                    HeatmapModel.Title = $"近场热力图 - {componentName}";
                    HeatmapModel.ResetAllAxes(); HeatmapModel.InvalidatePlot(true);

                    var spectrumSeries = new LineSeries { Title = "Live Trace", Color = OxyColors.Blue, StrokeThickness = 1 };
                    SpectrumModel.Series.Clear(); SpectrumModel.Series.Add(spectrumSeries); SpectrumModel.InvalidatePlot(true);

                    var sbPeak = new StringBuilder(); sbPeak.AppendLine("PhysicalX(mm),PhysicalY(mm),MaxAmplitude(dBuV/m)");
                    var sbFull = new StringBuilder(); bool isFullHeaderWritten = false;

                    for (int j = 0; j < scanSettings.NumY; j++)
                    {
                        for (int i = 0; i < scanSettings.NumX; i++)
                        {
                            if (_cancellationTokenSource.Token.IsCancellationRequested) goto StopScanLabel;

                            float targetX = scanSettings.StartX + i * (scanSettings.StopX - scanSettings.StartX) / (scanSettings.NumX - 1);
                            float targetY = scanSettings.StartY + j * (scanSettings.StopY - scanSettings.StartY) / (scanSettings.NumY - 1);

                            await _hardwareService.ActiveRobot.MoveToAsync(targetX, targetY, scanSettings.ScanHeightZ, robotAngle);
                            double[] traceData = await _hardwareService.ActiveDevice.GetTraceDataAsync(0);

                            if (traceData.Length > 0)
                            {
                                // ========================================================
                                // 修正数据：读数(dBm) + 107 + 探头因子
                                // ========================================================
                                for (int k = 0; k < traceData.Length; k++)
                                {
                                    double freq = startFreq + (double)k * (stopFreq - startFreq) / (traceData.Length - 1);
                                    double factor = GetInterpolatedFactor(freq);
                                    traceData[k] = traceData[k] + 107.0 + factor;
                                }

                                double maxVal = traceData.Max();
                                double ratioX = (targetX - xMin) / (xMax - xMin);
                                double ratioY = (targetY - yMin) / (yMax - yMin);
                                int arrayX = Math.Max(0, Math.Min((int)Math.Round(ratioX * (scanSettings.NumX - 1)), scanSettings.NumX - 1));
                                int arrayY = Math.Max(0, Math.Min((int)Math.Round(ratioY * (scanSettings.NumY - 1)), scanSettings.NumY - 1));

                                heatMapData[arrayX, arrayY] = maxVal;
                                HeatmapModel.InvalidatePlot(true);

                                spectrumSeries.Points.Clear();
                                for (int k = 0; k < traceData.Length; k++)
                                {
                                    double freq = startFreq + (double)k * (stopFreq - startFreq) / (traceData.Length - 1);
                                    spectrumSeries.Points.Add(new DataPoint(freq, traceData[k]));
                                }
                                SpectrumModel.InvalidatePlot(true);

                                sbPeak.AppendLine($"{targetX:F3},{targetY:F3},{maxVal:F3}");

                                if (!isFullHeaderWritten)
                                {
                                    sbFull.Append("PhysicalX(mm),PhysicalY(mm)");
                                    for (int k = 0; k < traceData.Length; k++)
                                    {
                                        double freq = startFreq + (double)k * (stopFreq - startFreq) / (traceData.Length - 1);
                                        sbFull.Append($",{freq:F0}Hz");
                                    }
                                    sbFull.AppendLine();
                                    isFullHeaderWritten = true;
                                }
                                sbFull.Append($"{targetX:F3},{targetY:F3}");
                                foreach (var val in traceData) sbFull.Append($",{val:F3}");
                                sbFull.AppendLine();
                            }
                        }
                    }

                    string baseName = $"{projectName}_{measurementName}_{componentName}";
                    string subFolder = $"{measurementName}_{componentName}";
                    SaveScanDataToCsv(selectedProject, sbPeak.ToString(), $"{baseName}_Peak.csv", subFolder);
                    SaveScanDataToCsv(selectedProject, sbFull.ToString(), $"{baseName}_FullTrace.csv", subFolder);
                    if (DutImageSource != null) SaveImage(selectedProject, DutImageSource, $"{baseName}_Capture.jpg", subFolder);
                    SaveHeatmapImage(selectedProject, HeatmapModel, $"{baseName}_HeatmapOverlay.png", subFolder);
                }

            StopScanLabel:;
                if (!_cancellationTokenSource.Token.IsCancellationRequested) MessageBox.Show("所有选定分量扫描完成！", "成功");
                else MessageBox.Show("扫描已停止。", "提示");
            }
            catch (Exception ex) { MessageBox.Show("扫描错误: " + ex.Message, "错误"); }
            finally
            {
                if (_hardwareService.ActiveRobot != null && _hardwareService.ActiveRobot.IsConnected)
                { try { var pos = await _hardwareService.ActiveRobot.GetPositionAsync(); await _hardwareService.ActiveRobot.MoveToAsync(pos.X, pos.Y, pos.Z, 90f); } catch { } }
                IsScanning = false;
            }
        }

        private async Task QBCExecuteStartScan()
        {
            if (_hardwareService.ActiveRobot == null || !_hardwareService.ActiveRobot.IsConnected ||
            _hardwareService.ActiveDevice == null || !_hardwareService.ActiveDevice.IsConnected)
            { MessageBox.Show("请先连接机械臂和测量仪器！", "提示"); return; }

            var selectedProject = Projects.FirstOrDefault(p => p.IsSelected);
            if (selectedProject == null) { MessageBox.Show("请先选择一个项目！", "提示"); return; }

            var scanSettings = selectedProject.ProjectData.ScanConfig;
            if (scanSettings.NumX < 2 || scanSettings.NumY < 2) { MessageBox.Show("扫描点数必须大于等于2！", "错误"); return; }
            if (!scanSettings.ScanHx && !scanSettings.ScanHy) { MessageBox.Show("请至少勾选一个扫描分量！", "提示"); return; }

            // 新增: 弹出参数设置窗口获取用户输入
            double inputError = 0.5;
            int inputK = 10;
            double inputInitRatio = 0.15;
            double inputStdDevCoef = 0.2;
            var paramsDialog = new QbcParamsDialog(inputError, inputK, inputInitRatio, inputStdDevCoef);
            if (paramsDialog.ShowDialog() != true) return; // 用户取消
            inputError = paramsDialog.ErrorVal;
            inputK = paramsDialog.KVal;
            inputInitRatio = paramsDialog.InitRatioVal;
            inputStdDevCoef = paramsDialog.StdDevCoefVal;

            UpdatePlotBackground();
            try { await _hardwareService.ActiveDevice.ConnectAsync(CurrentInstrumentSettings); }
            catch (Exception ex) { MessageBox.Show($"更新配置失败: {ex.Message}", "警告"); }

            IsScanning = true;
            _cancellationTokenSource = new CancellationTokenSource();

            // 新增: 统计计时和总采样点数
            var stopwatch = Stopwatch.StartNew();
            int totalSampledPoints = 0;
            int totalMaxPoints = 0;

            var tasks = new List<(string Name, float Angle)>();
            if (scanSettings.ScanHx) tasks.Add(("Hx", 0f));
            if (scanSettings.ScanHy) tasks.Add(("Hy", 90f));

            string projectName = SanitizeFileName(selectedProject.ProjectData.ProjectName);
            string measurementName = SanitizeFileName(GetCurrentMeasurementName(selectedProject));

            try
            {
                double centerFreq = CurrentInstrumentSettings.CenterFrequencyHz;
                double span = CurrentInstrumentSettings.SpanHz;
                double startFreq = centerFreq - (span / 2.0);
                double stopFreq = centerFreq + (span / 2.0);

                foreach (var task in tasks)
                {
                    if (_cancellationTokenSource.Token.IsCancellationRequested) break;

                    string componentName = task.Name;
                    float robotAngle = task.Angle;

                    var currentPos = await _hardwareService.ActiveRobot.GetPositionAsync();
                    await _hardwareService.ActiveRobot.MoveToAsync(currentPos.X, currentPos.Y, currentPos.Z, robotAngle);

                    double xMin = Math.Min(scanSettings.StartX, scanSettings.StopX);
                    double xMax = Math.Max(scanSettings.StartX, scanSettings.StopX);
                    double yMin = Math.Min(scanSettings.StartY, scanSettings.StopY);
                    double yMax = Math.Max(scanSettings.StartY, scanSettings.StopY);

                    var heatMapData = new double[scanSettings.NumX, scanSettings.NumY];
                    var heatMapSeries = new HeatMapSeries { X0 = xMin, X1 = xMax, Y0 = yMin, Y1 = yMax, Interpolate = true, RenderMethod = HeatMapRenderMethod.Bitmap, Data = heatMapData, CoordinateDefinition = HeatMapCoordinateDefinition.Edge };
                    HeatmapModel.Series.Clear(); HeatmapModel.Series.Add(heatMapSeries);
                    HeatmapModel.Title = $"QBC热力图 - {componentName}";
                    HeatmapModel.ResetAllAxes(); HeatmapModel.InvalidatePlot(true);

                    var spectrumSeries = new LineSeries { Title = "Live Trace", Color = OxyColors.Blue, StrokeThickness = 1 };
                    SpectrumModel.Series.Clear(); SpectrumModel.Series.Add(spectrumSeries); SpectrumModel.InvalidatePlot(true);

                    var sbPeak = new StringBuilder(); sbPeak.AppendLine("PhysicalX(mm),PhysicalY(mm),MaxAmplitude(dBuV/m)");
                    var sbFull = new StringBuilder(); bool isFullHeaderWritten = false;

                    int sumSampleCount = scanSettings.NumX * scanSettings.NumY;
                    int initPointCount = Math.Max(4, (int)Math.Round(sumSampleCount * inputInitRatio)); // 使用用户输入的初始采样比例
                    initPointCount = Math.Min(initPointCount, sumSampleCount);

                    Console.WriteLine($"[Initialization] Starting greedy scan (FPS) for {initPointCount} points...");

                    var allGridPoints = new List<(float X, float Y)>();
                    for (int j = 0; j < scanSettings.NumY; j++)
                    {
                        float targetY = scanSettings.StartY + j * (scanSettings.StopY - scanSettings.StartY) / Math.Max(1, scanSettings.NumY - 1);
                        for (int i = 0; i < scanSettings.NumX; i++)
                        {
                            float targetX = scanSettings.StartX + i * (scanSettings.StopX - scanSettings.StartX) / Math.Max(1, scanSettings.NumX - 1);
                            allGridPoints.Add((targetX, targetY));
                        }
                    }

                    var selectedIndices = new HashSet<int>();
                    var distancesSq = Enumerable.Repeat(double.MaxValue, allGridPoints.Count).ToArray();
                    int firstIndex = (scanSettings.NumY / 2) * scanSettings.NumX + (scanSettings.NumX / 2);

                    // 1. 离线使用贪婪算法（FPS）选出所有初始规划采样点
                    var plannedPoints = new List<(float X, float Y)>();

                    for (int step = 0; step < initPointCount; step++)
                    {
                        if (_cancellationTokenSource.Token.IsCancellationRequested) goto StopQBC;

                        int nextIndex = -1;
                        if (step == 0)
                        {
                            nextIndex = firstIndex;
                        }
                        else
                        {
                            double maxMinDistSq = -1;
                            for (int i = 0; i < allGridPoints.Count; i++)
                            {
                                if (!selectedIndices.Contains(i) && distancesSq[i] > maxMinDistSq)
                                {
                                    maxMinDistSq = distancesSq[i];
                                    nextIndex = i;
                                }
                            }
                        }

                        selectedIndices.Add(nextIndex);
                        var p = allGridPoints[nextIndex];
                        plannedPoints.Add(p);

                        for (int i = 0; i < allGridPoints.Count; i++)
                        {
                            if (!selectedIndices.Contains(i))
                            {
                                double dx = allGridPoints[i].X - p.X;
                                double dy = allGridPoints[i].Y - p.Y;
                                double distSq = dx * dx + dy * dy;
                                if (distSq < distancesSq[i])
                                {
                                    distancesSq[i] = distSq;
                                }
                            }
                        }
                    }

                    // 2. 对规划好的点按行排序：Y依次增大，同Y下X依次增大（加入Math.Round规避浮点计算微小误差）
                    var sortedPoints = plannedPoints
                        .OrderBy(p => Math.Round(p.Y, 3))
                        .ThenBy(p => Math.Round(p.X, 3))
                        .ToList();

                    List<SampledPoint> sampledPoints = new List<SampledPoint>();

                    // 3. 实际平滑移动并采样
                    foreach (var pt in sortedPoints)
                    {
                        if (_cancellationTokenSource.Token.IsCancellationRequested) goto StopQBC;

                        float targetX = pt.X;
                        float targetY = pt.Y;

                        await _hardwareService.ActiveRobot.MoveToAsync(targetX, targetY, scanSettings.ScanHeightZ, robotAngle);
                        double[] traceData = await _hardwareService.ActiveDevice.GetTraceDataAsync(0);
                        if (traceData.Length == 0) continue;

                        for (int k = 0; k < traceData.Length; k++)
                        {
                            double freq = startFreq + (double)k * (stopFreq - startFreq) / (traceData.Length - 1);
                            double factor = GetInterpolatedFactor(freq);
                            traceData[k] = traceData[k] + 107.0 + factor;
                        }

                        double maxVal = traceData.Max();
                        sampledPoints.Add(new SampledPoint { X = targetX, Y = targetY, Magnitude = maxVal });

                        RecordFullTraceData(ref isFullHeaderWritten, sbFull, targetX, targetY, traceData, startFreq, stopFreq);

                        // 更新热力图显示
                        double ratioX = (targetX - xMin) / (xMax - xMin);
                        double ratioY = (targetY - yMin) / (yMax - yMin);
                        int arrayX = Math.Max(0, Math.Min((int)Math.Round(ratioX * (scanSettings.NumX - 1)), scanSettings.NumX - 1));
                        int arrayY = Math.Max(0, Math.Min((int)Math.Round(ratioY * (scanSettings.NumY - 1)), scanSettings.NumY - 1));
                        heatMapData[arrayX, arrayY] = maxVal;
                        HeatmapModel.InvalidatePlot(true);
                    }

                    // --- [Step 1] 参数设定：自适应停止机制 ---
                    
                    // Error: 采样变化误差允许值（阈值），单位 dBuV/m
                    // 判定标准：当 S_n (RMSE) <= Error 时，认为模型趋于稳定
                    double Error = inputError; // 使用用户输入的 Error

                    // K: 需要连续满足误差标准的次数
                    // 只有连续 K 次满足稳定标准，才停止扫描，防止偶然收敛
                    int K = inputK; // 使用用户输入的 K

                    // count: 当前连续满足误差标准的计数器
                    // 初始为 0，满足条件 +=1，不满足重置为 0
                    int count = 0;

                    // P_prev: 上一次的全场 RBF 插值预测结果 (P_{n-1})
                    // 用于与当前结果 P_n 计算误差 S_n
                    double[]? P_prev = null;

                    // maxN: 全局最大点数限制
                    int maxN = scanSettings.NumX * scanSettings.NumY;

                    // 循环条件：
                    // 1. count < K: 尚未达到连续 K 次稳定
                    // 2. sampledPoints.Count < maxN: 未超过最大物理点数
                    while (count < K && sampledPoints.Count < maxN)
                    {
                        if (_cancellationTokenSource.Token.IsCancellationRequested) goto StopQBC;
                        var inputData = new QbcInputData { HyperParams = new HyperParams { X_min = xMin, X_max = xMax, Y_min = yMin, Y_max = yMax, Nx = scanSettings.NumX, Ny = scanSettings.NumY, Uncertainty_size = maxN, ConvergenceError = Error, StdDevCoef = inputStdDevCoef }, SampledPoints = sampledPoints };

                        // Use Task.Run to avoid blocking UI during heavy QBC calculation
                        var nextPointData = await Task.Run(() => CalculateNextSamplePoint(inputData));
                        
                        if (nextPointData.Status != "success") break;

                        float nextX = (float)nextPointData.Next_x;
                        float nextY = (float)nextPointData.Next_y;

                        await _hardwareService.ActiveRobot.MoveToAsync(nextX, nextY, scanSettings.ScanHeightZ, robotAngle);
                        double[] newTraceData = await _hardwareService.ActiveDevice.GetTraceDataAsync(0);
                        if (newTraceData.Length == 0) continue;

                        // 修正数据 (QBC迭代采样)
                        for (int k = 0; k < newTraceData.Length; k++)
                        {
                            double freq = startFreq + (double)k * (stopFreq - startFreq) / (newTraceData.Length - 1);
                            double factor = GetInterpolatedFactor(freq);
                            newTraceData[k] = newTraceData[k] + 107.0 + factor;
                        }

                        double newMaxVal = newTraceData.Max();
                        sampledPoints.Add(new SampledPoint { X = nextX, Y = nextY, Magnitude = newMaxVal });

                        RecordFullTraceData(ref isFullHeaderWritten, sbFull, nextX, nextY, newTraceData, startFreq, stopFreq);

                        // --- [Step 2] 全场插值 (P_n) 计算 ---
                        
                        // 使用当前所有采样点 (sampledPoints) 来预测全场分布
                        // filledData: 二维数组 [Nx, Ny]，即 P_n 的网格化表示
                        // 此过程使用 RBF 均值 (Multiple Kernels) 进行插值，较耗时，放在 Task.Run 中
                        var (filledData, _) = await Task.Run(() => FillUnsampledPointsWithRbfMean(sampledPoints, scanSettings));
                        
                        // 将二维结果展平为一维数组 P_n，方便计算 RMSE
                        // grid[i, j] -> P_n[index]
                        int totalPoints = scanSettings.NumX * scanSettings.NumY;
                        double[] P_n = new double[totalPoints];
                        for (int j = 0; j < scanSettings.NumY; j++)
                        {
                            for (int i = 0; i < scanSettings.NumX; i++)
                            {
                                P_n[j * scanSettings.NumX + i] = filledData[i, j];
                            }
                        }

                        // --- 误差 S_n 计算 (RMSE) ---
                        // 若 P_prev 不为空 (即至少是第二次迭代)，则可以计算误差 S_n
                        if (P_prev != null)
                        {
                            // S_n = sqrt( sum((P_n - P_prev)^2) / totalPoints )
                            double sumSqDiff = 0;
                            for (int k = 0; k < totalPoints; k++)
                            {
                                double diff = P_n[k] - P_prev[k];
                                sumSqDiff += diff * diff;
                            }
                            double Sn = Math.Sqrt(sumSqDiff / totalPoints);

                            // --- [Step 3] 逻辑分支判定 (稳定性检查) ---
                            // 如果 S_n <= Error: 这一次新增采样对模型影响很小 -> 可能趋于稳定
                            // 如果 S_n > Error: 这一次新增采样显著改变了模型 -> 尚不稳定
                            if (Sn <= Error)
                            {
                                count++;
                                // 可以在此增加 Debug 输出或 UI 状态更新: "稳定计数: {count}/{K}, 误差: {Sn:F3}"
                            }
                            else
                            {
                                count = 0; // 误差较大，前面积累的稳定次数作废，重置计数
                            }
                        }
                        
                        // 更新 P_prev (即 P_(n-1)) 为当前的 P_n，供下一次迭代使用
                        P_prev = P_n;

                        // 更新热力图 (使用插值后的完整数据，视觉效果更好，实时看到预测结果)
                        heatMapSeries.Data = filledData;
                        HeatmapModel.InvalidatePlot(true);

                        spectrumSeries.Points.Clear();
                        for (int k = 0; k < newTraceData.Length; k++)
                        {
                            double freq = startFreq + (double)k * (stopFreq - startFreq) / (newTraceData.Length - 1);
                            spectrumSeries.Points.Add(new DataPoint(freq, newTraceData[k]));
                        }
                        SpectrumModel.InvalidatePlot(true);
                    }

                    // --- [Step 4] 循环结束后的收敛状态报告 ---
                    string stopReason = (count >= K) 
                        ? $"模型已收敛 (连续 {K} 次误差 < {Error} dBuV/m)" 
                        : $"达到最大采样点数 ({maxN})";
                    
                    Console.WriteLine($"[{componentName}] AI 扫描结束: {stopReason}");
                    Console.WriteLine($"最终采样点数: {sampledPoints.Count} / {maxN}");

                    // 最终再次调用插值函数，确保用于保存的 CSV 数据是基于最终采样点的最佳推测
                    var (_, fullPointMap) = FillUnsampledPointsWithRbfMean(sampledPoints, scanSettings);
                    
                    sbPeak.Clear(); sbPeak.AppendLine("PhysicalX(mm),PhysicalY(mm),MaxAmplitude(dBuV/m)");
                    var xCoor = GenerateLinspace(xMin, xMax, scanSettings.NumX);
                    var yCoor = GenerateLinspace(yMin, yMax, scanSettings.NumY);

                    // Re-calculate fullPointMap explicitly because FillUnsampledPointsWithRbfMean is called inside loop but result not saved for final output
                    // We need to ensure fullPointMap is populated for CSV saving
                    for (int j = 0; j < scanSettings.NumY; j++)
                    {
                        // 保持与 FillUnsampledPointsWithRbfMean 完全一致的坐标精度处理，确保 Key 能匹配
                        float targetY = (float)Math.Round((float)yCoor[j], 3);
                        for (int i = 0; i < scanSettings.NumX; i++)
                        {
                            float targetX = (float)Math.Round((float)xCoor[i], 3);
                            var key = (targetX, targetY);
                            double val = fullPointMap.ContainsKey(key) ? fullPointMap[key] : 0;
                            sbPeak.AppendLine($"{targetX:F3},{targetY:F3},{val:F3}");
                        }
                    }

                    string baseName = $"{projectName}_{measurementName}_{componentName}";
                    string subFolder = $"{measurementName}_{componentName}";
                    SaveScanDataToCsv(selectedProject, sbPeak.ToString(), $"{baseName}_AI_Peak.csv", subFolder);
                    SaveScanDataToCsv(selectedProject, sbFull.ToString(), $"{baseName}_AI_FullTrace.csv", subFolder);
                    // if (DutImageSource != null) SaveImage(selectedProject, DutImageSource, $"{baseName}_Capture.jpg", subFolder);
                    SaveHeatmapImage(selectedProject, HeatmapModel, $"{baseName}_AI_HeatmapOverlay.png", subFolder);

                    // 计算全局范围，确保散点图与热力图颜色一致
                    double globalMin = fullPointMap.Values.Count > 0 ? fullPointMap.Values.Min() : 0;
                    double globalMax = fullPointMap.Values.Count > 0 ? fullPointMap.Values.Max() : 100;
                    if (Math.Abs(globalMax - globalMin) < 0.001) { globalMin -= 1; globalMax += 1; }

                    // 新增: 保存AI采样点分布图并累计统计数据
                    SaveSamplingDistributionImage(selectedProject, sampledPoints, xMin, xMax, yMin, yMax, $"{baseName}_SamplingPoints.png", subFolder, globalMin, globalMax);
                    totalSampledPoints += sampledPoints.Count;
                    totalMaxPoints += maxN;
                }

            StopQBC:;
                stopwatch.Stop();
                if (!_cancellationTokenSource.Token.IsCancellationRequested) 
                {
                    string msg = $"AI 扫描完成！\n耗时: {stopwatch.Elapsed:hh\\:mm\\:ss}\n采样点数: {totalSampledPoints} / {totalMaxPoints}";
                    MessageBox.Show(msg, "成功");
                }
                else MessageBox.Show("扫描已停止。", "提示");
            }
            catch (Exception ex) { MessageBox.Show("扫描错误: " + ex.Message, "错误"); }
            finally
            {
                if (_hardwareService.ActiveRobot != null && _hardwareService.ActiveRobot.IsConnected)
                { try { var pos = await _hardwareService.ActiveRobot.GetPositionAsync(); await _hardwareService.ActiveRobot.MoveToAsync(pos.X, pos.Y, pos.Z, 90f); } catch { } }
                IsScanning = false;
            }
        }

        /// <summary>
        /// 记录全频谱轨迹数据到 StringBuilder
        /// </summary>
        private void RecordFullTraceData(ref bool isFullHeaderWritten, StringBuilder sbFull, float x, float y, double[] traceData, double startFreq, double stopFreq)
        {
            // 如果是第一次记录，先写 CSV 表头（列名：X, Y, 频率1, 频率2...）
            if (!isFullHeaderWritten)
            {
                sbFull.Append("PhysicalX(mm),PhysicalY(mm)");
                for (int k = 0; k < traceData.Length; k++)
                {
                    double freq = startFreq + (double)k * (stopFreq - startFreq) / (traceData.Length - 1);
                    sbFull.Append($",{freq:F0}Hz");
                }
                sbFull.AppendLine();
                isFullHeaderWritten = true;
            }

            // 记录当前点的坐标和所有频谱幅值
            sbFull.Append($"{x:F3},{y:F3}");
            foreach (var val in traceData)
            {
                sbFull.Append($",{val:F3}");
            }
            sbFull.AppendLine();
        }

        private void SaveScanDataToCsv(ProjectViewModel project, string csvContent, string fileName, string subFolder = "") { try { string dataFolder = Path.Combine(project.ProjectFolderPath, "Data"); if (!string.IsNullOrEmpty(subFolder)) dataFolder = Path.Combine(dataFolder, subFolder); if (!Directory.Exists(dataFolder)) Directory.CreateDirectory(dataFolder); File.WriteAllText(Path.Combine(dataFolder, fileName), csvContent, Encoding.UTF8); } catch (Exception ex) { MessageBox.Show($"保存失败: {ex.Message}"); } }
        private void SaveImage(ProjectViewModel project, BitmapSource image, string fileName, string subFolder = "") { try { string dataFolder = Path.Combine(project.ProjectFolderPath, "Data"); if (!string.IsNullOrEmpty(subFolder)) dataFolder = Path.Combine(dataFolder, subFolder); if (!Directory.Exists(dataFolder)) Directory.CreateDirectory(dataFolder); string fullPath = Path.Combine(dataFolder, fileName); var encoder = new JpegBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using (var stream = new FileStream(fullPath, FileMode.Create)) { encoder.Save(stream); } } catch (Exception ex) { Console.WriteLine($"保存图片失败: {ex.Message}"); } }
        private void SaveHeatmapImage(ProjectViewModel project, PlotModel model, string fileName, string subFolder = "") { try { string dataFolder = Path.Combine(project.ProjectFolderPath, "Data"); if (!string.IsNullOrEmpty(subFolder)) dataFolder = Path.Combine(dataFolder, subFolder); if (!Directory.Exists(dataFolder)) Directory.CreateDirectory(dataFolder); string fullPath = Path.Combine(dataFolder, fileName); Application.Current.Dispatcher.Invoke(() => { var exporter = new PngExporter { Width = 1000, Height = 750 }; exporter.ExportToFile(model, fullPath); }); } catch (Exception ex) { Console.WriteLine($"保存热力图失败: {ex.Message}"); } }

        private void SaveSamplingDistributionImage(ProjectViewModel project, List<SampledPoint> points, double xMin, double xMax, double yMin, double yMax, string fileName, string subFolder = "", double? globalMin = null, double? globalMax = null)
        {
            try
            {
                string dataFolder = Path.Combine(project.ProjectFolderPath, "Data");
                if (!string.IsNullOrEmpty(subFolder)) dataFolder = Path.Combine(dataFolder, subFolder);
                if (!Directory.Exists(dataFolder)) Directory.CreateDirectory(dataFolder);
                string fullPath = Path.Combine(dataFolder, fileName);

                Application.Current.Dispatcher.Invoke(() =>
                {
                    var model = new PlotModel { Title = "AI采样点分布" };
                    model.PlotType = PlotType.Cartesian;
                    model.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Title = "Physical X (mm)", Minimum = xMin, Maximum = xMax });
                    model.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Title = "Physical Y (mm)", Minimum = yMin, Maximum = yMax });

                    var scatterSeries = new ScatterSeries { MarkerType = MarkerType.Circle, MarkerSize = 4, MarkerStroke = OxyColors.Black, MarkerStrokeThickness = 0.5 };
                    
                    var palette = OxyPalettes.Jet(100);
                    // 如果指定了全局最大最小值，使用该范围；否则使用点数据的范围
                    double axisMin = globalMin ?? (points.Count > 0 ? points.Min(p => p.Magnitude) : 0);
                    double axisMax = globalMax ?? (points.Count > 0 ? points.Max(p => p.Magnitude) : 100);
                    
                    if (points.Count > 0 && globalMin == null && globalMax == null)
                    {
                         // 如果没有指定，且有数据，确保Min < Max
                         if (Math.Abs(axisMax - axisMin) < 0.001) { axisMin -= 1; axisMax += 1; }
                    }

                    model.Axes.Add(new LinearColorAxis { Position = AxisPosition.Right, Palette = palette, Minimum = axisMin, Maximum = axisMax, Title = "Strength" });

                    if (points.Count > 0)
                    {
                        foreach (var p in points)
                        {
                            scatterSeries.Points.Add(new ScatterPoint(p.X, p.Y) { Value = p.Magnitude });
                        }
                    }

                    model.Series.Add(scatterSeries);

                    var exporter = new PngExporter { Width = 1000, Height = 750 };
                    exporter.ExportToFile(model, fullPath);
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"保存采样分布图失败: {ex.Message}");
            }
        }
        private void ExecuteStopScan() { _cancellationTokenSource?.Cancel(); }

        private static QbcOutputData CalculateNextSamplePoint(QbcInputData inputData)
        {
            try
            {
                var hyperParams = inputData.HyperParams;
                var sampledPoints = inputData.SampledPoints;
                if (sampledPoints == null || sampledPoints.Count == 0) return new QbcOutputData { Status = "error", Message = "已采样点为空" };
                double[][] xObs = sampledPoints.Select(p => new[] { (double)p.X, (double)p.Y }).ToArray();
                double[] yObs = sampledPoints.Select(p => p.Magnitude).ToArray();
                var xCoor = GenerateLinspace(hyperParams.X_min, hyperParams.X_max, hyperParams.Nx);
                var yCoor = GenerateLinspace(hyperParams.Y_min, hyperParams.Y_max, hyperParams.Ny);

                // 基于网格索引的最近邻近似：将已采样点映射到最近的网格索引，确保未采样点严格对齐网格
                var sampledIndices = new HashSet<(int, int)>();
                double xStep = (hyperParams.Nx > 1) ? (hyperParams.X_max - hyperParams.X_min) / (hyperParams.Nx - 1) : 0;
                double yStep = (hyperParams.Ny > 1) ? (hyperParams.Y_max - hyperParams.Y_min) / (hyperParams.Ny - 1) : 0;

                foreach (var p in sampledPoints)
                {
                    int i = (hyperParams.Nx > 1) ? (int)Math.Round(((double)p.X - hyperParams.X_min) / xStep) : 0;
                    int j = (hyperParams.Ny > 1) ? (int)Math.Round(((double)p.Y - hyperParams.Y_min) / yStep) : 0;
                    // 边界保护，防止采样点微小越界
                    i = Math.Max(0, Math.Min(i, hyperParams.Nx - 1));
                    j = Math.Max(0, Math.Min(j, hyperParams.Ny - 1));
                    sampledIndices.Add((i, j));
                }

                var unselectedPoints = new List<double[]>();
                for (int j = 0; j < hyperParams.Ny; j++)
                {
                    for (int i = 0; i < hyperParams.Nx; i++)
                    {
                        if (!sampledIndices.Contains((i, j)))
                        {
                            unselectedPoints.Add(new[] { xCoor[i], yCoor[j] });
                        }
                    }
                }

                if (unselectedPoints.Count == 0) return new QbcOutputData { Status = "error", Message = "没有可采样的新点了" };
                var kernels = new List<RbfKernel> { RbfKernel.Linear, RbfKernel.Cubic, RbfKernel.ThinPlateSpline, RbfKernel.Quintic };
                var predictions = new double[unselectedPoints.Count][];
                for (int i = 0; i < unselectedPoints.Count; i++) predictions[i] = new double[kernels.Count];
                for (int k = 0; k < kernels.Count; k++)
                {
                    var model = new RbfInterpolator(xObs, yObs, kernels[k], 5);
                    for (int i = 0; i < unselectedPoints.Count; i++) predictions[i][k] = model.Predict(unselectedPoints[i]);
                }
                var variances = new double[unselectedPoints.Count];
                for (int i = 0; i < unselectedPoints.Count; i++)
                {
                    var preds = predictions[i];
                    double mean = preds.Average();
                    variances[i] = preds.Sum(p => Math.Pow(p - mean, 2)) / preds.Length;
                }
                int maxVarIndex = 0; double maxVariance = variances[0];
                for (int i = 1; i < variances.Length; i++) { if (variances[i] > maxVariance) { maxVariance = variances[i]; maxVarIndex = i; } }

                double[] nextPoint;
                // 混合采样策略：如果最大方差过小 (说明所有模型意见一致，可能陷入局部最优或平坦区)
                // 强制进行"距离探索" (Distance-based Exploration)，选择距离现有采样点最远的点
                // 阈值 setting: 与 ConvergenceError 关联，使得对高精度要求的任务同样有更敏感的探索
                // 系数由用户输入提供
                double stdDevThreshold = hyperParams.ConvergenceError * hyperParams.StdDevCoef;

                if (maxVariance < stdDevThreshold)
                {
                    int bestDistIndex = -1;
                    double bestMinDistSq = -1.0;

                    for (int i = 0; i < unselectedPoints.Count; i++)
                    {
                        var candidate = unselectedPoints[i];
                        // 计算该候选点到所有已采样点的最小距离
                        double currentMinDistSq = double.MaxValue;
                        for (int k = 0; k < xObs.Length; k++)
                        {
                            double distSq = Math.Pow(candidate[0] - xObs[k][0], 2) + Math.Pow(candidate[1] - xObs[k][1], 2);
                            if (distSq < currentMinDistSq) currentMinDistSq = distSq;
                        }

                        // 我们要找"最小距离"最大的那个点 (即离大家最远的点)
                        if (currentMinDistSq > bestMinDistSq)
                        {
                            bestMinDistSq = currentMinDistSq;
                            bestDistIndex = i;
                        }
                    }
                    if (bestDistIndex != -1) nextPoint = unselectedPoints[bestDistIndex];
                    else nextPoint = unselectedPoints[maxVarIndex]; // Should not happen
                }
                else
                {
                    nextPoint = unselectedPoints[maxVarIndex];
                }

                // Return exact coordinates to avoid mismatch with grid points (distance check is sensitive)
                return new QbcOutputData { Status = "success", Message = "计算成功", Next_x = nextPoint[0], Next_y = nextPoint[1] };
            }
            catch (Exception ex) { return new QbcOutputData { Status = "error", Message = $"计算出错：{ex.Message}" }; }
        }

        private static double[] GenerateLinspace(double start, double end, int count)
        {
            if (count <= 0) throw new ArgumentException("count必须大于0");
            if (count == 1) return new[] { start };
            double[] result = new double[count];
            double step = (end - start) / (count - 1);
            for (int i = 0; i < count; i++) result[i] = start + step * i;
            return result;
        }

        private (double[,] filledHeatMapData, Dictionary<(float X, float Y), double> fullPointMap) FillUnsampledPointsWithRbfMean(List<SampledPoint> sampledPoints, ScanSettings scanSettings)
        {
            double xMin = Math.Min(scanSettings.StartX, scanSettings.StopX);
            double xMax = Math.Max(scanSettings.StartX, scanSettings.StopX);
            double yMin = Math.Min(scanSettings.StartY, scanSettings.StopY);
            double yMax = Math.Max(scanSettings.StartY, scanSettings.StopY);
            var xCoor = GenerateLinspace(xMin, xMax, scanSettings.NumX);
            var yCoor = GenerateLinspace(yMin, yMax, scanSettings.NumY);
            var sampledPointMap = new Dictionary<(float X, float Y), double>();
            foreach (var p in sampledPoints) sampledPointMap[((float)Math.Round(p.X, 3), (float)Math.Round(p.Y, 3))] = p.Magnitude;
            double[][] xObs = sampledPoints.Select(p => new[] { (double)p.X, (double)p.Y }).ToArray();
            double[] yObs = sampledPoints.Select(p => p.Magnitude).ToArray();
            var kernels = new List<RbfKernel> { RbfKernel.Linear, RbfKernel.Cubic, RbfKernel.ThinPlateSpline, RbfKernel.Quintic };
            var rbfModels = new List<RbfInterpolator>();
            foreach (var kernel in kernels) rbfModels.Add(new RbfInterpolator(xObs, yObs, kernel, 5));
            double[,] filledData = new double[scanSettings.NumX, scanSettings.NumY];
            var fullPointMap = new Dictionary<(float X, float Y), double>();
            for (int j = 0; j < scanSettings.NumY; j++)
            {
                float targetY = (float)Math.Round((float)yCoor[j], 3);
                for (int i = 0; i < scanSettings.NumX; i++)
                {
                    float targetX = (float)Math.Round((float)xCoor[i], 3);
                    var key = (targetX, targetY);
                    if (sampledPointMap.ContainsKey(key)) { filledData[i, j] = sampledPointMap[key]; fullPointMap[key] = sampledPointMap[key]; }
                    else
                    {
                        double[] predictions = new double[rbfModels.Count];
                        for (int k = 0; k < rbfModels.Count; k++) predictions[k] = rbfModels[k].Predict(new[] { (double)targetX, targetY });
                        double meanVal = predictions.Average(); filledData[i, j] = meanVal; fullPointMap[key] = meanVal;
                    }
                }
            }
            return (filledData, fullPointMap);
        }

        public class RbfInterpolator
        {
            private double[][] _xObs; private double[] _yObs; private RbfKernel _kernel; private int _degree; private MathNet.Numerics.LinearAlgebra.Vector<double> _weights; private int _polySize;
            public RbfInterpolator(double[][] xObs, double[] yObs, RbfKernel kernel, int degree)
            {
                _xObs = xObs; _yObs = yObs; _kernel = kernel; _degree = degree; Train();
            }
            private void Train()
            {
                int n = _xObs.Length; var polyBasisExample = GetPolynomialBasis(_xObs[0]); _polySize = polyBasisExample.Length;
                var phi = DenseMatrix.Create(n + _polySize, n + _polySize, 0.0);
                for (int i = 0; i < n; i++)
                {
                    for (int j = 0; j < n; j++) { double r = CalculateDistance(_xObs[i], _xObs[j]); phi[i, j] = EvaluateKernel(r); }
                }
                for (int i = 0; i < n; i++)
                {
                    double[] polys = GetPolynomialBasis(_xObs[i]);
                    for (int j = 0; j < _polySize; j++) { phi[i, n + j] = polys[j]; phi[n + j, i] = polys[j]; }
                }
                var b = DenseVector.OfArray(_yObs.Concat(new double[_polySize]).ToArray());
                phi += DenseMatrix.CreateIdentity(phi.RowCount) * 1e-10;
                _weights = phi.Solve(b);
            }
            public double Predict(double[] x)
            {
                double result = 0.0;
                for (int i = 0; i < _xObs.Length; i++) result += _weights[i] * EvaluateKernel(CalculateDistance(x, _xObs[i]));
                double[] polys = GetPolynomialBasis(x);
                for (int i = 0; i < _polySize; i++) result += _weights[_xObs.Length + i] * polys[i];
                return result;
            }
            private double CalculateDistance(double[] x1, double[] x2) { double sum = 0; for (int i = 0; i < x1.Length; i++) sum += Math.Pow(x1[i] - x2[i], 2); return Math.Sqrt(sum); }
            private double EvaluateKernel(double r) { if (r < 1e-12) return 0.0; return _kernel switch { RbfKernel.Linear => r, RbfKernel.Cubic => r * r * r, RbfKernel.ThinPlateSpline => r * r * Math.Log(r), RbfKernel.Quintic => Math.Pow(r, 5), _ => 0.0 }; }
            private double[] GetPolynomialBasis(double[] x) { var basis = new List<double> { 1.0 }; if (_degree >= 1) basis.AddRange(x); if (_degree >= 2) { basis.Add(x[0] * x[0]); basis.Add(x[0] * x[1]); basis.Add(x[1] * x[1]); } return basis.ToArray(); }
        }
    }
}