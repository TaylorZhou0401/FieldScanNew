using FieldScanNew.Infrastructure;
using FieldScanNew.Views; // 需要引用InputDialog
using System.Collections.ObjectModel;
using System.Windows.Input;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.TextBox;

namespace FieldScanNew.ViewModels
{
    public class MeasurementViewModel : ViewModelBase, IStepViewModel
    {
        public string DisplayName { get; set; }
        public ObservableCollection<IStepViewModel> Steps { get; }

        public ICommand RenameCommand { get; }
        public ProjectViewModel ParentProject { get; }
        public MeasurementViewModel(string name, ProjectViewModel parent)
        {
            DisplayName = name;
            ParentProject = parent; // **新增**: 保存父级引用
            RenameCommand = new RelayCommand(_ => ExecuteRename());

            // **核心修正**: 创建步骤时，将父级项目中的配置对象传递进去
            Steps = new ObservableCollection<IStepViewModel>
            {
                new InstrumentSetupViewModel(ParentProject.ProjectData.InstrumentConfig),
                new ProbeSetupViewModel(),
                new ScanSettingsViewModel(),
                new ZCalibViewModel(),
                new XYCalibViewModel(),
                new ScanAreaViewModel(ParentProject.ProjectData.ScanConfig) // <-- 传递配置
            };
        }
        private void ExecuteRename()
        {
            // 弹出一个输入对话框，让用户输入新名字
            var inputDialog = new InputDialog("请输入新的测量名称:", this.DisplayName);
            if (inputDialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(inputDialog.Answer))
            {
                DisplayName = inputDialog.Answer;
                OnPropertyChanged(nameof(DisplayName)); // 通知UI更新名称
            }
        }
    }
}