using System;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Data.Sqlite;

namespace SMTPCBManager;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        try { Db.Init(); Application.Run(new MainWindow()); }
        catch (Exception ex) { MessageBox.Show("Unable to start: " + ex.Message, "SMT PCB Manager"); }
    }
}

// Opens the Windows touch keyboard for operator text entry; no third-party keyboard required.
// Windows controls the keyboard window and its normal dismissal behavior.
internal static class TouchKeyboard
{
    private static DateTime lastLaunch = DateTime.MinValue;
    private static readonly string TabTip = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
        "microsoft shared", "ink", "TabTip.exe");

    public static void Register(Control root)
    {
        foreach (Control control in root.Controls)
        {
            if (control is TextBoxBase || control is NumericUpDown ||
                (control is ComboBox combo && combo.DropDownStyle != ComboBoxStyle.DropDownList))
            {
                control.Enter += (_, _) => ShowFor(control);
            }
            Register(control);
        }
    }

    private static void ShowFor(Control field)
    {
        // Prevent repeated launches when navigating between fields rapidly.
        if ((DateTime.UtcNow - lastLaunch).TotalMilliseconds > 900)
        {
            lastLaunch = DateTime.UtcNow;
            try
            {
                if (File.Exists(TabTip))
                    Process.Start(new ProcessStartInfo(TabTip) { UseShellExecute = true });
                else
                    Process.Start(new ProcessStartInfo("osk.exe") { UseShellExecute = true });
            }
            catch (Exception) { /* A physical keyboard remains usable if Windows blocks launch. */ }
        }

        // Keep focused fields accessible in the scrollable PCB IN / Edit forms.
        var parent = field.Parent;
        while (parent != null)
        {
            if (parent is ScrollableControl scroll && scroll.AutoScroll)
            {
                var target = scroll;
                var timer = new System.Windows.Forms.Timer { Interval = 300 };
                timer.Tick += (_, _) =>
                {
                    timer.Stop(); timer.Dispose();
                    if (!field.IsDisposed && !target.IsDisposed && field.ContainsFocus)
                        target.ScrollControlIntoView(field);
                };
                timer.Start();
                break;
            }
            parent = parent.Parent;
        }
    }
}

