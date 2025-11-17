using FieldScanNew.Models;
using System.Threading.Tasks;

namespace FieldScanNew.Services
{
    // 测量仪器的“标准插座”
    public interface IMeasurementDevice
    {
        string DeviceName { get; }
        bool IsConnected { get; }
        Task ConnectAsync(InstrumentSettings settings);
        void Disconnect();
        // **新增**: 获取一个测量值 (例如经过MaxHold后的)
        Task<double> GetMeasurementValueAsync(int delayMs);
    }
}