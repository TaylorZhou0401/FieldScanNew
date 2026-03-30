using FieldScanNew.Infrastructure;
using FieldScanNew.Models;
using FieldScanNew.Views;
using MathNet.Numerics.LinearAlgebra.Double;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using OxyPlot.Wpf;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using MessageBox = System.Windows.MessageBox;

namespace FieldScanNew.ViewModels
{
    public partial class MainViewModel
    {
        public class QbcInputData
        {
            public HyperParams HyperParams { get; set; } = new HyperParams();
            public List<SampledPoint> SampledPoints { get; set; } = new List<SampledPoint>();
        }

        public class HyperParams
        {
            public double X_min { get; set; }
            public double X_max { get; set; }
            public double Y_min { get; set; }
            public double Y_max { get; set; }
            public int Nx { get; set; }
            public int Ny { get; set; }
        }

        public class QbcOutputData
        {
            public string Status { get; set; } = "unknown";
            public string Message { get; set; } = "";
            public List<(float X, float Y)> NextPoints { get; set; } = new List<(float X, float Y)>();
            public int ClusterCount { get; set; }
        }

        public class SampledPoint
        {
            public float X { get; set; }
            public float Y { get; set; }
            public double Magnitude { get; set; }
            public int BatchId { get; set; }
        }

        public enum RbfKernel { Linear, Cubic, ThinPlateSpline, Quintic }

