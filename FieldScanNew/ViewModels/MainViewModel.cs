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
    public partial class MainViewModel : ViewModelBase
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

        private static double GetFrequencyAtIndex(double startFreq, double stopFreq, int index, int totalPoints)
        {
            if (totalPoints <= 1) return startFreq;
            return startFreq + (double)index * (stopFreq - startFreq) / (totalPoints - 1);
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
            var stopwatch = Stopwatch.StartNew();
            // 稳定延时（ms）: 机器人到位后等待该时间再开始测量，避免未稳态读数
            int settleDelayMs = 100;

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
                    if (_cancellationTokenSource.Token.IsCancellationRequested) break;
                    await Task.Delay(settleDelayMs);

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
                            if (_cancellationTokenSource.Token.IsCancellationRequested) goto StopScanLabel;
                            await Task.Delay(settleDelayMs);
                            double[] traceData = await _hardwareService.ActiveDevice.GetTraceDataAsync(0);

                            if (traceData.Length > 0)
                            {
                                // ========================================================
                                // 修正数据：读数(dBm) + 107 + 探头因子
                                // ========================================================
                                for (int k = 0; k < traceData.Length; k++)
                                {
                                    double freq = GetFrequencyAtIndex(startFreq, stopFreq, k, traceData.Length);
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
                                    double freq = GetFrequencyAtIndex(startFreq, stopFreq, k, traceData.Length);
                                    spectrumSeries.Points.Add(new DataPoint(freq, traceData[k]));
                                }
                                SpectrumModel.InvalidatePlot(true);

                                sbPeak.AppendLine($"{targetX:F3},{targetY:F3},{maxVal:F3}");

                                if (!isFullHeaderWritten)
                                {
                                    sbFull.Append("PhysicalX(mm),PhysicalY(mm)");
                                    for (int k = 0; k < traceData.Length; k++)
                                    {
                                        double freq = GetFrequencyAtIndex(startFreq, stopFreq, k, traceData.Length);
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
                stopwatch.Stop();
                if (!_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    MessageBox.Show($"所有选定分量扫描完成！\n耗时: {stopwatch.Elapsed:hh\\:mm\\:ss}", "成功");
                }
                else
                {
                    MessageBox.Show($"扫描已停止。\n耗时: {stopwatch.Elapsed:hh\\:mm\\:ss}", "提示");
                }
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
    }
}