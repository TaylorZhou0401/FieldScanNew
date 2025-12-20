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

            SpectrumModel = new PlotModel { Title = "实时频谱 (Trace)" };
            SpectrumModel.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Title = "Index" });
            SpectrumModel.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Title = "Amplitude (dBm)" });

            Projects = new ObservableCollection<ProjectViewModel>();
            _currentScanSettings = new ScanSettings();
            _currentInstrumentSettings = new InstrumentSettings();

            AddNewProjectCommand = new RelayCommand(ExecuteAddNewProject);
            LoadProjectCommand = new RelayCommand(ExecuteLoadProject);
            StartScanCommand = new RelayCommand(async _ => await ExecuteStartScan(), _ => !IsScanning);
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
        private void ExecuteLoadProject(object? parameter) { try { var openFileDialog = new OpenFileDialog { Filter = "项目文件 (*.json)|*.json" }; if (openFileDialog.ShowDialog() == true) { string filePath = openFileDialog.FileName; string fileContent = File.ReadAllText(filePath); if (string.IsNullOrWhiteSpace(fileContent)) { MessageBox.Show("项目文件为空或已损坏。", "错误"); return; } var projectData = JsonSerializer.Deserialize<ProjectData>(fileContent); if (projectData == null) { MessageBox.Show("无法解析项目文件。", "错误"); return; } string projectFolder = Path.GetDirectoryName(filePath) ?? string.Empty; if (string.IsNullOrEmpty(projectFolder)) { MessageBox.Show("无法获取项目文件夹路径。", "错误"); return; } var loadedProject = new ProjectViewModel(projectData.ProjectName, projectFolder, this) { ProjectData = projectData }; if (projectData.MeasurementNames != null) { foreach (var name in projectData.MeasurementNames) loadedProject.Measurements.Add(new MeasurementViewModel(name, loadedProject)); } Projects.Add(loadedProject); foreach (var proj in Projects.Where(p => p != loadedProject)) proj.IsSelected = false; loadedProject.IsSelected = true; LoadProjectDataIntoViewModel(loadedProject); } } catch (Exception ex) { MessageBox.Show("加载项目时发生严重错误: " + ex.Message, "错误"); } }
        public void AutoSaveCurrentProject(ProjectViewModel project) { if (project?.ProjectData == null) return; project.ProjectData.ScanConfig = this.CurrentScanSettings; project.ProjectData.InstrumentConfig = this.CurrentInstrumentSettings; project.ProjectData.MeasurementNames = project.Measurements.Select(m => m.DisplayName).ToList(); try { string filePath = Path.Combine(project.ProjectFolderPath, "project.json"); var options = new JsonSerializerOptions { WriteIndented = true }; string jsonString = JsonSerializer.Serialize(project.ProjectData, options); File.WriteAllText(filePath, jsonString); } catch (Exception ex) { Console.WriteLine($"自动保存失败: {ex.Message}"); } }

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

                for (int j = 0; j < scanSettings.NumY; j++)
                {
                    for (int i = 0; i < scanSettings.NumX; i++)
                    {
                        if (_cancellationTokenSource.Token.IsCancellationRequested) { MessageBox.Show("扫描已停止。", "提示"); IsScanning = false; return; }

                        float targetX = scanSettings.StartX + i * (scanSettings.StopX - scanSettings.StartX) / (scanSettings.NumX - 1);
                        float targetY = scanSettings.StartY + j * (scanSettings.StopY - scanSettings.StartY) / (scanSettings.NumY - 1);

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