        private async Task QBCExecuteStartScan()
        {
            if (_hardwareService.ActiveRobot == null || !_hardwareService.ActiveRobot.IsConnected ||
            _hardwareService.ActiveDevice == null || !_hardwareService.ActiveDevice.IsConnected)
            { MessageBox.Show("请先连接机械臂和测量仪器！", "提示"); return; }

            var selectedProject = Projects.FirstOrDefault(p => p.IsSelected);
            if (selectedProject == null) { MessageBox.Show("请先选择一个项目！", "提示"); return; }

            var scanSettings = selectedProject.ProjectData.ScanConfig;
            if (scanSettings.NumX < 2 || scanSettings.NumY < 2) { MessageBox.Show("扫描点数必须大于等于2！", "错误"); return; }
            if (!scanSettings.ScanHx && !scanSettings.ScanHy) { MessageBox.Show("请至少勾选一个扫描分量！", "提示"); return; }

            // 新增: 弹出参数设置窗口获取用户输入
            double inputError = -15.0;
            int inputK = 2;
            double inputInitRatio = 0.1;
            var paramsDialog = new QbcParamsDialog(inputError, inputK, inputInitRatio);
            if (paramsDialog.ShowDialog() != true) return; // 用户取消
            inputError = paramsDialog.ErrorVal;
            inputK = paramsDialog.KVal;
            inputInitRatio = paramsDialog.InitRatioVal;

            UpdatePlotBackground();
            try { await _hardwareService.ActiveDevice.ConnectAsync(CurrentInstrumentSettings); }
            catch (Exception ex) { MessageBox.Show($"更新配置失败: {ex.Message}", "警告"); }

            IsScanning = true;
            _cancellationTokenSource = new CancellationTokenSource();

            // 新增: 统计计时和总采样点数
            var stopwatch = Stopwatch.StartNew();
            // 稳定延时（ms）: 机器人到位后等待该时间再开始测量，避免未稳态读数
            int settleDelayMs = 100;
            int totalSampledPoints = 0;
            int totalMaxPoints = 0;

            var tasks = new List<(string Name, float Angle)>();
            if (scanSettings.ScanHx) tasks.Add(("Hx", 0f));
            if (scanSettings.ScanHy) tasks.Add(("Hy", 90f));

            string projectName = SanitizeFileName(selectedProject.ProjectData.ProjectName);
            string measurementName = SanitizeFileName(GetCurrentMeasurementName(selectedProject));

            try
            {
                double centerFreq = CurrentInstrumentSettings.CenterFrequencyHz;
                double span = CurrentInstrumentSettings.SpanHz;
                double startFreq = centerFreq - (span / 2.0);
                double stopFreq = centerFreq + (span / 2.0);

                foreach (var task in tasks)
                {
                    if (_cancellationTokenSource.Token.IsCancellationRequested) break;

                    string componentName = task.Name;
                    float robotAngle = task.Angle;

                    var currentPos = await _hardwareService.ActiveRobot.GetPositionAsync();
                    await _hardwareService.ActiveRobot.MoveToAsync(currentPos.X, currentPos.Y, currentPos.Z, robotAngle);
                    if (_cancellationTokenSource.Token.IsCancellationRequested) break;
                    await Task.Delay(settleDelayMs);

                    double xMin = Math.Min(scanSettings.StartX, scanSettings.StopX);
                    double xMax = Math.Max(scanSettings.StartX, scanSettings.StopX);
                    double yMin = Math.Min(scanSettings.StartY, scanSettings.StopY);
                    double yMax = Math.Max(scanSettings.StartY, scanSettings.StopY);

                    var heatMapData = new double[scanSettings.NumX, scanSettings.NumY];
                    var heatMapSeries = new HeatMapSeries { X0 = xMin, X1 = xMax, Y0 = yMin, Y1 = yMax, Interpolate = true, RenderMethod = HeatMapRenderMethod.Bitmap, Data = heatMapData, CoordinateDefinition = HeatMapCoordinateDefinition.Edge };
                    HeatmapModel.Series.Clear(); HeatmapModel.Series.Add(heatMapSeries);
                    HeatmapModel.Title = $"QBC热力图 - {componentName}";
                    HeatmapModel.ResetAllAxes(); HeatmapModel.InvalidatePlot(true);

                    var spectrumSeries = new LineSeries { Title = "Live Trace", Color = OxyColors.Blue, StrokeThickness = 1 };
                    SpectrumModel.Series.Clear(); SpectrumModel.Series.Add(spectrumSeries); SpectrumModel.InvalidatePlot(true);

                    var sbPeak = new StringBuilder(); sbPeak.AppendLine("PhysicalX(mm),PhysicalY(mm),MaxAmplitude(dBuV/m)");
                    var sbFull = new StringBuilder(); bool isFullHeaderWritten = false;

                    int sumSampleCount = scanSettings.NumX * scanSettings.NumY;
                    int initPointCount = Math.Max(4, (int)Math.Round(sumSampleCount * inputInitRatio)); // 使用用户输入的初始采样比例
                    initPointCount = Math.Min(initPointCount, sumSampleCount);

                    Console.WriteLine($"[Initialization] Starting greedy scan (FPS) for {initPointCount} points...");

                    var allGridPoints = new List<(float X, float Y)>();
                    for (int j = 0; j < scanSettings.NumY; j++)
                    {
                        float targetY = scanSettings.StartY + j * (scanSettings.StopY - scanSettings.StartY) / Math.Max(1, scanSettings.NumY - 1);
                        for (int i = 0; i < scanSettings.NumX; i++)
                        {
                            float targetX = scanSettings.StartX + i * (scanSettings.StopX - scanSettings.StartX) / Math.Max(1, scanSettings.NumX - 1);
                            allGridPoints.Add((targetX, targetY));
                        }
                    }

                    var selectedIndices = new HashSet<int>();
                    var distancesSq = Enumerable.Repeat(double.MaxValue, allGridPoints.Count).ToArray();
                    int firstIndex = (scanSettings.NumY / 2) * scanSettings.NumX + (scanSettings.NumX / 2);

                    // 1. 离线使用贪婪算法（FPS）选出所有初始规划采样点
                    var plannedPoints = new List<(float X, float Y)>();

                    for (int step = 0; step < initPointCount; step++)
                    {
                        if (_cancellationTokenSource.Token.IsCancellationRequested) goto StopQBC;

                        int nextIndex = -1;
                        if (step == 0)
                        {
                            nextIndex = firstIndex;
                        }
                        else
                        {
                            double maxMinDistSq = -1;
                            for (int i = 0; i < allGridPoints.Count; i++)
                            {
                                if (!selectedIndices.Contains(i) && distancesSq[i] > maxMinDistSq)
                                {
                                    maxMinDistSq = distancesSq[i];
                                    nextIndex = i;
                                }
                            }
                        }

                        selectedIndices.Add(nextIndex);
                        var p = allGridPoints[nextIndex];
                        plannedPoints.Add(p);

                        for (int i = 0; i < allGridPoints.Count; i++)
                        {
                            if (!selectedIndices.Contains(i))
                            {
                                double dx = allGridPoints[i].X - p.X;
                                double dy = allGridPoints[i].Y - p.Y;
                                double distSq = dx * dx + dy * dy;
                                if (distSq < distancesSq[i])
                                {
                                    distancesSq[i] = distSq;
                                }
                            }
                        }
                    }

                    // 2. 对规划好的点按行排序：Y依次增大，同Y下X依次增大（加入Math.Round规避浮点计算微小误差）
                    var sortedPoints = plannedPoints
                        .OrderBy(p => Math.Round(p.Y, 3))
                        .ThenBy(p => Math.Round(p.X, 3))
                        .ToList();

                    List<SampledPoint> sampledPoints = new List<SampledPoint>();

                    // 3. 实际平滑移动并采样
                    foreach (var pt in sortedPoints)
                    {
                        if (_cancellationTokenSource.Token.IsCancellationRequested) goto StopQBC;

                        float targetX = pt.X;
                        float targetY = pt.Y;

                        await _hardwareService.ActiveRobot.MoveToAsync(targetX, targetY, scanSettings.ScanHeightZ, robotAngle);
                        if (_cancellationTokenSource.Token.IsCancellationRequested) goto StopQBC;
                        await Task.Delay(settleDelayMs);
                        double[] traceData = await _hardwareService.ActiveDevice.GetTraceDataAsync(0);
                        if (traceData.Length == 0) continue;

                        for (int k = 0; k < traceData.Length; k++)
                        {
                            double freq = GetFrequencyAtIndex(startFreq, stopFreq, k, traceData.Length);
                            double factor = GetInterpolatedFactor(freq);
                            traceData[k] = traceData[k] + 107.0 + factor;
                        }

                        double maxVal = traceData.Max();
                        sampledPoints.Add(new SampledPoint { X = targetX, Y = targetY, Magnitude = maxVal, BatchId = 0 });

                        RecordFullTraceData(ref isFullHeaderWritten, sbFull, targetX, targetY, traceData, startFreq, stopFreq);

                        // 更新热力图显示
                        double ratioX = (targetX - xMin) / (xMax - xMin);
                        double ratioY = (targetY - yMin) / (yMax - yMin);
                        int arrayX = Math.Max(0, Math.Min((int)Math.Round(ratioX * (scanSettings.NumX - 1)), scanSettings.NumX - 1));
                        int arrayY = Math.Max(0, Math.Min((int)Math.Round(ratioY * (scanSettings.NumY - 1)), scanSettings.NumY - 1));
                        heatMapData[arrayX, arrayY] = maxVal;
                        HeatmapModel.InvalidatePlot(true);
                    }

                    // --- [Step 1] 参数设定：自适应停止机制 ---
                    
                    // Error: 采样变化相对误差允许值（阈值），单位 dB
                    // 判定标准：当 Sn_dB <= Error 时，认为模型趋于稳定
                    double Error = inputError; // 使用用户输入的 Error

                    // K: 需要连续满足误差标准的次数
                    // 只有连续 K 次满足稳定标准，才停止扫描，防止偶然收敛
                    int K = inputK; // 使用用户输入的 K

                    // count: 当前连续满足误差标准的计数器
                    // 初始为 0，满足条件 +=1，不满足重置为 0
                    int count = 0;

                    // maxN: 全局最大点数限制
                    int maxN = scanSettings.NumX * scanSettings.NumY;
                    int configuredUpperLimit = Math.Max(1, (int)Math.Ceiling(maxN * 0.45));
                    bool reachedConfiguredUpperLimit = false;
                    int batchIndex = 0;

                    // 循环条件：
                    // 1. count < K: 尚未达到连续 K 次稳定
                    // 2. sampledPoints.Count < maxN: 未超过最大物理点数
                    while (count < K && sampledPoints.Count < maxN)
                    {
                        if (_cancellationTokenSource.Token.IsCancellationRequested) goto StopQBC;

                            // Early stop safeguard: stop when reaching configured upper limit (45%)
                            if (sampledPoints.Count >= configuredUpperLimit)
                        {
                                reachedConfiguredUpperLimit = true;
                                Console.WriteLine($"[Stop] Configured upper limit hit: {sampledPoints.Count}/{configuredUpperLimit}");
                             break;
                        }

                        batchIndex++;

                        var inputData = new QbcInputData { HyperParams = new HyperParams { X_min = xMin, X_max = xMax, Y_min = yMin, Y_max = yMax, Nx = scanSettings.NumX, Ny = scanSettings.NumY }, SampledPoints = sampledPoints.ToList() };

                        // Use Task.Run to avoid blocking UI during heavy QBC calculation
                        var nextPointData = await Task.Run(() => CalculateNextBatchSamplePoints(inputData));

                        if (nextPointData.Status != "success" || nextPointData.NextPoints.Count == 0) break;

                        var currentPointPos = sampledPoints.Count > 0 ? (sampledPoints.Last().X, sampledPoints.Last().Y) : ((float)xMin, (float)yMin);
                        var optimizedPath = OptimizeScanPath(nextPointData.NextPoints, currentPointPos);

                        double[]? latestTraceData = null;
                        
                        List<Task<double>> maxStdDevTasks = new List<Task<double>>();

                        foreach (var targetPt in optimizedPath)
                        {
                            if (_cancellationTokenSource.Token.IsCancellationRequested) goto StopQBC;
                            if (sampledPoints.Count >= maxN) break;

                            float nextX = targetPt.X;
                            float nextY = targetPt.Y;

                            await _hardwareService.ActiveRobot.MoveToAsync(nextX, nextY, scanSettings.ScanHeightZ, robotAngle);
                            if (_cancellationTokenSource.Token.IsCancellationRequested) goto StopQBC;
                            await Task.Delay(settleDelayMs);
                            double[] newTraceData = await _hardwareService.ActiveDevice.GetTraceDataAsync(0);
                            if (newTraceData.Length == 0) continue;

                            // 修正数据 (QBC迭代采样)
                            for (int k = 0; k < newTraceData.Length; k++)
                            {
                                double freq = GetFrequencyAtIndex(startFreq, stopFreq, k, newTraceData.Length);
                                double factor = GetInterpolatedFactor(freq);
                                newTraceData[k] = newTraceData[k] + 107.0 + factor;
                            }

                            latestTraceData = newTraceData;

                            double newMaxVal = newTraceData.Max();
                            sampledPoints.Add(new SampledPoint { X = nextX, Y = nextY, Magnitude = newMaxVal, BatchId = batchIndex });

                            RecordFullTraceData(ref isFullHeaderWritten, sbFull, nextX, nextY, newTraceData, startFreq, stopFreq);
                            
                            // 启动后台任务: 计算加入此点后的全场预测最大标准差
                            var ptsSnapshot = sampledPoints.ToList();
                            var stdDevTask = Task.Run(() => CalculateMaxStandardDeviation(ptsSnapshot, scanSettings, xMin, xMax, yMin, yMax));
                            maxStdDevTasks.Add(stdDevTask);
                        }

                        // --- [Step 2] 全场插值 (P_n) 计算 ---

                        // 使用当前所有采样点 (sampledPoints) 来预测全场分布
                        // filledData: 二维数组 [Nx, Ny]，即 P_n 的网格化表示
                        // 此过程使用 RBF 均值 (Multiple Kernels) 进行插值，较耗时，放在 Task.Run 中
                        var (filledData, _) = await Task.Run(() => FillUnsampledPointsWithRbfMean(sampledPoints, scanSettings));

                        // 将二维结果展平为一维数组 P_n，方便计算 RMSE
                        // grid[i, j] -> P_n[index]
                        int totalPoints = scanSettings.NumX * scanSettings.NumY;
                        double[] P_n = new double[totalPoints];
                        for (int j = 0; j < scanSettings.NumY; j++)
                        {
                            for (int i = 0; i < scanSettings.NumX; i++)
                            {
                                P_n[j * scanSettings.NumX + i] = filledData[i, j];
                            }
                        }

                // --- [Step 3] 误差估算与逻辑判定 (从线性域转回dB的严格相对误差) ---
                        // 等待本批次所有后台标准差计算完成 (后台计算已基于线性场强，得到的为 sigma_lin)
                        double[] batchMaxStds = await Task.WhenAll(maxStdDevTasks);
                        double minMaxSigma_lin = batchMaxStds.Length > 0 ? batchMaxStds.Min() : 1e-12;
                        
                        // 1. 估算线性误差极差: Er_lin = 9.6 * sigma_lin
                        double expectedError_lin = minMaxSigma_lin * 9.6;
                        
                        // 2. 将绝对误差由线性域换算回绝对 dB 域: Er_AdB = 20*lg(Er_lin)
                        double Er_AdB = 20 * Math.Log10(Math.Max(expectedError_lin, 1e-12));

                        // P_n 存的是 dB 值，获取全场预测的最大场强 x_max (dB)
                        double maxField_dB = Math.Max(P_n.Max(), -100.0);

                        // 3. 计算相对于全场最大场的归一化误差 Er_dB = Er_AdB - x_max
                        double Sn_dB = Er_AdB - maxField_dB;

                        // 如果 Sn_dB <= Error: 说明此轮选出的点代表的不确定度已经很低 -> 可能趋于稳定
                        // 如果 Sn_dB > Error: 说明当前模型仍存在较大不确定区域 -> 尚不稳定
                        if (Sn_dB <= Error)
                        {
                            count++;
                            // 可以在此增加 Debug 输出或 UI 状态更新: "稳定计数: {count}/{K}, 估算误差: {Sn_dB:F2} dB"
                        }
                        else
                        {
                            count = 0; // 误差较大，前面积累的稳定次数作废，重置计数
                        }

                        // 更新热力图 (使用插值后的完整数据，视觉效果更好，实时看到预测结果)
                        heatMapSeries.Data = filledData;
                        HeatmapModel.InvalidatePlot(true);

                        if (latestTraceData != null)
                        {
                            spectrumSeries.Points.Clear();
                            for (int k = 0; k < latestTraceData.Length; k++)
                            {
                                double freq = GetFrequencyAtIndex(startFreq, stopFreq, k, latestTraceData.Length);
                                spectrumSeries.Points.Add(new DataPoint(freq, latestTraceData[k]));
                            }
                            SpectrumModel.InvalidatePlot(true);
                        }
                    }

                    // --- [Step 4] 循环结束后的收敛状态报告 ---
                    bool reachedUpperLimit = reachedConfiguredUpperLimit || sampledPoints.Count >= maxN;
                    string stopReason = (count >= K)
                        ? $"模型已收敛 (连续 {K} 次误差 < {Error} dB)"
                        : reachedUpperLimit
                            ? "扫描已达到设定上限"
                            : "未找到可继续采样点，扫描结束";
                    
                    Console.WriteLine($"[{componentName}] AI 扫描结束: {stopReason}");
                    Console.WriteLine($"最终采样点数: {sampledPoints.Count} / {maxN}");

                    // 最终再次调用插值函数，确保用于保存的 CSV 数据是基于最终采样点的最佳推测
                    var (_, fullPointMap) = FillUnsampledPointsWithRbfMean(sampledPoints, scanSettings);
                    
                    sbPeak.Clear(); sbPeak.AppendLine("PhysicalX(mm),PhysicalY(mm),MaxAmplitude(dBuV/m)");
                    var sbPeakSampled = new StringBuilder();
                    sbPeakSampled.AppendLine("PhysicalX(mm),PhysicalY(mm),MaxAmplitude(dBuV/m)");
                    foreach (var sp in sampledPoints)
                    {
                        sbPeakSampled.AppendLine($"{sp.X:F3},{sp.Y:F3},{sp.Magnitude:F3}");
                    }

                    var xCoor = GenerateLinspace(xMin, xMax, scanSettings.NumX);
                    var yCoor = GenerateLinspace(yMin, yMax, scanSettings.NumY);

                    // Re-calculate fullPointMap explicitly because FillUnsampledPointsWithRbfMean is called inside loop but result not saved for final output
                    // We need to ensure fullPointMap is populated for CSV saving
                    for (int j = 0; j < scanSettings.NumY; j++)
                    {
                        // 保持与 FillUnsampledPointsWithRbfMean 完全一致的坐标精度处理，确保 Key 能匹配
                        float targetY = (float)Math.Round((float)yCoor[j], 3);
                        for (int i = 0; i < scanSettings.NumX; i++)
                        {
                            float targetX = (float)Math.Round((float)xCoor[i], 3);
                            var key = (targetX, targetY);
                            double val = fullPointMap.ContainsKey(key) ? fullPointMap[key] : 0;
                            sbPeak.AppendLine($"{targetX:F3},{targetY:F3},{val:F3}");
                        }
                    }

                    string baseName = $"{projectName}_{measurementName}_{componentName}";
                    string subFolder = $"{measurementName}_{componentName}";
                    SaveScanDataToCsv(selectedProject, sbPeakSampled.ToString(), $"{baseName}_AI_PeakSampled.csv", subFolder);
                    SaveScanDataToCsv(selectedProject, sbPeak.ToString(), $"{baseName}_AI_Peak.csv", subFolder);
                    SaveScanDataToCsv(selectedProject, sbFull.ToString(), $"{baseName}_AI_FullTrace.csv", subFolder);
                    // if (DutImageSource != null) SaveImage(selectedProject, DutImageSource, $"{baseName}_Capture.jpg", subFolder);
                    SaveHeatmapImage(selectedProject, HeatmapModel, $"{baseName}_AI_HeatmapOverlay.png", subFolder);

                    // 计算全局范围，确保散点图与热力图颜色一致
                    double globalMin = fullPointMap.Values.Count > 0 ? fullPointMap.Values.Min() : 0;
                    double globalMax = fullPointMap.Values.Count > 0 ? fullPointMap.Values.Max() : 100;
                    if (Math.Abs(globalMax - globalMin) < 0.001) { globalMin -= 1; globalMax += 1; }

                    // 新增: 保存AI采样点分布图并累计统计数据
                    SaveSamplingDistributionImage(selectedProject, sampledPoints, xMin, xMax, yMin, yMax, $"{baseName}_SamplingPoints.png", subFolder, globalMin, globalMax);
                    totalSampledPoints += sampledPoints.Count;
                    totalMaxPoints += maxN;
                }

            StopQBC:;
                stopwatch.Stop();
                if (!_cancellationTokenSource.Token.IsCancellationRequested) 
                {
                    string msg = $"AI 扫描完成！\n耗时: {stopwatch.Elapsed:hh\\:mm\\:ss}\n采样点数: {totalSampledPoints} / {totalMaxPoints}";
                    MessageBox.Show(msg, "成功");
                }
                else
                {
                    string msg = $"扫描已停止。\n耗时: {stopwatch.Elapsed:hh\\:mm\\:ss}\n采样点数: {totalSampledPoints} / {totalMaxPoints}";
                    MessageBox.Show(msg, "提示");
                }
            }
            catch (Exception ex) { MessageBox.Show("扫描错误: " + ex.Message, "错误"); }
            finally
            {
                if (_hardwareService.ActiveRobot != null && _hardwareService.ActiveRobot.IsConnected)
                { try { var pos = await _hardwareService.ActiveRobot.GetPositionAsync(); await _hardwareService.ActiveRobot.MoveToAsync(pos.X, pos.Y, pos.Z, 90f); } catch { } }
                IsScanning = false;
            }
        }

