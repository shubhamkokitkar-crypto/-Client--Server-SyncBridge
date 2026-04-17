using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Net.WebSockets;
using System.Threading.Tasks;

namespace TCPClientWPF1
{
    public partial class MainWindow : Window
    {
        private TcpClient client;
        private NetworkStream stream;
        private Thread clientThread;

        private string filePath;
        private string Ip;
        private int Port;
        private FileSystemWatcher watcher;
        private bool isLoadingFiles = false;
        private bool isChangingSelection = false;

        private bool isConnected = false;
        private bool isManualRefresh = false;
        StudentsClientWindow studentsWindow;
        ClientWebSocket ws;
        CancellationTokenSource wsCts = new CancellationTokenSource();


        public MainWindow()
        {
            InitializeComponent();
            LoadIPAddresses();
            LoadDataFiles();
            StartFileWatcher();
            cmbPort.SelectedIndex = 0;

            cmbDataFiles.SelectionChanged += CmbDataFiles_SelectionChanged;
            cmbTables.SelectionChanged += CmbTables_SelectionChanged;
            // 🔥 NEW: Single Clear button
            btnClearTable.Content = "Clear Table";

        }
       
        #region CONNECT BUTTON

        // =============================
        // LOAD IP ADDRESSES
        // =============================
        private void LoadIPAddresses()
        {
            cmbIP.Items.Clear();
            cmbIP.Items.Add("127.0.0.1");        // Same PC
            cmbIP.Items.Add("10.252.255.213");   // LAN IP (change if needed)
            cmbIP.SelectedIndex = 0;
        }

