using FieldScanNew.Infrastructure;
using FieldScanNew.Models;
using System.Collections.ObjectModel;
using System.Windows.Input; // 需要引用 ICommand

namespace FieldScanNew.ViewModels
{
    public class ProjectViewModel : ViewModelBase, IStepViewModel
    {
        public string DisplayName { get; set; }
        public string ProjectFolderPath { get; }
        public ObservableCollection<MeasurementViewModel> Measurements { get; }
        public ProjectData ProjectData { get; set; }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                OnPropertyChanged();
                if (_isSelected && ParentMainViewModel != null)
                {
                    ParentMainViewModel.LoadProjectDataIntoViewModel(this);
                }
            }
        }

        private MainViewModel ParentMainViewModel { get; }

        // **新增**：用于添加新测量项的命令
        public ICommand AddNewMeasurementCommand { get; }

        public ProjectViewModel(string name, string folderPath, MainViewModel parent)
        {
            DisplayName = name;
            ProjectFolderPath = folderPath;
            ParentMainViewModel = parent;
            ProjectData = new ProjectData { ProjectName = name };

            // **核心改动**：初始化一个空的测量项列表
            Measurements = new ObservableCollection<MeasurementViewModel>();

            // 初始化命令，并指定要执行的方法
            AddNewMeasurementCommand = new RelayCommand(ExecuteAddNewMeasurement);
        }

        private void ExecuteAddNewMeasurement(object? parameter)
        {
            // **核心修正**：创建测量项时，把对自己的引用(this)传递下去
            var newMeasurement = new MeasurementViewModel($"New Measurement {Measurements.Count + 1}", this);
            Measurements.Add(newMeasurement);
        }
    }
}