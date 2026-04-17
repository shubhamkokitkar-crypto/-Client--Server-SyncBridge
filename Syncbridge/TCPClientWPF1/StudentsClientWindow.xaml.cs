using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace TCPClientWPF1
{
    public partial class StudentsClientWindow : Window
    {
        public StudentsClientWindow(List<Dictionary<string, string>> rows)
        {
            InitializeComponent();
            LoadTable(rows);
        }

        public void UpdateStudents(List<Dictionary<string, string>> rows)
        {
            LoadTable(rows);
        }

        private void LoadTable(List<Dictionary<string, string>> rows)
        {
            // ✅ clear table when empty rows passed
            if (rows == null || rows.Count == 0)
            {
                dgStudents.ItemsSource = null;
                dgStudents.Columns.Clear();
                dgStudents.Items.Refresh();
                return;
            }

            var newKeys = rows[0].Keys.ToList();


            var existingKeys = dgStudents.Columns
                                         .Cast<DataGridColumn>()
                                         .Select(c => c.Header.ToString())
                                         .ToList();

            // 🔥 Rebuild columns only if changed
            if (!existingKeys.SequenceEqual(newKeys))
            {
                dgStudents.Columns.Clear();

                foreach (var key in newKeys)
                {
                    DataGridTextColumn col = new DataGridTextColumn
                    {
                        Header = key,
                        Binding = new Binding($"[{key}]")
                    };

                    dgStudents.Columns.Add(col);
                }
            }

            // 🔥 Refresh latest rows
            dgStudents.ItemsSource = null;
            dgStudents.ItemsSource = rows.ToList();
            dgStudents.Items.Refresh();
        }
    }
}