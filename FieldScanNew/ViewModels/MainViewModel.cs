using FieldScanNew.Infrastructure;
using FieldScanNew.Models;
using FieldScanNew.Services;
using FieldScanNew.Views;
using OxyPlot;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq; // 需要添加Linq
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Media.Imaging;

using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using MessageBox = System.Windows.MessageBox;

namespace FieldScanNew.ViewModels
{
    public class MainViewModel : ViewModelBase
    {
        private readonly IDialogService _dialogService;
        public PlotModel HeatmapModel { get; set; }
        public PlotModel SpectrumModel { get; set; }
        public ObservableCollection<ProjectViewModel> Projects { get; }

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
                }
            }
        }

        private ScanSettings _currentScanSettings;
        public ScanSettings CurrentScanSettings
        {
            get => _currentScanSettings;
            set
            {
                if (_currentScanSettings != null) _currentScanSettings.PropertyChanged -= OnSettingsChanged;
                _currentScanSettings = value;
                if (_currentScanSettings != null) _currentScanSettings.PropertyChanged += OnSettingsChanged;
                OnPropertyChanged();
            }
        }

        private InstrumentSettings _currentInstrumentSettings;
        public InstrumentSettings CurrentInstrumentSettings
        {
            get => _currentInstrumentSettings;
            set
            {
                if (_currentInstrumentSettings != null) _currentInstrumentSettings.PropertyChanged -= OnSettingsChanged;
                _currentInstrumentSettings = value;
                if (_currentInstrumentSettings != null) _currentInstrumentSettings.PropertyChanged += OnSettingsChanged;
                OnPropertyChanged();
            }
        }

        public ICommand AddNewProjectCommand { get; }
        public ICommand LoadProjectCommand { get; }

        public MainViewModel()
        {
            _dialogService = new DialogService();
            HeatmapModel = new PlotModel { Title = "近场热力图" };
            SpectrumModel = new PlotModel { Title = "实时频谱" };
            Projects = new ObservableCollection<ProjectViewModel>();
            _currentScanSettings = new ScanSettings();
            _currentInstrumentSettings = new InstrumentSettings();

            AddNewProjectCommand = new RelayCommand(ExecuteAddNewProject);
            LoadProjectCommand = new RelayCommand(ExecuteLoadProject);

            CurrentScanSettings.PropertyChanged += OnSettingsChanged;
            CurrentInstrumentSettings.PropertyChanged += OnSettingsChanged;
        }

        private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
        {
            var selectedProject = Projects.FirstOrDefault(p => p.IsSelected);
            if (selectedProject != null)
            {
                AutoSaveCurrentProject(selectedProject);
            }
        }

        private void ExecuteAddNewProject(object? parameter)
        {
            try // **健壮性修正：包裹整个操作，防止任何意外导致闪退**
            {
                var inputDialog = new InputDialog("请输入新项目的名称:", "新项目");
                if (inputDialog.ShowDialog() != true) return;
                string projectName = inputDialog.Answer;
                if (string.IsNullOrWhiteSpace(projectName)) return;

                var folderDialog = new System.Windows.Forms.FolderBrowserDialog { Description = "请选择项目的存放路径", UseDescriptionForTitle = true };
                if (folderDialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

                string projectPath = Path.Combine(folderDialog.SelectedPath, projectName);
                if (Directory.Exists(projectPath)) { MessageBox.Show("同名项目文件夹已存在！", "错误"); return; }

                Directory.CreateDirectory(projectPath);

                // **健-壮性修正：创建时传入this，确保父子关系被建立**
                var newProject = new ProjectViewModel(projectName, projectPath, this);
                Projects.Add(newProject);

                // 取消选中其他所有项目
                foreach (var proj in Projects.Where(p => p != newProject))
                {
                    proj.IsSelected = false;
                }
                newProject.IsSelected = true; // 自动选中

                // **核心修正：立即保存一次，确保 project.json 文件被创建**
                AutoSaveCurrentProject(newProject);
            }
            catch (Exception ex)
            {
                MessageBox.Show("创建新项目时发生严重错误: " + ex.Message, "错误");
            }
        }

        private void ExecuteLoadProject(object? parameter)
        {
            try // **健壮性修正：包裹整个操作**
            {
                var openFileDialog = new OpenFileDialog { Filter = "项目文件 (*.json)|*.json" };
                if (openFileDialog.ShowDialog() == true)
                {
                    string filePath = openFileDialog.FileName;
                    string fileContent = File.ReadAllText(filePath);

                    // **健壮性修正：检查文件内容是否为空**
                    if (string.IsNullOrWhiteSpace(fileContent))
                    {
                        MessageBox.Show("项目文件为空或已损坏。", "错误");
                        return;
                    }

                    var projectData = JsonSerializer.Deserialize<ProjectData>(fileContent);
                    if (projectData == null) { MessageBox.Show("无法解析项目文件。", "错误"); return; }

                    string projectFolder = Path.GetDirectoryName(filePath) ?? string.Empty;
                    if (string.IsNullOrEmpty(projectFolder))
                    {
                        MessageBox.Show("无法获取项目文件夹路径。", "错误");
                        return;
                    }

                    // **健壮性修正：创建时传入this**
                    var loadedProject = new ProjectViewModel(projectData.ProjectName, projectFolder, this)
                    {
                        ProjectData = projectData
                    };

                    Projects.Add(loadedProject);
                    // 取消选中其他所有项目
                    foreach (var proj in Projects.Where(p => p != loadedProject))
                    {
                        proj.IsSelected = false;
                    }
                    loadedProject.IsSelected = true; // 自动选中
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("加载项目时发生严重错误: " + ex.Message, "错误");
            }
        }

        private void AutoSaveCurrentProject(ProjectViewModel project)
        {
            // **健壮性修正：在保存前进行非空检查**
            if (project?.ProjectData == null) return;

            project.ProjectData.ScanConfig = this.CurrentScanSettings;
            project.ProjectData.InstrumentConfig = this.CurrentInstrumentSettings;

            try
            {
                string filePath = Path.Combine(project.ProjectFolderPath, "project.json");
                var options = new JsonSerializerOptions { WriteIndented = true };
                string jsonString = JsonSerializer.Serialize(project.ProjectData, options);
                File.WriteAllText(filePath, jsonString);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"自动保存失败: {ex.Message}");
            }
        }

        internal void LoadProjectDataIntoViewModel(ProjectViewModel? project)
        {
            // **健壮性修正：确保project.ProjectData不为null**
            if (project?.ProjectData == null)
            {
                CurrentScanSettings = new ScanSettings();
                CurrentInstrumentSettings = new InstrumentSettings();
                return;
            }

            CurrentScanSettings = project.ProjectData.ScanConfig ?? new ScanSettings();
            CurrentInstrumentSettings = project.ProjectData.InstrumentConfig ?? new InstrumentSettings();
        }
    }
}