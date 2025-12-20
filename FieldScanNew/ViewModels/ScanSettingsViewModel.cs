using FieldScanNew.Infrastructure;
using FieldScanNew.Models;
using System.Collections.Generic;

namespace FieldScanNew.ViewModels
{
    public class ScanSettingsViewModel : ViewModelBase, IStepViewModel
    {
        public string DisplayName => "3. 高级扫描设置";

        private InstrumentSettings _settings;
        public InstrumentSettings Settings
        {
            get => _settings;
            set
            {
                if (_settings != value)
                {
                    _settings = value;
                    OnPropertyChanged();
                    // 刷新所有显示属性
                    OnPropertyChanged(nameof(CenterFreqDisplay));
                    OnPropertyChanged(nameof(SelectedCenterUnit));
                    OnPropertyChanged(nameof(SpanDisplay));
                    OnPropertyChanged(nameof(SelectedSpanUnit));
                    OnPropertyChanged(nameof(RbwDisplay));
                    OnPropertyChanged(nameof(SelectedRbwUnit));
                    OnPropertyChanged(nameof(VbwDisplay));
                    OnPropertyChanged(nameof(SelectedVbwUnit));
                }
            }
        }

        public List<string> FrequencyUnits { get; } = new List<string> { "Hz", "KHz", "MHz", "GHz" };

        public ScanSettingsViewModel(InstrumentSettings settings)
        {
            _settings = settings;
        }

        private double GetMultiplier(string unit)
        {
            return unit switch { "KHz" => 1e3, "MHz" => 1e6, "GHz" => 1e9, _ => 1.0 };
        }

        // ============ 中心频率 ============
        public double CenterFreqDisplay
        {
            get => _settings.CenterFrequencyHz / GetMultiplier(_settings.CenterFrequencyUnit);
            set { _settings.CenterFrequencyHz = value * GetMultiplier(_settings.CenterFrequencyUnit); OnPropertyChanged(); }
        }
        public string SelectedCenterUnit
        {
            get => _settings.CenterFrequencyUnit;
            set { if (_settings.CenterFrequencyUnit != value) { _settings.CenterFrequencyUnit = value; OnPropertyChanged(); OnPropertyChanged(nameof(CenterFreqDisplay)); } }
        }

        // ============ Span ============
        public double SpanDisplay
        {
            get => _settings.SpanHz / GetMultiplier(_settings.SpanUnit);
            set { _settings.SpanHz = value * GetMultiplier(_settings.SpanUnit); OnPropertyChanged(); }
        }
        public string SelectedSpanUnit
        {
            get => _settings.SpanUnit;
            set { if (_settings.SpanUnit != value) { _settings.SpanUnit = value; OnPropertyChanged(); OnPropertyChanged(nameof(SpanDisplay)); } }
        }

        // ============ RBW (新增) ============
        public double RbwDisplay
        {
            get
            {
                if (_settings.RbwHz <= 0) return -1; // Auto
                return _settings.RbwHz / GetMultiplier(_settings.RbwUnit);
            }
            set
            {
                if (value <= 0) _settings.RbwHz = -1; // Auto
                else _settings.RbwHz = value * GetMultiplier(_settings.RbwUnit);
                OnPropertyChanged();
            }
        }
        public string SelectedRbwUnit
        {
            get => _settings.RbwUnit;
            set { if (_settings.RbwUnit != value) { _settings.RbwUnit = value; OnPropertyChanged(); OnPropertyChanged(nameof(RbwDisplay)); } }
        }

        // ============ VBW (新增) ============
        public double VbwDisplay
        {
            get
            {
                if (_settings.VbwHz <= 0) return -1;
                return _settings.VbwHz / GetMultiplier(_settings.VbwUnit);
            }
            set
            {
                if (value <= 0) _settings.VbwHz = -1;
                else _settings.VbwHz = value * GetMultiplier(_settings.VbwUnit);
                OnPropertyChanged();
            }
        }
        public string SelectedVbwUnit
        {
            get => _settings.VbwUnit;
            set { if (_settings.VbwUnit != value) { _settings.VbwUnit = value; OnPropertyChanged(); OnPropertyChanged(nameof(VbwDisplay)); } }
        }
    }
}