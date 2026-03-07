using System.Collections.Generic;
using FieldScanNew.Infrastructure; // 如果 InstrumentSettings 在这里

namespace FieldScanNew.Models
{
    public class ProjectData
    {
        public string ProjectName { get; set; } = string.Empty;
        public string? DutImagePath { get; set; }
        public string? ActiveProbeName { get; set; }

        public InstrumentSettings InstrumentConfig { get; set; }
        public ScanSettings ScanConfig { get; set; }

        // 测量项列表
        public List<string> MeasurementNames { get; set; } = new List<string>();

        // ====================================================================
        // **核心修正：添加校准参数定义，解决 CS1061 报错**
        // ====================================================================
        public bool IsCalibrated { get; set; } = false;

        public double MatrixM11 { get; set; } = 1.0;
        public double MatrixM12 { get; set; } = 0.0;
        public double MatrixM21 { get; set; } = 0.0;
        public double MatrixM22 { get; set; } = 1.0;
        public double RotateAngle { get; set; } = 0.0;
        public double OffsetX { get; set; } = 0.0;
        public double OffsetY { get; set; } = 0.0;
        

        public ProjectData()
        {
            InstrumentConfig = new InstrumentSettings();
            ScanConfig = new ScanSettings();
        }
    }
}