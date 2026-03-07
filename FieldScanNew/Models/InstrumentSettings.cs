using FieldScanNew.Infrastructure;
using System.Collections.Generic; // 关键：必须引用此命名空间以使用 List

namespace FieldScanNew.Models
{
    public class InstrumentSettings : ViewModelBase
    {
        private string _ipAddress = "192.168.1.51";
        public string IpAddress { get => _ipAddress; set { _ipAddress = value; OnPropertyChanged(); } }

        private int _port = 5025;
        public int Port { get => _port; set { _port = value; OnPropertyChanged(); } }

        // 频率参数
        private double _centerFrequencyHz = 1e9;
        public double CenterFrequencyHz { get => _centerFrequencyHz; set { _centerFrequencyHz = value; OnPropertyChanged(); } }

        private string _centerFrequencyUnit = "GHz";
        public string CenterFrequencyUnit { get => _centerFrequencyUnit; set { _centerFrequencyUnit = value; OnPropertyChanged(); } }

        private double _spanHz = 100e6;
        public double SpanHz { get => _spanHz; set { _spanHz = value; OnPropertyChanged(); } }

        private string _spanUnit = "MHz";
        public string SpanUnit { get => _spanUnit; set { _spanUnit = value; OnPropertyChanged(); } }

        private int _points = 1001;
        public int Points { get => _points; set { _points = value; OnPropertyChanged(); } }

        // 幅度参数
        private double _referenceLevelDb = 0;
        public double ReferenceLevelDb { get => _referenceLevelDb; set { _referenceLevelDb = value; OnPropertyChanged(); } }

        // 带宽参数 (RBW/VBW)
        // 默认 -1 代表 Auto
        private double _rbwHz = -1;
        public double RbwHz { get => _rbwHz; set { _rbwHz = value; OnPropertyChanged(); } }

        private string _rbwUnit = "KHz"; // 默认单位
        public string RbwUnit { get => _rbwUnit; set { _rbwUnit = value; OnPropertyChanged(); } }

        private double _vbwHz = -1;
        public double VbwHz { get => _vbwHz; set { _vbwHz = value; OnPropertyChanged(); } }

        private string _vbwUnit = "KHz"; // 默认单位
        public string VbwUnit { get => _vbwUnit; set { _vbwUnit = value; OnPropertyChanged(); } }

        // ==========================================
        // 核心修复：添加 ProbePoints 属性
        // ==========================================
        private List<ProbePoint> _probePoints = new List<ProbePoint>();

        /// <summary>
        /// 存储探头校准因子的列表 (频率 Hz, 因子 dB)
        /// </summary>
        public List<ProbePoint> ProbePoints
        {
            get => _probePoints;
            set { _probePoints = value; OnPropertyChanged(); }
        }
    }
}