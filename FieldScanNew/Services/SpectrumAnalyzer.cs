using FieldScanNew.Models;
using Ivi.Visa;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FieldScanNew.Services
{
    // 定义支持的厂商类型
    public enum AnalyzerBrand
    {
        Keysight, // Agilent / HP
        RohdeSchwarz,
        RohdeSchwarzFsh,
        Rigol,
        Unknown
    }

    public class SpectrumAnalyzer : IMeasurementDevice
    {
        public string DeviceName => IsRohdeSchwarzFsh ? "R&S FSH8 (VISA)" : "Spectrum Analyzer (VISA)";
        public bool IsConnected { get; private set; } = false;
        private IMessageBasedSession? _saSession;
        private readonly AnalyzerBrand _preferredBrand;

        // 新增：当前识别到的品牌
        private AnalyzerBrand _currentBrand = AnalyzerBrand.Unknown;

        public SpectrumAnalyzer(AnalyzerBrand preferredBrand = AnalyzerBrand.Unknown)
        {
            _preferredBrand = preferredBrand;
            _currentBrand = preferredBrand;
        }

        private bool IsRohdeSchwarz =>
            _currentBrand == AnalyzerBrand.RohdeSchwarz ||
            _currentBrand == AnalyzerBrand.RohdeSchwarzFsh;

        private bool IsRohdeSchwarzFsh => _currentBrand == AnalyzerBrand.RohdeSchwarzFsh;

        public async Task ConnectAsync(InstrumentSettings settings)
        {
            if (IsConnected) Disconnect();

            await Task.Run(() =>
            {
                try
                {
                    _saSession = OpenVisaSession(settings);

                    _saSession.TimeoutMilliseconds = 3000;
                    _saSession.TerminationCharacterEnabled = true;
                    _saSession.TerminationCharacter = (byte)'\n';
                    _saSession.SendEndEnabled = true;

                    // 1. 询问仪器身份
                    _saSession.FormattedIO.WriteLine("*IDN?");
                    string idn = _saSession.FormattedIO.ReadLine().ToUpper();

                    // 2. 识别品牌 (简单逻辑)
                    if (idn.Contains("FSH8") || idn.Contains("FSH 8") || idn.Contains("FSH-8") ||
                        (idn.Contains("ROHDE") && idn.Contains("SCHWARZ") && idn.Contains("FSH")))
                        _currentBrand = AnalyzerBrand.RohdeSchwarzFsh;
                    else if (idn.Contains("KEYSIGHT") || idn.Contains("AGILENT") || idn.Contains("HP"))
                        _currentBrand = AnalyzerBrand.Keysight;
                    else if (idn.Contains("ROHDE") && idn.Contains("SCHWARZ"))
                        _currentBrand = AnalyzerBrand.RohdeSchwarz;
                    else if (idn.Contains("RIGOL"))
                        _currentBrand = AnalyzerBrand.Rigol;
                    else if (_preferredBrand != AnalyzerBrand.Unknown)
                        _currentBrand = _preferredBrand;
                    else
                        _currentBrand = AnalyzerBrand.Keysight; // 默认尝试按 Keysight 处理

                    // 3. 通用参数下发 (大部分 SCPI 通用)
                    _saSession.FormattedIO.WriteLine(string.Format(CultureInfo.InvariantCulture, ":FREQ:CENT {0}", settings.CenterFrequencyHz));
                    _saSession.FormattedIO.WriteLine(string.Format(CultureInfo.InvariantCulture, ":FREQ:SPAN {0}", settings.SpanHz));

                    // 差异化指令：参考电平
                    if (IsRohdeSchwarz)
                        _saSession.FormattedIO.WriteLine(string.Format(CultureInfo.InvariantCulture, ":DISP:TRAC:Y:RLEV {0}", settings.ReferenceLevelDb));
                    else
                        _saSession.FormattedIO.WriteLine(string.Format(CultureInfo.InvariantCulture, ":DISP:WIND:TRAC:Y:RLEV {0}", settings.ReferenceLevelDb));

                    if (settings.Points > 0 && !IsRohdeSchwarzFsh)
                        _saSession.FormattedIO.WriteLine($":SWE:POIN {settings.Points}");

                    if (IsRohdeSchwarzFsh)
                        _saSession.FormattedIO.WriteLine(":UNIT:POW DBM");

                    // 带宽设置
                    if (settings.RbwHz > 0)
                        _saSession.FormattedIO.WriteLine(string.Format(CultureInfo.InvariantCulture, ":BAND {0}", settings.RbwHz));
                    else
                        _saSession.FormattedIO.WriteLine(":BAND:AUTO ON");

                    if (settings.VbwHz > 0)
                        _saSession.FormattedIO.WriteLine(string.Format(CultureInfo.InvariantCulture, ":BAND:VID {0}", settings.VbwHz));
                    else
                        _saSession.FormattedIO.WriteLine(":BAND:VID:AUTO ON");

                    // 差异化指令：设置数据格式
                    // 大多数现代仪器都支持 :FORM ASC，但 R&S 某些型号可能不同
                    _saSession.FormattedIO.WriteLine(":FORM ASC");

                    // 4. 初始化控制
                    // 注意：R&S 连续扫描开关通常也是 :INIT:CONT OFF
                    _saSession.FormattedIO.WriteLine(":INIT:CONT OFF");

                    _saSession.TimeoutMilliseconds = 30000;
                    IsConnected = true;
                }
                catch (Exception ex)
                {
                    Disconnect();
                    throw new Exception($"连接失败: {ex.Message}");
                }
            });
        }

        private IMessageBasedSession OpenVisaSession(InstrumentSettings settings)
        {
            Exception? lastException = null;

            foreach (var visaAddress in GetVisaAddressCandidates(settings))
            {
                object? visaSession = null;
                try
                {
                    visaSession = GlobalResourceManager.Open(visaAddress);
                    if (visaSession is IMessageBasedSession messageBasedSession)
                        return messageBasedSession;

                    if (visaSession is IDisposable disposableSession)
                        disposableSession.Dispose();

                    lastException = new Exception($"无法创建VISA消息会话: {visaAddress}");
                }
                catch (Exception ex)
                {
                    try
                    {
                        if (visaSession is IDisposable disposableSession)
                            disposableSession.Dispose();
                    }
                    catch { }
                    lastException = ex;
                }
            }

            throw new Exception(lastException?.Message ?? "无法创建VISA会话。");
        }

        private IEnumerable<string> GetVisaAddressCandidates(InstrumentSettings settings)
        {
            string ipAddress = settings.IpAddress;

            if (_preferredBrand == AnalyzerBrand.RohdeSchwarzFsh)
            {
                yield return $"TCPIP::{ipAddress}";
                yield return $"TCPIP0::{ipAddress}::INSTR";
                yield return $"TCPIP0::{ipAddress}::inst0::INSTR";
                yield break;
            }

            yield return $"TCPIP0::{ipAddress}::inst0::INSTR";
            yield return $"TCPIP0::{ipAddress}::INSTR";
            yield return $"TCPIP::{ipAddress}";
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

                    // 1. 自适应超时 (通用)
                    formattedIO.WriteLine(":SWE:TIME?");
                    string sweTimeStr = formattedIO.ReadLine();
                    if (double.TryParse(sweTimeStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double sweTimeSec))
                    {
                        int neededTimeMs = (int)(sweTimeSec * 1000) + 5000;
                        if (neededTimeMs > _saSession.TimeoutMilliseconds)
                            _saSession.TimeoutMilliseconds = neededTimeMs;
                    }

                    // 2. 发起扫描 (通用)
                    // R&S 和 Keysight 都支持 :INIT; *WAI
                    formattedIO.WriteLine(":INIT; *WAI");

                    if (delayMs > 0) Thread.Sleep(delayMs);

                    // 3. 读取 Trace 数据 (差异化最大点)
                    string dataStr = "";
                    if (_currentBrand == AnalyzerBrand.RohdeSchwarz)
                    {
                        // R&S 通常用 TRAC? TRACE1
                        formattedIO.WriteLine(":TRAC? TRACE1");
                    }
                    else if (IsRohdeSchwarzFsh)
                    {
                        formattedIO.WriteLine(":TRAC:DATA? TRACE1");
                    }
                    else
                    {
                        // Keysight / Rigol 通常用 TRAC:DATA? TRACE1
                        formattedIO.WriteLine(":TRAC:DATA? TRACE1");
                    }

                    dataStr = formattedIO.ReadLine();

                    if (string.IsNullOrWhiteSpace(dataStr)) return new double[0];

                    // 处理返回数据，有些仪器可能以 # 开头表示二进制块头，但在 ASCII 模式下通常直接是逗号分隔
                    // 简单的容错处理
                    if (dataStr.StartsWith("#"))
                    {
                        // 这里可能需要更复杂的二进制解析，或者确保前面发送了 :FORM ASC
                        // 如果确定是 ASCII 但带了头，需要截取，暂且按纯文本 CSV 处理
                    }

                    return dataStr.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                  .Select(s => {
                                      // 尝试解析，防止科学计数法或其他异常字符导致崩溃
                                      if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out double v))
                                          return v;
                                      return -150.0; // 默认底噪
                                  })
                                  .ToArray();
                }
                catch (Exception ex)
                {
                    throw new Exception($"读取Trace失败 [{_currentBrand}]: {ex.Message}");
                }
            });
        }
    }
}
