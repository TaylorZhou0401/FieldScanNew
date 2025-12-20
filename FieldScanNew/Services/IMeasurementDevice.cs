using FieldScanNew.Models;
using System.Threading.Tasks;

namespace FieldScanNew.Services
{
    public interface IMeasurementDevice
    {
        string DeviceName { get; }
        bool IsConnected { get; }
        Task ConnectAsync(InstrumentSettings settings);
        void Disconnect();

        // 原有的单点获取
        Task<double> GetMeasurementValueAsync(int delayMs);

        // **核心新增：获取整条频谱曲线数据 (数组)**
        Task<double[]> GetTraceDataAsync(int delayMs);
    }
}