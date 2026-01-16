using FieldScanNew.Infrastructure;

namespace FieldScanNew.Models
{
    public class ScanSettings : ViewModelBase
    {
        private float _startX;
        public float StartX { get => _startX; set { _startX = value; OnPropertyChanged(); } }

        private float _startY;
        public float StartY { get => _startY; set { _startY = value; OnPropertyChanged(); } }

        private float _stopX;
        public float StopX { get => _stopX; set { _stopX = value; OnPropertyChanged(); } }

        private float _stopY;
        public float StopY { get => _stopY; set { _stopY = value; OnPropertyChanged(); } }

        private int _numX;
        public int NumX { get => _numX; set { _numX = value; OnPropertyChanged(); } }

        private int _numY;
        public int NumY { get => _numY; set { _numY = value; OnPropertyChanged(); } }

        private float _scanHeightZ;
        public float ScanHeightZ { get => _scanHeightZ; set { _scanHeightZ = value; OnPropertyChanged(); } }

        private float _scanAngleR;
        public float ScanAngleR { get => _scanAngleR; set { _scanAngleR = value; OnPropertyChanged(); } }

        // ================================================================
        // **新增：扫描分量选择**
        // ================================================================
        private bool _scanHx = true; // 默认勾选 X 分量
        public bool ScanHx { get => _scanHx; set { _scanHx = value; OnPropertyChanged(); } }

        private bool _scanHy = false;
        public bool ScanHy { get => _scanHy; set { _scanHy = value; OnPropertyChanged(); } }
    }
}