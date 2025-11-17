using FieldScanNew.Infrastructure;
using FieldScanNew.Services;
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace FieldScanNew.ViewModels
{
    public class ZCalibViewModel : ViewModelBase, IStepViewModel
    {
        public string DisplayName => "4. Z轴校准";
        private readonly HardwareService _hardwareService;

        private float _stepSize = 1.0f;
        public float StepSize { get => _stepSize; set { _stepSize = value; OnPropertyChanged(); } }

        public ICommand MoveJogCommand { get; }

        public ZCalibViewModel()
        {
            _hardwareService = HardwareService.Instance;
            MoveJogCommand = new RelayCommand(async (param) => await ExecuteMoveJog(param));
        }

        private async Task ExecuteMoveJog(object? parameter)
        {
            // **核心修正**：使用抽象的 ActiveRobot
            if (_hardwareService.ActiveRobot == null || !_hardwareService.ActiveRobot.IsConnected)
            {
                System.Windows.MessageBox.Show("请先在“仪器连接”中连接机器人。", "提示");
                return;
            }

            float x = 0, y = 0, z = 0;
            switch (parameter?.ToString())
            {
                case "X+": x = StepSize; break;
                case "X-": x = -StepSize; break;
                case "Y+": y = StepSize; break;
                case "Y-": y = -StepSize; break;
                case "Z+": z = StepSize; break;
                case "Z-": z = -StepSize; break;
            }

            try
            {
                // **核心修正**：调用抽象接口的方法
                await _hardwareService.ActiveRobot.MoveJogAsync(x, y, z);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("移动机器人失败: " + ex.Message, "错误");
            }
        }
    }
}