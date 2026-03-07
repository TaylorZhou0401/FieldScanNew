using System;
using System.Collections.Generic;
using System.Linq;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;
// using FieldScanNew.Models; // Mocked internally

namespace QbcIndependentTest
{
    // --- Mock Classes dependencies ---
    public class ScanSettings
    {
        public int NumX { get; set; } = 10;
        public int NumY { get; set; } = 10;
        public double StartX { get; set; } = 0;
        public double StartY { get; set; } = 0;
        public double StopX { get; set; } = 90;
        public double StopY { get; set; } = 90;
        public float ScanHeightZ { get; set; } = 10;
    }

    public class QbcInputData
    {
        public HyperParams HyperParams { get; set; }
        public List<SampledPoint> SampledPoints { get; set; }
    }

    public class HyperParams
    {
        public double X_min { get; set; }
        public double X_max { get; set; }
        public double Y_min { get; set; }
        public double Y_max { get; set; }
        public int Nx { get; set; }
        public int Ny { get; set; }
        public int Uncertainty_size { get; set; }
    }

    public class QbcOutputData
    {
        public string Status { get; set; }
        public string Message { get; set; }
        public double Next_x { get; set; }
        public double Next_y { get; set; }
    }

    public class SampledPoint
    {
        public float X { get; set; }
        public float Y { get; set; }
        public double Magnitude { get; set; }
    }

    public enum RbfKernel { Linear, Cubic, ThinPlateSpline, Quintic }

    public class QbcLogic
    {
        public static void Main(string[] args)
        {
            Console.WriteLine("QBC Algorithm Standalone Test");
            
            // 1. Setup Mock ScanSettings
            var scanSettings = new ScanSettings { NumX = 5, NumY = 5, StartX = 0, StopX = 40, StartY = 0, StopY = 40 };
            
            // 2. Initial Sample Points (Simulate a few points)
            var sampledPoints = new List<SampledPoint>();
            // Add Initial Grid points (e.g. 4 corners)
            sampledPoints.Add(new SampledPoint { X = 0, Y = 0, Magnitude = 10 });
            sampledPoints.Add(new SampledPoint { X = 40, Y = 0, Magnitude = 15 });
            sampledPoints.Add(new SampledPoint { X = 0, Y = 40, Magnitude = 12 });
            sampledPoints.Add(new SampledPoint { X = 40, Y = 40, Magnitude = 20 });
            // Add a random point
            sampledPoints.Add(new SampledPoint { X = 20, Y = 20, Magnitude = 25 });
            
            Console.WriteLine($"Initial Points: {sampledPoints.Count}");

            // 3. Test CalculateNextSamplePoint
            var hyperParams = new HyperParams 
            { 
                X_min = Math.Min(scanSettings.StartX, scanSettings.StopX), 
                X_max = Math.Max(scanSettings.StartX, scanSettings.StopX), 
                Y_min = Math.Min(scanSettings.StartY, scanSettings.StopY), 
                Y_max = Math.Max(scanSettings.StartY, scanSettings.StopY), 
                Nx = scanSettings.NumX, 
                Ny = scanSettings.NumY, 
                Uncertainty_size = 25 
            };
            var inputData = new QbcInputData { HyperParams = hyperParams, SampledPoints = sampledPoints };

            Console.WriteLine("Calculating Next Point...");
            var result = CalculateNextSamplePoint(inputData);
            
            Console.WriteLine($"Result Status: {result.Status}");
            if (result.Status == "success")
            {
                Console.WriteLine($"Next Point Suggestion: ({result.Next_x}, {result.Next_y})");
            }
            else
            {
                Console.WriteLine($"Error: {result.Message}");
            }

            // 4. Test FillUnsampledPointsWithRbfMean
            Console.WriteLine("\nTesting Full Field Interpolation...");
            var logic = new QbcLogic();
            var (filledData, fullPointMap) = logic.FillUnsampledPointsWithRbfMean(sampledPoints, scanSettings);
            
            Console.WriteLine("Interpolation Complete. Data Grid Sample (5x5):");
            for(int j=0; j<scanSettings.NumY; j++)
            {
                for(int i=0; i<scanSettings.NumX; i++)
                {
                    Console.Write($"{filledData[i, j]:F1}\t");
                }
                Console.WriteLine();
            }

            Console.WriteLine("\nTest Finished. Press Enter to exit.");
            Console.ReadLine();
        }

