using System.Windows;

namespace FieldScanNew.Views
{
    public partial class QbcParamsDialog : Window
    {
        public double ErrorVal { get; private set; }
        public int KVal { get; private set; }
        public double InitRatioVal { get; private set; }
        public double StdDevCoefVal { get; private set; }

        public QbcParamsDialog(double defaultError = 0.15, int defaultK = 15, double defaultInitRatio = 0.1, double defaultStdDevCoef = 0.2)
        {
            InitializeComponent();
            TxtError.Text = defaultError.ToString("F2");
            TxtK.Text = defaultK.ToString();
            TxtInitRatio.Text = defaultInitRatio.ToString("F2");
            TxtStdDevCoef.Text = defaultStdDevCoef.ToString("F2");
        }

        private void BtnConfirm_Click(object sender, RoutedEventArgs e)
        {
            if (double.TryParse(TxtError.Text, out double err) && 
                int.TryParse(TxtK.Text, out int k) &&
                double.TryParse(TxtInitRatio.Text, out double initRatio) &&
                double.TryParse(TxtStdDevCoef.Text, out double stdDevCoef))
            {
                ErrorVal = err;
                KVal = k;
                InitRatioVal = initRatio;
                StdDevCoefVal = stdDevCoef;
                DialogResult = true;
                Close();
            }
            else
            {
                MessageBox.Show("输入格式错误，请输入有效的数字！", "错误");
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}