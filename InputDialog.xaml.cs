using System.Windows;

namespace ZerodhaOxySocket
{
    public partial class InputDialog : Window
    {
        public string Answer { get; private set; } = "";
        public InputDialog(string question)
        {
            InitializeComponent();
            lblQuestion.Text = question;
            txtAnswer.Focus();
        }
        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            Answer = txtAnswer.Text.Trim();
            DialogResult = true;
        }
    }
}
