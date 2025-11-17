using System;

namespace FieldScanNew.Services
{
    // 这是一个简单的位置结构体，供服务之间传递数据
    public struct RobotPosition { public float X, Y, Z, R; }

    // 硬件“管理器”，使用单例模式
    public class HardwareService
    {
        private static readonly Lazy<HardwareService> _instance = new Lazy<HardwareService>(() => new HardwareService());
        public static HardwareService Instance => _instance.Value;

        // 硬件“插座”
        public IRobotArm? ActiveRobot { get; private set; }
        public IMeasurementDevice? ActiveDevice { get; private set; }

        private HardwareService() { }

        // “插拔”方法
        public void SetActiveRobot(IRobotArm robot)
        {
            ActiveRobot?.Disconnect();
            ActiveRobot = robot;
        }

        public void SetActiveDevice(IMeasurementDevice device)
        {
            ActiveDevice?.Disconnect();
            ActiveDevice = device;
        }
    }
}