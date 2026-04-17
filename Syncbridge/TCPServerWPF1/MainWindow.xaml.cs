using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Collections.Generic;
using System.Linq;
using System.Data.SqlClient;
using System.Text.Json;
using ExcelDataReader;
using System.Data;

namespace WpfTcpServer
{
    public partial class MainWindow : Window
    {
        TcpListener server;
        NetworkStream stream;
        Thread serverThread;
        string filePath;
        string serverIp;
        int serverPort;
        TcpClient client;
        string folder;
        string connectionString = @"Server=LAPTOP-31C6UJES\SQLEXPRESS;Database=SemiconductorDB;Trusted_Connection=True;";
        object logLock = new object();
        string liveSessionLogs = "";
        DateTime lastFileEvent = DateTime.MinValue;
        StudentsWindow studentsWindow;   
        HttpListener wsListener;
        List<WebSocket> wsClients = new List<WebSocket>();
        CancellationTokenSource cts = new CancellationTokenSource();
        FileSystemWatcher dataWatcher;
        SqlConnection sqlConnection;


        volatile bool isServerRunning = false;


        

        public MainWindow()
        {
            InitializeComponent();
            LoadIPAddresses();
            LoadDataFiles(); 
            cmbPort.SelectedIndex = 0;
            folder = @"C:\Users\Shubham\source\repos\TcpWebBridge\TcpWebBridge\Logs";
            LoadSqlTables();

            // 🔥 NEW: Single Clear button
            btnClearTable.Content = "Clear Table";

            cmbDataFiles.SelectionChanged += CmbDataFiles_SelectionChanged;
            cmbSqlTables.SelectionChanged += CmbSqlTables_SelectionChanged;


        }
        private void LoadDataFiles()
        {
            string dataFolder = @"C:\Users\Shubham\source\repos\TcpWebBridge\TcpWebBridge\Data";

            if (!Directory.Exists(dataFolder))
                return;

            cmbDataFiles.Items.Clear();

            // ✅ Default option
            cmbDataFiles.Items.Add("Select File");

            // ✅ Separator
            cmbDataFiles.Items.Add("----------------");

            var files = Directory.GetFiles(dataFolder)
          .Where(f =>
             !Path.GetFileName(f).StartsWith("~$") &&   // 🔥 ADD THIS
               (
                     f.EndsWith(".csv") ||
                     f.EndsWith(".txt") ||
                     f.EndsWith(".json") ||
                     f.EndsWith(".xlsx") ||
                     f.EndsWith(".xls")
                )
            )
                 .Select(f => Path.GetFileName(f))
                 .ToList();

            // ✅ Add all files
            foreach (var file in files)
            {
                cmbDataFiles.Items.Add(file);
            }

            // Default selection
            cmbDataFiles.SelectedIndex = 0;
        }
        private List<string> GetSqlTableNames()
        {
            var tables = new List<string>();

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();

                DataTable dt = conn.GetSchema("Tables");

                foreach (DataRow row in dt.Rows)
                {
                    string tableName = row["TABLE_NAME"].ToString();
                    tables.Add(tableName);
                }
            }