        private async Task QBCExecuteSinglePointScan()
        {
            if (_hardwareService.ActiveRobot == null || !_hardwareService.ActiveRobot.IsConnected ||
            _hardwareService.ActiveDevice == null || !_hardwareService.ActiveDevice.IsConnected)
            { MessageBox.Show("请先连接机械臂和测量仪器！", "提示"); return; }

            var selectedProject = Projects.FirstOrDefault(p => p.IsSelected);
            if (selectedProject == null) { MessageBox.Show("请先选择一个项目！", "提示"); return; }

            var scanSettings = selectedProject.ProjectData.ScanConfig;
            if (scanSettings.NumX < 2 || scanSettings.NumY < 2) { MessageBox.Show("扫描点数必须大于等于2！", "错误"); return; }
            if (!scanSettings.ScanHx && !scanSettings.ScanHy) { MessageBox.Show("请至少勾选一个扫描分量！", "提示"); return; }

            int maxN = scanSettings.NumX * scanSettings.NumY;
            int defaultLimit = Math.Max(6, (int)Math.Ceiling(maxN * 0.30));
            defaultLimit = Math.Min(defaultLimit, maxN);
            double inputInitRatio = 0.1;
            var paramsDialog = new SparseQbcParamsDialog("单点QBC", maxN, defaultLimit, inputInitRatio);
            if (paramsDialog.ShowDialog() != true) return;
            int sampleLimit = paramsDialog.SampleLimitVal;
            inputInitRatio = paramsDialog.InitRatioVal;

            UpdatePlotBackground();
            try { await _hardwareService.ActiveDevice.ConnectAsync(CurrentInstrumentSettings); }
            catch (Exception ex) { MessageBox.Show($"更新配置失败: {ex.Message}", "警告"); }

            IsScanning = true;
            _cancellationTokenSource = new CancellationTokenSource();
            var stopwatch = Stopwatch.StartNew();
            int settleDelayMs = 100;

            var tasks = new List<(string Name, float Angle)>();
            if (scanSettings.ScanHx) tasks.Add(("Hx", 0f));
            if (scanSettings.ScanHy) tasks.Add(("Hy", 90f));

            string projectName = SanitizeFileName(selectedProject.ProjectData.ProjectName);
            string measurementName = SanitizeFileName(GetCurrentMeasurementName(selectedProject));

            try
            {
                double centerFreq = CurrentInstrumentSettings.CenterFrequencyHz;
                double span = CurrentInstrumentSettings.SpanHz;
                double startFreq = centerFreq - (span / 2.0);
                double stopFreq = centerFreq + (span / 2.0);

                foreach (var task in tasks)
                {
                    if (_cancellationTokenSource.Token.IsCancellationRequested) break;

                    string componentName = task.Name;
                    float robotAngle = task.Angle;

                    var currentPos = await _hardwareService.ActiveRobot.GetPositionAsync();
                    await _hardwareService.ActiveRobot.MoveToAsync(currentPos.X, currentPos.Y, currentPos.Z, robotAngle);
                    if (_cancellationTokenSource.Token.IsCancellationRequested) break;
                    await Task.Delay(settleDelayMs);

                    double xMin = Math.Min(scanSettings.StartX, scanSettings.StopX);
                    double xMax = Math.Max(scanSettings.StartX, scanSettings.StopX);
                    double yMin = Math.Min(scanSettings.StartY, scanSettings.StopY);
                    double yMax = Math.Max(scanSettings.StartY, scanSettings.StopY);

                    var heatMapData = new double[scanSettings.NumX, scanSettings.NumY];
                    var heatMapSeries = new HeatMapSeries { X0 = xMin, X1 = xMax, Y0 = yMin, Y1 = yMax, Interpolate = true, RenderMethod = HeatMapRenderMethod.Bitmap, Data = heatMapData, CoordinateDefinition = HeatMapCoordinateDefinition.Edge };
                    HeatmapModel.Series.Clear(); HeatmapModel.Series.Add(heatMapSeries);
                    HeatmapModel.Title = $"单点QBC热力图 - {componentName}";
                    HeatmapModel.ResetAllAxes(); HeatmapModel.InvalidatePlot(true);

                    var spectrumSeries = new LineSeries { Title = "Live Trace", Color = OxyColors.Blue, StrokeThickness = 1 };
                    SpectrumModel.Series.Clear(); SpectrumModel.Series.Add(spectrumSeries); SpectrumModel.InvalidatePlot(true);

                    var sbFull = new StringBuilder();
                    bool isFullHeaderWritten = false;
                    var sampledPoints = new List<SampledPoint>();

                    int initCount = Math.Max(4, (int)Math.Round(maxN * inputInitRatio));
                    initCount = Math.Min(initCount, sampleLimit);
                    var allGridPoints = BuildGridPoints(scanSettings);
                    var initPoints = SelectGreedySamplePoints(allGridPoints, scanSettings.NumX, scanSettings.NumY, initCount)
                        .OrderBy(p => Math.Round(p.Y, 3))
                        .ThenBy(p => Math.Round(p.X, 3))
                        .ToList();

                    foreach (var pt in initPoints)
                    {
                        if (_cancellationTokenSource.Token.IsCancellationRequested) goto StopSingleQBC;

                        await _hardwareService.ActiveRobot.MoveToAsync(pt.X, pt.Y, scanSettings.ScanHeightZ, robotAngle);
                        if (_cancellationTokenSource.Token.IsCancellationRequested) goto StopSingleQBC;
                        await Task.Delay(settleDelayMs);
                        double[] traceData = await _hardwareService.ActiveDevice.GetTraceDataAsync(0);
                        if (traceData.Length == 0) continue;

                        for (int k = 0; k < traceData.Length; k++)
                        {
                            double freq = GetFrequencyAtIndex(startFreq, stopFreq, k, traceData.Length);
                            double factor = GetInterpolatedFactor(freq);
                            traceData[k] = traceData[k] + 107.0 + factor;
                        }

                        double maxVal = traceData.Max();
                        sampledPoints.Add(new SampledPoint { X = pt.X, Y = pt.Y, Magnitude = maxVal, BatchId = 0 });
                        RecordFullTraceData(ref isFullHeaderWritten, sbFull, pt.X, pt.Y, traceData, startFreq, stopFreq);

                        spectrumSeries.Points.Clear();
                        for (int k = 0; k < traceData.Length; k++)
                        {
                            double freq = GetFrequencyAtIndex(startFreq, stopFreq, k, traceData.Length);
                            spectrumSeries.Points.Add(new DataPoint(freq, traceData[k]));
                        }
                        SpectrumModel.InvalidatePlot(true);
                    }

                    while (sampledPoints.Count < sampleLimit)
                    {
                        if (_cancellationTokenSource.Token.IsCancellationRequested) goto StopSingleQBC;

                        var inputData = new QbcInputData
                        {
                            HyperParams = new HyperParams
                            {
                                X_min = xMin,
                                X_max = xMax,
                                Y_min = yMin,
                                Y_max = yMax,
                                Nx = scanSettings.NumX,
                                Ny = scanSettings.NumY
                            },
                            SampledPoints = sampledPoints.ToList()
                        };

                        var next = await Task.Run(() => CalculateNextSingleSamplePoint(inputData));
                        if (next.Status != "success" || next.NextPoints.Count == 0) break;

                        var target = next.NextPoints[0];
                        await _hardwareService.ActiveRobot.MoveToAsync(target.X, target.Y, scanSettings.ScanHeightZ, robotAngle);
                        if (_cancellationTokenSource.Token.IsCancellationRequested) goto StopSingleQBC;
                        await Task.Delay(settleDelayMs);
                        double[] newTraceData = await _hardwareService.ActiveDevice.GetTraceDataAsync(0);
                        if (newTraceData.Length == 0) continue;

                        for (int k = 0; k < newTraceData.Length; k++)
                        {
                            double freq = GetFrequencyAtIndex(startFreq, stopFreq, k, newTraceData.Length);
                            double factor = GetInterpolatedFactor(freq);
                            newTraceData[k] = newTraceData[k] + 107.0 + factor;
                        }

                        double newMaxVal = newTraceData.Max();
                        sampledPoints.Add(new SampledPoint { X = target.X, Y = target.Y, Magnitude = newMaxVal, BatchId = sampledPoints.Count });
                        RecordFullTraceData(ref isFullHeaderWritten, sbFull, target.X, target.Y, newTraceData, startFreq, stopFreq);

                        var (filledDataLoop, _) = await Task.Run(() => FillUnsampledPointsWithRbfMean(sampledPoints, scanSettings));
                        heatMapSeries.Data = filledDataLoop;
                        HeatmapModel.InvalidatePlot(true);

                        spectrumSeries.Points.Clear();
                        for (int k = 0; k < newTraceData.Length; k++)
                        {
                            double freq = GetFrequencyAtIndex(startFreq, stopFreq, k, newTraceData.Length);
                            spectrumSeries.Points.Add(new DataPoint(freq, newTraceData[k]));
                        }
                        SpectrumModel.InvalidatePlot(true);
                    }

                    if (sampledPoints.Count == 0) continue;

                    var (_, fullPointMap) = FillUnsampledPointsWithRbfMean(sampledPoints, scanSettings);
                    var sbPeakPred = new StringBuilder();
                    sbPeakPred.AppendLine("PhysicalX(mm),PhysicalY(mm),MaxAmplitude(dBuV/m)");
                    var sbPeakSampled = new StringBuilder();
                    sbPeakSampled.AppendLine("PhysicalX(mm),PhysicalY(mm),MaxAmplitude(dBuV/m)");
                    foreach (var sp in sampledPoints)
                    {
                        sbPeakSampled.AppendLine($"{sp.X:F3},{sp.Y:F3},{sp.Magnitude:F3}");
                    }

                    var xCoor = GenerateLinspace(xMin, xMax, scanSettings.NumX);
                    var yCoor = GenerateLinspace(yMin, yMax, scanSettings.NumY);
                    for (int j = 0; j < scanSettings.NumY; j++)
                    {
                        float targetY = (float)Math.Round((float)yCoor[j], 3);
                        for (int i = 0; i < scanSettings.NumX; i++)
                        {
                            float targetX = (float)Math.Round((float)xCoor[i], 3);
                            var key = (targetX, targetY);
                            double val = fullPointMap.ContainsKey(key) ? fullPointMap[key] : 0;
                            sbPeakPred.AppendLine($"{targetX:F3},{targetY:F3},{val:F3}");
                        }
                    }

                    string baseName = $"{projectName}_{measurementName}_{componentName}";
                    string subFolder = $"{measurementName}_{componentName}";
                    SaveScanDataToCsv(selectedProject, sbPeakSampled.ToString(), $"{baseName}_AI_Single_PeakSampled.csv", subFolder);
                    SaveScanDataToCsv(selectedProject, sbPeakPred.ToString(), $"{baseName}_AI_Single_Peak.csv", subFolder);
                    SaveScanDataToCsv(selectedProject, sbFull.ToString(), $"{baseName}_AI_Single_FullTrace.csv", subFolder);
                    SaveHeatmapImage(selectedProject, HeatmapModel, $"{baseName}_AI_Single_HeatmapOverlay.png", subFolder);

                    double globalMin = fullPointMap.Values.Count > 0 ? fullPointMap.Values.Min() : 0;
                    double globalMax = fullPointMap.Values.Count > 0 ? fullPointMap.Values.Max() : 100;
                    if (Math.Abs(globalMax - globalMin) < 0.001) { globalMin -= 1; globalMax += 1; }
                    SaveSamplingDistributionImage(selectedProject, sampledPoints, xMin, xMax, yMin, yMax, $"{baseName}_AI_Single_SamplingPoints.png", subFolder, globalMin, globalMax);
                }

            StopSingleQBC:;
                stopwatch.Stop();
                if (!_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    MessageBox.Show($"单点QBC扫描完成！\n耗时: {stopwatch.Elapsed:hh\\:mm\\:ss}", "成功");
                }
                else
                {
                    MessageBox.Show($"扫描已停止。\n耗时: {stopwatch.Elapsed:hh\\:mm\\:ss}", "提示");
                }
            }
            catch (Exception ex) { MessageBox.Show("扫描错误: " + ex.Message, "错误"); }
            finally
            {
                if (_hardwareService.ActiveRobot != null && _hardwareService.ActiveRobot.IsConnected)
                { try { var pos = await _hardwareService.ActiveRobot.GetPositionAsync(); await _hardwareService.ActiveRobot.MoveToAsync(pos.X, pos.Y, pos.Z, 90f); } catch { } }
                IsScanning = false;
            }
        }

