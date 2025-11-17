using FieldScanNew.Models;
using Ivi.Visa;
using System;
using System.Threading.Tasks;

namespace FieldScanNew.Services
{
    // 频谱仪的“插头”
    public class SpectrumAnalyzer : IMeasurementDevice
    {
        public string DeviceName => "Spectrum Analyzer (VISA)";
        public bool IsConnected { get; private set; } = false;
        private IMessageBasedSession? _saSession;

        public async Task ConnectAsync(InstrumentSettings settings)
        {
            if (IsConnected) return;
            await Task.Run(() =>
            {
                // 修正了连接字符串，加入了板卡号'0'
                string visaAddress = $"TCPIP0::{settings.IpAddress}::{settings.Port}::SOCKET";
                var visaSession = GlobalResourceManager.Open(visaAddress);
                _saSession = visaSession as IMessageBasedSession;
                if (_saSession == null) throw new Exception("无法创建VISA会话。");
                _saSession.TimeoutMilliseconds = 30000;
                IsConnected = true;
            });
        }

        public void Disconnect()
        {
            if (IsConnected && _saSession != null)
            {
                _saSession.Dispose();
                IsConnected = false;
                _saSession = null;
            }
        }
        public async Task<double> GetMeasurementValueAsync(int delayMs)
        {
            if (!IsConnected || _saSession == null) throw new InvalidOperationException("频谱仪未连接");

            return await Task.Run(() =>
            {
                var formattedIO = _saSession.FormattedIO;
                // 复刻旧项目中 Sa.cs 的 ReNewMaxHoldAndRead 方法逻辑
                formattedIO.WriteLine(":TRAC:TYPE MAXH;"); // 设置为最大值保持
                Thread.Sleep(delayMs);                     // 等待
                formattedIO.WriteLine(":CALC:MARK:MAX;");  // 移动Marker到最大值
                formattedIO.WriteLine(":CALC:MARK:Y?;");   // 查询Marker的Y值（功率）
                return formattedIO.ReadLineDouble();       // 读取返回的功率值
            });
        }
    }
}