internal static class Db
{
    public static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SMTPCBManager");
    public static readonly string FilePath = Path.Combine(Folder, "pcb_inventory.db");
    private static string Cs => new SqliteConnectionStringBuilder { DataSource = FilePath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
    public static SqliteConnection Open()
    {
        var c = new SqliteConnection(Cs); c.Open();
        using var p = c.CreateCommand(); p.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA synchronous=FULL;"; p.ExecuteNonQuery();
        return c;
    }
    public static void Init()
    {
        Directory.CreateDirectory(Folder);
        using var c = Open();
        using (var journal = c.CreateCommand()) { journal.CommandText = "PRAGMA journal_mode=WAL;"; journal.ExecuteNonQuery(); }
        using var q = c.CreateCommand();
        q.CommandText = @"
CREATE TABLE IF NOT EXISTS PCB_Inventory(
 StockID INTEGER PRIMARY KEY AUTOINCREMENT,
 ArticleNumber TEXT NOT NULL, SAPNumber TEXT NOT NULL, CustomerName TEXT NOT NULL,
 Rack TEXT NOT NULL, RowNumber TEXT NOT NULL,
 FullPCBQty INTEGER NOT NULL CHECK(FullPCBQty>0),
 CompletedQty INTEGER NOT NULL CHECK(CompletedQty>=0 AND CompletedQty<=FullPCBQty),
 AvailableQty INTEGER NOT NULL CHECK(AvailableQty>=0 AND AvailableQty<=FullPCBQty),
 Status2D INTEGER NOT NULL CHECK(Status2D IN(0,1)),
 StatusP2D INTEGER NOT NULL CHECK(StatusP2D IN(0,1)),
 StatusFL INTEGER NOT NULL CHECK(StatusFL IN(0,1)),
 StatusTaping INTEGER NOT NULL CHECK(StatusTaping IN(0,1)),
 Remarks TEXT, EmployeeIN TEXT NOT NULL, DateIN TEXT NOT NULL,
 IsEdited INTEGER NOT NULL DEFAULT 0, LastEditedBy TEXT, LastEditedAt TEXT);
CREATE TABLE IF NOT EXISTS PCB_Transactions(
 TransactionID INTEGER PRIMARY KEY AUTOINCREMENT,
 StockID INTEGER NOT NULL REFERENCES PCB_Inventory(StockID),
 TransactionType TEXT NOT NULL CHECK(TransactionType IN('IN','OUT')),
 PCBQuantity INTEGER NOT NULL CHECK(PCBQuantity>0),
 EmployeeNumber TEXT NOT NULL, TransactionDate TEXT NOT NULL, Remarks TEXT);
CREATE TABLE IF NOT EXISTS PCB_EditHistory(
 EditID INTEGER PRIMARY KEY AUTOINCREMENT,
 StockID INTEGER NOT NULL REFERENCES PCB_Inventory(StockID),
 EmployeeNumber TEXT NOT NULL, EditedAt TEXT NOT NULL,
 PreviousData TEXT NOT NULL, UpdatedData TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS Humidity_Readings(
 ReadingID INTEGER PRIMARY KEY AUTOINCREMENT, SensorID TEXT NOT NULL,
 HumidityRH REAL NOT NULL CHECK(HumidityRH BETWEEN 0 AND 100), RecordedAt TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS idx_article ON PCB_Inventory(ArticleNumber);
CREATE INDEX IF NOT EXISTS idx_sap ON PCB_Inventory(SAPNumber);
CREATE INDEX IF NOT EXISTS idx_transactions_stock ON PCB_Transactions(StockID);
CREATE TRIGGER IF NOT EXISTS prevent_transaction_update BEFORE UPDATE ON PCB_Transactions BEGIN SELECT RAISE(ABORT,'Transaction history is immutable'); END;
CREATE TRIGGER IF NOT EXISTS prevent_transaction_delete BEFORE DELETE ON PCB_Transactions BEGIN SELECT RAISE(ABORT,'Transaction history is immutable'); END;";
        q.ExecuteNonQuery();
        // Upgrade existing installations without deleting existing records.
        foreach (var column in new[] { ("IsEdited", "INTEGER NOT NULL DEFAULT 0"), ("LastEditedBy", "TEXT"), ("LastEditedAt", "TEXT") })
        {
            using var check = c.CreateCommand(); check.CommandText = "PRAGMA table_info(PCB_Inventory)";
            using var reader = check.ExecuteReader(); bool exists = false;
            while (reader.Read()) if (string.Equals(reader.GetString(1), column.Item1, StringComparison.OrdinalIgnoreCase)) exists = true;
            reader.Close();
            if (!exists) { using var alter = c.CreateCommand(); alter.CommandText = $"ALTER TABLE PCB_Inventory ADD COLUMN {column.Item1} {column.Item2}"; alter.ExecuteNonQuery(); }
        }
    }
    public static SqliteCommand Cmd(SqliteConnection c, string sql, SqliteTransaction? tx = null, params (string key, object value)[] args)
    {
        var q = c.CreateCommand(); q.CommandText = sql; if (tx != null) q.Transaction = tx;
        foreach (var (key, value) in args) q.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return q;
    }
    public static DataTable Table(string sql, params (string key, object value)[] args)
    {
        using var c = Open(); using var q = Cmd(c, sql, null, args); using var reader = q.ExecuteReader();
        var t = new DataTable(); t.Load(reader); return t;
    }
    public static long In(string article, string sap, string customer, string rack, string row,
        int full, int completed, bool d2, bool p2d, bool fl, bool taping, string remarks, string employee)
    {
        if (new[] { article, sap, customer, rack, row, employee }.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("All required text fields and Employee Number must be provided.");
        if (full <= 0 || completed < 0 || completed > full) throw new ArgumentException("Invalid full/completed quantity.");
        using var c = Open(); using var tx = c.BeginTransaction();
        try
        {
            using (var q = Cmd(c, @"INSERT INTO PCB_Inventory(ArticleNumber,SAPNumber,CustomerName,Rack,RowNumber,FullPCBQty,CompletedQty,AvailableQty,Status2D,StatusP2D,StatusFL,StatusTaping,Remarks,EmployeeIN,DateIN)
VALUES(@a,@s,@c,@r,@row,@f,@done,@f,@d2,@p2d,@fl,@tap,@note,@emp,@date)", tx,
                ("@a", article.Trim()), ("@s", sap.Trim()), ("@c", customer.Trim()), ("@r", rack.Trim()), ("@row", row.Trim()),
                ("@f", full), ("@done", completed), ("@d2", d2 ? 1 : 0), ("@p2d", p2d ? 1 : 0),
                ("@fl", fl ? 1 : 0), ("@tap", taping ? 1 : 0), ("@note", remarks.Trim()), ("@emp", employee.Trim()), ("@date", DateTime.UtcNow.ToString("O")))) q.ExecuteNonQuery();
            long id; using (var q = Cmd(c, "SELECT last_insert_rowid()", tx)) id = (long)q.ExecuteScalar()!;
            using (var q = Cmd(c, "INSERT INTO PCB_Transactions(StockID,TransactionType,PCBQuantity,EmployeeNumber,TransactionDate,Remarks) VALUES(@id,'IN',@qty,@emp,@date,@note)", tx,
                ("@id", id), ("@qty", full), ("@emp", employee.Trim()), ("@date", DateTime.UtcNow.ToString("O")), ("@note", remarks.Trim()))) q.ExecuteNonQuery();
            tx.Commit(); return id;
        }
        catch { tx.Rollback(); throw; }
    }
    // A stock record can be withdrawn once, in its entirety. No partial OUT.
    public static int Out(long stockId, string employee)
    {
        if (stockId <= 0 || string.IsNullOrWhiteSpace(employee)) throw new ArgumentException("Select a stock record and enter Employee Number.");
        using var c = Open(); using var tx = c.BeginTransaction();
        try
        {
            int qty;
            using (var q = Cmd(c, "SELECT AvailableQty FROM PCB_Inventory WHERE StockID=@id", tx, ("@id", stockId)))
            {
                var result = q.ExecuteScalar();
                if (result is null) throw new InvalidOperationException("Stock record not found.");
                qty = Convert.ToInt32(result);
            }
            if (qty <= 0) throw new InvalidOperationException("This stock has already been withdrawn. Refresh inventory.");
            using (var q = Cmd(c, "UPDATE PCB_Inventory SET AvailableQty=0 WHERE StockID=@id AND AvailableQty=@qty AND AvailableQty>0", tx, ("@id", stockId), ("@qty", qty)))
                if (q.ExecuteNonQuery() != 1) throw new InvalidOperationException("Stock changed. Refresh and select again.");
            using (var q = Cmd(c, "INSERT INTO PCB_Transactions(StockID,TransactionType,PCBQuantity,EmployeeNumber,TransactionDate) VALUES(@id,'OUT',@qty,@emp,@date)", tx,
                ("@id", stockId), ("@qty", qty), ("@emp", employee.Trim()), ("@date", DateTime.UtcNow.ToString("O")))) q.ExecuteNonQuery();
            tx.Commit(); return qty;
        }
        catch { tx.Rollback(); throw; }
    }
    // Only descriptive fields are editable. Full, completed, available quantities and IN employee remain unchanged.
    public static void Edit(long stockId, string article, string sap, string customer, string rack, string row,
        bool d2, bool p2d, bool fl, bool taping, string remarks, string editor)
    {
        if (stockId <= 0 || new[] { article, sap, customer, rack, row, editor }.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("All required fields and editing Employee Number must be provided.");
        using var c = Open(); using var tx = c.BeginTransaction();
        try
        {
            string before;
            using (var q = Cmd(c, "SELECT ArticleNumber,SAPNumber,CustomerName,Rack,RowNumber,Status2D,StatusP2D,StatusFL,StatusTaping,Remarks FROM PCB_Inventory WHERE StockID=@id", tx, ("@id", stockId)))
            using (var reader = q.ExecuteReader())
            {
                if (!reader.Read()) throw new InvalidOperationException("Stock record not found.");
                var data = new object[reader.FieldCount]; reader.GetValues(data);
                before = JsonSerializer.Serialize(data);
            }
            string after = JsonSerializer.Serialize(new object[] { article.Trim(), sap.Trim(), customer.Trim(), rack.Trim(), row.Trim(), d2 ? 1 : 0, p2d ? 1 : 0, fl ? 1 : 0, taping ? 1 : 0, remarks.Trim() });
            var when = DateTime.UtcNow.ToString("O");
            using (var q = Cmd(c, @"UPDATE PCB_Inventory SET ArticleNumber=@a,SAPNumber=@s,CustomerName=@c,Rack=@r,RowNumber=@row,
Status2D=@d2,StatusP2D=@p2d,StatusFL=@fl,StatusTaping=@tap,Remarks=@note,
IsEdited=1,LastEditedBy=@editor,LastEditedAt=@date WHERE StockID=@id", tx,
                ("@a", article.Trim()), ("@s", sap.Trim()), ("@c", customer.Trim()), ("@r", rack.Trim()), ("@row", row.Trim()),
                ("@d2", d2 ? 1 : 0), ("@p2d", p2d ? 1 : 0), ("@fl", fl ? 1 : 0), ("@tap", taping ? 1 : 0),
                ("@note", remarks.Trim()), ("@editor", editor.Trim()), ("@date", when), ("@id", stockId)))
                if (q.ExecuteNonQuery() != 1) throw new InvalidOperationException("Unable to update record.");
            using (var q = Cmd(c, "INSERT INTO PCB_EditHistory(StockID,EmployeeNumber,EditedAt,PreviousData,UpdatedData) VALUES(@id,@emp,@date,@before,@after)", tx,
                ("@id", stockId), ("@emp", editor.Trim()), ("@date", when), ("@before", before), ("@after", after))) q.ExecuteNonQuery();
            tx.Commit();
        }
        catch { tx.Rollback(); throw; }
    }
    public static void Backup(string destination)
    {
        if (Path.GetFullPath(destination).Equals(Path.GetFullPath(FilePath), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Choose a different backup path.");
        if (File.Exists(destination)) throw new InvalidOperationException("Backup file already exists. Choose a new filename.");
        using var source = Open(); using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination }.ToString());
        target.Open(); source.BackupDatabase(target); target.Close();
        using var verify = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Mode = SqliteOpenMode.ReadOnly }.ToString());
        verify.Open(); using var q = verify.CreateCommand(); q.CommandText = "PRAGMA integrity_check";
        if (!string.Equals(q.ExecuteScalar()?.ToString(), "ok", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Backup integrity check failed.");
    }
    public static void Restore(string backup)
    {
        if (!System.IO.File.Exists(backup)) throw new FileNotFoundException("Backup not found.");
        using (var verify = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup, Mode = SqliteOpenMode.ReadOnly }.ToString()))
        {
            verify.Open(); using var q = verify.CreateCommand(); q.CommandText = "PRAGMA integrity_check";
            if (q.ExecuteScalar()?.ToString() != "ok") throw new InvalidOperationException("Invalid backup database.");
            using var schema = verify.CreateCommand(); schema.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN ('PCB_Inventory','PCB_Transactions','Humidity_Readings')";
            if (Convert.ToInt32(schema.ExecuteScalar()) != 3) throw new InvalidOperationException("Backup does not contain required tables.");
        }
        var safety = Path.Combine(Folder, "pre_restore_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".db");
        Backup(safety);
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup, Mode = SqliteOpenMode.ReadOnly }.ToString());
        source.Open(); using var destination = Open(); source.BackupDatabase(destination);
    }
}

internal static class Classify
{
    public static (string label, Color color) Get(bool d, bool p, bool f, bool t)
    {
        if (d && p && f && t) return ("2D + P2D + FL + Taping", ColorTranslator.FromHtml("#22C55E"));
        if (d && p && f && !t) return ("2D + P2D + FL", ColorTranslator.FromHtml("#3B82F6"));
        if (!d && !p && !f && t) return ("Taping Only", ColorTranslator.FromHtml("#F97316"));
        if (d && !p && !f && !t) return ("2D Only", ColorTranslator.FromHtml("#EAB308"));
        if (!d && p && !f && !t) return ("P2D Only", ColorTranslator.FromHtml("#EF4444"));
        return f ? ("Other FL Combination", ColorTranslator.FromHtml("#9CA3AF")) : ("Other Without FL", ColorTranslator.FromHtml("#A855F7"));
    }
}

// Device protocol: a supported USB-Serial device emits a UTF-8 line at least every
// two seconds: {"deviceId":"DC001","humidityRH":8.5}. No sensor control.
// Other USB devices are ignored. This is a protocol contract, NOT universal USB sensing.
internal sealed class HumidityUsbReader : IDisposable
{
    private sealed class PortState
    {
        public SerialPort Port = null!;
        public string DeviceId = "";
        public double Humidity;
        public DateTime LastUtc;
        public StringBuilder Buffer = new();
    }
    private readonly Dictionary<string, PortState> ports = new(StringComparer.OrdinalIgnoreCase);
    public record Reading(string DeviceId, double Humidity, string PortName);
    public IReadOnlyList<Reading> GetReadings()
    {
        foreach (var port in SerialPort.GetPortNames())
        {
            if (ports.ContainsKey(port)) continue;
            try
            {
                var serial = new SerialPort(port, 9600, Parity.None, 8, StopBits.One) { ReadTimeout = 100, NewLine = "\n", DtrEnable = false, RtsEnable = false };
                serial.Open();
                ports[port] = new PortState { Port = serial };
            }
            catch (Exception) { /* Other application owns this port, or port unavailable. */ }
        }
        foreach (var key in ports.Keys.ToArray())
        {
            var state = ports[key];
            try
            {
                if (!SerialPort.GetPortNames().Contains(key, StringComparer.OrdinalIgnoreCase)) { state.Port.Dispose(); ports.Remove(key); continue; }
                int count = Math.Min(state.Port.BytesToRead, 2048);
                if (count > 0)
                {
                    var data = new byte[count];
                    state.Port.Read(data, 0, count);
                    state.Buffer.Append(Encoding.UTF8.GetString(data));
                    if (state.Buffer.Length > 4096) state.Buffer.Clear();
                    string value = state.Buffer.ToString();
                    int pos;
                    while ((pos = value.IndexOf('\n')) >= 0)
                    {
                        string line = value[..pos].Trim(); value = value[(pos + 1)..];
                        try
                        {
                            using var doc = JsonDocument.Parse(line);
                            var root = doc.RootElement;
                            if (!root.TryGetProperty("deviceId", out var idNode) || !root.TryGetProperty("humidityRH", out var rhNode)) continue;
                            string? id = idNode.GetString();
                            if (string.IsNullOrWhiteSpace(id) || id.Length > 32 || !id.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_')) continue;
                            if (!rhNode.TryGetDouble(out double rh) || !double.IsFinite(rh) || rh < 0 || rh > 100) continue;
                            state.DeviceId = id; state.Humidity = rh; state.LastUtc = DateTime.UtcNow;
                        }
                        catch (JsonException) { /* Ignore unrelated serial traffic. */ }
                        catch (InvalidOperationException) { /* Ignore malformed payload. */ }
                    }
                    state.Buffer.Clear(); state.Buffer.Append(value);
                }
            }
            catch (Exception) { state.Port.Dispose(); ports.Remove(key); }
        }
        return ports.Where(x => x.Value.DeviceId.Length > 0 && DateTime.UtcNow - x.Value.LastUtc < TimeSpan.FromSeconds(6))
            .GroupBy(x => x.Value.DeviceId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.Value.LastUtc).First())
            .OrderBy(x => x.Value.DeviceId, StringComparer.OrdinalIgnoreCase).Take(10)
            .Select(x => new Reading(x.Value.DeviceId, x.Value.Humidity, x.Key)).ToArray();
    }
    public void Dispose() { foreach (var s in ports.Values) s.Port.Dispose(); ports.Clear(); }
}

internal sealed class MainWindow : Form
{
    // Windows 8 Metro-inspired palette. Classification colors remain unchanged.
    static readonly Color MetroBlue = ColorTranslator.FromHtml("#00A4EF");
    static readonly Color MetroPurple = ColorTranslator.FromHtml("#68217A");
    static readonly Color MetroGreen = ColorTranslator.FromHtml("#00A300");
    static readonly Color MetroOrange = ColorTranslator.FromHtml("#F09609");
    static readonly Color MetroRed = ColorTranslator.FromHtml("#E51400");
    static readonly Color MetroCharcoal = ColorTranslator.FromHtml("#1D1D1D");
    static readonly Color MetroBackground = ColorTranslator.FromHtml("#F2F2F2");

    readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    readonly TabControl humidityTabs = new() { Dock = DockStyle.Fill };
    readonly HumidityUsbReader usb = new();
    readonly ToolStripStatusLabel statusText = new("Ready");
    readonly DataGridView inventory = Grid(), outGrid = Grid(), history = Grid();
    readonly Label dashboard = new() { Dock = DockStyle.Top, Height = 180, Font = new Font("Segoe UI", 12), Padding = new Padding(15) };
    readonly System.Windows.Forms.Timer displayTimer = new() { Interval = 5000 };
    readonly TextBox search = new() { Width = 240 };
    readonly TextBox outSearch = new() { Width = 240 };
    readonly TextBox outEmp = new() { Width = 170 };
    public MainWindow()
    {
        Text = "SMT PCB Dry Cabinet Manager — Offline";
        // A conventional borderless WinForms workstation window; no custom shortcuts or kiosk restrictions.
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(1024, 680);
        WindowState = FormWindowState.Maximized;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 11);
        ForeColor = MetroCharcoal;
        BackColor = MetroBackground;
        tabs.Font = new Font("Segoe UI", 11, FontStyle.Regular);
        tabs.ItemSize = new Size(120, 44);
        tabs.SizeMode = TabSizeMode.Fixed;
        StyleMetroTabs(tabs);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        root.Controls.Add(new Label { Text = "SMT PCB MANAGEMENT SYSTEM", Dock = DockStyle.Fill, BackColor = MetroBlue, ForeColor = Color.White, Font = new Font("Segoe UI", 17, FontStyle.Regular), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(18, 0, 0, 0) }, 0, 0);
        root.Controls.Add(tabs, 0, 1);
        var status = new StatusStrip { Dock = DockStyle.Fill, SizingGrip = false, BackColor = Color.FromArgb(225, 225, 225) };
        status.Items.Add(statusText); root.Controls.Add(status, 0, 2);
        Controls.Add(root);
        MakeDashboard(); MakeIn(); MakeOut(); MakeInventory(); MakeEdit(); MakeHistory(); MakeReports();
        TouchKeyboard.Register(this);
        displayTimer.Tick += (_, _) => RefreshHumidity();
        displayTimer.Interval = 1000;
        displayTimer.Start();
        tabs.SelectedIndexChanged += (_, _) => RefreshAll(); RefreshAll();
    }
    static void StyleMetroTabs(TabControl control)
    {
        control.DrawMode = TabDrawMode.OwnerDrawFixed;
        control.DrawItem += (_, e) =>
        {
            var selected = e.Index == control.SelectedIndex;
            var rect = control.GetTabRect(e.Index);
            using var brush = new SolidBrush(selected ? MetroPurple : MetroCharcoal);
            e.Graphics.FillRectangle(brush, rect);
            TextRenderer.DrawText(e.Graphics, control.TabPages[e.Index].Text,
                control.Font, rect, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        };
    }
    static DataGridView Grid() => new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, RowHeadersVisible = false, RowTemplate = { Height = 46 }, ColumnHeadersHeight = 46, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing };
    static NumericUpDown Num() => new() { Minimum = 0, Maximum = 100000000, Width = 170, Height = 42, ThousandsSeparator = true };
    static TextBox Txt(int width = 240) => new() { Width = width, MinimumSize = new Size(0, 36) };
    static Button Btn(string title, Action action)
    {
        var background = title.Contains("OUT", StringComparison.OrdinalIgnoreCase) ? MetroRed
            : title.Contains("SAVE", StringComparison.OrdinalIgnoreCase) ? MetroGreen
            : title.Contains("EDIT", StringComparison.OrdinalIgnoreCase) ? MetroOrange
            : MetroBlue;
        var b = new Button { Text = title, FlatStyle = FlatStyle.Flat, BackColor = background,
            ForeColor = Color.White, AutoSize = true, MinimumSize = new Size(145, 44), Margin = new Padding(8),
            UseVisualStyleBackColor = false };
        b.FlatAppearance.BorderSize = 0;
        b.Click += (_, _) => action();
        return b;
    }
    static FlowLayoutPanel Flow() => new() { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(12) };
    TabPage Page(string name) { var p = new TabPage(name) { Padding = new Padding(12), BackColor = MetroBackground }; tabs.TabPages.Add(p); return p; }
    static void Add(FlowLayoutPanel panel, string name, Control control)
    {
        var wrap = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(9) };
        wrap.Controls.Add(new Label { Text = name, AutoSize = true }); wrap.Controls.Add(control); panel.Controls.Add(wrap);
    }
    static ComboBox YesNo() { var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150, Height = 42 }; c.Items.AddRange(new object[] { "NO", "YES" }); c.SelectedIndex = 0; return c; }
    static bool Yes(ComboBox c) => c.SelectedIndex == 1;
    static void Error(Exception ex) => MessageBox.Show(ex.Message, "Operation failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
    void MakeDashboard()
    {
        var p = Page("Dashboard");
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(14) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 140));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        dashboard.Dock = DockStyle.Fill;
        dashboard.Font = new Font("Segoe UI", 12);
        layout.Controls.Add(dashboard, 0, 0);
        layout.Controls.Add(new Label { Text = "DRY CABINET HUMIDITY", Dock = DockStyle.Fill, Font = new Font("Segoe UI", 14), TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
        humidityTabs.Font = new Font("Segoe UI", 12);
        humidityTabs.ItemSize = new Size(145, 44);
        humidityTabs.SizeMode = TabSizeMode.Fixed;
        StyleMetroTabs(humidityTabs);
        layout.Controls.Add(humidityTabs, 0, 2);
        p.Controls.Add(layout);
    }
    void MakeIn()
    {
        var p = Page("PCB IN"); var form = Flow(); form.WrapContents = true; form.Dock = DockStyle.Fill; form.AutoScroll = true;
        var article = Txt(); var sap = Txt(); var customer = Txt(); var rack = Txt(140); var row = Txt(140);
        var full = Num(); full.Minimum = 1; full.Value = 1; var completed = Num(); var d2 = YesNo(); var p2d = YesNo(); var fl = YesNo(); var tape = YesNo();
        var remarks = Txt(320); var emp = Txt(170);
        Add(form, "Article Number *", article); Add(form, "SAP Number *", sap); Add(form, "Customer *", customer);
        Add(form, "Rack *", rack); Add(form, "Row *", row); Add(form, "Full PCB Qty *", full); Add(form, "Completed Qty *", completed);
        Add(form, "2D", d2); Add(form, "P2D", p2d); Add(form, "Factory Logic", fl); Add(form, "Taping completed", tape);
        Add(form, "Remarks (optional)", remarks); Add(form, "Employee Number *", emp);
        bool inSaving = false;
        var saveIn = Btn("SAVE PCB IN", () =>
        {
            if (inSaving) return;
            inSaving = true; saveIn.Enabled = false; statusText.Text = "Processing PCB IN...";
            try { Cursor = Cursors.WaitCursor; } catch { }
            try
            {
                long id = Db.In(article.Text, sap.Text, customer.Text, rack.Text, row.Text, (int)full.Value, (int)completed.Value, Yes(d2), Yes(p2d), Yes(fl), Yes(tape), remarks.Text, emp.Text);
                MessageBox.Show($"PCB IN saved successfully. Stock ID: {id}");
                foreach (var t in new[] { article, sap, customer, rack, row, remarks, emp }) t.Clear();
                full.Value = 1; completed.Value = 0; foreach (var c in new[] { d2, p2d, fl, tape }) c.SelectedIndex = 0;
                RefreshAll();
            }
            catch (Exception ex) { Error(ex); }
            finally { inSaving = false; saveIn.Enabled = true; statusText.Text = "Ready"; Cursor = Cursors.Default; }
        });
        form.Controls.Add(saveIn); p.Controls.Add(form);
    }
    void MakeOut()
    {
        var p = Page("PCB OUT"); var bar = Flow();
        Add(bar, "SAP Number *", outSearch);
        bar.Controls.Add(Btn("Find stock", RefreshOut));
        Add(bar, "Employee Number *", outEmp);
        var outStatus = new Label { Text = "Select a matching stock row. Full available quantity will be withdrawn.", AutoSize = true, Padding = new Padding(8) };
        bar.Controls.Add(outStatus);
        bool outSaving = false;
        var saveOut = Btn("CONFIRM FULL PCB OUT", () =>
        {
            if (outSaving) return;
            outSaving = true; statusText.Text = "Processing PCB OUT..."; Cursor = Cursors.WaitCursor;
            try
            {
                if (outGrid.SelectedRows.Count != 1 || outGrid.CurrentRow == null || outGrid.CurrentRow.IsNewRow) throw new ArgumentException("Select a stock record first.");
                long id = Convert.ToInt64(outGrid.CurrentRow.Cells["StockID"].Value);
                int available = Convert.ToInt32(outGrid.CurrentRow.Cells["AvailableQty"].Value);
                string sap = Convert.ToString(outGrid.CurrentRow.Cells["SAPNumber"].Value) ?? "";
                if (string.IsNullOrWhiteSpace(outEmp.Text)) throw new ArgumentException("Employee Number is required.");
                if (MessageBox.Show($"SAP: {sap}\nStock ID: {id}\nFull OUT quantity: {available} PCBs\nProceed?", "Confirm full OUT", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                int qty = Db.Out(id, outEmp.Text);
                MessageBox.Show($"PCB OUT saved successfully. Quantity: {qty}.");
                outEmp.Clear(); RefreshAll();
            }
            catch (Exception ex) { Error(ex); }
            finally { outSaving = false; statusText.Text = "Ready"; Cursor = Cursors.Default; }
        });
        bar.Controls.Add(saveOut);
        p.Controls.Add(outGrid); p.Controls.Add(bar);
    }
    void MakeInventory()
    {
        var p = Page("Inventory"); var bar = Flow(); Add(bar, "SAP Number", search);
        bar.Controls.Add(Btn("Search / Refresh", RefreshInventory)); p.Controls.Add(inventory); p.Controls.Add(bar);
    }
    void MakeEdit()
    {
        var p = Page("Edit Record"); var bar = Flow(); var editSap = Txt();
        var rows = Grid(); var editor = Txt(160);
        Add(bar, "SAP Number *", editSap);
        bar.Controls.Add(Btn("Find records", () =>
        {
            if (string.IsNullOrWhiteSpace(editSap.Text)) { MessageBox.Show("Enter SAP Number."); return; }
            rows.DataSource = Db.Table(InventorySql + " WHERE SAPNumber=@sap ORDER BY StockID DESC", ("@sap", editSap.Text.Trim()));
            ColorRows(rows); rows.ClearSelection(); rows.CurrentCell = null;
        }));
        Add(bar, "Editing Employee Number *", editor);
        bar.Controls.Add(Btn("EDIT SELECTED RECORD", () =>
        {
            if (rows.SelectedRows.Count != 1 || rows.CurrentRow == null) { MessageBox.Show("Select a record."); return; }
            if (string.IsNullOrWhiteSpace(editor.Text)) { MessageBox.Show("Enter editing Employee Number."); return; }
            using var dialog = new EditRecordDialog(rows.CurrentRow, editor.Text);
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                rows.DataSource = Db.Table(InventorySql + " WHERE SAPNumber=@sap ORDER BY StockID DESC", ("@sap", editSap.Text.Trim()));
                ColorRows(rows); RefreshAll();
            }
        }));
        p.Controls.Add(rows); p.Controls.Add(bar);
    }
    void MakeHistory()
    {
        var p = Page("History"); var bar = Flow(); var term = Txt(); var kind = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
        kind.Items.AddRange(new object[] { "ALL", "IN", "OUT" }); kind.SelectedIndex = 0;
        Add(bar, "Article / Employee / Stock ID", term); Add(bar, "Transaction type", kind);
        bar.Controls.Add(Btn("Search history", () => RefreshHistory(term.Text, kind.Text)));
        bar.Controls.Add(Btn("Export history CSV", () => Export(history))); p.Controls.Add(history); p.Controls.Add(bar);
    }
    void MakeReports()
    {
        var p = Page("Reports"); var bar = Flow();
        bar.Controls.Add(Btn("Export Inventory CSV", () => { RefreshInventory(); Export(inventory); }));
        bar.Controls.Add(Btn("Export History CSV", () => { RefreshHistory("", "ALL"); Export(history); }));
        p.Controls.Add(bar);
    }
    void MakeBackup()
    {
        var p = Page("Backup / Restore"); var bar = Flow();
        bar.Controls.Add(new Label { Text = "Restore is a maintenance operation. Close other application instances first.\nA safety backup is created automatically before restoring.", AutoSize = true, Padding = new Padding(10) });
        bar.Controls.Add(Btn("Create Backup", () =>
        {
            using var dlg = new SaveFileDialog { Filter = "SQLite database (*.db)|*.db", FileName = "pcb_backup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".db" };
            if (dlg.ShowDialog() != DialogResult.OK) return;
            try { Db.Backup(dlg.FileName); MessageBox.Show("Backup created and verified."); } catch (Exception ex) { Error(ex); }
        }));
        bar.Controls.Add(Btn("Restore Backup", () =>
        {
            using var dlg = new OpenFileDialog { Filter = "SQLite database (*.db)|*.db" }; if (dlg.ShowDialog() != DialogResult.OK) return;
            if (MessageBox.Show("This replaces current data. Continue? A safety backup will be created first.", "Restore database", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            try { displayTimer.Stop(); Db.Restore(dlg.FileName); Db.Init(); RefreshAll(); MessageBox.Show("Database restored. Restart the application before continuing."); Close(); }
            catch (Exception ex) { Error(ex); }
        })); p.Controls.Add(bar);
    }
    void RefreshAll() { RefreshInventory(); RefreshOut(); RefreshDashboard(); RefreshHistory("", "ALL"); RefreshHumidity(); }
    void RefreshDashboard()
    {
        try
        {
            var t = Db.Table(@"SELECT COUNT(*) AS StockRecords, COALESCE(SUM(AvailableQty),0) AS AvailablePCBs, COUNT(DISTINCT ArticleNumber) AS Articles,
(SELECT COUNT(*) FROM PCB_Transactions WHERE TransactionType='IN') AS InTransactions,
(SELECT COUNT(*) FROM PCB_Transactions WHERE TransactionType='OUT') AS OutTransactions FROM PCB_Inventory");
            var r = t.Rows[0];
            dashboard.Text = $"SMT PCB DRY CABINET MANAGER\n\nStock records: {r["StockRecords"]}     Available PCBs: {r["AvailablePCBs"]}     Unique articles: {r["Articles"]}\nIN transactions: {r["InTransactions"]}     OUT transactions: {r["OutTransactions"]}";
        }
        catch (Exception ex) { Error(ex); }
    }
    const string InventorySql = @"SELECT StockID,ArticleNumber,SAPNumber,CustomerName,Rack,RowNumber,FullPCBQty,CompletedQty,AvailableQty,Status2D,StatusP2D,StatusFL,StatusTaping,Remarks,EmployeeIN,DateIN,IsEdited,LastEditedBy,LastEditedAt FROM PCB_Inventory";
    void RefreshInventory()
    {
        try { inventory.DataSource = Db.Table(InventorySql + " WHERE @term='' OR SAPNumber=@term ORDER BY StockID DESC", ("@term", search.Text.Trim())); ColorRows(inventory); }
        catch (Exception ex) { Error(ex); }
    }
    void RefreshOut()
    {
        try { outGrid.DataSource = Db.Table(InventorySql + " WHERE AvailableQty>0 AND SAPNumber=@sap ORDER BY StockID DESC", ("@sap", outSearch.Text.Trim())); ColorRows(outGrid); outGrid.ClearSelection(); outGrid.CurrentCell = null; }
        catch (Exception ex) { Error(ex); }
    }
    static void ColorRows(DataGridView grid)
    {
        foreach (DataGridViewRow row in grid.Rows)
        {
            bool Get(string name) => Convert.ToInt32(row.Cells[name].Value) == 1;
            var (label, color) = Classify.Get(Get("Status2D"), Get("StatusP2D"), Get("StatusFL"), Get("StatusTaping"));
            row.DefaultCellStyle.BackColor = Color.FromArgb(80 + (color.R * 175 / 255), 80 + (color.G * 175 / 255), 80 + (color.B * 175 / 255));
            row.DefaultCellStyle.ForeColor = Color.Black;
            row.Tag = label;
        }
    }
    void RefreshHistory(string term, string kind)
    {
        try
        {
            history.DataSource = Db.Table(@"SELECT t.TransactionID,t.StockID,i.ArticleNumber,i.SAPNumber,t.TransactionType,t.PCBQuantity,t.EmployeeNumber,t.TransactionDate,t.Remarks
FROM PCB_Transactions t JOIN PCB_Inventory i ON i.StockID=t.StockID
WHERE (@kind='ALL' OR t.TransactionType=@kind) AND (@term='' OR CAST(t.StockID AS TEXT)=@term OR i.ArticleNumber LIKE @like OR t.EmployeeNumber LIKE @like)
ORDER BY t.TransactionID DESC", ("@kind", kind), ("@term", term.Trim()), ("@like", "%" + term.Trim() + "%"));
        }
        catch (Exception ex) { Error(ex); }
    }
    void RefreshHumidity()
    {
        try
        {
            var readings = usb.GetReadings();
            string? selected = humidityTabs.SelectedTab?.Name;
            var active = readings.Select(x => x.DeviceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (TabPage tab in humidityTabs.TabPages.Cast<TabPage>().ToArray())
                if (!active.Contains(tab.Name)) { humidityTabs.TabPages.Remove(tab); tab.Dispose(); }
            foreach (var r in readings)
            {
                var tab = humidityTabs.TabPages.Cast<TabPage>().FirstOrDefault(x => x.Name.Equals(r.DeviceId, StringComparison.OrdinalIgnoreCase));
                if (tab == null)
                {
                    tab = new TabPage(r.DeviceId) { Name = r.DeviceId, BackColor = Color.White, Padding = new Padding(24) };
                    tab.Controls.Add(new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 30), Name = "reading" });
                    humidityTabs.TabPages.Add(tab);
                }
                tab.Controls["reading"]!.Text = $"{r.Humidity:F1} %RH\n\n{r.DeviceId}  •  {r.PortName}";
            }
            if (selected != null && humidityTabs.TabPages.ContainsKey(selected)) humidityTabs.SelectedTab = humidityTabs.TabPages[selected];
        }
        catch (Exception) { /* Monitoring must never interrupt PCB transactions. */ }
    }
    static void Export(DataGridView grid)
    {
        if (grid.Columns.Count == 0) { MessageBox.Show("No report data available."); return; }
        using var dlg = new SaveFileDialog { Filter = "CSV files (*.csv)|*.csv", FileName = "SMT_Report_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        try
        {
            static string Csv(string? value) { value ??= ""; if (value.Length > 0 && "=+-@\t\r".Contains(value[0])) value = "'" + value; return "\"" + value.Replace("\"", "\"\"") + "\""; }
            using var writer = new StreamWriter(dlg.FileName, false, new UTF8Encoding(true));
            writer.WriteLine(string.Join(",", grid.Columns.Cast<DataGridViewColumn>().Select(c => Csv(c.HeaderText))));
            foreach (DataGridViewRow row in grid.Rows)
                if (!row.IsNewRow) writer.WriteLine(string.Join(",", row.Cells.Cast<DataGridViewCell>().Select(c => Csv(Convert.ToString(c.Value, CultureInfo.InvariantCulture)))));
            MessageBox.Show("CSV exported successfully.");
        }
        catch (Exception ex) { Error(ex); }
    }
    protected override void OnFormClosing(FormClosingEventArgs e) { displayTimer.Stop(); usb.Dispose(); base.OnFormClosing(e); }
}

internal sealed class EditRecordDialog : Form
{
    public EditRecordDialog(DataGridViewRow source, string employee)
    {
        Text = "Edit PCB Record"; Width = 740; Height = 720; AutoScroll = true;
        StartPosition = FormStartPosition.CenterParent; Font = new Font("Segoe UI", 11);
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true, Padding = new Padding(14) };
        Controls.Add(panel);
        string Value(string key) => Convert.ToString(source.Cells[key].Value) ?? "";
        TextBox Field(string label, string key)
        {
            var wrap = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(10) };
            wrap.Controls.Add(new Label { Text = label, AutoSize = true });
            var box = new TextBox { Width = 260, Text = Value(key), MinimumSize = new Size(0, 40) };
            wrap.Controls.Add(box); panel.Controls.Add(wrap); return box;
        }
        ComboBox Status(string label, string key)
        {
            var wrap = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(10) };
            wrap.Controls.Add(new Label { Text = label, AutoSize = true });
            var box = new ComboBox { Width = 180, DropDownStyle = ComboBoxStyle.DropDownList };
            box.Items.AddRange(new object[] { "NO", "YES" }); box.SelectedIndex = Convert.ToInt32(source.Cells[key].Value);
            wrap.Controls.Add(box); panel.Controls.Add(wrap); return box;
        }
        var article = Field("Article Number", "ArticleNumber"); var sap = Field("SAP Number", "SAPNumber");
        var customer = Field("Customer", "CustomerName"); var rack = Field("Rack", "Rack"); var row = Field("Row", "RowNumber");
        var d2 = Status("2D", "Status2D"); var p2d = Status("P2D", "StatusP2D");
        var fl = Status("FL", "StatusFL"); var tape = Status("Taping", "StatusTaping");
        var remarks = Field("Remarks", "Remarks");
        panel.Controls.Add(new Label { AutoSize = true, Padding = new Padding(10), Text = $"Full Qty: {Value("FullPCBQty")}  |  Completed: {Value("CompletedQty")}  |  Available: {Value("AvailableQty")}\nQuantities cannot be edited. Editing employee: {employee}" });
        var save = new Button { Text = "SAVE EDIT", Width = 210, Height = 60 };
        bool busy = false;
        save.Click += (_, _) =>
        {
            if (busy) return; busy = true; save.Enabled = false; Cursor = Cursors.WaitCursor;
            try
            {
                Db.Edit(Convert.ToInt64(source.Cells["StockID"].Value), article.Text, sap.Text, customer.Text, rack.Text, row.Text,
                    d2.SelectedIndex == 1, p2d.SelectedIndex == 1, fl.SelectedIndex == 1, tape.SelectedIndex == 1, remarks.Text, employee);
                MessageBox.Show("Record updated and tagged EDITED."); DialogResult = DialogResult.OK; Close();
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Edit failed"); }
            finally { busy = false; save.Enabled = true; Cursor = Cursors.Default; }
        };
        panel.Controls.Add(save);
        TouchKeyboard.Register(this);
    }
}