        // --- Extracted Logic Area ---

        public static QbcOutputData CalculateNextSamplePoint(QbcInputData inputData)
        {
            try
            {
                var hyperParams = inputData.HyperParams;
                var sampledPoints = inputData.SampledPoints;
                if (sampledPoints == null || sampledPoints.Count == 0) return new QbcOutputData { Status = "error", Message = "已采样点为空" };
                double[][] xObs = sampledPoints.Select(p => new[] { (double)p.X, (double)p.Y }).ToArray();
                double[] yObs = sampledPoints.Select(p => p.Magnitude).ToArray();
                var xCoor = GenerateLinspace(hyperParams.X_min, hyperParams.X_max, hyperParams.Nx);
                var yCoor = GenerateLinspace(hyperParams.Y_min, hyperParams.Y_max, hyperParams.Ny);

                // 基于网格索引的最近邻近似：将已采样点映射到最近的网格索引，确保未采样点严格对齐网格
                var sampledIndices = new HashSet<(int, int)>();
                double xStep = (hyperParams.Nx > 1) ? (hyperParams.X_max - hyperParams.X_min) / (hyperParams.Nx - 1) : 0;
                double yStep = (hyperParams.Ny > 1) ? (hyperParams.Y_max - hyperParams.Y_min) / (hyperParams.Ny - 1) : 0;

                foreach (var p in sampledPoints)
                {
                    int i = (hyperParams.Nx > 1) ? (int)Math.Round(((double)p.X - hyperParams.X_min) / xStep) : 0;
                    int j = (hyperParams.Ny > 1) ? (int)Math.Round(((double)p.Y - hyperParams.Y_min) / yStep) : 0;
                    // 边界保护，防止采样点微小越界
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

                if (unselectedPoints.Count == 0) return new QbcOutputData { Status = "error", Message = "没有可采样的新点了" };
                var kernels = new List<RbfKernel> { RbfKernel.Linear, RbfKernel.Cubic, RbfKernel.ThinPlateSpline, RbfKernel.Quintic };
                var predictions = new double[unselectedPoints.Count][];
                for (int i = 0; i < unselectedPoints.Count; i++) predictions[i] = new double[kernels.Count];
                for (int k = 0; k < kernels.Count; k++)
                {
                    var model = new RbfInterpolator(xObs, yObs, kernels[k], 5);
                    for (int i = 0; i < unselectedPoints.Count; i++) predictions[i][k] = model.Predict(unselectedPoints[i]);
                }
                var variances = new double[unselectedPoints.Count];
                for (int i = 0; i < unselectedPoints.Count; i++)
                {
                    var preds = predictions[i];
                    double mean = preds.Average();
                    variances[i] = preds.Sum(p => Math.Pow(p - mean, 2)) / preds.Length;
                }
                int maxVarIndex = 0; double maxVariance = variances[0];
                for (int i = 1; i < variances.Length; i++) { if (variances[i] > maxVariance) { maxVariance = variances[i]; maxVarIndex = i; } }
                var nextPoint = unselectedPoints[maxVarIndex];
                // Return exact coordinates to avoid mismatch with grid points (distance check is sensitive)
                return new QbcOutputData { Status = "success", Message = "计算成功", Next_x = nextPoint[0], Next_y = nextPoint[1] };
            }
            catch (Exception ex) { return new QbcOutputData { Status = "error", Message = $"计算出错：{ex.Message}" }; }
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

        public (double[,] filledHeatMapData, Dictionary<(float X, float Y), double> fullPointMap) FillUnsampledPointsWithRbfMean(List<SampledPoint> sampledPoints, ScanSettings scanSettings)
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
            double[] yObs = sampledPoints.Select(p => p.Magnitude).ToArray();
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
                        double meanVal = predictions.Average(); filledData[i, j] = meanVal; fullPointMap[key] = meanVal;
                    }
                }
            }
            return (filledData, fullPointMap);
        }

        public class RbfInterpolator
        {
            private double[][] _xObs; private double[] _yObs; private RbfKernel _kernel; private int _degree; private MathNet.Numerics.LinearAlgebra.Vector<double> _weights; private int _polySize;
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
