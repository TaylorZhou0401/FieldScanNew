using FieldScanNew.Models;
using Ivi.Visa;
using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FieldScanNew.Services
{
    public class SpectrumAnalyzer : IMeasurementDevice
    {
        public string DeviceName => "Spectrum Analyzer (VISA)";
        public bool IsConnected { get; private set; } = false;
        private IMessageBasedSession? _saSession;

        public async Task ConnectAsync(InstrumentSettings settings)
        {
            if (IsConnected) Disconnect();

            await Task.Run(() =>
            {
                try
                {
                    string visaAddress = $"TCPIP0::{settings.IpAddress}::inst0::INSTR";
                    var visaSession = GlobalResourceManager.Open(visaAddress);
                    _saSession = visaSession as IMessageBasedSession;

                    if (_saSession == null) throw new Exception("无法创建VISA会话。");

                    _saSession.TimeoutMilliseconds = 3000;
                    _saSession.TerminationCharacterEnabled = true;
                    _saSession.TerminationCharacter = (byte)'\n';
                    _saSession.SendEndEnabled = true;

                    _saSession.FormattedIO.WriteLine("*IDN?");
                    string idn = _saSession.FormattedIO.ReadLine();

                    // 下发参数
                    _saSession.FormattedIO.WriteLine(string.Format(CultureInfo.InvariantCulture, ":FREQ:CENT {0}", settings.CenterFrequencyHz));
                    _saSession.FormattedIO.WriteLine(string.Format(CultureInfo.InvariantCulture, ":FREQ:SPAN {0}", settings.SpanHz));
                    _saSession.FormattedIO.WriteLine(string.Format(CultureInfo.InvariantCulture, ":DISP:WIND:TRAC:Y:RLEV {0}", settings.ReferenceLevelDb));

                    if (settings.Points > 0)
                        _saSession.FormattedIO.WriteLine($":SWE:POIN {settings.Points}");

                    // 下发 RBW / VBW
                    if (settings.RbwHz > 0)
                        _saSession.FormattedIO.WriteLine(string.Format(CultureInfo.InvariantCulture, ":BAND {0}", settings.RbwHz));
                    else
                        _saSession.FormattedIO.WriteLine(":BAND:AUTO ON");

                    if (settings.VbwHz > 0)
                        _saSession.FormattedIO.WriteLine(string.Format(CultureInfo.InvariantCulture, ":BAND:VID {0}", settings.VbwHz));
                    else
                        _saSession.FormattedIO.WriteLine(":BAND:VID:AUTO ON");

                    _saSession.FormattedIO.WriteLine(":DET:TRAC POS");
                    _saSession.FormattedIO.WriteLine(":INIT:CONT OFF"); // 关闭连续扫描
                    _saSession.FormattedIO.WriteLine(":FORM:DATA ASC");

                    _saSession.TimeoutMilliseconds = 30000; // 默认30秒
                    IsConnected = true;
                }
                catch (Exception ex)
                {
                    Disconnect();
                    throw new Exception($"连接失败: {ex.Message}");
                }
            });
        }

        public void Disconnect()
        {
            if (_saSession != null) { try { _saSession.Dispose(); } catch { } _saSession = null; }
            IsConnected = false;
        }

        public async Task<double> GetMeasurementValueAsync(int delayMs)
        {
            var trace = await GetTraceDataAsync(delayMs);
            return trace.Length > 0 ? trace.Max() : -120.0;
        }

        public async Task<double[]> GetTraceDataAsync(int delayMs)
        {
            if (!IsConnected || _saSession == null) throw new InvalidOperationException("未连接");

            return await Task.Run(() =>
            {
                try
                {
                    var formattedIO = _saSession.FormattedIO;

                    // =========================================================
                    // **核心修正：智能自适应超时**
                    // =========================================================
                    // 1. 询问仪器：当前设置下，扫一次要多久？
                    formattedIO.WriteLine(":SWE:TIME?");
                    string sweTimeStr = formattedIO.ReadLine();

                    if (double.TryParse(sweTimeStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double sweTimeSec))
                    {
                        // 计算所需时间 (秒 -> 毫秒) + 5秒缓冲
                        int neededTimeMs = (int)(sweTimeSec * 1000) + 5000;

                        // 如果需要的时间超过了当前的 Visa Timeout，就临时加长
                        if (neededTimeMs > _saSession.TimeoutMilliseconds)
                        {
                            _saSession.TimeoutMilliseconds = neededTimeMs;
                        }
                    }
                    // =========================================================

                    // 2. 刷新测量
                    formattedIO.WriteLine(":TRAC:TYPE WRIT");

                    // 3. 发起扫描并等待完成 (*WAI)
                    // 现在不用担心超时了，因为我们刚才已经延长时间了
                    formattedIO.WriteLine(":INIT:IMM; *WAI");

                    if (delayMs > 0) Thread.Sleep(delayMs);

                    // 4. 读取数据
                    formattedIO.WriteLine(":TRAC:DATA? TRACE1");
                    string dataStr = formattedIO.ReadLine();

                    if (string.IsNullOrWhiteSpace(dataStr)) return new double[0];

                    return dataStr.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                  .Select(s => double.Parse(s, CultureInfo.InvariantCulture))
                                  .ToArray();
                }
                catch (Exception ex)
                {
                    throw new Exception($"读取Trace失败: {ex.Message}");
                }
            });
        }
    }
}