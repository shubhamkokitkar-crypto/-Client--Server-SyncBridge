using System.Data;
using System.Windows;

namespace WpfTcpServer
{
    public partial class StudentsWindow : Window
    {
        public StudentsWindow(DataTable table)
        {
            InitializeComponent();
            dgStudents.ItemsSource = table.DefaultView;
        }

        public void UpdateTable(DataTable table)
        {
            dgStudents.ItemsSource = null;
            dgStudents.ItemsSource = table.DefaultView;
        }
    }
}