            return tables;
        }
        private List<Dictionary<string, string>> FetchTableData(string tableName)
        {
            var list = new List<Dictionary<string, string>>();

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();

                string query = $"SELECT * FROM [{tableName}]";

                SqlCommand cmd = new SqlCommand(query, conn);
                SqlDataReader reader = cmd.ExecuteReader();

                while (reader.Read())
                {
                    var row = new Dictionary<string, string>();

                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        row[reader.GetName(i)] = reader[i]?.ToString();
                    }

                    list.Add(row);
                }
            }

            return list;
        }
        private void CmbDataFiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmbDataFiles.SelectedItem == null)
                return;

            string selected = cmbDataFiles.SelectedItem.ToString();

            if (selected == "Select File" || selected == "----------------")
            {
                cmbDataFiles.SelectedIndex = 0;
                return;
            }

            // ✅ Deselect SQL when file selected
            if (cmbSqlTables != null)
            {
                cmbSqlTables.SelectedIndex = 0;
            }
        }
        private void CmbSqlTables_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmbSqlTables.SelectedItem == null)
                return;

            string selected = cmbSqlTables.SelectedItem.ToString();

            if (selected == "Select SQL Table" || selected == "----------------")
            {
                return;
            }

            // ✅ Deselect file when SQL selected
            if (cmbDataFiles != null)
            {
                cmbDataFiles.SelectedIndex = 0;
            }
        }

        // =============================
        // LOAD IP ADDRESSES
        // =============================
        private void LoadIPAddresses()
        {
            cmbIP.Items.Clear();
            cmbIP.Items.Add("127.0.0.1");   // Localhost
            cmbIP.Items.Add("10.252.255.213"); // Your LAN IP (change if needed)
            cmbIP.SelectedIndex = 0;
        }

        void StartCsvWatcher()
        {
            string dataFolder = @"C:\Users\Shubham\source\repos\TcpWebBridge\TcpWebBridge\Data";

            dataWatcher = new FileSystemWatcher(dataFolder);

            dataWatcher.Filter = "*.*";
            dataWatcher.NotifyFilter =
                NotifyFilters.LastWrite |
                NotifyFilters.FileName |
                NotifyFilters.Size |
                NotifyFilters.CreationTime;

            dataWatcher.IncludeSubdirectories = false;

            dataWatcher.Changed += OnDataFileChanged;
            dataWatcher.Created += OnDataFileChanged;
            dataWatcher.Renamed += OnDataFileChanged;
            dataWatcher.Deleted += OnDataFileChanged;

            dataWatcher.EnableRaisingEvents = true;
        }
        private async void OnDataFileChanged(object sender, FileSystemEventArgs e)
        {
            try
            {
                if (e.ChangeType != WatcherChangeTypes.Deleted && !File.Exists(e.FullPath))
                    return;

                if ((DateTime.Now - lastFileEvent).TotalMilliseconds < 500)
                    return;

                lastFileEvent = DateTime.Now;

                await Task.Delay(800);
                if (e.Name.StartsWith("~$"))
                    return;

                DataTable table = null;

                for (int i = 0; i < 3; i++)
                {
                    try
                    {
                        table = ReadAnyFile(e.FullPath);
                        break;
                    }
                    catch
                    {
                        await Task.Delay(500); // wait and retry
                    }
                }
                await Dispatcher.InvokeAsync(() =>
                {
                    // 1️⃣ update Students window automatically
                  //  if (studentsWindow != null && studentsWindow.IsVisible)
                   // {
                   //     studentsWindow.UpdateTable(table);
                    //}

                    // 2️⃣ still send to WebSocket
                    var list = ConvertTableToList(table);

                    var response = new
                    {
                        type = "studentTableData",
                        file = Path.GetFileName(e.FullPath),
                        data = list
                    };

                    string json = JsonSerializer.Serialize(response);
                    BroadcastToWebSockets(json);
                    // ✅ SEND TO TCP CLIENT ALSO
                    if (client != null && client.Connected && stream != null)
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(json);
                        stream.Write(bytes, 0, bytes.Length);
                    }
                });
               // Log($"File updated → {e.Name}");
            }
            catch (Exception ex)
            {
                Log("Watcher error: " + ex.Message);
            }
        }

        private void LoadSqlTables()
        {
            try
            {
                cmbSqlTables.Items.Clear();

                // ✅ Default option
                cmbSqlTables.Items.Add("Select SQL Table");

                // ✅ Separator
                cmbSqlTables.Items.Add("----------------");

                var tables = GetSqlTableNames();

                foreach (var t in tables)
                {
                    cmbSqlTables.Items.Add(t);
                }

                cmbSqlTables.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error loading SQL tables: " + ex.Message);
            }
        }
        private void btnStart_Click(object sender, RoutedEventArgs e)
        {
            serverIp = cmbIP.Text.Trim();
            if (cmbPort.SelectedItem != null)
            {
                serverPort = int.Parse(((ComboBoxItem)cmbPort.SelectedItem).Content.ToString());
            }
            StartCsvWatcher();   // ADD THIS LINE
            // reset cancellation token
            cts = new CancellationTokenSource();

            // restart websocket listener if stopped
            if (wsListener == null || !wsListener.IsListening)
            {
                wsListener = new HttpListener();
                wsListener.Prefixes.Add("http://127.0.0.1:55001/"); // ✅ safe por
                wsListener.Start();  // VERY IMPORTANT: starts the listener

                Task.Run(() => HandleWebSocketConnections(cts.Token));
            }

            serverThread = new Thread(StartServer);
            serverThread.IsBackground = true;
            serverThread.Start();
        }

        async Task HandleWebSocketConnections(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var context = await wsListener.GetContextAsync();
                    if (context.Request.IsWebSocketRequest)
                    {
                        var wsContext = await context.AcceptWebSocketAsync(null);
                        var ws = wsContext.WebSocket;
                        lock (wsClients)
                        {
                            wsClients.Add(ws);
                        }
                      //  Log("WEB CLIENT CONNECTED");

                        // ✅ send JSON status to TCP client
                       
                        string json = "{\"type\":\"status\",\"value\":\"connected\"}";
                        var buffer = Encoding.UTF8.GetBytes(json);
                        var segment = new ArraySegment<byte>(buffer);
                        await ws.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
                        _ = Task.Run(() => HandleWebSocketClient(ws, token));
                    }
                    else
                    {
                        string filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "index.html");
                        if (File.Exists(filePath))
                        {
                            string content = File.ReadAllText(filePath);
                            byte[] buffer = Encoding.UTF8.GetBytes(content);
                            context.Response.ContentType = "text/html";
                            context.Response.ContentLength64 = buffer.Length;
                            await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                            context.Response.Close();
                        }
                        else
                        {
                            context.Response.StatusCode = 404;
                            context.Response.Close();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log("WebSocket error: " + ex.Message);
                }
            }
        }

        async Task HandleWebSocketClient(WebSocket ws, CancellationToken token)
        {
            bool isRealWebUI = false;
            var buffer = new byte[1024];
            while (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                try
                {
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        string message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        if (message == "webUIConnected")
                        {
                            isRealWebUI = true;

                            Log("WEB CLIENT CONNECTED");

                            string tcpJson = JsonSerializer.Serialize(new
                            {
                                type = "webStatus",
                                status = "connected"
                            });

                            SendToTcpClient(tcpJson);

                            continue;
                        }

                        if (message.StartsWith("wpfClient|"))
                        {
                            message = message.Replace("wpfClient|", "");
                        }
                        Console.WriteLine("Received WebSocket message: " + message);

                        // --- Handle getFiles Command (New) ---
                        if (message.StartsWith("getFiles|"))
                        {
                            var parts = message.Split('|');
                            if (parts.Length >= 2)
                            {
                                string targetDate = parts[1];
                                var files = GetLogFilesByDate(targetDate);
                                string json = $"{{\"type\":\"fileList\",\"files\":[{string.Join(",", files.Select(f => $"\"{f}\""))}]}}";
                                await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)), WebSocketMessageType.Text, true, CancellationToken.None);
                            }
                        }
                        else if (message == "getDatabaseFiles")
                        {
                            string dataFolder = @"C:\Users\Shubham\source\repos\TcpWebBridge\TcpWebBridge\Data";

                            var files = Directory.GetFiles(dataFolder)
                                       .Where(f => !Path.GetFileName(f).StartsWith("~$")) // 🔥 ADD
                                       .Select(f => Path.GetFileName(f))
                                       .ToList();

                            string json = JsonSerializer.Serialize(new
                            {
                                type = "dbFileList",
                                files = files
                            });

                            await ws.SendAsync(
                                new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)),
                                WebSocketMessageType.Text,
                                true,
                                CancellationToken.None
                            );
                        }
                        else if (message == "getSqlData")
                        {
                            var data = FetchStudentsFromSql();

                            var response = new
                            {
                                type = "sqlData",
                                data = data
                            };

                            string json = JsonSerializer.Serialize(response);

                            await ws.SendAsync(
                                new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)),
                                WebSocketMessageType.Text,
                                true,
                                CancellationToken.None);
                        }
                        else if (message.StartsWith("fetchDatabaseFile|"))
                        {
                            var parts = message.Split('|');

                            if (parts.Length == 2)
                            {
                                string fileName = parts[1];

                                string dataFolder = @"C:\Users\Shubham\source\repos\TcpWebBridge\TcpWebBridge\Data";
                                string fullPath = Path.Combine(dataFolder, fileName);

                                DataTable table = null;

                                for (int i = 0; i < 3; i++)
                                {
                                    try
                                    {
                                        table = ReadAnyFile(fullPath);
                                        break;
                                    }
                                    catch
                                    {
                                        await Task.Delay(300);
                                    }
                                }

                                var list = ConvertTableToList(table);

                                var response = new
                                {
                                    type = "studentTableData",
                                    data = list
                                };

                                string json = JsonSerializer.Serialize(response);

                                await ws.SendAsync(
                                    new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)),
                                    WebSocketMessageType.Text,
                                    true,
                                    CancellationToken.None);

                                //Log("Sent database table data");
                            }
                        }
                        // --------------------------------------
                        else if (message.StartsWith("getTimes|"))
                        {
                            var parts = message.Split('|');
                            if (parts.Length == 2)
                            {
                                string date = parts[1];
                                var times = GetTimesByDate(date);

                                string json = $"{{\"type\":\"timeList\",\"times\":[{string.Join(",", times.Select(t => $"\"{t}\""))}]}}";
                                await ws.SendAsync(
                                    new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)),
                                    WebSocketMessageType.Text, true, CancellationToken.None);
                            }

                        }
                        else if (message.StartsWith("getFileByTime|"))
                        {
                            var parts = message.Split('|');
                            if (parts.Length == 3)
                            {
                                string date = parts[1];
                                string time = parts[2];

                                var files = GetFileByDateTime(date, time);
                                string json = $"{{\"type\":\"fileList\",\"files\":[{string.Join(",", files.Select(f => $"\"{f}\""))}]}}";
                                await ws.SendAsync(
                                    new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)),
                                    WebSocketMessageType.Text, true, CancellationToken.None);
                            }
                        }
                        else if (message == "getSqlTables")
                        {
                            var tables = GetSqlTableNames();

                            var response = new
                            {
                                type = "sqlTableList",
                                tables = tables
                            };

                            string json = JsonSerializer.Serialize(response);

                            await ws.SendAsync(
                                new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)),
                                WebSocketMessageType.Text,
                                true,
                                CancellationToken.None);
                        }
                        else if (message == "getFileStudents")
                        {
                            var file = Directory.GetFiles(@"C:\Users\Shubham\source\repos\TcpWebBridge\TcpWebBridge\Data").FirstOrDefault();

                            var table = ReadAnyFile(file);
                            var data = ConvertTableToList(table);

                            var response = new
                            {
                                type = "studentTableData",
                                data = data
                            };

                            string json = JsonSerializer.Serialize(response);

                            await ws.SendAsync(
                                new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)),
                                WebSocketMessageType.Text,
                                true,
                                CancellationToken.None);
                        }
                        else if (message.StartsWith("refreshStudents|"))
                        {
                            var parts = message.Split('|');

                            string fileName = parts[1];

                            string dataFolder = @"C:\Users\Shubham\source\repos\TcpWebBridge\TcpWebBridge\Data";
                            string file = Path.Combine(dataFolder, fileName);

                            var table = ReadAnyFile(file);
                            var list = ConvertTableToList(table);

                            var response = new
                            {
                                type = "studentTableData",
                                data = list
                            };

                            string json = JsonSerializer.Serialize(response);

                            await ws.SendAsync(
                                new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)),
                                WebSocketMessageType.Text,
                                true,
                                CancellationToken.None);
                        }
                        else if (message == "getFileList")
                        {
                            var files = GetLogFiles();
                            string json = $"{{\"type\":\"fileList\",\"files\":[{string.Join(",", files.Select(f => $"\"{f}\""))}]}}";
                            await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)), WebSocketMessageType.Text, true, CancellationToken.None);
                        }
                        else if (message.StartsWith("fetchHistory|"))
                        {
                            var parts = message.Split('|');
                            if (parts.Length == 4)
                            {
                                string date = parts[1];
                                string time = parts[2];
                                string file = parts[3];
                                var historyData = FetchHistory(date, time, file);
                                string json = $"{{\"type\":\"history\",\"data\":[{string.Join(",", historyData.Select(d => $"\"{d.Replace("\"", "\\\"")}\""))}]}}";
                                await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)), WebSocketMessageType.Text, true, CancellationToken.None);
                            }
                        }
                        else if (message.StartsWith("connectDatabase|"))
                        {
                            var parts = message.Split('|');
                            if (parts.Length == 5)
                            {
                                string server = parts[1];
                                string database = parts[2];
                                string username = parts[3];
                                string password = parts[4];

                                if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
                                    connectionString = $"Server={server};Database={database};Trusted_Connection=True;";
                                else
                                    connectionString = $"Server={server};Database={database};User Id={username};Password={password};";

                                try
                                {
                                    using (SqlConnection conn = new SqlConnection(connectionString))
                                    {
                                        conn.Open();
                                        Log("Database connected successfully.", isStatus: true, statusValue: "DB Connected");

                                        // ✅ SEND TO TCP CLIENT
                                        if (client != null && client.Connected && stream != null)
                                        {
                                            var response = new
                                            {
                                                type = "dbStatus",
                                                status = "connected"
                                            };

                                            string json = JsonSerializer.Serialize(response);
                                            byte[] data = Encoding.UTF8.GetBytes(json);
                                            stream.Write(data, 0, data.Length);
                                           
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Log("Database connection failed: " + ex.Message, isStatus: true, statusValue: "DB Error");
                                }
                            }
                        }

                        // ✅ ADD THIS BLOCK HERE
                        else if (message.StartsWith("disconnectDatabase"))
                        {
                            try
                            {
                                sqlConnection?.Close();
                                sqlConnection = null;

                                Log("Database disconnected.", isStatus: true, statusValue: "DB Disconnected");

                                // ✅ SEND TO TCP CLIENT
                                if (client != null && client.Connected && stream != null)
                                {
                                    var response = new
                                    {
                                        type = "dbStatus",
                                        status = "disconnected"
                                    };

                                    string json = JsonSerializer.Serialize(response);
                                    byte[] data = Encoding.UTF8.GetBytes(json);
                                    stream.Write(data, 0, data.Length);
                                }
                            }
                            catch (Exception ex)
                            {
                                Log("DB Disconnect Error: " + ex.Message);
                            }
                        }
                        else if (message.StartsWith("fetchSqlTable|"))
                        {
                            var parts = message.Split('|');

                            if (parts.Length == 2)
                            {
                                string tableName = parts[1];

                                var data = FetchTableData(tableName);

                                var response = new
                                {
                                    type = "studentTableData",
                                    data = data
                                };

                                string json = JsonSerializer.Serialize(response);

                                await ws.SendAsync(
                                    new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)),
                                    WebSocketMessageType.Text,
                                    true,
                                    CancellationToken.None);
                            }
                        }
                        else if (message == "getCurrentLog")
                        {
                            var lines = liveSessionLogs
                                        .Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);

                            foreach (var line in lines)
                            {
                                string escaped = line.Replace("\\", "\\\\")
                                                     .Replace("\"", "\\\"")
                                                     .Replace("\r", "")
                                                     .Replace("\n", "");

                                string json = $"{{\"type\":\"live\",\"message\":\"{escaped}\"}}";
                                await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)),
                                                   WebSocketMessageType.Text,
                                                   true,
                                                   CancellationToken.None);
                            }
                        }
                        else if (message == "getDB")
                        {
                            var dbData = GetMockDatabaseContent();
                            string json = $"{{\"type\":\"data\",\"content\":[{string.Join(",", dbData.Select(d => $"\"{d.Replace("\"", "\\\"")}\""))}]}}";
                            await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)), WebSocketMessageType.Text, true, CancellationToken.None);
                        }
                       
                        else if (message == "startServer")
                        {
                            if (serverThread == null || !serverThread.IsAlive)
                            {
                                serverThread = new Thread(StartServer);
                                serverThread.IsBackground = true;
                                serverThread.Start();
                                Log("Server started via WebSocket", isStatus: true, statusValue: "Server started");
                            }
                        }
                        else
                        {
                            Log("WebSocket: " + message);
                        }
                    }
                    else if (result.MessageType == WebSocketMessageType.Close)
                    {
                        string json = "{\"type\":\"status\",\"value\":\"disconnected\"}";
                        var buffer2 = Encoding.UTF8.GetBytes(json);
                        var segment = new ArraySegment<byte>(buffer2);
                        await ws.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
                        lock (wsClients)
                        {
                            wsClients.Remove(ws);
                        }
                        if (isRealWebUI)
                        {
                            Log("WEB CLIENT DISCONNECTED");

                            string tcpJson = JsonSerializer.Serialize(new
                            {
                                type = "webStatus",
                                status = "disconnected"
                            });

                            SendToTcpClient(tcpJson);
                        }

                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", token);
                    }
                }
                catch
                {
                    lock (wsClients)
                    {
                        wsClients.Remove(ws);
                    }
                    if (isRealWebUI)
                    {
                        Log("WEB CLIENT DISCONNECTED");

                        string tcpJson = JsonSerializer.Serialize(new
                        {
                            type = "webStatus",
                            status = "disconnected"
                        });

                        SendToTcpClient(tcpJson);
                    }
                    
                    break;

                }
            }
        }
        void CreateNewLogFile()
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm");
            filePath = Path.Combine(folder, "ServerLog_" + timestamp + ".txt");
        }
        void InitializeLogFile()
        {
            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            var files = Directory.GetFiles(folder, "ServerLog_*.txt")
                                 .OrderByDescending(f => f)
                                 .ToList();

            if (files.Count > 0)
            {
                filePath = files.First();

                DateTime creationTime = File.GetCreationTime(filePath);

                if ((DateTime.Now - creationTime).TotalHours >= 1)
                {
                    CreateNewLogFile();
                }
            }
            else
            {
                CreateNewLogFile();
            }
        }

        void StartServer()
        {
            try
            {
                server = new TcpListener(IPAddress.Any, serverPort);
                server.Start();
                isServerRunning = true;
                InitializeLogFile();
                Log("SERVER STARTED at port " + serverPort, isStatus: true, statusValue: "Server started");

                client = server.AcceptTcpClient();
                Log("CLIENT CONNECTED..", isStatus: true, statusValue: "connected");


                stream = client.GetStream();
                byte[] buffer = new byte[1024];

                while (isServerRunning)
                {
                    int bytesRead;
                    try
                    {
                        bytesRead = stream.Read(buffer, 0, buffer.Length);
                    }
                    catch
                    {
                        break;
                    }
                    if (bytesRead == 0)
                    {
                        Log("Client disconnected", isStatus: true, statusValue: "disconnected");
                        break;
                    }

                    string message = Encoding.UTF8.GetString(buffer, 0, bytesRead).Trim();
                    if (message.Contains("WEB CLIENT CONNECTED") ||
                        message.Contains("WEB CLIENT DISCONNECTED"))
                    {
                        continue;
                    }
                    // ✅ HANDLE REFRESH REQUEST FROM CLIENT
                    if (message.Contains("\"type\":\"refreshStudents\""))
                    {
                        try
                        {
                            var doc = JsonDocument.Parse(message);

                            string fileName = doc.RootElement.GetProperty("file").GetString();

                            string dataFolder = @"C:\Users\Shubham\source\repos\TcpWebBridge\TcpWebBridge\Data";
                            string fullPath = Path.Combine(dataFolder, fileName);

                            DataTable table = ReadAnyFile(fullPath);
                            var data = ConvertTableToList(table);

                            var response = new
                            {
                                type = "studentTableData",
                                file = fileName,
                                data = data
                            };

                            string json = JsonSerializer.Serialize(response);

                            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
                            stream.Write(jsonBytes, 0, jsonBytes.Length);

                            Log("Server: Refreshed data sent for " + fileName);

                            continue;
                        }
                        catch (Exception ex)
                        {
                            Log("Refresh error: " + ex.Message);
                        }
                    }
                    // ❌ Ignore JSON request logs
                    if (!message.Contains("\"type\":\"getFileStudents\""))
                    {
                        Log("Client: " + message);
                    }

                    // ⭐ HANDLE CSV REQUEST (UPDATED FOR CLIENT DROPDOWN)
                    try
                    {
                        if (message.Contains("\"type\":\"getFileStudents\""))
                        {
                            var doc = JsonDocument.Parse(message);

                            string fileName = doc.RootElement.GetProperty("file").GetString();

                            string dataFolder = @"C:\Users\Shubham\source\repos\TcpWebBridge\TcpWebBridge\Data";
                            string fullPath = Path.Combine(dataFolder, fileName);

                            DataTable table = ReadAnyFile(fullPath);
                            var data = ConvertTableToList(table);

                            var response = new
                            {
                                type = "studentTableData",
                                file = fileName,
                                data = data
                            };

                            string json = JsonSerializer.Serialize(response);

                            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
                            stream.Write(jsonBytes, 0, jsonBytes.Length);

                           // Log("Server: Sent table for " + fileName);

                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("JSON error: " + ex.Message);
                    }
                    // Check if message is already datetime (auto reply)
                    DateTime parsedDate;
                    bool isAutoReply = DateTime.TryParseExact(
                        message,
                        "yyyy-MM-dd HH:mm:ss",
                        null,
                        System.Globalization.DateTimeStyles.None,
                        out parsedDate
                    );

                    if (!isAutoReply)
                    {
                        string reply = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                        byte[] data = Encoding.UTF8.GetBytes(reply);
                        stream.Write(data, 0, data.Length);
                        Log("Server: " + reply);
                    }
                }
            }
            catch (SocketException)
            {
                //Log(ex.Message, isStatus: true, statusValue: "Error");
            }
            catch (Exception ex)
            {
                Log("Server Error:" + ex.Message, isStatus: true, statusValue: "Error");
            }
            finally
            {
                isServerRunning = false;

                stream?.Close();
                client?.Close();
                server?.Stop();

                stream = null;
                client = null;
                server = null;
            }
        }
  
        private List<Dictionary<string, string>> FetchStudentsFromSql()
        {
            var list = new List<Dictionary<string, string>>();

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();

                string query = @"
                                SELECT 
                                    w.WaferID,
                                    w.BatchNumber,
                                    w.Material,
                                    c.ChipID,
                                    c.ChipName,
                                    c.PositionX,
                                    c.PositionY,
                                    t.Voltage,
                                    t.CurrentValue,
                                    t.Temperature,
                                    t.Result,
                                    t.TestTime
                                FROM Wafers w
                                JOIN Chips c ON w.WaferID = c.WaferID
                                JOIN TestResults t ON c.ChipID = t.ChipID";
                SqlCommand cmd = new SqlCommand(query, conn);
                SqlDataReader reader = cmd.ExecuteReader();

                while (reader.Read())
                {
                    var row = new Dictionary<string, string>();

                    row["WaferID"] = reader["WaferID"].ToString();
                    row["BatchNumber"] = reader["BatchNumber"].ToString();
                    row["Material"] = reader["Material"].ToString();

                    row["ChipID"] = reader["ChipID"].ToString();
                    row["ChipName"] = reader["ChipName"].ToString();
                    row["PositionX"] = reader["PositionX"].ToString();
                    row["PositionY"] = reader["PositionY"].ToString();

                    row["Voltage"] = reader["Voltage"].ToString();
                    row["CurrentValue"] = reader["CurrentValue"].ToString();
                    row["Temperature"] = reader["Temperature"].ToString();
                    row["Result"] = reader["Result"].ToString();
                    row["TestTime"] = reader["TestTime"].ToString();

                    list.Add(row);
                }
            }

            return list;
        }
        private void btnSend_Click(object sender, RoutedEventArgs e)
        {
            string message = txtMessage.Text.Trim();  // ✅ CAPTURE MESSAGE FIRST
            if (string.IsNullOrEmpty(message))
            {
                MessageBox.Show("Please enter a message first.");
                return;
            }

            if (client == null || !client.Connected || stream == null)
            {
                MessageBox.Show("Client not connected.");
                return;
            }

            try
            {
                byte[] data = Encoding.UTF8.GetBytes(message);
                stream.Write(data, 0, data.Length);
                Log("Server: " + message);  // ✅ NOW message has content
                txtMessage.Clear();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error sending message: " + ex.Message);
                Log("Error sending message: " + ex.Message);
            }
        }
        private void btnFetchCsv_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (cmbDataFiles.SelectedItem == null ||
                    cmbDataFiles.SelectedItem.ToString() == "Select File" ||
                    cmbDataFiles.SelectedItem.ToString() == "----------------")
                {
                    MessageBox.Show("Please select a valid file.");
                    return;
                }
                string dataFolder = @"C:\Users\Shubham\source\repos\TcpWebBridge\TcpWebBridge\Data";
                string fileName = cmbDataFiles.SelectedItem.ToString();

                string fullPath = Path.Combine(dataFolder, fileName);

                DataTable table = ReadAnyFile(fullPath);

                if (studentsWindow == null || !studentsWindow.IsVisible)
                {
                    studentsWindow = new StudentsWindow(table);
                    studentsWindow.Show();
                }
                else
                {
                    studentsWindow.UpdateTable(table);
                }

               // Log("Loaded table from " + fileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        void Log(string message, bool isStatus = false, string statusValue = "")
        {
            lock (logLock)
            {
                try
                {
                    if (!Directory.Exists(folder))
                        Directory.CreateDirectory(folder);

                    // 1 Hour Check
                    if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                    {
                        DateTime creationTime = File.GetCreationTime(filePath);

                        if ((DateTime.Now - creationTime).TotalHours >= 1)
                        {
                            CreateNewLogFile();
                        }
                    }

                    string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    string logText = timestamp + " | " + message;
                    liveSessionLogs += logText + Environment.NewLine;

                    if (!string.IsNullOrEmpty(filePath))
                        File.AppendAllText(filePath, logText + Environment.NewLine);

                    Dispatcher.Invoke(() =>
                    {
                        txtLog.AppendText(logText + Environment.NewLine);
                        txtLog.ScrollToEnd();
                    });

                    if (isStatus)
                    {
                        string statusJson = $"{{\"type\":\"status\",\"value\":\"{statusValue}\"}}";
                        BroadcastToWebSockets(statusJson);
                    }

                    string escaped = logText.Replace("\\", "\\\\")
                                            .Replace("\"", "\\\"")
                                            .Replace("\r", "")
                                            .Replace("\n", "");

                    string json = $"{{\"type\":\"live\",\"message\":\"{escaped}\"}}";
                    BroadcastToWebSockets(json);
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() =>
                        MessageBox.Show("Logging error: " + ex.Message));
                }
            }
        }

        async void BroadcastToWebSockets(string message)
        {
            var buffer = Encoding.UTF8.GetBytes(message);
            var segment = new ArraySegment<byte>(buffer);
            foreach (var ws in wsClients.ToArray())
            {
                if (ws.State == WebSocketState.Open)
                {
                    try
                    {
                        await ws.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                    catch
                    {
                        lock (wsClients)
                        {
                            wsClients.Remove(ws);
                        }
                    }
                }
            }
        }
        private void SendToTcpClient(string message)
        {
            try
            {
                if (client != null && client.Connected && stream != null)
                {
                    byte[] data = Encoding.UTF8.GetBytes(message);
                    stream.Write(data, 0, data.Length);
                }
            }
            catch
            {
                // ignore send errors
            }
        }
        private void btnOpenLog_Click(object sender, RoutedEventArgs e)
        {
            if (File.Exists(filePath))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = filePath,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to open log file: {ex.Message}");
                }
            }
            else
            {
                MessageBox.Show("Log file not found!");
            }
        }

        private void btnClearLog_Click(object sender, RoutedEventArgs e)
        {
            if (File.Exists(filePath))
            {
                File.WriteAllText(filePath, string.Empty);
                MessageBox.Show("Log Cleared successfully");
            }
            else
            {
                MessageBox.Show("Log file not found!");
            }
        }

        private void cmbPort_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
        }

        private List<string> GetLogFiles()
        {
            if (Directory.Exists(folder))
            {
                return Directory.GetFiles(folder, "ServerLog_*.txt")
                                .Select(f => Path.GetFileName(f))
                                .OrderByDescending(f => f)
                                .ToList();
            }
            return new List<string>();
        }

        // New Method: Get files for a specific date
        private List<string> GetLogFilesByDate(string date)
        {
            if (Directory.Exists(folder))
            {
                // Files are named ServerLog_yyyy-MM-dd_HH-mm.txt
                string searchPattern = $"ServerLog_{date}*";
                return Directory.GetFiles(folder, searchPattern)
                                .Select(f => Path.GetFileName(f))
                                .OrderByDescending(f => f)
                                .ToList();
            }
            return new List<string>();
        }

        private List<string> GetTimesByDate(string date)
        {
            var times = new List<string>();
            if (Directory.Exists(folder))
            {
                string pattern = $"ServerLog_{date}_*.txt";
                var files = Directory.GetFiles(folder, pattern);
                foreach (var file in files)
                {
                    string fileName = Path.GetFileNameWithoutExtension(file);

                    var parts = fileName.Split('_');
                    if (parts.Length == 3)
                    {
                        times.Add(parts[2]);//HH-mm
                    }
                }
                times = times.OrderByDescending(t => t).ToList();
            }
            return times;
        }

        private List<string> GetFileByDateTime(string date, string time)
        {
            var result = new List<string>();
            if (Directory.Exists(folder))
            {
                string pattern = $"ServerLog_{date}_{time}.txt";
                var files = Directory.GetFiles(folder, pattern);

                foreach (var file in files)
                {
                    result.Add(Path.GetFileName(file));
                }
            }
            return result;
        }

        private List<string> FetchHistory(string date, string time, string file)
        {
            var data = new List<string>();
            try
            {
                string filePath = Path.Combine(folder, file);
                if (File.Exists(filePath))
                {
                    var lines = File.ReadAllLines(filePath);
                    DateTime targetDateTime;
                    // Convert 12-01 → 12:01 before parsing
                    string formattedTime = time.Replace("-", ":");

                    if (DateTime.TryParse($"{date} {formattedTime}", out targetDateTime))
                    {
                        foreach (var line in lines)
                        {
                            var parts = line.Split(new[] { " | " }, 2, StringSplitOptions.None);
                            if (parts.Length == 2)
                            {
                                DateTime lineDateTime;
                                if (DateTime.TryParse(parts[0], out lineDateTime))
                                {
                                    if (lineDateTime >= targetDateTime)
                                    {
                                        // ✅ Show only real chat messages
                                        if (parts[1].StartsWith("Client:") || parts[1].StartsWith("Server:"))
                                        {
                                            data.Add(line);
                                        }
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        data.Add("Invalid date/time format.");
                    }
                }
                else
                {
                    data.Add("File not found: " + file);
                }
            }
            catch (Exception ex)
            {
                data.Add($"Error fetching history: {ex.Message}");
            }
            return data;
        }

        private List<string> GetMockDatabaseContent()
        {
            var data = new List<string>();
            try
            {
                if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                {
                    var lines = File.ReadAllLines(filePath);
                    // Return last 100 lines
                    int start = Math.Max(0, lines.Length - 100);
                    for (int i = start; i < lines.Length; i++)
                    {
                        data.Add(lines[i]);
                    }
                }
                else
                {
                    data.Add("No log file available.");
                }
            }
            catch (Exception ex)
            {
                data.Add($"Error fetching mock DB content: {ex.Message}");
            }
            return data;
        }

        private List<Dictionary<string, string>> ConvertTableToList(DataTable table)
        {
            var list = new List<Dictionary<string, string>>();

            if (table == null)
                return list;

            foreach (DataRow row in table.Rows)
            {
                var dict = new Dictionary<string, string>();

                foreach (DataColumn col in table.Columns)
                {
                    dict[col.ColumnName] = row[col]?.ToString();
                }

                list.Add(dict);
            }

            return list;
        }
        private DataTable ReadAnyFile(string path)
{
         DataTable table = new DataTable();

        if (!File.Exists(path))
             return table;

         string ext = Path.GetExtension(path).ToLower();

        try
         {
        // CSV or TXT
        if (ext == ".csv" || ext == ".txt")
        {
              var lines = File.ReadAllLines(path, Encoding.UTF8);

             if (lines.Length == 0)
             return table;

            var headers = lines[0].Split(',');

            foreach (var h in headers)
                table.Columns.Add(h);

                    for (int i = 1; i < lines.Length; i++)
                    {
                        var values = lines[i].Split(',');

                        DataRow row = table.NewRow();

                        for (int j = 0; j < table.Columns.Count && j < values.Length; j++)
                        {
                            row[j] = values[j];
                        }

                        table.Rows.Add(row);
                    }
                }

        // JSON
        else if (ext == ".json")
        {
            var json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(json);

            if (data != null)
            {
                foreach (var key in data[0].Keys)
                    table.Columns.Add(key);

                foreach (var row in data)
                {
                    table.Rows.Add(row.Values.Select(v => v?.ToString()).ToArray());
                }
            }
        }

                // Excel
                else if (ext == ".xlsx" || ext == ".xls")
                {
                    System.Text.Encoding.RegisterProvider(
                        System.Text.CodePagesEncodingProvider.Instance);

                    using (var stream = new FileStream(
                      path,
                    FileMode.Open,
                     FileAccess.Read,
                     FileShare.ReadWrite))
                    using (var reader = ExcelReaderFactory.CreateReader(stream))
                    {
                        var result = reader.AsDataSet(new ExcelDataSetConfiguration()
                        {
                            ConfigureDataTable = (_) => new ExcelDataTableConfiguration()
                            {
                                UseHeaderRow = true   // ⭐ IMPORTANT FIX
                            }
                        });

                        table = result.Tables[0];
                    }
                }

                // Other Files
                else
        {
            FileInfo info = new FileInfo(path);

            table.Columns.Add("Name");
            table.Columns.Add("Type");
            table.Columns.Add("Size");

            table.Rows.Add(info.Name, ext, info.Length + " bytes");
        }
    }
    catch
    {
    }

    return table;
}
        private void btnClearTable_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (studentsWindow != null && studentsWindow.IsVisible)
                {
                    // Create empty DataTable to clear current table
                    DataTable emptyTable = new DataTable();
                    studentsWindow.UpdateTable(emptyTable);
                    Log("Table cleared", isStatus: true, statusValue: "Table Cleared");
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

        private void btnFetchSql_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (cmbSqlTables.SelectedItem == null ||
                    cmbSqlTables.SelectedItem.ToString() == "Select SQL Table" ||
                    cmbSqlTables.SelectedItem.ToString() == "----------------")
                {
                    MessageBox.Show("Please select SQL table.");
                    return;
                }

                string tableName = cmbSqlTables.SelectedItem.ToString();

                var data = FetchTableData(tableName);

                DataTable dt = new DataTable();

                if (data.Count > 0)
                {
                    foreach (var col in data[0].Keys)
                        dt.Columns.Add(col);

                    foreach (var row in data)
                        dt.Rows.Add(row.Values.ToArray());
                }

                if (studentsWindow == null || !studentsWindow.IsVisible)
                {
                    studentsWindow = new StudentsWindow(dt);
                    studentsWindow.Show();
                }
                else
                {
                    studentsWindow.UpdateTable(dt);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("SQL Fetch Error: " + ex.Message);
            }
        }
        private void btnRefresh_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 🔹 CASE 1: SQL TABLE SELECTED
                if (cmbSqlTables.SelectedItem != null &&
                    cmbSqlTables.SelectedItem.ToString() != "Select SQL Table" &&
                    cmbSqlTables.SelectedItem.ToString() != "----------------")
                {
                    string tableName = cmbSqlTables.SelectedItem.ToString();

                    var data = FetchTableData(tableName);

                    DataTable dt = new DataTable();

                    if (data.Count > 0)
                    {
                        foreach (var col in data[0].Keys)
                            dt.Columns.Add(col);

                        foreach (var row in data)
                            dt.Rows.Add(row.Values.ToArray());
                    }

                    if (studentsWindow != null && studentsWindow.IsVisible)
                    {
                        studentsWindow.UpdateTable(dt);
                    }

                    Log("Refreshed SQL Table: " + tableName);
                    return;
                }

                // 🔹 CASE 2: FILE SELECTED
                if (cmbDataFiles.SelectedItem != null &&
                    cmbDataFiles.SelectedItem.ToString() != "Select File" &&
                    cmbDataFiles.SelectedItem.ToString() != "----------------")
                {
                    string dataFolder = @"C:\Users\Shubham\source\repos\TcpWebBridge\TcpWebBridge\Data";
                    string fileName = cmbDataFiles.SelectedItem.ToString();
                    string fullPath = Path.Combine(dataFolder, fileName);

                    DataTable table = ReadAnyFile(fullPath);

                    if (studentsWindow != null && studentsWindow.IsVisible)
                    {
                        studentsWindow.UpdateTable(table);
                    }

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
            cts.Cancel();
            wsListener?.Stop();
            base.OnClosed(e);
        }
    }
}