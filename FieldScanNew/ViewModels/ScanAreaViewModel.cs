using FieldScanNew.Infrastructure;
using FieldScanNew.Models; // 需要引用Models

namespace FieldScanNew.ViewModels
{
    public class ScanAreaViewModel : ViewModelBase, IStepViewModel
    {
        public string DisplayName => "6. 扫描区域配置";

        // **新增**: 持有从项目传入的扫描设置对象的引用
        public ScanSettings Settings { get; }

        public ScanAreaViewModel(ScanSettings settings)
        {
            Settings = settings;
        }
    }
}