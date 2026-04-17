using System.Collections.Generic;
using System.Windows;

namespace TCPClientWPF1
{
    public partial class StudentsWindow : Window
    {
        public StudentsWindow(List<Student> students)
        {
            InitializeComponent();
            dgStudents.ItemsSource = students;
        }
    }
}