        private async Task QBCExecuteBatchFixedScan()
        {
            if (_hardwareService.ActiveRobot == null || !_hardwareService.ActiveRobot.IsConnected ||
            _hardwareService.ActiveDevice == null || !_hardwareService.ActiveDevice.IsConnected)
            { MessageBox.Show("请先连接机械臂和测量仪器！", "提示"); return; }

            var selectedProject = Projects.FirstOrDefault(p => p.IsSelected);
            if (selectedProject == null) { MessageBox.Show("请先选择一个项目！", "提示"); return; }

            var scanSettings = selectedProject.ProjectData.ScanConfig;
            if (scanSettings.NumX < 2 || scanSettings.NumY < 2) { MessageBox.Show("扫描点数必须大于等于2！", "错误"); return; }
            if (!scanSettings.ScanHx && !scanSettings.ScanHy) { MessageBox.Show("请至少勾选一个扫描分量！", "提示"); return; }

            int maxN = scanSettings.NumX * scanSettings.NumY;
            int defaultLimit = Math.Max(8, (int)Math.Ceiling(maxN * 0.40));
            defaultLimit = Math.Min(defaultLimit, maxN);
            double inputInitRatio = 0.1;
            var paramsDialog = new SparseQbcParamsDialog("Batch-QBC", maxN, defaultLimit, inputInitRatio);
            if (paramsDialog.ShowDialog() != true) return;
            int sampleLimit = paramsDialog.SampleLimitVal;
            inputInitRatio = paramsDialog.InitRatioVal;

            UpdatePlotBackground();
            try { await _hardwareService.ActiveDevice.ConnectAsync(CurrentInstrumentSettings); }
            catch (Exception ex) { MessageBox.Show($"更新配置失败: {ex.Message}", "警告"); }

            IsScanning = true;
            _cancellationTokenSource = new CancellationTokenSource();
            var stopwatch = Stopwatch.StartNew();
            int settleDelayMs = 100;

            var tasks = new List<(string Name, float Angle)>();
            if (scanSettings.ScanHx) tasks.Add(("Hx", 0f));
            if (scanSettings.ScanHy) tasks.Add(("Hy", 90f));

            string projectName = SanitizeFileName(selectedProject.ProjectData.ProjectName);
            string measurementName = SanitizeFileName(GetCurrentMeasurementName(selectedProject));

            try
            {
                double centerFreq = CurrentInstrumentSettings.CenterFrequencyHz;
                double span = CurrentInstrumentSettings.SpanHz;
                double startFreq = centerFreq - (span / 2.0);
                double stopFreq = centerFreq + (span / 2.0);

                foreach (var task in tasks)
                {
                    if (_cancellationTokenSource.Token.IsCancellationRequested) break;

                    string componentName = task.Name;
                    float robotAngle = task.Angle;

                    var currentPos = await _hardwareService.ActiveRobot.GetPositionAsync();
                    await _hardwareService.ActiveRobot.MoveToAsync(currentPos.X, currentPos.Y, currentPos.Z, robotAngle);
                    if (_cancellationTokenSource.Token.IsCancellationRequested) break;
                    await Task.Delay(settleDelayMs);

                    double xMin = Math.Min(scanSettings.StartX, scanSettings.StopX);
                    double xMax = Math.Max(scanSettings.StartX, scanSettings.StopX);
                    double yMin = Math.Min(scanSettings.StartY, scanSettings.StopY);
                    double yMax = Math.Max(scanSettings.StartY, scanSettings.StopY);

                    var heatMapData = new double[scanSettings.NumX, scanSettings.NumY];
                    var heatMapSeries = new HeatMapSeries { X0 = xMin, X1 = xMax, Y0 = yMin, Y1 = yMax, Interpolate = true, RenderMethod = HeatMapRenderMethod.Bitmap, Data = heatMapData, CoordinateDefinition = HeatMapCoordinateDefinition.Edge };
                    HeatmapModel.Series.Clear(); HeatmapModel.Series.Add(heatMapSeries);
                    HeatmapModel.Title = $"Batch-QBC热力图 - {componentName}";
                    HeatmapModel.ResetAllAxes(); HeatmapModel.InvalidatePlot(true);

                    var spectrumSeries = new LineSeries { Title = "Live Trace", Color = OxyColors.Blue, StrokeThickness = 1 };
                    SpectrumModel.Series.Clear(); SpectrumModel.Series.Add(spectrumSeries); SpectrumModel.InvalidatePlot(true);

                    var sbFull = new StringBuilder();
                    bool isFullHeaderWritten = false;
                    var sampledPoints = new List<SampledPoint>();

                    int initCount = Math.Max(4, (int)Math.Round(maxN * inputInitRatio));
                    initCount = Math.Min(initCount, sampleLimit);
                    var allGridPoints = BuildGridPoints(scanSettings);
                    var initPoints = SelectGreedySamplePoints(allGridPoints, scanSettings.NumX, scanSettings.NumY, initCount)
                        .OrderBy(p => Math.Round(p.Y, 3))
                        .ThenBy(p => Math.Round(p.X, 3))
                        .ToList();

                    foreach (var pt in initPoints)
                    {
                        if (_cancellationTokenSource.Token.IsCancellationRequested) goto StopBatchQBC;

                        await _hardwareService.ActiveRobot.MoveToAsync(pt.X, pt.Y, scanSettings.ScanHeightZ, robotAngle);
                        if (_cancellationTokenSource.Token.IsCancellationRequested) goto StopBatchQBC;
                        await Task.Delay(settleDelayMs);
                        double[] traceData = await _hardwareService.ActiveDevice.GetTraceDataAsync(0);
                        if (traceData.Length == 0) continue;

                        for (int k = 0; k < traceData.Length; k++)
                        {
                            double freq = GetFrequencyAtIndex(startFreq, stopFreq, k, traceData.Length);
                            double factor = GetInterpolatedFactor(freq);
                            traceData[k] = traceData[k] + 107.0 + factor;
                        }

                        double maxVal = traceData.Max();
                        sampledPoints.Add(new SampledPoint { X = pt.X, Y = pt.Y, Magnitude = maxVal, BatchId = 0 });
                        RecordFullTraceData(ref isFullHeaderWritten, sbFull, pt.X, pt.Y, traceData, startFreq, stopFreq);
                    }

                    int batchIndex = 0;
                    while (sampledPoints.Count < sampleLimit)
                    {
                        if (_cancellationTokenSource.Token.IsCancellationRequested) goto StopBatchQBC;

                        batchIndex++;
                        var inputData = new QbcInputData
                        {
                            HyperParams = new HyperParams
                            {
                                X_min = xMin,
                                X_max = xMax,
                                Y_min = yMin,
                                Y_max = yMax,
                                Nx = scanSettings.NumX,
                                Ny = scanSettings.NumY
                            },
                            SampledPoints = sampledPoints.ToList()
                        };

                        var nextPointData = await Task.Run(() => CalculateNextBatchSamplePointsOriginal(inputData));
                        if (nextPointData.Status != "success" || nextPointData.NextPoints.Count == 0) break;

                        var currentPointPos = sampledPoints.Count > 0 ? (sampledPoints.Last().X, sampledPoints.Last().Y) : ((float)xMin, (float)yMin);
                        var optimizedPath = OptimizeScanPath(nextPointData.NextPoints, currentPointPos);

                        foreach (var targetPt in optimizedPath)
                        {
                            if (_cancellationTokenSource.Token.IsCancellationRequested) goto StopBatchQBC;
                            if (sampledPoints.Count >= sampleLimit) break;

                            await _hardwareService.ActiveRobot.MoveToAsync(targetPt.X, targetPt.Y, scanSettings.ScanHeightZ, robotAngle);
                            if (_cancellationTokenSource.Token.IsCancellationRequested) goto StopBatchQBC;
                            await Task.Delay(settleDelayMs);
                            double[] newTraceData = await _hardwareService.ActiveDevice.GetTraceDataAsync(0);
                            if (newTraceData.Length == 0) continue;

                            for (int k = 0; k < newTraceData.Length; k++)
                            {
                                double freq = GetFrequencyAtIndex(startFreq, stopFreq, k, newTraceData.Length);
                                double factor = GetInterpolatedFactor(freq);
                                newTraceData[k] = newTraceData[k] + 107.0 + factor;
                            }

                            double newMaxVal = newTraceData.Max();
                            sampledPoints.Add(new SampledPoint { X = targetPt.X, Y = targetPt.Y, Magnitude = newMaxVal, BatchId = batchIndex });
                            RecordFullTraceData(ref isFullHeaderWritten, sbFull, targetPt.X, targetPt.Y, newTraceData, startFreq, stopFreq);

                            var (filledDataLoop, _) = await Task.Run(() => FillUnsampledPointsWithRbfMean(sampledPoints, scanSettings));
                            heatMapSeries.Data = filledDataLoop;
                            HeatmapModel.InvalidatePlot(true);

                            spectrumSeries.Points.Clear();
                            for (int k = 0; k < newTraceData.Length; k++)
                            {
                                double freq = GetFrequencyAtIndex(startFreq, stopFreq, k, newTraceData.Length);
                                spectrumSeries.Points.Add(new DataPoint(freq, newTraceData[k]));
                            }
                            SpectrumModel.InvalidatePlot(true);
                        }
                    }

                    if (sampledPoints.Count == 0) continue;

                    var (_, fullPointMap) = FillUnsampledPointsWithRbfMean(sampledPoints, scanSettings);
                    var sbPeakPred = new StringBuilder();
                    sbPeakPred.AppendLine("PhysicalX(mm),PhysicalY(mm),MaxAmplitude(dBuV/m)");
                    var sbPeakSampled = new StringBuilder();
                    sbPeakSampled.AppendLine("PhysicalX(mm),PhysicalY(mm),MaxAmplitude(dBuV/m)");
                    foreach (var sp in sampledPoints)
                    {
                        sbPeakSampled.AppendLine($"{sp.X:F3},{sp.Y:F3},{sp.Magnitude:F3}");
                    }

                    var xCoor = GenerateLinspace(xMin, xMax, scanSettings.NumX);
                    var yCoor = GenerateLinspace(yMin, yMax, scanSettings.NumY);
                    for (int j = 0; j < scanSettings.NumY; j++)
                    {
                        float targetY = (float)Math.Round((float)yCoor[j], 3);
                        for (int i = 0; i < scanSettings.NumX; i++)
                        {
                            float targetX = (float)Math.Round((float)xCoor[i], 3);
                            var key = (targetX, targetY);
                            double val = fullPointMap.ContainsKey(key) ? fullPointMap[key] : 0;
                            sbPeakPred.AppendLine($"{targetX:F3},{targetY:F3},{val:F3}");
                        }
                    }

                    string baseName = $"{projectName}_{measurementName}_{componentName}";
                    string subFolder = $"{measurementName}_{componentName}";
                    SaveScanDataToCsv(selectedProject, sbPeakSampled.ToString(), $"{baseName}_AI_Batch_PeakSampled.csv", subFolder);
                    SaveScanDataToCsv(selectedProject, sbPeakPred.ToString(), $"{baseName}_AI_Batch_Peak.csv", subFolder);
                    SaveScanDataToCsv(selectedProject, sbFull.ToString(), $"{baseName}_AI_Batch_FullTrace.csv", subFolder);
                    SaveHeatmapImage(selectedProject, HeatmapModel, $"{baseName}_AI_Batch_HeatmapOverlay.png", subFolder);

                    double globalMin = fullPointMap.Values.Count > 0 ? fullPointMap.Values.Min() : 0;
                    double globalMax = fullPointMap.Values.Count > 0 ? fullPointMap.Values.Max() : 100;
                    if (Math.Abs(globalMax - globalMin) < 0.001) { globalMin -= 1; globalMax += 1; }
                    SaveSamplingDistributionImage(selectedProject, sampledPoints, xMin, xMax, yMin, yMax, $"{baseName}_AI_Batch_SamplingPoints.png", subFolder, globalMin, globalMax);
                }

            StopBatchQBC:;
                stopwatch.Stop();
                if (!_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    MessageBox.Show($"Batch-QBC扫描完成！\n耗时: {stopwatch.Elapsed:hh\\:mm\\:ss}", "成功");
                }
                else
                {
                    MessageBox.Show($"扫描已停止。\n耗时: {stopwatch.Elapsed:hh\\:mm\\:ss}", "提示");
                }
            }
            catch (Exception ex) { MessageBox.Show("扫描错误: " + ex.Message, "错误"); }
            finally
            {
                if (_hardwareService.ActiveRobot != null && _hardwareService.ActiveRobot.IsConnected)
                { try { var pos = await _hardwareService.ActiveRobot.GetPositionAsync(); await _hardwareService.ActiveRobot.MoveToAsync(pos.X, pos.Y, pos.Z, 90f); } catch { } }
                IsScanning = false;
            }
        }

