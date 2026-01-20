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

            // 初始化频谱图横轴为频率
            SpectrumModel = new PlotModel { Title = "实时频谱 (Trace)" };
            SpectrumModel.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Title = "Frequency (Hz)" });
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
                // ================== 频率坐标计算逻辑 ==================
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

                    var sbPeak = new StringBuilder(); sbPeak.AppendLine("PhysicalX(mm),PhysicalY(mm),MaxAmplitude(dBm)");
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
                                double maxVal = traceData.Max();
                                double ratioX = (targetX - xMin) / (xMax - xMin);
                                double ratioY = (targetY - yMin) / (yMax - yMin);
                                int arrayX = Math.Max(0, Math.Min((int)Math.Round(ratioX * (scanSettings.NumX - 1)), scanSettings.NumX - 1));
                                int arrayY = Math.Max(0, Math.Min((int)Math.Round(ratioY * (scanSettings.NumY - 1)), scanSettings.NumY - 1));

                                heatMapData[arrayX, arrayY] = maxVal;
                                HeatmapModel.InvalidatePlot(true);

                                // 修改频谱图横坐标为计算后的频率
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
                    HeatmapModel.Title = $"QBC热力图 - {componentName}";
                    HeatmapModel.ResetAllAxes(); HeatmapModel.InvalidatePlot(true);

                    var spectrumSeries = new LineSeries { Title = "Live Trace", Color = OxyColors.Blue, StrokeThickness = 1 };
                    SpectrumModel.Series.Clear(); SpectrumModel.Series.Add(spectrumSeries); SpectrumModel.InvalidatePlot(true);

                    var sbPeak = new StringBuilder(); sbPeak.AppendLine("PhysicalX(mm),PhysicalY(mm),MaxAmplitude(dBm)");

                    int sumSampleCount = scanSettings.NumX * scanSettings.NumY;
                    int targetSampleCount = (int)Math.Round(3.13 * Math.Pow(sumSampleCount, 0.602));
                    targetSampleCount = Math.Max(4, Math.Min(targetSampleCount, sumSampleCount));

                    int initMaxCount = targetSampleCount - 1;
                    int initPointCount = Math.Max(4, (int)Math.Round(initMaxCount * 0.2));
                    initPointCount = Math.Min(initPointCount, initMaxCount);
                    int gridCols = (int)Math.Round(Math.Sqrt(initPointCount * (double)scanSettings.NumX / scanSettings.NumY));
                    int gridRows = (int)Math.Round((double)initPointCount / gridCols);
                    gridCols = Math.Max(2, Math.Min(gridCols, scanSettings.NumX));
                    gridRows = Math.Max(2, Math.Min(gridRows, scanSettings.NumY));

                    int xStepIndex = (scanSettings.NumX - 1) / (gridCols - 1);
                    int yStepIndex = (scanSettings.NumY - 1) / (gridRows - 1);

                    List<SampledPoint> sampledPoints = new List<SampledPoint>();

                    for (int row = 0; row < gridRows; row++)
                    {
                        if (_cancellationTokenSource.Token.IsCancellationRequested) goto StopQBC;
                        int yIndex = row * yStepIndex;
                        float targetY = scanSettings.StartY + yIndex * (scanSettings.StopY - scanSettings.StartY) / (scanSettings.NumY - 1);

                        for (int col = 0; col < gridCols; col++)
                        {
                            if (_cancellationTokenSource.Token.IsCancellationRequested) goto StopQBC;
                            int xIndex = col * xStepIndex;
                            float targetX = scanSettings.StartX + xIndex * (scanSettings.StopX - scanSettings.StartX) / (scanSettings.NumX - 1);

                            await _hardwareService.ActiveRobot.MoveToAsync(targetX, targetY, scanSettings.ScanHeightZ, robotAngle);
                            double[] traceData = await _hardwareService.ActiveDevice.GetTraceDataAsync(0);
                            if (traceData.Length == 0) continue;

                            double maxVal = traceData.Max();
                            sampledPoints.Add(new SampledPoint { X = targetX, Y = targetY, Magnitude = maxVal });

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
                        }
                    }

                    while (sampledPoints.Count < targetSampleCount)
                    {
                        if (_cancellationTokenSource.Token.IsCancellationRequested) goto StopQBC;
                        var inputData = new QbcInputData { HyperParams = new HyperParams { X_min = xMin, X_max = xMax, Y_min = yMin, Y_max = yMax, Nx = scanSettings.NumX, Ny = scanSettings.NumY, Uncertainty_size = targetSampleCount }, SampledPoints = sampledPoints };
                        var nextPointData = CalculateNextSamplePoint(inputData);
                        if (nextPointData.Status != "success") break;

                        float nextX = (float)nextPointData.Next_x;
                        float nextY = (float)nextPointData.Next_y;

                        await _hardwareService.ActiveRobot.MoveToAsync(nextX, nextY, scanSettings.ScanHeightZ, robotAngle);
                        double[] newTraceData = await _hardwareService.ActiveDevice.GetTraceDataAsync(0);
                        if (newTraceData.Length == 0) continue;

                        double newMaxVal = newTraceData.Max();
                        sampledPoints.Add(new SampledPoint { X = nextX, Y = nextY, Magnitude = newMaxVal });

                        double ratioX = (nextX - xMin) / (xMax - xMin);
                        double ratioY = (nextY - yMin) / (yMax - yMin);
                        int arrayX = Math.Max(0, Math.Min((int)Math.Round(ratioX * (scanSettings.NumX - 1)), scanSettings.NumX - 1));
                        int arrayY = Math.Max(0, Math.Min((int)Math.Round(ratioY * (scanSettings.NumY - 1)), scanSettings.NumY - 1));
                        heatMapData[arrayX, arrayY] = newMaxVal;
                        HeatmapModel.InvalidatePlot(true);
                    }

                    var (filledHeatMapData, fullPointMap) = FillUnsampledPointsWithRbfMean(sampledPoints, scanSettings);
                    heatMapSeries.Data = filledHeatMapData;
                    HeatmapModel.InvalidatePlot(true);

                    sbPeak.Clear(); sbPeak.AppendLine("PhysicalX(mm),PhysicalY(mm),MaxAmplitude(dBm)");
                    var xCoor = GenerateLinspace(xMin, xMax, scanSettings.NumX);
                    var yCoor = GenerateLinspace(yMin, yMax, scanSettings.NumY);

                    for (int j = 0; j < scanSettings.NumY; j++)
                    {
                        float targetY = (float)yCoor[j];
                        for (int i = 0; i < scanSettings.NumX; i++)
                        {
                            float targetX = (float)xCoor[i];
                            var key = ((float)Math.Round(targetX, 3), (float)Math.Round(targetY, 3));
                            double val = fullPointMap.ContainsKey(key) ? fullPointMap[key] : 0;
                            sbPeak.AppendLine($"{targetX:F3},{targetY:F3},{val:F3}");
                        }
                    }

                    string baseName = $"{projectName}_{measurementName}_{componentName}";
                    string subFolder = $"{measurementName}_{componentName}";
                    SaveScanDataToCsv(selectedProject, sbPeak.ToString(), $"{baseName}_Peak.csv", subFolder);
                    if (DutImageSource != null) SaveImage(selectedProject, DutImageSource, $"{baseName}_Capture.jpg", subFolder);
                    SaveHeatmapImage(selectedProject, HeatmapModel, $"{baseName}_HeatmapOverlay.png", subFolder);
                }

            StopQBC:;
                if (!_cancellationTokenSource.Token.IsCancellationRequested) MessageBox.Show("QBC 扫描完成！", "成功");
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

        private void SaveScanDataToCsv(ProjectViewModel project, string csvContent, string fileName, string subFolder = "") { try { string dataFolder = Path.Combine(project.ProjectFolderPath, "Data"); if (!string.IsNullOrEmpty(subFolder)) dataFolder = Path.Combine(dataFolder, subFolder); if (!Directory.Exists(dataFolder)) Directory.CreateDirectory(dataFolder); File.WriteAllText(Path.Combine(dataFolder, fileName), csvContent, Encoding.UTF8); } catch (Exception ex) { MessageBox.Show($"保存失败: {ex.Message}"); } }
        private void SaveImage(ProjectViewModel project, BitmapSource image, string fileName, string subFolder = "") { try { string dataFolder = Path.Combine(project.ProjectFolderPath, "Data"); if (!string.IsNullOrEmpty(subFolder)) dataFolder = Path.Combine(dataFolder, subFolder); if (!Directory.Exists(dataFolder)) Directory.CreateDirectory(dataFolder); string fullPath = Path.Combine(dataFolder, fileName); var encoder = new JpegBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using (var stream = new FileStream(fullPath, FileMode.Create)) { encoder.Save(stream); } } catch (Exception ex) { Console.WriteLine($"保存图片失败: {ex.Message}"); } }
        private void SaveHeatmapImage(ProjectViewModel project, PlotModel model, string fileName, string subFolder = "") { try { string dataFolder = Path.Combine(project.ProjectFolderPath, "Data"); if (!string.IsNullOrEmpty(subFolder)) dataFolder = Path.Combine(dataFolder, subFolder); if (!Directory.Exists(dataFolder)) Directory.CreateDirectory(dataFolder); string fullPath = Path.Combine(dataFolder, fileName); Application.Current.Dispatcher.Invoke(() => { var exporter = new PngExporter { Width = 1000, Height = 750 }; exporter.ExportToFile(model, fullPath); }); } catch (Exception ex) { Console.WriteLine($"保存热力图失败: {ex.Message}"); } }
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
                var gridPoints = new List<double[]>();
                foreach (var y in yCoor) { foreach (var x in xCoor) { gridPoints.Add(new[] { x, y }); } }
                var unselectedPoints = new List<double[]>();
                foreach (var point in gridPoints)
                {
                    bool isSampled = false;
                    foreach (var sampled in xObs)
                    {
                        double distance = Math.Sqrt(Math.Pow(point[0] - sampled[0], 2) + Math.Pow(point[1] - sampled[1], 2));
                        if (distance <= 1e-3) { isSampled = true; break; }
                    }
                    if (!isSampled) unselectedPoints.Add(point);
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
                var nextPoint = unselectedPoints[maxVarIndex];
                return new QbcOutputData { Status = "success", Message = "计算成功", Next_x = Math.Round(nextPoint[0], 2), Next_y = Math.Round(nextPoint[1], 2) };
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