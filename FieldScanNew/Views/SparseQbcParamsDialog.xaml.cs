using System;
using System.Windows;

namespace FieldScanNew.Views
{
    public partial class SparseQbcParamsDialog : Window
    {
        private readonly int _maxSampleLimit;

        public int SampleLimitVal { get; private set; }
        public double InitRatioVal { get; private set; }

        public SparseQbcParamsDialog(string modeName, int maxSampleLimit, int defaultSampleLimit, double defaultInitRatio)
        {
            InitializeComponent();
            _maxSampleLimit = Math.Max(1, maxSampleLimit);

            Title = modeName + "参数设置";
            TxtHeader.Text = modeName + "参数配置";
            TxtHint.Text = "说明: 采样点上限范围 1~" + _maxSampleLimit + "；初始贪婪比例范围 0~1。";

            TxtSampleLimit.Text = Math.Max(1, Math.Min(defaultSampleLimit, _maxSampleLimit)).ToString();
            TxtInitRatio.Text = defaultInitRatio.ToString("F2");
        }

        private void BtnConfirm_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(TxtSampleLimit.Text, out int sampleLimit))
            {
                MessageBox.Show("采样点上限请输入有效整数。", "提示");
                return;
            }

            if (!double.TryParse(TxtInitRatio.Text, out double initRatio))
            {
                MessageBox.Show("初始贪婪比例请输入有效数字。", "提示");
                return;
            }

            sampleLimit = Math.Max(1, Math.Min(sampleLimit, _maxSampleLimit));
            initRatio = Math.Max(0.01, Math.Min(initRatio, 1.0));

            SampleLimitVal = sampleLimit;
            InitRatioVal = initRatio;
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