        /// <summary>
        /// 记录全频谱轨迹数据到 StringBuilder
        /// </summary>
        private void RecordFullTraceData(ref bool isFullHeaderWritten, StringBuilder sbFull, float x, float y, double[] traceData, double startFreq, double stopFreq)
        {
            // 如果是第一次记录，先写 CSV 表头（列名：X, Y, 频率1, 频率2...）
            if (!isFullHeaderWritten)
            {
                sbFull.Append("PhysicalX(mm),PhysicalY(mm)");
                for (int k = 0; k < traceData.Length; k++)
                {
                    double freq = GetFrequencyAtIndex(startFreq, stopFreq, k, traceData.Length);
                    sbFull.Append($",{freq:F0}Hz");
                }
                sbFull.AppendLine();
                isFullHeaderWritten = true;
            }

            // 记录当前点的坐标和所有频谱幅值
            sbFull.Append($"{x:F3},{y:F3}");
            foreach (var val in traceData)
            {
                sbFull.Append($",{val:F3}");
            }
            sbFull.AppendLine();
        }

        private void SaveSamplingDistributionImage(ProjectViewModel project, List<SampledPoint> points, double xMin, double xMax, double yMin, double yMax, string fileName, string subFolder = "", double? globalMin = null, double? globalMax = null)
        {
            try
            {
                string dataFolder = Path.Combine(project.ProjectFolderPath, "Data");
                if (!string.IsNullOrEmpty(subFolder)) dataFolder = Path.Combine(dataFolder, subFolder);
                if (!Directory.Exists(dataFolder)) Directory.CreateDirectory(dataFolder);
                string fullPath = Path.Combine(dataFolder, fileName);

                Application.Current.Dispatcher.Invoke(() =>
                {
                    var model = new PlotModel { Title = "AI采样点分布" };
                    model.PlotType = PlotType.Cartesian;
                    model.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Title = "Physical X (mm)", Minimum = xMin, Maximum = xMax });
                    model.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Title = "Physical Y (mm)", Minimum = yMin, Maximum = yMax });

                    var scatterSeries = new ScatterSeries { MarkerType = MarkerType.Circle, MarkerSize = 4, MarkerStroke = OxyColors.Black, MarkerStrokeThickness = 0.5 };
                    
                    var palette = OxyPalettes.Jet(100);
                    // 如果指定了全局最大最小值，使用该范围；否则使用点数据的范围
                    double axisMin = globalMin ?? (points.Count > 0 ? points.Min(p => p.Magnitude) : 0);
                    double axisMax = globalMax ?? (points.Count > 0 ? points.Max(p => p.Magnitude) : 100);
                    
                    if (points.Count > 0 && globalMin == null && globalMax == null)
                    {
                         // 如果没有指定，且有数据，确保Min < Max
                         if (Math.Abs(axisMax - axisMin) < 0.001) { axisMin -= 1; axisMax += 1; }
                    }

                    model.Axes.Add(new LinearColorAxis { Position = AxisPosition.Right, Palette = palette, Minimum = axisMin, Maximum = axisMax, Title = "Strength" });

                    if (points.Count > 0)
                    {
                        foreach (var p in points)
                        {
                            scatterSeries.Points.Add(new ScatterPoint(p.X, p.Y) { Value = p.Magnitude });
                        }
                    }

                    model.Series.Add(scatterSeries);

