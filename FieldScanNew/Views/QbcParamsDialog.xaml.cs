using System.Windows;

namespace FieldScanNew.Views
{
    public partial class QbcParamsDialog : Window
    {
        public double ErrorVal { get; private set; }
        public int KVal { get; private set; }

        public QbcParamsDialog(double defaultError = 0.5, int defaultK = 10)
        {
            InitializeComponent();
            TxtError.Text = defaultError.ToString("F2");
            TxtK.Text = defaultK.ToString();
        }

        private void BtnConfirm_Click(object sender, RoutedEventArgs e)
        {
            if (double.TryParse(TxtError.Text, out double err) && int.TryParse(TxtK.Text, out int k))
            {
                ErrorVal = err;
                KVal = k;
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