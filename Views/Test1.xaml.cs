using System.Windows.Controls;
using MultiSerialTool.ViewModels;

namespace MultiSerialTool.Views
{
    /// <summary>
    /// Test1.xaml 的交互逻辑。
    /// </summary>
    public partial class Test1 : System.Windows.Controls.UserControl
    {
        public Test1()
        {
            InitializeComponent();
            DataContext = new Test1ViewModel();
        }
    }
}