                    var exporter = new PngExporter { Width = 1000, Height = 750 };
                    exporter.ExportToFile(model, fullPath);
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"保存采样分布图失败: {ex.Message}");
            }
        }

        private static QbcOutputData CalculateNextBatchSamplePoints(QbcInputData inputData)
        {
            try
            {
                var hyperParams = inputData.HyperParams;
                var sampledPoints = inputData.SampledPoints;
                if (sampledPoints == null || sampledPoints.Count == 0) return new QbcOutputData { Status = "error", Message = "No sampled points" };

                int totalPoints = hyperParams.Nx * hyperParams.Ny;
                //超参数：在这里修改M的计算方式，确保它不会过大
                int M = Math.Max(1, (int)(totalPoints * 0.10));

                double[][] xObs = sampledPoints.Select(p => new[] { (double)p.X, (double)p.Y }).ToArray();
                // 退化成线性：将 Magnitude 从 dB 还原为线性物理量用于 RBF 拟合并计算方差
                double[] yObs = sampledPoints.Select(p => Math.Pow(10, p.Magnitude / 20.0)).ToArray();

                var xCoor = GenerateLinspace(hyperParams.X_min, hyperParams.X_max, hyperParams.Nx);
                var yCoor = GenerateLinspace(hyperParams.Y_min, hyperParams.Y_max, hyperParams.Ny);

                // Optimize: Use HashSet for fast lookup
                var sampledIndices = new HashSet<(int, int)>();
                double xStep = (hyperParams.Nx > 1) ? (hyperParams.X_max - hyperParams.X_min) / (hyperParams.Nx - 1) : 0;
                double yStep = (hyperParams.Ny > 1) ? (hyperParams.Y_max - hyperParams.Y_min) / (hyperParams.Ny - 1) : 0;

                foreach (var p in sampledPoints)
                {
                    int i = (hyperParams.Nx > 1) ? (int)Math.Round(((double)p.X - hyperParams.X_min) / xStep) : 0;
                    int j = (hyperParams.Ny > 1) ? (int)Math.Round(((double)p.Y - hyperParams.Y_min) / yStep) : 0;
                    i = Math.Max(0, Math.Min(i, hyperParams.Nx - 1));
                    j = Math.Max(0, Math.Min(j, hyperParams.Ny - 1));
                    sampledIndices.Add((i, j));
                }

                var unselectedPoints = new List<double[]>();
                // Only consider points not yet sampled
                for (int j = 0; j < hyperParams.Ny; j++)
                {
                    for (int i = 0; i < hyperParams.Nx; i++)
                    {
                        if (!sampledIndices.Contains((i, j)))
                        {
                            unselectedPoints.Add(new[] { xCoor[i], yCoor[j] });
                        }
                    }
                }

                if (unselectedPoints.Count == 0) return new QbcOutputData { Status = "error", Message = "No more points to sample" };

                // QBC Committee Members (Kernels)
                var kernels = new List<RbfKernel> { RbfKernel.Linear, RbfKernel.Cubic, RbfKernel.ThinPlateSpline, RbfKernel.Quintic };

                var getVariances = new Func<double[][], double[], List<double[]>, (double[] vars, double[] means)>((xO, yO, unsel) => {
                    var preds = new double[unsel.Count][];
                    for (int i = 0; i < unsel.Count; i++) preds[i] = new double[kernels.Count];
                    for (int k = 0; k < kernels.Count; k++)
                    {
                        var model = new RbfInterpolator(xO, yO, kernels[k], 5);
                        for (int i = 0; i < unsel.Count; i++) preds[i][k] = model.Predict(unsel[i]);
                    }
                    var vars = new double[unsel.Count];
                    var mns = new double[unsel.Count];
                    for (int i = 0; i < unsel.Count; i++)
                    {
                        double m = preds[i].Average();
                        mns[i] = m;
                        vars[i] = preds[i].Sum(p => Math.Pow(p - m, 2)) / preds[i].Length;
                    }
                    return (vars, mns);
                });

                var (variances, means) = getVariances(xObs, yObs, unselectedPoints);

                var indexedUnselected = unselectedPoints.Select((pt, idx) => new { Pt = pt, Var = variances[idx], Mean = means[idx] })
                                                        .OrderByDescending(x => x.Var)
                                                        .ToList();

                int actualM = Math.Min(M, indexedUnselected.Count);
                if (actualM == 0) return new QbcOutputData { Status = "error", Message = "Candidate pool is empty" };

                var candidatePool = indexedUnselected.Take(actualM).ToList();
                double minVarThreshold = candidatePool.Min(c => c.Var);

                // 超参数：在这里修改K的计算方式，确保它不会过大
                int K = Math.Max(1, (int)(actualM * 0.25));
                K = Math.Min(K, candidatePool.Count);

                var rand = new Random(42);
                var centroids = candidatePool.OrderBy(x => rand.Next()).Take(K).Select(c => new[] { c.Pt[0], c.Pt[1] }).ToList();
                var clusters = new List<int>[K];
                for (int i = 0; i < K; i++) clusters[i] = new List<int>();

                int maxIters = 50;
                for (int iter = 0; iter < maxIters; iter++)
                {
                    for (int i = 0; i < K; i++) clusters[i].Clear();
                    for (int i = 0; i < candidatePool.Count; i++)
                    {
                        int bestClust = 0;
                        double bestDist = double.MaxValue;
                        for (int k = 0; k < K; k++)
                        {
                            double dSq = Math.Pow(candidatePool[i].Pt[0] - centroids[k][0], 2) + Math.Pow(candidatePool[i].Pt[1] - centroids[k][1], 2);
                            if (dSq < bestDist) { bestDist = dSq; bestClust = k; }
                        }
                        clusters[bestClust].Add(i);
                    }

                    bool changed = false;
                    for (int k = 0; k < K; k++)
                    {
                        if (clusters[k].Count == 0) continue;
                        double sumW = 0, sumX = 0, sumY = 0;
                        foreach (int idx in clusters[k])
                        {
                            double w = candidatePool[idx].Var;
                            sumW += w;
                            sumX += candidatePool[idx].Pt[0] * w;
                            sumY += candidatePool[idx].Pt[1] * w;
                        }
                        if (sumW > 0)
                        {
                            double nX = sumX / sumW;
                            double nY = sumY / sumW;
                            if (Math.Abs(nX - centroids[k][0]) > 1e-5 || Math.Abs(nY - centroids[k][1]) > 1e-5) changed = true;
                            centroids[k][0] = nX;
                            centroids[k][1] = nY;
                        }
                    }
                    if (!changed) break;
                }

                var finalPoints = new List<(float X, float Y)>();
                var currentXObs = xObs.ToList();
                var currentYObs = yObs.ToList();

                // 单层局部方差衰减策略（替换原几何距离防聚集）
                // 目标：只评估候选点邻域内方差收益，避免全局重复计算，并沿用 V_low 判据。
                var rankedCandidates = candidatePool.OrderByDescending(c => c.Var).ToList();

                // 保守参数：邻域略大、衰减较温和、V_low 阈值略提高以提升稳健性
                double localRadiusGrid = 2.4;
                double decayAlpha = 0.35;
                double avgCandidateVar = rankedCandidates.Count > 0 ? rankedCandidates.Average(c => c.Var) : 0.0;
                double V_low = Math.Max(minVarThreshold * 1.20, avgCandidateVar * 0.70);

                int candCount = rankedCandidates.Count;
                var candGridX = new double[candCount];
                var candGridY = new double[candCount];
                var dynamicVars = new double[candCount];
                var gridToCandidate = new Dictionary<(int gx, int gy), int>();

                for (int i = 0; i < candCount; i++)
                {
                    var c = rankedCandidates[i];
                    candGridX[i] = xStep > 0 ? (c.Pt[0] - hyperParams.X_min) / xStep : 0;
                    candGridY[i] = yStep > 0 ? (c.Pt[1] - hyperParams.Y_min) / yStep : 0;
                    dynamicVars[i] = c.Var;
                    int gx = (int)Math.Round(candGridX[i]);
                    int gy = (int)Math.Round(candGridY[i]);
                    if (!gridToCandidate.ContainsKey((gx, gy))) gridToCandidate[(gx, gy)] = i;
                }

                int rCeil = (int)Math.Ceiling(localRadiusGrid);
                double radiusSq = localRadiusGrid * localRadiusGrid;
                var neighborOffsets = new List<(int dx, int dy, double weight)>();
                for (int dy = -rCeil; dy <= rCeil; dy++)
                {
                    for (int dx = -rCeil; dx <= rCeil; dx++)
                    {
                        double dSq = dx * dx + dy * dy;
                        if (dSq > radiusSq) continue;
                        double d = Math.Sqrt(dSq);
                        double w = 1.0 - d / (localRadiusGrid + 1e-12);
                        if (w > 0) neighborOffsets.Add((dx, dy, w));
                    }
                }

                // 单层循环：候选点仅扫描一次，不再做“点对点几何距离双层比较”
                for (int i = 0; i < candCount && finalPoints.Count < K; i++)
                {
                    if (dynamicVars[i] < V_low) continue;

                    int gx = (int)Math.Round(candGridX[i]);
                    int gy = (int)Math.Round(candGridY[i]);

                    double localWeightedVarSum = 0.0;
                    double localWeightSum = 0.0;
                    foreach (var ofst in neighborOffsets)
                    {
                        var key = (gx + ofst.dx, gy + ofst.dy);
                        if (gridToCandidate.TryGetValue(key, out int nIdx))
                        {
                            localWeightedVarSum += dynamicVars[nIdx] * ofst.weight;
                            localWeightSum += ofst.weight;
                        }
                    }

                    double localMeanVar = localWeightSum > 0 ? localWeightedVarSum / localWeightSum : dynamicVars[i];
                    if (localMeanVar < V_low) continue;

                    var chosen = rankedCandidates[i];
                    finalPoints.Add(((float)chosen.Pt[0], (float)chosen.Pt[1]));
                    currentXObs.Add(chosen.Pt);
                    currentYObs.Add(chosen.Mean);

                    // 局部衰减：模拟新增采样点主要影响其邻域不确定度
                    foreach (var ofst in neighborOffsets)
                    {
                        var key = (gx + ofst.dx, gy + ofst.dy);
                        if (gridToCandidate.TryGetValue(key, out int nIdx))
                        {
                            double factor = 1.0 - decayAlpha * ofst.weight;
                            if (factor < 0.1) factor = 0.1;
                            dynamicVars[nIdx] = Math.Max(0.0, dynamicVars[nIdx] * factor);
                        }
                    }
                }

                if (finalPoints.Count == 0 && rankedCandidates.Count > 0)
                {
                    var cPt = rankedCandidates[0];
                    finalPoints.Add(((float)cPt.Pt[0], (float)cPt.Pt[1]));
                }

                return new QbcOutputData { Status = "success", Message = "Calculated", NextPoints = finalPoints, ClusterCount = K };
            }
            catch (Exception ex) { return new QbcOutputData { Status = "error", Message = $"Internal Error: {ex.Message}" }; }
        }

        private static QbcOutputData CalculateNextSingleSamplePoint(QbcInputData inputData)
        {
            try
            {
                var hyperParams = inputData.HyperParams;
                var sampledPoints = inputData.SampledPoints;
                if (sampledPoints == null || sampledPoints.Count == 0) return new QbcOutputData { Status = "error", Message = "No sampled points" };

                double[][] xObs = sampledPoints.Select(p => new[] { (double)p.X, (double)p.Y }).ToArray();
                double[] yObs = sampledPoints.Select(p => Math.Pow(10, p.Magnitude / 20.0)).ToArray();

                var xCoor = GenerateLinspace(hyperParams.X_min, hyperParams.X_max, hyperParams.Nx);
                var yCoor = GenerateLinspace(hyperParams.Y_min, hyperParams.Y_max, hyperParams.Ny);

                var sampledIndices = new HashSet<(int, int)>();
                double xStep = (hyperParams.Nx > 1) ? (hyperParams.X_max - hyperParams.X_min) / (hyperParams.Nx - 1) : 0;
                double yStep = (hyperParams.Ny > 1) ? (hyperParams.Y_max - hyperParams.Y_min) / (hyperParams.Ny - 1) : 0;
                foreach (var p in sampledPoints)
                {
                    int i = (hyperParams.Nx > 1) ? (int)Math.Round(((double)p.X - hyperParams.X_min) / xStep) : 0;
                    int j = (hyperParams.Ny > 1) ? (int)Math.Round(((double)p.Y - hyperParams.Y_min) / yStep) : 0;
                    i = Math.Max(0, Math.Min(i, hyperParams.Nx - 1));
                    j = Math.Max(0, Math.Min(j, hyperParams.Ny - 1));
                    sampledIndices.Add((i, j));
                }

                var unselectedPoints = new List<double[]>();
                for (int j = 0; j < hyperParams.Ny; j++)
                {
                    for (int i = 0; i < hyperParams.Nx; i++)
                    {
                        if (!sampledIndices.Contains((i, j)))
                        {
                            unselectedPoints.Add(new[] { xCoor[i], yCoor[j] });
                        }
                    }
                }

                if (unselectedPoints.Count == 0) return new QbcOutputData { Status = "error", Message = "No more points to sample" };

                var kernels = new List<RbfKernel> { RbfKernel.Linear, RbfKernel.Cubic, RbfKernel.ThinPlateSpline, RbfKernel.Quintic };
                var preds = new double[unselectedPoints.Count][];
                for (int i = 0; i < unselectedPoints.Count; i++) preds[i] = new double[kernels.Count];
                for (int k = 0; k < kernels.Count; k++)
                {
                    var model = new RbfInterpolator(xObs, yObs, kernels[k], 5);
                    for (int i = 0; i < unselectedPoints.Count; i++) preds[i][k] = model.Predict(unselectedPoints[i]);
                }

                int bestIdx = 0;
                double bestVar = double.MinValue;
                for (int i = 0; i < unselectedPoints.Count; i++)
                {
                    double mean = preds[i].Average();
                    double var = preds[i].Sum(p => Math.Pow(p - mean, 2)) / preds[i].Length;
                    if (var > bestVar)
                    {
                        bestVar = var;
                        bestIdx = i;
                    }
                }

                var pt = unselectedPoints[bestIdx];
                return new QbcOutputData
                {
                    Status = "success",
                    Message = "Calculated",
                    NextPoints = new List<(float X, float Y)> { ((float)pt[0], (float)pt[1]) },
                    ClusterCount = 1
                };
            }
            catch (Exception ex) { return new QbcOutputData { Status = "error", Message = $"Internal Error: {ex.Message}" }; }
        }

        private static QbcOutputData CalculateNextBatchSamplePointsOriginal(QbcInputData inputData)
        {
            try
            {
                var hyperParams = inputData.HyperParams;
                var sampledPoints = inputData.SampledPoints;
                if (sampledPoints == null || sampledPoints.Count == 0) return new QbcOutputData { Status = "error", Message = "No sampled points" };

                int totalPoints = hyperParams.Nx * hyperParams.Ny;
                int M = Math.Max(1, (int)(totalPoints * 0.10));

                double[][] xObs = sampledPoints.Select(p => new[] { (double)p.X, (double)p.Y }).ToArray();
                double[] yObs = sampledPoints.Select(p => Math.Pow(10, p.Magnitude / 20.0)).ToArray();

                var xCoor = GenerateLinspace(hyperParams.X_min, hyperParams.X_max, hyperParams.Nx);
                var yCoor = GenerateLinspace(hyperParams.Y_min, hyperParams.Y_max, hyperParams.Ny);

                var sampledIndices = new HashSet<(int, int)>();
                double xStep = (hyperParams.Nx > 1) ? (hyperParams.X_max - hyperParams.X_min) / (hyperParams.Nx - 1) : 0;
                double yStep = (hyperParams.Ny > 1) ? (hyperParams.Y_max - hyperParams.Y_min) / (hyperParams.Ny - 1) : 0;

                foreach (var p in sampledPoints)
                {
                    int i = (hyperParams.Nx > 1) ? (int)Math.Round(((double)p.X - hyperParams.X_min) / xStep) : 0;
                    int j = (hyperParams.Ny > 1) ? (int)Math.Round(((double)p.Y - hyperParams.Y_min) / yStep) : 0;
                    i = Math.Max(0, Math.Min(i, hyperParams.Nx - 1));
                    j = Math.Max(0, Math.Min(j, hyperParams.Ny - 1));
                    sampledIndices.Add((i, j));
                }

                var unselectedPoints = new List<double[]>();
                for (int j = 0; j < hyperParams.Ny; j++)
                {
                    for (int i = 0; i < hyperParams.Nx; i++)
                    {
                        if (!sampledIndices.Contains((i, j))) unselectedPoints.Add(new[] { xCoor[i], yCoor[j] });
                    }
                }

                if (unselectedPoints.Count == 0) return new QbcOutputData { Status = "error", Message = "No more points to sample" };

                var kernels = new List<RbfKernel> { RbfKernel.Linear, RbfKernel.Cubic, RbfKernel.ThinPlateSpline, RbfKernel.Quintic };
                var preds = new double[unselectedPoints.Count][];
                for (int i = 0; i < unselectedPoints.Count; i++) preds[i] = new double[kernels.Count];
                for (int k = 0; k < kernels.Count; k++)
                {
                    var model = new RbfInterpolator(xObs, yObs, kernels[k], 5);
                    for (int i = 0; i < unselectedPoints.Count; i++) preds[i][k] = model.Predict(unselectedPoints[i]);
                }

                var indexedUnselected = new List<(double[] Pt, double Var, double Mean)>();
                for (int i = 0; i < unselectedPoints.Count; i++)
                {
                    double mean = preds[i].Average();
                    double var = preds[i].Sum(p => Math.Pow(p - mean, 2)) / preds[i].Length;
                    indexedUnselected.Add((unselectedPoints[i], var, mean));
                }
                indexedUnselected = indexedUnselected.OrderByDescending(x => x.Var).ToList();

                int actualM = Math.Min(M, indexedUnselected.Count);
                if (actualM == 0) return new QbcOutputData { Status = "error", Message = "Candidate pool is empty" };

                var candidatePool = indexedUnselected.Take(actualM).ToList();

                int K = Math.Max(1, (int)(actualM * 0.25));
                K = Math.Min(K, candidatePool.Count);

                var rand = new Random(42);
                var centroids = candidatePool.OrderBy(x => rand.Next()).Take(K).Select(c => new[] { c.Pt[0], c.Pt[1] }).ToList();
                var clusters = new List<int>[K];
                for (int i = 0; i < K; i++) clusters[i] = new List<int>();

                int maxIters = 50;
                for (int iter = 0; iter < maxIters; iter++)
                {
                    for (int i = 0; i < K; i++) clusters[i].Clear();
                    for (int i = 0; i < candidatePool.Count; i++)
                    {
                        int bestClust = 0;
                        double bestDist = double.MaxValue;
                        for (int k = 0; k < K; k++)
                        {
                            double dSq = Math.Pow(candidatePool[i].Pt[0] - centroids[k][0], 2) + Math.Pow(candidatePool[i].Pt[1] - centroids[k][1], 2);
                            if (dSq < bestDist) { bestDist = dSq; bestClust = k; }
                        }
                        clusters[bestClust].Add(i);
                    }

                    bool changed = false;
                    for (int k = 0; k < K; k++)
                    {
                        if (clusters[k].Count == 0) continue;
                        double sumW = 0, sumX = 0, sumY = 0;
                        foreach (int idx in clusters[k])
                        {
                            double w = candidatePool[idx].Var;
                            sumW += w;
                            sumX += candidatePool[idx].Pt[0] * w;
                            sumY += candidatePool[idx].Pt[1] * w;
                        }
                        if (sumW > 0)
                        {
                            double nX = sumX / sumW;
                            double nY = sumY / sumW;
                            if (Math.Abs(nX - centroids[k][0]) > 1e-5 || Math.Abs(nY - centroids[k][1]) > 1e-5) changed = true;
                            centroids[k][0] = nX;
                            centroids[k][1] = nY;
                        }
                    }
                    if (!changed) break;
                }

                var initialBatch = new List<int>();
                for (int k = 0; k < K; k++)
                {
                    if (clusters[k].Count > 0)
                    {
                        int bestIdx = clusters[k].OrderByDescending(idx => candidatePool[idx].Var).First();
                        initialBatch.Add(bestIdx);
                    }
                }

                var finalPoints = new List<(float X, float Y)>();
                var currentXObs = xObs.ToList();
                var currentYObs = yObs.ToList();
                double gridMinAllowedDist = 1.6;

                // 保留原始费时链路：对每个WKMC代表点都基于“当前已选样本”重新拟合并重算候选方差
                var repPoints = initialBatch.Select(idx => candidatePool[idx].Pt).ToList();
                foreach (var repPt in repPoints)
                {
                    if (candidatePool.Count == 0) break;

                    var repPreds = new double[candidatePool.Count][];
                    for (int i = 0; i < candidatePool.Count; i++) repPreds[i] = new double[kernels.Count];
                    for (int k = 0; k < kernels.Count; k++)
                    {
                        var model = new RbfInterpolator(currentXObs.ToArray(), currentYObs.ToArray(), kernels[k], 5);
                        for (int i = 0; i < candidatePool.Count; i++) repPreds[i][k] = model.Predict(candidatePool[i].Pt);
                    }

                    for (int i = 0; i < candidatePool.Count; i++)
                    {
                        double meanDyn = repPreds[i].Average();
                        double varDyn = repPreds[i].Sum(p => Math.Pow(p - meanDyn, 2)) / repPreds[i].Length;
                        candidatePool[i] = (candidatePool[i].Pt, varDyn, meanDyn);
                    }

                    // 保留WKMC语义：以簇代表点为中心，在其邻域里挑重算后方差最大的点
                    var nearCandidates = candidatePool
                        .Select((c, i) => new { Item = c, Index = i, DistSq = Math.Pow(c.Pt[0] - repPt[0], 2) + Math.Pow(c.Pt[1] - repPt[1], 2) })
                        .OrderBy(x => x.DistSq)
                        .Take(Math.Max(1, candidatePool.Count / Math.Max(1, K)))
                        .OrderByDescending(x => x.Item.Var)
                        .ToList();

                    if (nearCandidates.Count == 0) continue;
                    var chosen = nearCandidates[0];
                    var cPt = chosen.Item;

                    if (finalPoints.Count > 0)
                    {
                        bool isTooClose = false;
                        double cGridX = xStep > 0 ? (cPt.Pt[0] - hyperParams.X_min) / xStep : 0;
                        double cGridY = yStep > 0 ? (cPt.Pt[1] - hyperParams.Y_min) / yStep : 0;
                        foreach (var fp in finalPoints)
                        {
                            double fpGridX = xStep > 0 ? (fp.X - hyperParams.X_min) / xStep : 0;
                            double fpGridY = yStep > 0 ? (fp.Y - hyperParams.Y_min) / yStep : 0;
                            double gridDist = Math.Sqrt(Math.Pow(fpGridX - cGridX, 2) + Math.Pow(fpGridY - cGridY, 2));
                            if (gridDist < gridMinAllowedDist)
                            {
                                isTooClose = true;
                                break;
                            }
                        }
                        if (isTooClose)
                        {
                            candidatePool.RemoveAt(chosen.Index);
                            continue;
                        }
                    }

                    finalPoints.Add(((float)cPt.Pt[0], (float)cPt.Pt[1]));
                    currentXObs.Add(cPt.Pt);
                    currentYObs.Add(cPt.Mean);
                    candidatePool.RemoveAt(chosen.Index);
                }

                // 兜底：若代表点筛选后还没选满，从剩余候选继续“重算+双层EVC”补齐
                while (finalPoints.Count < K && candidatePool.Count > 0)
                {
                    var repPreds = new double[candidatePool.Count][];
                    for (int i = 0; i < candidatePool.Count; i++) repPreds[i] = new double[kernels.Count];
                    for (int k = 0; k < kernels.Count; k++)
                    {
                        var model = new RbfInterpolator(currentXObs.ToArray(), currentYObs.ToArray(), kernels[k], 5);
                        for (int i = 0; i < candidatePool.Count; i++) repPreds[i][k] = model.Predict(candidatePool[i].Pt);
                    }

                    for (int i = 0; i < candidatePool.Count; i++)
                    {
                        double meanDyn = repPreds[i].Average();
                        double varDyn = repPreds[i].Sum(p => Math.Pow(p - meanDyn, 2)) / repPreds[i].Length;
                        candidatePool[i] = (candidatePool[i].Pt, varDyn, meanDyn);
                    }

                    int bestDynIdx = candidatePool.Select((c, i) => new { c.Var, Idx = i }).OrderByDescending(x => x.Var).First().Idx;
                    var cPt = candidatePool[bestDynIdx];

                    bool isTooClose = false;
                    if (finalPoints.Count > 0)
                    {
                        double cGridX = xStep > 0 ? (cPt.Pt[0] - hyperParams.X_min) / xStep : 0;
                        double cGridY = yStep > 0 ? (cPt.Pt[1] - hyperParams.Y_min) / yStep : 0;
                        foreach (var fp in finalPoints)
                        {
                            double fpGridX = xStep > 0 ? (fp.X - hyperParams.X_min) / xStep : 0;
                            double fpGridY = yStep > 0 ? (fp.Y - hyperParams.Y_min) / yStep : 0;
                            double gridDist = Math.Sqrt(Math.Pow(fpGridX - cGridX, 2) + Math.Pow(fpGridY - cGridY, 2));
                            if (gridDist < gridMinAllowedDist) { isTooClose = true; break; }
                        }
                    }

                    if (!isTooClose)
                    {
                        finalPoints.Add(((float)cPt.Pt[0], (float)cPt.Pt[1]));
                        currentXObs.Add(cPt.Pt);
                        currentYObs.Add(cPt.Mean);
                    }

                    candidatePool.RemoveAt(bestDynIdx);
                }

                if (finalPoints.Count == 0 && initialBatch.Count > 0)
                {
                    var fallback = indexedUnselected.First();
                    finalPoints.Add(((float)fallback.Pt[0], (float)fallback.Pt[1]));
                }

                return new QbcOutputData { Status = "success", Message = "Calculated", NextPoints = finalPoints, ClusterCount = K };
            }
            catch (Exception ex) { return new QbcOutputData { Status = "error", Message = $"Internal Error: {ex.Message}" }; }
        }

        private static double CalculateMaxStandardDeviation(List<SampledPoint> sampledPoints, ScanSettings settings, double xMin, double xMax, double yMin, double yMax)
        {
            if (sampledPoints.Count < 4) return 0;

            double[][] xObs = sampledPoints.Select(p => new[] { (double)p.X, (double)p.Y }).ToArray();
            // 在计算标准差前，退化成线性：这样计算出的 maxVar 也是基于线性域平方的
            double[] yObs = sampledPoints.Select(p => Math.Pow(10, p.Magnitude / 20.0)).ToArray();

            var xCoor = GenerateLinspace(xMin, xMax, settings.NumX);
            var yCoor = GenerateLinspace(yMin, yMax, settings.NumY);

            var sampledIndices = new HashSet<(int, int)>();
            double xStep = (settings.NumX > 1) ? (xMax - xMin) / (settings.NumX - 1) : 0;
            double yStep = (settings.NumY > 1) ? (yMax - yMin) / (settings.NumY - 1) : 0;

            foreach (var p in sampledPoints)
            {
                int i = (settings.NumX > 1) ? (int)Math.Round(((double)p.X - xMin) / xStep) : 0;
                int j = (settings.NumY > 1) ? (int)Math.Round(((double)p.Y - yMin) / yStep) : 0;
                i = Math.Max(0, Math.Min(i, settings.NumX - 1));
                j = Math.Max(0, Math.Min(j, settings.NumY - 1));
                sampledIndices.Add((i, j));
            }

            var kernels = new List<RbfKernel> { RbfKernel.Linear, RbfKernel.Cubic, RbfKernel.ThinPlateSpline, RbfKernel.Quintic };
            var models = new List<RbfInterpolator>();
            foreach (var kern in kernels)
            {
                models.Add(new RbfInterpolator(xObs, yObs, kern, 5));
            }

            double maxVar = 0.0;

            for (int j = 0; j < settings.NumY; j++)
            {
                for (int i = 0; i < settings.NumX; i++)
                {
                    if (!sampledIndices.Contains((i, j)))
                    {
                        double[] pt = new[] { xCoor[i], yCoor[j] };
                        double[] preds = new double[kernels.Count];
                        for (int k = 0; k < kernels.Count; k++) preds[k] = models[k].Predict(pt);

                        double mean = preds.Average();
                        double var = preds.Sum(p => (p - mean) * (p - mean)) / preds.Length;
                        if (var > maxVar) maxVar = var;
                    }
                }
            }

            return Math.Sqrt(maxVar);
        }

        private static List<(float X, float Y)> OptimizeScanPath(List<(float X, float Y)> points, (float X, float Y) startPos)
        {
            if (points == null || points.Count <= 1) return points ?? new List<(float X, float Y)>();

            // 1. Nearest Neighbor Initialization
            var unvisited = points.ToList();
            var route = new List<(float X, float Y)>();
            var current = startPos;

            while (unvisited.Count > 0)
            {
                int bestIdx = 0;
                double bestDist = double.MaxValue;
                for (int i = 0; i < unvisited.Count; i++)
                {
                    double dSq = Math.Pow(unvisited[i].X - current.X, 2) + Math.Pow(unvisited[i].Y - current.Y, 2);
                    if (dSq < bestDist)
                    {
                        bestDist = dSq;
                        bestIdx = i;
                    }
                }
                current = unvisited[bestIdx];
                route.Add(current);
                unvisited.RemoveAt(bestIdx);
            }

            // 2. 2-Opt Optimization (Open Loop)
            var fullRoute = new List<(float X, float Y)> { startPos };
            fullRoute.AddRange(route);

            bool improvement = true;
            int n = fullRoute.Count;
            int maxIters = 100; // safeguard against infinite loops
            int iters = 0;

            while (improvement && iters < maxIters)
            {
                improvement = false;
                iters++;
                for (int i = 1; i < n - 1; i++) // Index 0 is fixed startPos
                {
                    for (int k = i + 1; k < n; k++)
                    {
                        double dRem = Dist(fullRoute[i - 1], fullRoute[i]);
                        double dAdd = Dist(fullRoute[i - 1], fullRoute[k]);

                        if (k < n - 1)
                        {
                            dRem += Dist(fullRoute[k], fullRoute[k + 1]);
                            dAdd += Dist(fullRoute[i], fullRoute[k + 1]);
                        }

                        if (dAdd < dRem - 1e-5) // Improvement found
                        {
                            // Reverse segment fullRoute[i...k]
                            fullRoute.Reverse(i, k - i + 1);
                            improvement = true;
                        }
                    }
                }
            }

            fullRoute.RemoveAt(0); // Remove startPos, return only the targets
            return fullRoute;
        }

        private static double Dist((float X, float Y) p1, (float X, float Y) p2)
        {
            return Math.Sqrt(Math.Pow(p1.X - p2.X, 2) + Math.Pow(p1.Y - p2.Y, 2));
        }

        private static double[] GenerateLinspace(double start, double end, int count)
        {
            if (count <= 0) throw new ArgumentException("count必须大于0");
            if (count == 1) return new[] { start };
            double[] result = new double[count];
            double step = (end - start) / (count - 1);
            for (int i = 0; i < count; i++) result[i] = start + step * i;
            return result;
        }

        private (double[,] filledHeatMapData, Dictionary<(float X, float Y), double> fullPointMap) FillUnsampledPointsWithRbfMean(List<SampledPoint> sampledPoints, ScanSettings scanSettings)
        {
            double xMin = Math.Min(scanSettings.StartX, scanSettings.StopX);
            double xMax = Math.Max(scanSettings.StartX, scanSettings.StopX);
            double yMin = Math.Min(scanSettings.StartY, scanSettings.StopY);
            double yMax = Math.Max(scanSettings.StartY, scanSettings.StopY);
            var xCoor = GenerateLinspace(xMin, xMax, scanSettings.NumX);
            var yCoor = GenerateLinspace(yMin, yMax, scanSettings.NumY);
            var sampledPointMap = new Dictionary<(float X, float Y), double>();
            foreach (var p in sampledPoints) sampledPointMap[((float)Math.Round(p.X, 3), (float)Math.Round(p.Y, 3))] = p.Magnitude;
            double[][] xObs = sampledPoints.Select(p => new[] { (double)p.X, (double)p.Y }).ToArray();
            // 预测全场热力图时，同样按线性域拟合，保证最后呈现的热力图与逻辑一致
            double[] yObs = sampledPoints.Select(p => Math.Pow(10, p.Magnitude / 20.0)).ToArray();
            var kernels = new List<RbfKernel> { RbfKernel.Linear, RbfKernel.Cubic, RbfKernel.ThinPlateSpline, RbfKernel.Quintic };
            var rbfModels = new List<RbfInterpolator>();
            foreach (var kernel in kernels) rbfModels.Add(new RbfInterpolator(xObs, yObs, kernel, 5));
            double[,] filledData = new double[scanSettings.NumX, scanSettings.NumY];
            var fullPointMap = new Dictionary<(float X, float Y), double>();
            for (int j = 0; j < scanSettings.NumY; j++)
            {
                float targetY = (float)Math.Round((float)yCoor[j], 3);
                for (int i = 0; i < scanSettings.NumX; i++)
                {
                    float targetX = (float)Math.Round((float)xCoor[i], 3);
                    var key = (targetX, targetY);
                    if (sampledPointMap.ContainsKey(key)) { filledData[i, j] = sampledPointMap[key]; fullPointMap[key] = sampledPointMap[key]; }
                    else
                    {
                        double[] predictions = new double[rbfModels.Count];
                        for (int k = 0; k < rbfModels.Count; k++) predictions[k] = rbfModels[k].Predict(new[] { (double)targetX, targetY });
                        double meanVal_lin = predictions.Average();
                        // 线性预测值转换回 dB 用于热力图显示
                        double meanVal_dB = 20 * Math.Log10(Math.Max(meanVal_lin, 1e-12));
                        filledData[i, j] = meanVal_dB; fullPointMap[key] = meanVal_dB;
                    }
                }
            }
            return (filledData, fullPointMap);
        }

        public class RbfInterpolator
        {
            private double[][] _xObs; private double[] _yObs; private RbfKernel _kernel; private int _degree; private MathNet.Numerics.LinearAlgebra.Vector<double> _weights = null!; private int _polySize;
            public RbfInterpolator(double[][] xObs, double[] yObs, RbfKernel kernel, int degree)
            {
                _xObs = xObs; _yObs = yObs; _kernel = kernel; _degree = degree; Train();
            }
            private void Train()
            {
                int n = _xObs.Length; var polyBasisExample = GetPolynomialBasis(_xObs[0]); _polySize = polyBasisExample.Length;
                var phi = DenseMatrix.Create(n + _polySize, n + _polySize, 0.0);
                for (int i = 0; i < n; i++)
                {
                    for (int j = 0; j < n; j++) { double r = CalculateDistance(_xObs[i], _xObs[j]); phi[i, j] = EvaluateKernel(r); }
                }
                for (int i = 0; i < n; i++)
                {
                    double[] polys = GetPolynomialBasis(_xObs[i]);
                    for (int j = 0; j < _polySize; j++) { phi[i, n + j] = polys[j]; phi[n + j, i] = polys[j]; }
                }
                var b = DenseVector.OfArray(_yObs.Concat(new double[_polySize]).ToArray());
                phi += DenseMatrix.CreateIdentity(phi.RowCount) * 1e-10;
                _weights = phi.Solve(b);
            }
            public double Predict(double[] x)
            {
                double result = 0.0;
                for (int i = 0; i < _xObs.Length; i++) result += _weights[i] * EvaluateKernel(CalculateDistance(x, _xObs[i]));
                double[] polys = GetPolynomialBasis(x);
                for (int i = 0; i < _polySize; i++) result += _weights[_xObs.Length + i] * polys[i];
                return result;
            }
            private double CalculateDistance(double[] x1, double[] x2) { double sum = 0; for (int i = 0; i < x1.Length; i++) sum += Math.Pow(x1[i] - x2[i], 2); return Math.Sqrt(sum); }
            private double EvaluateKernel(double r) { if (r < 1e-12) return 0.0; return _kernel switch { RbfKernel.Linear => r, RbfKernel.Cubic => r * r * r, RbfKernel.ThinPlateSpline => r * r * Math.Log(r), RbfKernel.Quintic => Math.Pow(r, 5), _ => 0.0 }; }
            private double[] GetPolynomialBasis(double[] x) { var basis = new List<double> { 1.0 }; if (_degree >= 1) basis.AddRange(x); if (_degree >= 2) { basis.Add(x[0] * x[0]); basis.Add(x[0] * x[1]); basis.Add(x[1] * x[1]); } return basis.ToArray(); }
        }
    }
}
