using System.Threading.Tasks;

namespace FieldScanNew.Services
{
    // 机械臂的“标准插座”
    public interface IRobotArm
    {
        string DeviceName { get; }
        bool IsConnected { get; }
        Task ConnectAsync();
        void Disconnect();
        Task MoveJogAsync(float stepX, float stepY, float stepZ);
        Task<RobotPosition> GetPositionAsync();

        // **新增**: 移动到绝对坐标
        Task MoveToAsync(float x, float y, float z, float r);
    }
}