        async Task ConnectWebSocket()
        {
            try
            {
                if (ws != null && ws.State == WebSocketState.Open)
                    return;
                ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri("ws://127.0.0.1:55001/"), CancellationToken.None);

                //Log("WebSocket Connected");

                // ✅ AUTO LOAD TABLES
                string msg = "wpfClient|getSqlTables";
                byte[] buffer = Encoding.UTF8.GetBytes(msg);

                await ws.SendAsync(new ArraySegment<byte>(buffer),
                    WebSocketMessageType.Text, true, CancellationToken.None);

                _ = Task.Run(ReceiveWebSocket);
            }
            catch (Exception ex)
            {
                Log("WS Error: " + ex.Message);
            }
        }
        async Task ReceiveWebSocket()
        {
            var buffer = new byte[8192];

            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), wsCts.Token);

                string message = Encoding.UTF8.GetString(buffer, 0, result.Count);

                try
                {
                    var doc = JsonDocument.Parse(message);
                    string type = doc.RootElement.GetProperty("type").GetString();

                    // ✅ TABLE LIST
                    if (type == "sqlTableList")
                    {
                        var tables = doc.RootElement.GetProperty("tables");

                        Dispatcher.Invoke(() =>
                        {
                            cmbTables.Items.Clear();

                            // ✅ Default option
                            cmbTables.Items.Add("Select SQL Table");

                            // ✅ Separator
                            cmbTables.Items.Add("----------------");

                            // ✅ Add real tables
                            foreach (var t in tables.EnumerateArray())
                            {
                                cmbTables.Items.Add(t.GetString());
                            }

                            // ✅ Set default selection
                            cmbTables.SelectedIndex = 0;
                        });
                    }

                    // ✅ TABLE DATA
                    else if (type == "studentTableData")
                    {
                        // ✅ Only update on manual fetch/refresh
                        if (!isManualRefresh)
                            continue;

                        var data = doc.RootElement.GetProperty("data");

                        var rows = new List<Dictionary<string, string>>();

                        foreach (var row in data.EnumerateArray())
                        {
                            var dict = new Dictionary<string, string>();

                            foreach (var col in row.EnumerateObject())
                            {
                                dict[col.Name] = col.Value.ToString();
                            }

                            rows.Add(dict);
                        }

                        Dispatcher.Invoke(() =>
                        {
                            if (studentsWindow == null || !studentsWindow.IsVisible)
                            {
                                studentsWindow = new StudentsClientWindow(rows);
                                studentsWindow.Show();
                            }
                            else
                            {
                                studentsWindow.UpdateStudents(rows);
                            }
                        });

                        // ✅ Reset flag after update
                        isManualRefresh = false;
                    }
                }
                catch
                {
                    Log("WS: " + message);
                }
            }
        }

        private void StartFileWatcher()
        {
            string dataFolder = @"C:\Users\Shubham\source\repos\TcpWebBridge\TcpWebBridge\Data";

            if (!Directory.Exists(dataFolder))
                return;

            watcher = new FileSystemWatcher(dataFolder);

            watcher.Filter = "*.*";   // ✅ ADD
            watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite; // ✅ ADD

            watcher.EnableRaisingEvents = true;

            watcher.Created += (s, e) => Dispatcher.Invoke(LoadDataFiles);
            watcher.Deleted += (s, e) => Dispatcher.Invoke(LoadDataFiles);
            watcher.Renamed += (s, e) => Dispatcher.Invoke(LoadDataFiles);
            watcher.Changed += (s, e) => { }; // optional
        }

        private void LoadDataFiles()
        {
            string dataFolder = @"C:\Users\Shubham\source\repos\TcpWebBridge\TcpWebBridge\Data";

            if (!Directory.Exists(dataFolder))
                return;

            isLoadingFiles = true;

            string previouslySelected = cmbDataFiles.SelectedItem?.ToString();

            cmbDataFiles.Items.Clear();

            cmbDataFiles.Items.Add("Select File");
            cmbDataFiles.Items.Add("----------------");

            var files = Directory.GetFiles(dataFolder)
                .Where(f =>
                    !Path.GetFileName(f).StartsWith("~$") &&
                    (
                        f.EndsWith(".csv") ||
                        f.EndsWith(".txt") ||
                        f.EndsWith(".json") ||
                        f.EndsWith(".xlsx") ||
                        f.EndsWith(".xls")
                    ))
                .Select(f => Path.GetFileName(f))
                .ToList();

            foreach (var file in files)
            {
                cmbDataFiles.Items.Add(file);
            }

            if (!string.IsNullOrEmpty(previouslySelected) &&
                files.Contains(previouslySelected))
            {
                cmbDataFiles.SelectedItem = previouslySelected;
            }
            else
            {
                cmbDataFiles.SelectedIndex = 0;
            }

            isLoadingFiles = false;
        }
        private void CmbDataFiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isLoadingFiles || isChangingSelection)
                return;

            if (cmbDataFiles.SelectedItem == null)
                return;

            string selected = cmbDataFiles.SelectedItem.ToString();

            if (selected == "Select File" || selected == "----------------")
            {
                cmbDataFiles.SelectedIndex = 0;
                return;
            }

            // ✅ only deselect SQL, no auto refresh
            isChangingSelection = true;
            cmbTables.SelectedIndex = 0;
            isChangingSelection = false;
        }
        private void CmbTables_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isChangingSelection)
                return;

            if (cmbTables.SelectedItem == null)
                return;

            string selected = cmbTables.SelectedItem.ToString();

            if (selected == "Select SQL Table" || selected == "----------------")
            {
                return;
            }

            // ✅ only deselect file, no auto refresh
            isChangingSelection = true;
            cmbDataFiles.SelectedIndex = 0;
            isChangingSelection = false;
        }
        private void btnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (isConnected)
            {
                MessageBox.Show("Already connected to server.");
                return;
            }

            Ip = cmbIP.Text.Trim();

            if (string.IsNullOrWhiteSpace(Ip))
            {
                MessageBox.Show("Please enter IP address.");
                return;
            }

            if (cmbPort.SelectedItem == null)
            {
                MessageBox.Show("Please select a port.",
                    "Port Required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            Port = int.Parse(((ComboBoxItem)cmbPort.SelectedItem).Content.ToString());

            btnConnect.IsEnabled = false;
            

            clientThread = new Thread(ConnectToServer);
            clientThread.IsBackground = true;
            clientThread.Start();
        }

        #endregion

        #region CONNECT TO SERVER

        private void ConnectToServer()
        {
            try
            {
                Log("Trying to connect...");

                client = new TcpClient();
                client.Connect(IPAddress.Parse(Ip), Port);

                if (!client.Connected)
                {
                    Log("Connection failed.");
                    return;
                }

                stream = client.GetStream();
                isConnected = true;
                Dispatcher.Invoke(async () =>
                {
                    await ConnectWebSocket();
                });

                Log($"Connected to server {Ip}:{Port}");

                byte[] buffer = new byte[8192];

                while (client != null && client.Connected)
                {
                    int bytesRead = stream.Read(buffer, 0, buffer.Length);

                    if (bytesRead <= 0)
                    {
                        Log("Server disconnected.");
                        break;
                    }

                    string message = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                    // ✅ HANDLE DATABASE STATUS (ADD THIS BLOCK)
                    if (message.Contains("\"type\":\"dbStatus\""))
                    {
                        try
                        {
                            var doc = JsonDocument.Parse(message);
                            string status = doc.RootElement.GetProperty("status").GetString();

                            if (status == "connected")
                            {
                                Log("Database connected successfully.");
                            }
                            else if (status == "disconnected")
                            {
                                Log("Database disconnected.");
                            }
                        }
                        catch
                        {
                            Log("DB Status Parse Error");
                        }

                        continue; // ✅ IMPORTANT (stop further processing)
                    }
                    // ✅ HANDLE WEB CLIENT STATUS (ADD THIS BLOCK)
                    if (message.Contains("\"type\":\"webStatus\""))
                    {
                        try
                        {
                            var doc = JsonDocument.Parse(message);
                            string status = doc.RootElement.GetProperty("status").GetString();

                            if (status == "connected")
                            {
                                Log("WEB CLIENT CONNECTED");
                            }
                            else if (status == "disconnected")
                            {
                                Log("WEB CLIENT DISCONNECTED");
                            }
                        }
                        catch
                        {
                            Log("Web status parse error");
                        }

                        continue; // ✅ IMPORTANT
                    }

                    // ✅ ADD STUDENT TABLE HANDLER HERE
                    if (message.Contains("\"type\":\"studentTableData\""))
                    {
                        try
                        {
                            // ✅ Ignore auto updates from server
                            if (!isManualRefresh)
                                continue;

                            var doc = JsonDocument.Parse(message);
                            var data = doc.RootElement.GetProperty("data");

                            var rows = new List<Dictionary<string, string>>();

                            foreach (var row in data.EnumerateArray())
                            {
                                var dict = new Dictionary<string, string>();

                                foreach (var col in row.EnumerateObject())
                                {
                                    dict[col.Name] = col.Value.ToString();
                                }

                                rows.Add(dict);
                            }

                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                if (studentsWindow == null || !studentsWindow.IsVisible)
                                {
                                    studentsWindow = new StudentsClientWindow(rows);
                                    studentsWindow.Show();
                                }
                                else
                                {
                                    studentsWindow.UpdateStudents(rows);
                                }
                            }));

                            // ✅ Reset after manual update
                            isManualRefresh = false;

                            continue;
                        }
                        catch
                        {
                        }
                    }
                    // ✅ HANDLE DATABASE MESSAGES CLEANLY
                    if (message.Contains("Database connected successfully") ||
                        message.Contains("Database disconnected"))
                    {
                        Log(message);   // ✅ clean log (no "Server:" prefix)
                    }
                    else if (!message.StartsWith("refreshStudents|"))
                    {
                        Log("Server: " + message);

                        // ✅ only reply to normal chat message
                        if (!message.Contains("WEB CLIENT") &&
                            !message.Contains("\"type\":\"webStatus\"") &&
                            !message.Contains("\"type\":\"dbStatus\""))
                        {
                            HandleAutoReply(message);
                        }
                    }
                }
            }
            catch (SocketException ex)
            {
                Log("Unable to connect to server: " + ex.Message);
            }
            catch (Exception ex)
            {
                Log("Error: " + ex.Message);
            }
            finally
            {
                Disconnect();
            }
        }

        #endregion

        #region AUTO REPLY LOGIC

        private void HandleAutoReply(string message)
        {
            DateTime parsedDate;

            bool isAutoReply = DateTime.TryParseExact(
                message,
                "yyyy-MM-dd HH:mm:ss",
                null,
                System.Globalization.DateTimeStyles.None,
                out parsedDate);

            if (!isAutoReply && stream != null && stream.CanWrite)
            {
                try
                {
                    string reply = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    byte[] data = Encoding.UTF8.GetBytes(reply);
                    stream.Write(data, 0, data.Length);

                    Log("Client: " + reply);
                }
                catch
                {
                    Log("Failed to send auto reply.");
                }
            }
        }

        #endregion

        #region SEND BUTTON

        private void btnSend_Click(object sender, RoutedEventArgs e)
        {
            if (!isConnected || stream == null || !stream.CanWrite)
            {
                MessageBox.Show("Not connected to server.");
                return;
            }

            string message = txtMessage.Text.Trim();

            if (string.IsNullOrWhiteSpace(message))
                return;

            try
            {
                byte[] data = Encoding.UTF8.GetBytes(message);
                stream.Write(data, 0, data.Length);

                Log("Client: " + message);
                txtMessage.Clear();
            }
            catch (Exception ex)
            {
                Log("Send failed: " + ex.Message);
            }
        }

        private async void btnFetchTable_Click(object sender, RoutedEventArgs e)
        {
            if (ws == null || ws.State != WebSocketState.Open)
            {
                MessageBox.Show("WebSocket not connected.");
                return;
            }

            if (cmbTables.SelectedItem == null ||
                cmbTables.SelectedItem.ToString() == "Select SQL Table" ||
                cmbTables.SelectedItem.ToString() == "----------------")
            {
                MessageBox.Show("Please select a valid SQL table.");
                return;
            }

            string table = cmbTables.SelectedItem.ToString();

            // ✅ IMPORTANT
            isManualRefresh = true;

            string msg = $"fetchSqlTable|{table}";

            byte[] buffer = Encoding.UTF8.GetBytes(msg);

            await ws.SendAsync(new ArraySegment<byte>(buffer),
                WebSocketMessageType.Text,
                true,
                CancellationToken.None);
        }

        private void btnFetchCsv_Click(object sender, RoutedEventArgs e)
        {
            if (!isConnected || stream == null)
            {
                MessageBox.Show("Not connected to server.");
                return;
            }

            if (cmbDataFiles.SelectedItem == null ||
               cmbDataFiles.SelectedItem.ToString() == "Select File" ||
               cmbDataFiles.SelectedItem.ToString() == "----------------")
            {
                MessageBox.Show("Please select a valid file.");
                return;
            }

            try
            {
                string fileName = cmbDataFiles.SelectedItem.ToString();
                isManualRefresh = true;

                var request = new
                {
                    type = "getFileStudents",
                    file = fileName
                };

                string json = JsonSerializer.Serialize(request);

                byte[] data = Encoding.UTF8.GetBytes(json);
                stream.Write(data, 0, data.Length);

                //Log("Client: Requesting table for " + fileName);
            }
            catch (Exception ex)
            {
                Log("Fetch CSV failed: " + ex.Message);
            }
        }
        #endregion

        #region LOG FUNCTION

        private void Log(string message)
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "WpfTcpLogs");

            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            filePath = Path.Combine(folder, "ClientLogs.txt");

            string logText =
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                + " | "
                + message
                + Environment.NewLine;

            try
            {
                File.AppendAllText(filePath, logText);
            }
            catch
            {
                // Ignore logging file errors
            }

            Dispatcher.Invoke(() =>
            {
                txtChat.AppendText(logText);
                txtChat.ScrollToEnd();
            });
        }

        #endregion

        #region LOG BUTTONS

        private void btnOpenLog_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = filePath,
                    UseShellExecute = true
                });
            }
            else
            {
                MessageBox.Show("Log file not found!");
            }
        }

        private void btnClearLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                {
                    File.WriteAllText(filePath, string.Empty);   // Clear only file
                    MessageBox.Show("Log file cleared successfully.");
                }
                else
                {
                    MessageBox.Show("Log file not found!");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error clearing log: " + ex.Message);
            }
        }

        #endregion

        #region DISCONNECT CLEANUP

        private void Disconnect()
        {
            isConnected = false;

            try { stream?.Close(); } catch { }
            try { client?.Close(); } catch { }

            Dispatcher.Invoke(() =>
            {
                btnConnect.IsEnabled = true;
            });

            Log("Disconnected.");
        }

        private void btnClearTable_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (studentsWindow != null && studentsWindow.IsVisible)
                {
                    studentsWindow.UpdateStudents(new List<Dictionary<string, string>>());

                    Log("Table cleared");
                }
                else
                {
                    MessageBox.Show("No table window open.");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Clear error: " + ex.Message);
            }
        }
        private async void btnRefresh_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 🔹 CASE 1: SQL TABLE SELECTED (WebSocket)
                if (cmbTables.SelectedItem != null &&
                    cmbTables.SelectedItem.ToString() != "Select SQL Table" &&
                    cmbTables.SelectedItem.ToString() != "----------------")
                {
                    if (ws == null || ws.State != WebSocketState.Open)
                    {
                        MessageBox.Show("WebSocket not connected.");
                        return;
                    }

                    string table = cmbTables.SelectedItem.ToString();
                    isManualRefresh = true;

                    string msg = $"fetchSqlTable|{table}";
                    byte[] buffer = Encoding.UTF8.GetBytes(msg);

                    await ws.SendAsync(new ArraySegment<byte>(buffer),
                        WebSocketMessageType.Text,
                        true,
                        CancellationToken.None);

                    Log("Refreshed SQL Table: " + table);
                    return;
                }

                // 🔹 CASE 2: FILE SELECTED (TCP)
                if (cmbDataFiles.SelectedItem != null &&
                    cmbDataFiles.SelectedItem.ToString() != "Select File" &&
                    cmbDataFiles.SelectedItem.ToString() != "----------------")
                {
                    if (!isConnected || stream == null)
                    {
                        MessageBox.Show("Not connected to server.");
                        return;
                    }

                    string fileName = cmbDataFiles.SelectedItem.ToString();
                    isManualRefresh = true;

                    var request = new
                    {
                        type = "refreshStudents",
                        file = fileName
                    };

                    string json = JsonSerializer.Serialize(request);

                    byte[] data = Encoding.UTF8.GetBytes(json);
                    stream.Write(data, 0, data.Length);

                    Log("Refreshed File: " + fileName);
                    return;
                }

                // 🔹 NOTHING SELECTED
                MessageBox.Show("Please select SQL table or file to refresh.");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Refresh error: " + ex.Message);
            }
        }
        protected override void OnClosed(EventArgs e)
        {
            Disconnect();
            base.OnClosed(e);
        }

        #endregion
    }
}


