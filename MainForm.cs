using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Vos3000Agent
{
    public class MainForm : Form
    {
        // ===== Bảng màu lấy từ logo GTINX CTS =====
        internal static readonly Color BrandDark = Color.FromArgb(0x00, 0x69, 0x35);   // #006935 xanh đậm
        internal static readonly Color BrandGreen = Color.FromArgb(0x00, 0xA3, 0x50);  // #00A350 xanh lá
        internal static readonly Color BrandRed = Color.FromArgb(0xE9, 0x01, 0x00);    // #E90100 đỏ
        internal static readonly Color BrandTint = Color.FromArgb(0xEC, 0xF6, 0xF0);   // nền nhạt pha xanh
        internal static readonly Color BrandTintDark = Color.FromArgb(0xDD, 0xEE, 0xE4);

        private Image _logoImg;   // logo vẽ header (đã cắt viền trắng)
        private Image _watermarkImg;   // logo mờ dạng bản quyền
        private Label _watermark;      // nhãn giữ watermark nằm trên page

        /// Ảnh logo đã làm mờ thành watermark (opacity thấp, màu xám xanh).
        private Image MakeWatermark(Image src, int targetW)
        {
            if (src == null) return null;
            try
            {
                int h = Math.Max(1, (int)((double)targetW * src.Height / src.Width));
                var bmp = new Bitmap(targetW, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using var g = Graphics.FromImage(bmp);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                var attr = new System.Drawing.Imaging.ImageAttributes();
                // chuyển logo về 1 màu xanh nhạt, alpha rất thấp
                var cm = new System.Drawing.Imaging.ColorMatrix(new[]
                {
                    new float[] { 0, 0, 0, 0, 0 },
                    new float[] { 0, 0, 0, 0, 0 },
                    new float[] { 0, 0, 0, 0, 0 },
                    new float[] { 0, 0, 0, 0.07f, 0 },   // alpha = 7%
                    new float[] { 0.0f, 0.41f, 0.21f, 0, 1 } // RGB -> xanh #006935
                });
                attr.SetColorMatrix(cm);
                g.DrawImage(src, new Rectangle(0, 0, targetW, h), 0, 0, src.Width, src.Height,
                    GraphicsUnit.Pixel, attr);
                return bmp;
            }
            catch { return null; }
        }

        /// Đặt watermark vào tab page đang chọn (góc phải dưới), nằm dưới các control khác.
        private void PlaceWatermark()
        {
            try
            {
                if (IsDisposed || _tabs == null) return;
                var page = _tabs.SelectedTab;
                if (page == null) return;

                if (_watermark == null || _watermark.IsDisposed)
                {
                    _watermark = new Label
                    {
                        AutoSize = false,
                        BackColor = Color.Transparent,
                        Enabled = false
                    };
                }
                if (_watermarkImg == null) _watermarkImg = MakeWatermark(_logoImg, 300);
                if (_watermarkImg == null) return;

                if (_watermark.Parent != page) page.Controls.Add(_watermark);
                _watermark.Size = _watermarkImg.Size;
                _watermark.Image = _watermarkImg;
                int margin = 18;
                _watermark.Location = new Point(
                    Math.Max(0, page.ClientSize.Width - _watermark.Width - margin),
                    Math.Max(0, page.ClientSize.Height - _watermark.Height - margin));
                _watermark.SendToBack();       // dưới cùng, không che control
                if (_watermark.Parent != null) _watermark.BringToFront();
                _watermark.SendToBack();
            }
            catch { }
        }

        private Settings _settings;
        private Dictionary<string, GuiProfile> _profiles;
        private PollWorker _worker;

        private readonly List<Dictionary<string, string>> _rows = new();
        private readonly Dictionary<string, int> _counts = new()
        {
            [Status.Success] = 0, [Status.Failed] = 0, [Status.Unknown] = 0, [Status.Simulated] = 0
        };

        // Các task tab (đã bỏ "Mở hạn mức" và "Khóa hạn mức")
        private static readonly (string Key, string Title)[] Tasks = System.Array.Empty<(string, string)>();

        // Controls
        private TabControl _tabs;
        private Label _statusLabel;
        private TabPage _historyPage, _healthPage, _configPage;   // nhóm "Khác" (dropdown)
        private TabPage[] _extraPages;
        private Button _moreBtn;          // nút "Khác ▾" nằm trong thanh tab
        private ContextMenuStrip _moreMenu;
        private ToolTip _statusTip;       // tooltip cho đèn trạng thái
        private readonly Dictionary<string, Label> _metricLabels = new();
        private TextBox _detectBox;
        private DataGridView _historyGrid;
        private readonly Dictionary<string, DataGridView> _taskGrids = new();

        // Config fields
        private CheckBox _chkTls, _chkSim, _chkAuto, _chkLive;
        // Xác minh TLS — áp dụng cho MỌI API của tool (nguồn số, healthcheck, API agent)
        private CheckBox _tlsVerifyChk;

        // DB của tool
        private TextBox _dbNameBox, _dbUserBox, _dbPassBox, _dbLogBox;
        private TextBox _vosDbNameBox;      // database của VOS3000 (e_customer)
        private NumericUpDown _dbPortNum;
        private Label _dbStateLbl;
        private System.Windows.Forms.Timer _statsTimer;
        private Label _sourceDot;         // đèn kết nối Server nguồn (healthcheck API)

        // Healthcheck
        private readonly HealthChecker _health = new();
        private TextBox _healthDomainBox, _healthEndpointBox;
        private NumericUpDown _healthIntervalNum, _healthTimeoutNum;
        private Label _healthStateLbl, _healthSummaryLbl;
        private DataGridView _healthGrid;
        private int _healthOkCount, _healthFailCount;
        private long _healthLastMs;

        public MainForm()
        {
            Text = "VOS3000 Agent";
            Width = 1160;
            Height = 760;
            MinimumSize = new Size(980, 640);
            Icon = LoadAppIcon();

            _settings = Config.LoadSettings();
            _profiles = Config.LoadProfiles();
            Config.EnsureProfiles();

            BuildUi();
            LoadConfigIntoUi();

            // Trạng thái gửi báo cáo Telegram hiển thị ngay trên tab Cấu hình
            TelegramNotifier.ReportDone += OnTelegramReportDone;

            // Lịch sync prefix tự chạy nếu mở app có bật + đã cấu hình URL
            if (_settings.prefix_auto_start && !string.IsNullOrWhiteSpace(_settings.prefix_api_url))
            {
                _prefixSyncer.Configure(_settings, m => MapAppendLog(m));
                _prefixSyncer.Start(_settings.prefix_interval_seconds);
            }

            FormClosing += (_, e) =>
            {
                _worker?.Stop();
                _health.Stop();
                _prefixSyncer.Stop();
            };

            // DB của tool: cấu hình + thống kê tổng quan khi app đã hiện giao diện
            ToolDb.Configure(_settings);
            CustomerDb.Configure(_settings);
            Shown += (_, _) => { PlaceMore(); LoadAppConfigFromDb(); RefreshDbStats(); };

            // Refresh thống kê runtime — mỗi 5s đọc lại DB, card Tổng quan luôn tươi
            _statsTimer = new System.Windows.Forms.Timer { Interval = 5000 };
            _statsTimer.Tick += (_, _) => { if (!IsDisposed) RefreshDbStats(); };
            _statsTimer.Start();
        }

        // ================= UI =================

        /// Icon app — ưu tiên bản trong %APPDATA% (bootstrap giải nén), rồi cạnh exe.
        private static Icon LoadAppIcon()
        {
            try
            {
                var p = Bootstrap.ResolveIcon();
                if (p != null && File.Exists(p)) return new Icon(p);
            }
            catch { }
            return SystemIcons.Application;
        }

        /// Logo GTINX CTS cho header — nạp PNG, cắt khoảng trắng cho gọn.
        private Image LoadLogo()
        {
            try
            {
                var p = Bootstrap.ResolveLogo();
                if (p == null || !File.Exists(p)) return null;
                using var src = new Bitmap(p);
                // tìm biên nội dung (bỏ viền trắng)
                int minX = src.Width, minY = src.Height, maxX = 0, maxY = 0;
                for (int y = 0; y < src.Height; y += 2)
                    for (int x = 0; x < src.Width; x += 2)
                    {
                        var c = src.GetPixel(x, y);
                        if (c.A > 40 && !(c.R > 240 && c.G > 240 && c.B > 240))
                        {
                            if (x < minX) minX = x;
                            if (y < minY) minY = y;
                            if (x > maxX) maxX = x;
                            if (y > maxY) maxY = y;
                        }
                    }
                if (maxX <= minX || maxY <= minY) return (Image)src.Clone();
                var rect = Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
                return src.Clone(rect, src.PixelFormat);
            }
            catch { return null; }
        }

        private void BuildUi()
        {
            // Header — nền xanh đậm theo logo, logo bên trái
            var header = new Panel { Dock = DockStyle.Top, Height = 64, Padding = new Padding(14, 8, 12, 6), BackColor = BrandDark };
            var logoBox = new PictureBox
            {
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.White,
                Location = new Point(14, 12),
                Size = new Size(190, 40)
            };
            _logoImg = LoadLogo();
            if (_logoImg != null) logoBox.Image = _logoImg;
            else logoBox.Visible = false;
            // bo góc nhẹ cho khung logo bằng vùng bo tròn
            var titleLbl = new Label
            {
                Text = "VOS3000 Agent",
                Font = new Font("Segoe UI", 16, FontStyle.Bold),
                ForeColor = Color.White,
                AutoSize = true,
                BackColor = Color.Transparent,
                Location = new Point(logoBox.Right + 16, 17)
            };
            _statusLabel = new Label
            {
                AutoSize = false,
                Size = new Size(20, 20),
                ForeColor = BrandGreen,
                Cursor = Cursors.Help,
                BackColor = BrandDark
            };
            _sourceDot = new Label
            {
                AutoSize = false,
                Size = new Size(20, 20),
                ForeColor = Color.Gray,
                Cursor = Cursors.Help,
                BackColor = BrandDark
            };
            _statusLabel.Paint += DrawStatusDot;
            _sourceDot.Paint += DrawStatusDot;
            _statusTip = new ToolTip { ShowAlways = true };
            _statusTip.SetToolTip(_statusLabel, Msg.Health.TooltipReady);
            _statusTip.SetToolTip(_sourceDot, Msg.Health.TooltipSourceNotPinged);
            header.Controls.Add(logoBox);
            header.Controls.Add(titleLbl);
            header.Controls.Add(_statusLabel);
            header.Controls.Add(_sourceDot);
            void PlaceDots()
            {
                _statusLabel.Location = new Point(header.Width - 20 - 14, 20);
                _sourceDot.Location = new Point(_statusLabel.Left - 20 - 6, 20);
            }
            header.Resize += (_, _) => PlaceDots();
            PlaceDots();
            Controls.Add(header);

            // Tab control
            _tabs = new TabControl { Dock = DockStyle.Fill };
            _tabs.Location = new Point(0, header.Height);
            _tabs.Size = new Size(ClientSize.Width, ClientSize.Height - header.Height);
            _tabs.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(_tabs);

            // Dashboard
            var dashboard = new TabPage("Tổng quan") { Padding = new Padding(10) };
            _tabs.TabPages.Add(dashboard);
            BuildDashboard(dashboard);

            var mapping = new TabPage("Add Số (VOS)") { Padding = new Padding(10) };
            _tabs.TabPages.Add(mapping);
            BuildMappingTab(mapping);

            // Khóa số (VOS3000 TCP 1202) — danh sách số từ Mapping Caller Prefix
            var lockNum = new TabPage("Khóa số (VOS)") { Padding = new Padding(10) };
            _tabs.TabPages.Add(lockNum);
            BuildNumberLockTab(lockNum);

            // Số API chưa có trên VOS3000 (chỉ lưu DB — không tự add)
            _missingPage = new TabPage("Số chưa có (VOS)") { Padding = new Padding(10) };
            _tabs.TabPages.Add(_missingPage);
            BuildMissingNumbersTab(_missingPage);

            // Số đang có trên VOS3000, đọc lại + gom theo từng account sau mỗi vòng Add Số
            _acctPage = new TabPage("Số theo account (VOS)") { Padding = new Padding(10) };
            _tabs.TabPages.Add(_acctPage);
            BuildAccountNumbersTab(_acctPage);

            // Nhóm "Khác" — chỉ mở qua dropdown, không chiếm chỗ trên thanh tab
            _historyPage = new TabPage("Lịch sử") { Padding = new Padding(10) };
            BuildHistory(_historyPage);
            _healthPage = new TabPage("Check API") { Padding = new Padding(10) };
            BuildHealthTab(_healthPage);
            _configPage = new TabPage("Cấu hình") { Padding = new Padding(12) };
            BuildConfig(_configPage);
            _extraPages = new[] { _historyPage, _healthPage, _configPage };
            SetupMoreMenu();
        }

        /// Thẻ thống kê: Panel viền trái màu + nhãn nhỏ + số lớn (không còn khung chữ GroupBox).
        private Panel MakeStatCard(string label, int width, out Label valueLbl)
        {
            var card = new Panel
            {
                Width = width,
                Height = 86,
                MinimumSize = new Size(width, 86),   // FlowLayout overflow: giữ đúng bề rộng thẻ
                MaximumSize = new Size(width, 86),
                Margin = new Padding(0, 0, 12, 12),
                BackColor = Color.White,
                Padding = new Padding(14, 0, 12, 0)
            };
            var accent = new Panel { Dock = DockStyle.Left, Width = 4, BackColor = BrandGreen };
            var lblCap = new Label
            {
                Text = label,
                Dock = DockStyle.Top,
                Height = 24,
                TextAlign = ContentAlignment.BottomLeft,
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = Color.FromArgb(0x5E, 0x7A, 0x6A)
            };
            var lblVal = new Label
            {
                Text = "0",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 21, FontStyle.Bold),
                ForeColor = BrandDark
            };
            card.Controls.Add(lblVal);
            card.Controls.Add(lblCap);
            card.Controls.Add(accent);
            // viền mảnh bo nhẹ
            card.Paint += (s, e) =>
            {
                var r = ((Panel)s).ClientRectangle; r.Width--; r.Height--;
                using var pen = new Pen(Color.FromArgb(0xE2, 0xEC, 0xE6));
                e.Graphics.DrawRectangle(pen, r);
            };
            valueLbl = lblVal;
            return card;
        }

        /// <summary>Đặt nút "Khác ▾" ngay sau tab cuối cùng trên thanh tab.</summary>
        private void PlaceMore()
        {
            if (IsDisposed || _tabs == null || _moreBtn == null || _tabs.TabCount == 0) return;
            try
            {
                var r = _tabs.GetTabRect(_tabs.TabCount - 1);   // tab cao nhất đang hiện
                _moreBtn.Size = new Size(
                    TextRenderer.MeasureText("Khác ▾", Font).Width + 14,
                    Math.Max(22, r.Height - 1));
                // GetTabRect là tọa độ trong _tabs — đổi về tọa độ Form trước khi đặt
                var pScreen = _tabs.PointToScreen(new Point(r.Right + 4, r.Top));
                _moreBtn.Location = PointToClient(pScreen);
            }
            catch { }
        }

        /// <summary>Dựng nút "Khác ▾" + menu chứa 3 trang phụ: Lịch sử / Check API / Cấu hình.</summary>
        private void SetupMoreMenu()
        {
            _moreBtn = new Button
            {
                Text = "Khác ▾",
                AutoSize = false,
                FlatStyle = FlatStyle.Flat,
                BackColor = SystemColors.Control,
                ForeColor = SystemColors.ControlText,
                Margin = new Padding(0),
                Cursor = Cursors.Hand
            };
            _moreBtn.FlatAppearance.BorderSize = 0;

            _moreMenu = new ContextMenuStrip { ShowImageMargin = false };
            void AddMenu(string text, TabPage page)
            {
                var item = new ToolStripMenuItem(text);
                item.Click += (_, _) => ShowExtraPage(page);
                _moreMenu.Items.Add(item);
            }
            AddMenu("Lịch sử", _historyPage);
            AddMenu("Check API", _healthPage);
            AddMenu("Cấu hình", _configPage);

            _moreBtn.Click += (_, _) => _moreMenu.Show(_moreBtn, new Point(0, _moreBtn.Height));
            Controls.Add(_moreBtn);
            _moreBtn.BringToFront();
            _tabs.SelectedIndexChanged += (_, _) => { HideExtraPagesExcept(_tabs.SelectedTab); PlaceMore(); };
            Resize += (_, _) => PlaceMore();
        }

        /// <summary>Hiện 1 tab phụ (thêm lại vào thanh tab rồi chọn).</summary>
        private void ShowExtraPage(TabPage page)
        {
            if (IsDisposed || page == null) return;
            if (!_tabs.TabPages.Contains(page))
                _tabs.TabPages.Add(page);
            _tabs.SelectedTab = page;
            PlaceMore();
        }

        /// <summary>Chỉ giữ tab đang xem trên thanh tab; các tab phụ khác gỡ ra (vẫn còn trong menu "Khác").</summary>
        private void HideExtraPagesExcept(TabPage keep)
        {
            if (_extraPages == null) return;
            for (int i = _tabs.TabPages.Count - 1; i >= 0; i--)
            {
                var p = _tabs.TabPages[i];
                if (p == keep) continue;
                if (Array.IndexOf(_extraPages, p) >= 0 && !ReferenceEquals(p, keep))
                    _tabs.TabPages.Remove(p);
            }
            PlaceMore();
        }

        private void BuildDashboard(TabPage page)
        {
            // Chỉ thống kê từ DB — các card tự wrap theo cửa sổ
            var cards = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Padding = new Padding(0, 6, 0, 0)
            };

            foreach (var (key, label, w) in new[]
            {
                ("fail_today_db", "Lỗi hôm nay", 180),
                ("lock_db", "Đã khóa", 150),
                ("unlock_db", "Đã mở", 150),
                ("sync_db", "Số add từ API", 190),
                ("mapped_db", "Số đang trên VOS", 200),
                ("mapped_today_db", "Mapping hôm nay", 190),
                ("mapped_history_db", "Tổng số từng mapping", 210)
            })
            {
                var card = MakeStatCard(label, w, out var lbl);
                _metricLabels[key] = lbl;
                cards.Controls.Add(card);
            }
            page.Controls.Add(cards);
        }

        private void BuildHistory(TabPage page)
        {
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40 };
            var bLoadDb = new Button { Text = "Tải log từ DB", AutoSize = true, Margin = new Padding(3) };
            var bExport = new Button { Text = "Xuất CSV", AutoSize = true, Margin = new Padding(3) };
            bLoadDb.Click += (_, _) => LoadHistoryFromDb();
            bExport.Click += (_, _) => ExportCsv();
            bar.Controls.Add(bLoadDb);
            bar.Controls.Add(bExport);
            page.Controls.Add(bar);

            _historyGrid = MakeGrid();
            _historyGrid.Dock = DockStyle.Fill;
            _historyGrid.Columns.Add("time", "Thời gian");
            _historyGrid.Columns.Add("type", "Nghiệp vụ");
            _historyGrid.Columns.Add("job", "Job ID");
            _historyGrid.Columns.Add("account", "Account");
            _historyGrid.Columns.Add("status", "Trạng thái");
            _historyGrid.Columns.Add("message", "Thông tin");
            page.Controls.Add(_historyGrid);
            _historyGrid.BringToFront();   // Fill dock phải nằm trước khi layout — tránh bị thanh Top che phần trên
        }

        private void BuildConfig(TabPage page)
        {
            // nhiều nhóm cấu hình xếp dọc -> cho phép cuộn khi cửa sổ thấp
            page.AutoScroll = true;

            // ===== Thứ tự: [DB tool] [Nguồn API] [Lưu + tùy chọn] =====
            var dbTable = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 2,
                Padding = new Padding(0)
            };
            dbTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            dbTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            int dbRow = 0;
            void DbRow(string label, Control ctl)
            {
                var lbl = new Label { Text = label, Anchor = AnchorStyles.Left, AutoSize = true, Margin = new Padding(0, 6, 12, 6) };
                ctl.Anchor = AnchorStyles.Left;
                dbTable.RowCount++;
                dbTable.Controls.Add(lbl, 0, dbRow);
                dbTable.Controls.Add(ctl, 1, dbRow);
                dbRow++;
            }

            // DB host dùng chung IP với "Server VOS3000" (_mapServerBox) — không có ô riêng
            _dbPortNum = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = _settings.db_port <= 0 ? 3306 : _settings.db_port, Width = 100 };
            _dbNameBox = new TextBox { Width = 300, Text = string.IsNullOrWhiteSpace(_settings.db_name) ? "vos_tool" : _settings.db_name };
            _vosDbNameBox = new TextBox { Width = 300, Text = string.IsNullOrWhiteSpace(_settings.vos_db_name) ? "vos3000db" : _settings.vos_db_name };
            _dbUserBox = new TextBox { Width = 300, Text = string.IsNullOrWhiteSpace(_settings.db_user) ? "toolEtl" : _settings.db_user };
            _dbPassBox = new TextBox { Width = 300, Text = _settings.db_pass ?? "", UseSystemPasswordChar = true };
            _mapPortNum = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = _settings.vos_port <= 0 ? 1202 : _settings.vos_port, Width = 100 };

            // ===== VOS3000 lên đầu (IP khai báo 1 lần — dùng chung cho DB + Add số + Khóa số) =====
            _mapServerBox = new TextBox { Width = 300, Text = string.IsNullOrWhiteSpace(_settings.vos_server) ? "172.29.101.32" : _settings.vos_server };
            DbRow("Server VOS3000", _mapServerBox);
            DbRow("Port (VOS)", _mapPortNum);

            // Tai khoan dang nhap VOS3000 (TCP 1202): nam trong packet login cua session template.
            // O trong = giu nguyen user/pass cua template.
            _vosUserBox = new TextBox { Width = 300, Text = _settings.vos_user ?? "" };
            _vosPassBox = new TextBox { Width = 300, Text = _settings.vos_pass ?? "", UseSystemPasswordChar = true };
            DbRow("User VOS3000 (1202)", _vosUserBox);
            DbRow("Password VOS3000 (1202)", _vosPassBox);
            _vosLoginLbl = new Label { AutoSize = true, ForeColor = Color.Gray, Margin = new Padding(0, 6, 0, 6) };
            var vosBar = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            var bVosLogin = new Button { Text = "Luu tai khoan login", AutoSize = true, Margin = new Padding(0, 2, 8, 2) };
            bVosLogin.Click += (_, _) => ApplyVosLogin();
            var bVosTest = new Button { Text = "Test login", AutoSize = true, Margin = new Padding(0, 2, 8, 2) };
            bVosTest.Click += (_, _) => TestVosLogin();
            var bVosShow = new Button { Text = "Xem login trong file", AutoSize = true, Margin = new Padding(0, 2, 8, 2) };
            bVosShow.Click += (_, _) => ShowCurrentVosLogin();
            vosBar.Controls.AddRange(new Control[] { bVosLogin, bVosTest, bVosShow, _vosLoginLbl });
            DbRow("", vosBar);

            DbRow("Port (DB)", _dbPortNum);
            DbRow("Tên database (tự đặt)", _dbNameBox);
            DbRow("DB VOS3000 (e_customer)", _vosDbNameBox);
            DbRow("User", _dbUserBox);
            DbRow("Password", _dbPassBox);

            page.Controls.Add(dbTable);

            var dbBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(0, 8, 0, 0) };
            _dbStateLbl = new Label
            {
                Text = Msg.State.None,
                AutoSize = true,
                Font = new Font("Segoe UI", 10, FontStyle.Bold),
                ForeColor = Color.Gray,
                Margin = new Padding(3, 9, 16, 3)
            };
            var bDbTest = new Button { Text = "Test kết nối", AutoSize = true, Margin = new Padding(3) };
            var bDbInit = new Button { Text = "Khởi tạo DB", AutoSize = true, Margin = new Padding(3) };
            var bDbGen = new Button { Text = "Gen bảng", AutoSize = true, Margin = new Padding(3) };
            bDbTest.Click += (_, _) => RunDbAction("test");
            bDbInit.Click += (_, _) => RunDbAction("init");
            bDbGen.Click += (_, _) => RunDbAction("gen");
            dbBar.Controls.Add(bDbTest);
            dbBar.Controls.Add(bDbInit);
            dbBar.Controls.Add(bDbGen);
            dbBar.Controls.Add(_dbStateLbl);
            page.Controls.Add(dbBar);

            _dbLogBox = new TextBox
            {
                Dock = DockStyle.Top,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 9),
                Height = 120,
                BackColor = Color.FromArgb(18, 18, 18),
                ForeColor = Color.FromArgb(200, 255, 200)
            };
            page.Controls.Add(_dbLogBox);

            // ===== Nguồn API danh sách số (sync prefix) =====
            var apiSec = BuildPrefixApiSection();
            page.Controls.Add(apiSec);

            // ===== Monitor Telegram (báo cáo sau mỗi vòng sync) =====
            var tgSec = BuildTelegramSection();
            page.Controls.Add(tgSec);

            // ===== Tùy chọn API dùng chung =====
            var tlsSec = BuildTlsSection();
            page.Controls.Add(tlsSec);

            var bSave = new Button { Text = "Lưu cấu hình", AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 10, 0, 0) };
            bSave.Click += (_, _) => SaveConfig();

            // Thanh nút dưới cùng: Lưu cấu hình + Tải tài liệu hướng dẫn (DOCX)
            var bottomBar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 46,
                Padding = new Padding(0, 10, 0, 0),
                FlowDirection = FlowDirection.LeftToRight
            };
            bSave.Margin = new Padding(0, 3, 10, 3);
            var bDoc = new Button
            {
                Text = "⬇  Tải tài liệu hướng dẫn (.docx)",
                AutoSize = true,
                Margin = new Padding(0, 3, 3, 3),
                BackColor = BrandGreen,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            bDoc.FlatAppearance.BorderSize = 0;
            bDoc.Click += (_, _) => DownloadGuideDocx();
            bottomBar.Controls.Add(bSave);
            bottomBar.Controls.Add(bDoc);

            // Dock WinForms xử lý theo thứ tự add NGƯỢC — AddRange phần tử cuối đợi dock trước (nên là dbTable để nằm trên cùng)
            page.Controls.AddRange(new Control[] { bottomBar, tlsSec, tgSec, apiSec, _dbLogBox, dbBar, dbTable });
        }

        /// Xuất tài liệu hướng dẫn cài đặt & sử dụng ra file .docx (OpenXML tối giản, không cần Office).
        private void DownloadGuideDocx()
        {
            using var dlg = new SaveFileDialog
            {
                DefaultExt = "docx",
                FileName = $"Huong-Dan-VOS3000-Agent-{DateTime.Now:yyyyMMdd}.docx",
                Filter = "Tài liệu Word (*.docx)|*.docx"
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                DocxGuide.Write(dlg.FileName, _settings);
                if (MessageBox.Show(this,
                        $"Đã tạo tài liệu hướng dẫn:\n{dlg.FileName}\n\nMở ngay không?",
                        "Tài liệu hướng dẫn", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dlg.FileName)
                    { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Tạo tài liệu lỗi: " + ex.Message, "Tài liệu hướng dẫn",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// Chua Đọc/ghi DB settings UI -> settings rồi chạy hành động DB.
        private void ApplyDbConfig()
        {
            // DB host dùng chung IP với Server VOS3000 — đọc từ ô Server VOS3000
            _settings.db_host = _mapServerBox != null ? _mapServerBox.Text.Trim() : _settings.db_host;
            _settings.db_port = (int)_dbPortNum.Value;
            _settings.db_name = _dbNameBox.Text.Trim();
            _settings.vos_db_name = _vosDbNameBox != null ? _vosDbNameBox.Text.Trim() : _settings.vos_db_name;
            _settings.db_user = _dbUserBox.Text.Trim();
            _settings.db_pass = _dbPassBox.Text.Trim();
            _settings.Normalize();
            CustomerDb.Configure(_settings);
        }

        private void DbAppendLog(string s)
        {
            if (IsDisposed) return;
            try { Invoke(() => _dbLogBox.AppendText(s + Environment.NewLine)); } catch { }
        }

        private void SetDbState(string text, Color color)
        {
            if (IsDisposed) return;
            try { Invoke(() => { _dbStateLbl.Text = text; _dbStateLbl.ForeColor = color; }); } catch { }
        }

        private async void RunDbAction(string kind)
        {
            ApplyDbConfig();
            string from = $"{_settings.db_host}:{_settings.db_port}/"+_settings.db_name;
            try { Config.SaveSettings(_settings); } catch { }
            ToolDb.Configure(_settings);

            switch (kind)
            {
                case "test":
                    SetDbState(Msg.State.Testing, Color.DarkOrange);
                    var (tok, to) = ToolDb.TestConnection();
                    DbAppendLog($"[TEST {from}] {(tok ? "✓ OK" : "✗ LỖI")} — {to}");
                    SetDbState(tok ? Msg.State.ConnectingOk : Msg.State.ConnectError, tok ? Color.Green : Color.Red);
                    break;
                case "init":
                    SetDbState(Msg.State.Initializing, Color.DarkOrange);
                    var (iok, io) = await ToolDb.InitDatabaseAsync(skipTables: false);
                    DbAppendLog($"[KHỞI TẠO {from}] {(iok ? "✓ OK" : "✗ LỖI")} — {io}");
                    SetDbState(iok ? Msg.State.DbReady : Msg.State.InitError, iok ? Color.Green : Color.Red);
                    break;
                case "gen":
                    SetDbState(Msg.State.Generating, Color.DarkOrange);
                    var (gok, go) = await ToolDb.GenTablesAsync();
                    DbAppendLog($"[GEN BẢNG {from}] {(gok ? "✓ OK" : "✗ LỖI")} — {go}");
                    SetDbState(gok ? Msg.State.TablesReady : Msg.State.GenError, gok ? Color.Green : Color.Red);
                    break;
            }
            if (ToolDb.HasConfig())
            {
                RefreshDbStats();
                if (kind == "init" || kind == "gen")
                {
                    // ghi cấu hình hiện tại vào t_config (nguồn phụ ngoài settings.json)
                    await ToolDb.SaveConfigToDbAsync(_settings);
                }
            }
        }

        /// Nạp cấu hình từ t_config (nếu có) về app — chỉ khi DB đã khởi tạo.
        private async void LoadAppConfigFromDb()
        {
            if (!ToolDb.HasConfig() || IsDisposed) return;
            var json = await ToolDb.LoadConfigJsonAsync();
            if (string.IsNullOrEmpty(json)) return;   // chưa từng lưu cấu hình lên DB
            try
            {
                var fromDb = System.Text.Json.JsonSerializer.Deserialize<Settings>(json, Config.JsonOpts);
                if (fromDb == null) return;

                // Không lấy db_* từ DB (tránh khoá bản thân ngoài DB khi DB đổi chỗ)
                fromDb.db_host = _settings.db_host;
                fromDb.db_port = _settings.db_port;
                fromDb.db_name = _settings.db_name;
                fromDb.db_user = _settings.db_user;
                fromDb.db_pass = _settings.db_pass;
                fromDb.vos_db_name = _settings.vos_db_name;
                fromDb.Normalize();

                bool changed = System.Text.Json.JsonSerializer.Serialize(fromDb, Config.JsonOpts)
                            != System.Text.Json.JsonSerializer.Serialize(_settings, Config.JsonOpts);
                _settings = fromDb;
                ToolDb.Configure(_settings);
                CustomerDb.Configure(_settings);
                ApplySettingsToUi();
                try { Config.SaveSettings(_settings); } catch { }
                if (changed) SetStatus("Đã nạp cấu hình từ DB (t_config).");
            }
            catch { /* JSON hỏng thì bỏ qua, dùng config file */ }
        }

        /// Đẩy giá trị _settings vào các ô nhập UI (sau khi nạp từ DB).
        private void ApplySettingsToUi()
        {
            try { Invoke(() =>
            {
                if (_mapServerBox != null)
                {
                    _mapServerBox.Text = _settings.vos_server;
                    _mapPortNum.Value = Math.Clamp(_settings.vos_port, 1, 65535);
                }
                if (_dbNameBox != null)
                {
                    _dbPortNum.Value = Math.Clamp(_settings.db_port <= 0 ? 3306 : _settings.db_port, 1, 65535);
                    _dbNameBox.Text = _settings.db_name ?? "";
                    if (_vosDbNameBox != null) _vosDbNameBox.Text = _settings.vos_db_name ?? "";
                    _dbUserBox.Text = _settings.db_user ?? "";
                    _dbPassBox.Text = _settings.db_pass ?? "";
                }
                if (_prefixApiBox != null)
                {
                    _prefixApiBox.Text = _settings.prefix_api_url ?? "";
                    _prefixKeyBox.Text = _settings.prefix_api_key ?? "";
                    _prefixFieldBox.Text = string.IsNullOrWhiteSpace(_settings.prefix_api_field) ? "data.phone" : _settings.prefix_api_field;
                    _prefixIntervalNum.Value = Math.Clamp(_settings.prefix_interval_seconds <= 0 ? 60 : _settings.prefix_interval_seconds, 10, 86400);
                    _prefixAutoChk.Checked = _settings.prefix_auto_start;
                    if (_prefixCleanChk != null) _prefixCleanChk.Checked = _settings.prefix_remove_invalid;
                }
                if (_tgEnableChk != null)
                {
                    _tgEnableChk.Checked = _settings.telegram_enabled;
                    _tgTokenBox.Text = _settings.telegram_bot_token ?? "";
                    _tgChatBox.Text = _settings.telegram_chat_id ?? "";
                    _tgNumbersChk.Checked = _settings.telegram_include_numbers;
                    _tgOnlyChangeChk.Checked = _settings.telegram_only_on_change;
                    _tgMaxNum.Value = Math.Clamp(_settings.telegram_max_numbers <= 0 ? 200 : _settings.telegram_max_numbers, 10, 2000);
                    SetTgState(TelegramNotifier.Enabled(_settings) ? "● Sẵn sàng" : "● Chưa cấu hình",
                        TelegramNotifier.Enabled(_settings) ? Color.Green : Color.Gray);
                }
                // Mốc sync: hiển thị mốc hiện tại (đọc lại sau khi app kết nối DB xong)
                RefreshWatermarkLabel();
                // _chkTls… đã bỏ khỏi UI — không cần đẩy giá trị
                if (_healthDomainBox != null)
                {
                    _healthDomainBox.Text = _settings.health_domain ?? "";
                    _healthEndpointBox.Text = _settings.health_endpoint ?? "";
                    _healthIntervalNum.Value = Math.Clamp(_settings.health_interval_seconds, 1, 3600);
                    _healthTimeoutNum.Value = Math.Clamp(_settings.health_timeout_seconds, 1, 120);
                }
                if (_tlsVerifyChk != null)
                    _tlsVerifyChk.Checked = _settings.verify_tls;
                RefreshWatermarkLabel();
            }); } catch { }
        }

        private void BuildHealthTab(TabPage page)
        {
            // --- Hàng nhập domain + endpoint ---
            var inputTable = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 2
            };
            inputTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            inputTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            int r = 0;
            void AddRow(string label, Control ctl)
            {
                var lbl = new Label
                {
                    Text = label,
                    Anchor = AnchorStyles.Left,
                    AutoSize = true,
                    Margin = new Padding(0, 8, 12, 8)
                };
                ctl.Anchor = AnchorStyles.Left | AnchorStyles.Right;
                inputTable.RowCount++;
                inputTable.Controls.Add(lbl, 0, r);
                inputTable.Controls.Add(ctl, 1, r);
                r++;
            }

            _healthDomainBox = new TextBox
            {
                Text = string.IsNullOrWhiteSpace(_settings.health_domain)
                    ? (_settings.api_base_url ?? "")
                    : _settings.health_domain
            };
            _healthEndpointBox = new TextBox
            {
                Text = string.IsNullOrWhiteSpace(_settings.health_endpoint) ? "/health" : _settings.health_endpoint
            };
            _healthIntervalNum = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 3600,
                Value = Math.Clamp(_settings.health_interval_seconds <= 0 ? 5 : _settings.health_interval_seconds, 1, 3600),
                Width = 80
            };
            _healthTimeoutNum = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 120,
                Value = Math.Clamp(_settings.health_timeout_seconds <= 0 ? 5 : _settings.health_timeout_seconds, 1, 120),
                Width = 80
            };
            // "Xác minh chứng chỉ TLS" đã chuyển sang tab Cấu hình (áp dụng cho mọi API)

            AddRow("Domain API nguồn", _healthDomainBox);
            AddRow("Endpoint (health)", _healthEndpointBox);
            AddRow("Chu kỳ ping (giây)", _healthIntervalNum);
            AddRow("Timeout (giây)", _healthTimeoutNum);
            page.Controls.Add(inputTable);

            // --- Thanh trạng thái + nút ---
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(0, 6, 0, 0) };
            _healthStateLbl = new Label
            {
                Text = Msg.State.NotRun,
                AutoSize = true,
                Font = new Font("Segoe UI", 11, FontStyle.Bold),
                ForeColor = Color.Gray,
                Margin = new Padding(3, 8, 16, 3)
            };
            var bStart = new Button { Text = "Bắt đầu ping", AutoSize = true, Margin = new Padding(3) };
            var bStop = new Button { Text = "Dừng ping", AutoSize = true, Margin = new Padding(3) };
            var bPingOnce = new Button { Text = "Ping 1 lần", AutoSize = true, Margin = new Padding(3) };
            var bClear = new Button { Text = "Xoá log", AutoSize = true, Margin = new Padding(3) };
            bStart.Click += (_, _) => StartHealth();
            bStop.Click += (_, _) => StopHealth();
            bPingOnce.Click += (_, _) => PingOnce();
            bClear.Click += (_, _) => _healthGrid.Rows.Clear();
            bar.Controls.Add(bStart);
            bar.Controls.Add(bStop);
            bar.Controls.Add(bPingOnce);
            bar.Controls.Add(bClear);
            bar.Controls.Add(_healthStateLbl);
            page.Controls.Add(bar);

            _healthSummaryLbl = new Label
            {
                Text = "Thành công: 0   |   Lỗi: 0   |   Lần cuối: —",
                Dock = DockStyle.Top,
                Height = 24,
                ForeColor = SystemColors.GrayText
            };
            page.Controls.Add(_healthSummaryLbl);

            // --- Bảng kết quả ---
            _healthGrid = MakeGrid();
            _healthGrid.Dock = DockStyle.Fill;
            _healthGrid.Columns.Add("time", "Thời gian");
            _healthGrid.Columns.Add("url", "URL");
            _healthGrid.Columns.Add("status", "HTTP");
            _healthGrid.Columns.Add("ms", "Latency (ms)");
            _healthGrid.Columns.Add("ok", "Kết quả");
            _healthGrid.Columns.Add("message", "Chi tiết");
            foreach (DataGridViewColumn c in _healthGrid.Columns)
                c.FillWeight = c.Name switch
                {
                    "time" => 15,
                    "url" => 30,
                    "status" => 8,
                    "ms" => 12,
                    "ok" => 12,
                    _ => 38
                };
            page.Controls.Add(_healthGrid);
            _healthGrid.BringToFront();    // tránh bị thanh nút/summary dock Top che phần trên

            _health.Result += OnHealthResult;
            ApplyHealthConfig();
        }

        // Mapping Prefix (VOS3000 TCP 1202)
        private TextBox _mapServerBox;
        private NumericUpDown _mapPortNum;
        private TextBox _vosUserBox, _vosPassBox;
        private Label _vosLoginLbl;
        private Label _mapStateLbl;
        private TextBox _mapLogBox;
        private readonly List<Button> _mapButtons = new();

        // API danh sách số + lịch
        private TextBox _prefixApiBox, _prefixKeyBox, _prefixFieldBox;
        private NumericUpDown _prefixIntervalNum;
        private CheckBox _prefixAutoChk;
        private CheckBox _prefixCleanChk;

        private Label _prefixWatermarkLbl;
        private Label _prefixSchedLbl;
        private readonly PrefixApiSyncer _prefixSyncer = new();

        // Monitor Telegram (báo cáo sau mỗi vòng sync)
        private CheckBox _tgEnableChk, _tgNumbersChk, _tgOnlyChangeChk;
        private TextBox _tgTokenBox, _tgChatBox;
        private NumericUpDown _tgMaxNum;
        private Label _tgStateLbl;
        private Button _tgTestBtn;

        // Khóa số (VOS3000 TCP 1202)
        private DataGridView _lockGrid;
        private TextBox _lockLogBox, _lockSearchBox;
        private Label _lockStateLbl, _lockPageLbl;
        private NumericUpDown _lockPageNum;
        private readonly List<Button> _lockButtons = new();
        private List<string> _lockAll = new();        // toan bo danh sach so (gop moi account, moi so 1 lan)
        private List<MappingEntry> _lockMappings = new();   // account call-out (gateway mapping) doc lan "load" gan nhat
        private List<RoutingEntry> _lockRoutings = new();   // account call-in (gateway routing / settlement)
        private int _lockPage;
        private readonly HashSet<string> _lockPicked = new();   // gốc số (TailKey) đang được tích chọn để khóa/mở
        private const int LockPageSize = 20;

        // Số API chưa có trên VOS3000 (t_number_missing)
        private TabPage _missingPage;
        private DataGridView _missingGrid;
        private TextBox _missingSearchBox, _missingLogBox;
        private Label _missingStateLbl, _missingCountLbl;
        private List<MissingNumberRow> _missingAll = new();
        private readonly List<Button> _missingButtons = new();
        private bool _missingLoading;

        // Số đang có trên VOS3000, tổng hợp theo từng account (t_account_number_set)
        private TabPage _acctPage;
        private DataGridView _acctGrid;
        private TextBox _acctSearchBox, _acctLogBox;
        private Label _acctStateLbl, _acctCountLbl;
        private ComboBox _acctAccountBox, _acctDirectionBox;
        private List<AccountNumberSetRow> _acctRows = new();
        private readonly List<Button> _acctButtons = new();
        private bool _acctLoading;

        private void BuildMappingTab(TabPage page)
        {
            // Thứ tự Dock: Fill trước, rồi Top (đúng như các tab khác)
            _mapLogBox = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                ReadOnly = true,
                WordWrap = false,
                Font = new Font("Consolas", 9),
                Dock = DockStyle.Fill
            };
            page.Controls.Add(_mapLogBox);

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(0, 6, 0, 0) };
            _mapStateLbl = new Label
            {
                Text = Msg.State.NoAction,
                AutoSize = true,
                Font = new Font("Segoe UI", 11, FontStyle.Bold),
                ForeColor = Color.Gray,
                Margin = new Padding(3, 8, 16, 3)
            };
            var bLoad = new Button { Text = "Load prefix", AutoSize = true, Margin = new Padding(3) };
            var bClear = new Button { Text = "Xoá log", AutoSize = true, Margin = new Padding(3) };
            bLoad.Click += (_, _) => RunMapping("load");
            bClear.Click += (_, _) => _mapLogBox.Clear();
            _mapButtons.AddRange(new[] { bLoad });
            bar.Controls.Add(bLoad);
            bar.Controls.Add(bClear);
            bar.Controls.Add(_mapStateLbl);
            page.Controls.Add(bar);

            // ---- Hàng nút API + lịch ----
            var apiBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(0, 4, 0, 0) };
            _prefixSchedLbl = new Label
            {
                Text = Msg.State.ScheduleOff,
                AutoSize = true,
                Font = new Font("Segoe UI", 11, FontStyle.Bold),
                ForeColor = Color.Gray,
                Margin = new Padding(3, 8, 16, 3)
            };
            var bApiOnce = new Button { Text = "API: Lấy + Add ngay 1 lần", AutoSize = true, Margin = new Padding(3) };
            var bSchedStart = new Button { Text = "Bắt đầu lịch", AutoSize = true, Margin = new Padding(3) };
            var bSchedStop = new Button { Text = "Tạm dừng lịch", AutoSize = true, Margin = new Padding(3) };
            bApiOnce.Click += (_, _) => RunPrefixOnce();
            bSchedStart.Click += (_, _) => StartPrefixSchedule();
            bSchedStop.Click += (_, _) => StopPrefixSchedule();
            apiBar.Controls.Add(bApiOnce);
            apiBar.Controls.Add(bSchedStart);
            apiBar.Controls.Add(bSchedStop);
            apiBar.Controls.Add(_prefixSchedLbl);
            page.Controls.Add(apiBar);

            // (Server VOS3000 + Port đã chuyển vào tab Cấu hình — cùng chỗ khai báo DB;
            // ô URL / key / field / chu kỳ của nguồn API cũng nằm ở tab Cấu hình)

            _prefixSyncer.Result += OnPrefixSyncResult;
        }

        /// Tao control nhom "Nguon API danh sach so" (o day goi trong tab Cau hinh).
        private Control BuildPrefixApiSection()
        {
            var sec = new GroupBox
            {
                Text = "Nguồn API danh sách số (sync prefix)",
                Dock = DockStyle.Top,
                AutoSize = true,
                Padding = new Padding(10, 0, 10, 10)
            };
            var t = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 2
            };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            int r = 0;
            void AddRow(string label, Control ctl)
            {
                var lbl = new Label { Text = label, Anchor = AnchorStyles.Left, AutoSize = true, Margin = new Padding(0, 6, 12, 6) };
                ctl.Anchor = AnchorStyles.Left | AnchorStyles.Right;
                t.RowCount++;
                t.Controls.Add(lbl, 0, r);
                t.Controls.Add(ctl, 1, r);
                r++;
            }

            _prefixApiBox = new TextBox
            {
                Text = _settings.prefix_api_url ?? ""
            };
            _prefixKeyBox = new TextBox
            {
                Text = _settings.prefix_api_key ?? "",
                UseSystemPasswordChar = true
            };            _prefixFieldBox = new TextBox
            {
                Text = string.IsNullOrWhiteSpace(_settings.prefix_api_field) ? "data.phone" : _settings.prefix_api_field
            };
            _prefixIntervalNum = new NumericUpDown
            {
                Minimum = 10,
                Maximum = 86400,
                Value = Math.Clamp(_settings.prefix_interval_seconds <= 0 ? 60 : _settings.prefix_interval_seconds, 10, 86400),
                Width = 90
            };
            _prefixAutoChk = new CheckBox { Text = "Tự chạy lịch sync khi mở app", Checked = _settings.prefix_auto_start, AutoSize = true };
            _prefixCleanChk = new CheckBox
            {
                Text = "Dọn số rác trên server (số đầu 0, số giả lập, không bắt đầu 84)",
                Checked = _settings.prefix_remove_invalid,
                AutoSize = true
            };

            AddRow("URL API danh sách số", _prefixApiBox);
            AddRow("API key (header api-key)", _prefixKeyBox);
            AddRow("Field JSON (path)", _prefixFieldBox);
            AddRow("Chu kỳ lịch đồng bộ (giây)", _prefixIntervalNum);
            AddRow("", _prefixAutoChk);
            AddRow("", _prefixCleanChk);

            var wmBar = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Padding = new Padding(0, 2, 0, 2) };
            _prefixWatermarkLbl = new Label
            {
                AutoSize = true,
                ForeColor = Color.Gray,
                Margin = new Padding(3, 8, 12, 3)
            };
            var bWmReset = new Button { Text = "Xoá mốc (lần sau gọi full)", AutoSize = true, Margin = new Padding(3) };
            bWmReset.Click += (_, _) => ResetSyncWatermark();
            wmBar.Controls.Add(_prefixWatermarkLbl);
            wmBar.Controls.Add(bWmReset);
            t.RowCount++;
            t.Controls.Add(new Label { Text = "", AutoSize = true }, 0, r);
            t.Controls.Add(wmBar, 1, r);
            r++;

            sec.Controls.Add(t);
            return sec;
        }

        private void RefreshWatermarkLabel()
        {
            if (_prefixWatermarkLbl == null || _prefixWatermarkLbl.IsDisposed) return;
            long? wm = null;
            string how = null;
            try { wm = SyncWatermark.Read(_settings, out how); } catch { }
            _prefixWatermarkLbl.Text = wm.HasValue
                ? $"Mốc hiện tại: {wm.Value} (lưu ở {how})"
                : "Mốc hiện tại: chưa có — lần sync tới sẽ gọi full";
        }

        private void ResetSyncWatermark()
        {
            ApplyMapConfig();
            try { SyncWatermark.Reset(_settings); } catch { }
            RefreshWatermarkLabel();
            MapAppendLog("  Đã xoá mốc last_created_at — lần sync tới sẽ gọi full danh sách.");
        }

        /// Nhóm "Monitor Telegram": sau mỗi vòng gọi API lấy danh sách số + update VOS3000,
        /// bot gửi trạng thái + danh sách số đã xử lý + thời gian hoàn thành vào group.
        private Control BuildTelegramSection()
        {
            var sec = new GroupBox
            {
                Text = "Monitor Telegram — báo cáo sau mỗi vòng đồng bộ số",
                Dock = DockStyle.Top,
                AutoSize = true,
                Padding = new Padding(10, 0, 10, 10)
            };
            var t = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 2
            };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            int r = 0;
            void AddRow(string label, Control ctl)
            {
                var lbl = new Label { Text = label, Anchor = AnchorStyles.Left, AutoSize = true, Margin = new Padding(0, 6, 12, 6) };
                ctl.Anchor = AnchorStyles.Left | AnchorStyles.Right;
                t.RowCount++;
                t.Controls.Add(lbl, 0, r);
                t.Controls.Add(ctl, 1, r);
                r++;
            }

            _tgEnableChk = new CheckBox
            {
                Text = "Bật gửi báo cáo ra group Telegram sau mỗi vòng sync",
                Checked = _settings.telegram_enabled,
                AutoSize = true
            };
            _tgTokenBox = new TextBox
            {
                Text = _settings.telegram_bot_token ?? "",
                UseSystemPasswordChar = true
            };
            _tgChatBox = new TextBox { Text = _settings.telegram_chat_id ?? "" };
            _tgNumbersChk = new CheckBox
            {
                Text = "Kèm danh sách số đã xử lý trong tin nhắn",
                Checked = _settings.telegram_include_numbers,
                AutoSize = true
            };
            _tgOnlyChangeChk = new CheckBox
            {
                Text = "Chỉ bắn khi có số thay đổi — bỏ tick để nhận tin ở MỌI vòng (kể cả không đổi)",
                Checked = _settings.telegram_only_on_change,
                AutoSize = true
            };
            _tgMaxNum = new NumericUpDown
            {
                Minimum = 10,
                Maximum = 2000,
                Increment = 10,
                Value = Math.Clamp(_settings.telegram_max_numbers <= 0 ? 200 : _settings.telegram_max_numbers, 10, 2000),
                Width = 90
            };

            AddRow("", _tgEnableChk);
            AddRow("Bot token", _tgTokenBox);
            AddRow("Chat ID nhóm (dạng -100…)", _tgChatBox);
            AddRow("", _tgNumbersChk);
            AddRow("Số tối đa liệt kê / danh sách", _tgMaxNum);
            AddRow("", _tgOnlyChangeChk);

            var warn = new Label
            {
                Text = "Lưu ý: nếu bật “Chỉ bắn khi có số thay đổi”, vòng sync không đổi số nào sẽ KHÔNG gửi tin " +
                       "(log tab Add Số vẫn ghi “Telegram: bỏ qua…”). Vòng sync LỖI thì luôn được bắn.",
                AutoSize = true,
                ForeColor = Color.FromArgb(0x8A, 0x6D, 0x00),
                Margin = new Padding(0, 2, 6, 4)
            };
            t.RowCount++;
            t.Controls.Add(new Label { Text = "", AutoSize = true }, 0, r);
            t.Controls.Add(warn, 1, r);
            r++;

            var bar = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Padding = new Padding(0, 4, 0, 2) };
            _tgTestBtn = new Button { Text = "📨 Gửi thử", AutoSize = true, Margin = new Padding(3) };
            _tgTestBtn.Click += async (_, _) => await SendTelegramTestAsync();
            _tgStateLbl = new Label
            {
                Text = TelegramNotifier.Enabled(_settings) ? "● Sẵn sàng" : "● Chưa cấu hình",
                AutoSize = true,
                ForeColor = TelegramNotifier.Enabled(_settings) ? Color.Green : Color.Gray,
                Margin = new Padding(10, 9, 3, 3)
            };
            var hint = new Label
            {
                Text = "Lấy chat ID: thêm bot vào nhóm → gửi 1 tin trong nhóm → mở " +
                       "https://api.telegram.org/bot<TOKEN>/getUpdates để xem id âm (-100…).",
                AutoSize = true,
                ForeColor = Color.Gray,
                Margin = new Padding(3, 9, 3, 3)
            };
            bar.Controls.Add(_tgTestBtn);
            bar.Controls.Add(_tgStateLbl);
            t.RowCount++;
            t.Controls.Add(new Label { Text = "", AutoSize = true }, 0, r);
            t.Controls.Add(bar, 1, r);
            r++;
            t.RowCount++;
            t.Controls.Add(new Label { Text = "", AutoSize = true }, 0, r);
            t.Controls.Add(hint, 1, r);
            r++;

            sec.Controls.Add(t);
            return sec;
        }

        /// Gửi 1 tin test để kiểm tra token / chat id / quyền của bot trong nhóm.
        private async Task SendTelegramTestAsync()
        {
            ApplyMapConfig();   // đọc token/chat id đang nhập vào _settings
            if (!TelegramNotifier.Enabled(_settings))
            {
                MessageBox.Show(this, "Bật monitor, nhập Bot token và Chat ID nhóm trước.", "Telegram",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            SetTgState("● Đang gửi…", Color.DarkOrange);
            if (_tgTestBtn != null) _tgTestBtn.Enabled = false;
            try
            {
                var err = await Task.Run(() =>
                    TelegramNotifier.SendHtml(_settings, TelegramNotifier.BuildTestMessage(_settings)));
                if (err == null)
                {
                    SetTgState("● Đã gửi test OK", Color.Green);
                    MessageBox.Show(this, "Đã gửi tin test vào nhóm Telegram.", "Telegram",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    SetTgState("● Lỗi gửi test", Color.Red);
                    MessageBox.Show(this, "Gửi test lỗi:\n" + err, "Telegram",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            finally
            {
                if (_tgTestBtn != null && !_tgTestBtn.IsDisposed) _tgTestBtn.Enabled = true;
            }
        }

        private void SetTgState(string text, Color color)
        {
            if (IsDisposed || _tgStateLbl == null) return;
            try { _tgStateLbl.Text = text; _tgStateLbl.ForeColor = color; } catch { }
        }

        /// Kết quả gửi báo cáo Telegram sau mỗi vòng sync (gọi từ thread nền).
        private void OnTelegramReportDone(bool ok, string error)
        {
            if (IsDisposed) return;
            try
            {
                Invoke(() =>
                {
                    if (error == "skip-no-change")
                        SetTgState($"● Bỏ qua — không có số đổi ({DateTime.Now:HH:mm:ss})", Color.Gray);
                    else if (ok)
                        SetTgState($"● Đã gửi báo cáo {DateTime.Now:HH:mm:ss}", Color.Green);
                    else
                        SetTgState($"● Gửi lỗi: {PrefixApiSyncer.Trunc(error, 40)}", Color.Red);
                });
            }
            catch { }
        }

        /// Tùy chọn API dùng chung: xác minh chứng chỉ TLS cho MỌI request HTTP của tool
        /// (nguồn danh sách số, healthcheck server nguồn, API agent).
        private Control BuildTlsSection()
        {
            var sec = new GroupBox
            {
                Text = "Kết nối API (áp dụng cho mọi API của tool)",
                Dock = DockStyle.Top,
                AutoSize = true,
                Padding = new Padding(10, 0, 10, 10)
            };
            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(2, 6, 0, 2)
            };
            _tlsVerifyChk = new CheckBox
            {
                Text = "Xác minh chứng chỉ TLS (bỏ tick nếu server dùng chứng chỉ tự ký / CA nội bộ)",
                Checked = _settings.verify_tls,
                AutoSize = true,
                Margin = new Padding(2, 2, 2, 2)
            };
            var hint = new Label
            {
                Text = "Khi bỏ tick: mọi API (https) sẽ chấp nhận chứng chỉ không hợp lệ — chỉ dùng trong mạng nội bộ.",
                AutoSize = true,
                ForeColor = Color.FromArgb(0x8A, 0x6D, 0x00),
                Margin = new Padding(22, 0, 2, 2)
            };

            flow.Controls.Add(_tlsVerifyChk);
            sec.Controls.Add(flow);
            return sec;
        }

        private static string DefaultSessionPath()
        {
            // Bản portable: session mẫu được bootstrap giải nén vào %APPDATA%
            string inData = Path.Combine(Bootstrap.DataDir, "vos_session_capture.bin");
            if (File.Exists(inData)) return inData;
            string p1 = Path.Combine(AppContext.BaseDirectory, "Data", "vos_session_capture.bin");
            if (File.Exists(p1)) return p1;
            string p2 = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ProxyCapture", "bin", "Debug", "net8.0", "capture", "s01_c2s.bin");
            if (File.Exists(p2)) return Path.GetFullPath(p2);
            return p1;
        }


        // ================== TAI KHOAN DANG NHAP VOS3000 (TCP 1202) ==================
        // User/pass KHONG nam o dau khac: no nam trong packet login cua session template.
        // Muon doi -> tao 1 file session moi (chi thay packet login) va tro settings sang file do.

        private void SetVosLoginState(string text, Color color)
        {
            if (_vosLoginLbl == null || _vosLoginLbl.IsDisposed) return;
            _vosLoginLbl.Text = text;
            _vosLoginLbl.ForeColor = color;
        }

        private void ApplyVosLogin()
        {
            ApplyMapConfig();

            string server = _settings.vos_server;
            string user = _settings.vos_user;
            string pass = _settings.vos_pass;
            string basePath = _settings.vos_session_file;

            if (string.IsNullOrWhiteSpace(basePath))
            {
                MessageBox.Show(this, "Chua co file session template (Data\\vos_session_capture.bin).", "Tai khoan VOS",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // khi da co session da doi login thi phai sua tu BAN GOC, nu khong se doi nguoc
            string source = basePath;
            if (!string.IsNullOrWhiteSpace(_settings.vos_session_login_file) && File.Exists(_settings.vos_session_login_file))
                source = basePath;

            string target = Bootstrap.LoginSessionPath;
            SetVosLoginState("Dang doi login...", Color.DarkOrange);

            Task.Run(() =>
            {
                try
                {
                    var pk = VosProto.SplitPackets(File.ReadAllBytes(source));
                    int idx = Replayer.VosLoginTemplate.FindLoginIndex(pk);
                    if (idx < 0)
                    {
                        SetVosLoginState("!! Khong tim thay packet login trong session.", Color.Red);
                        return;
                    }

                    var current = Replayer.VosLoginTemplate.Read(pk[idx]);
                    string effUser = string.IsNullOrEmpty(user) ? current.LoginName : user;

                    var saved = Replayer.VosLoginTemplate.SaveWithLogin(source, target, effUser, pass,
                        out string err, out int loginIdx, m => SetVosLoginState(m, Color.Gray));
                    if (saved == null)
                    {
                        SetVosLoginState("!! " + err, Color.Red);
                        return;
                    }

                    Invoke(() =>
                    {
                        _settings.vos_session_login_file = saved;
                        _settings.vos_user = effUser;
                        _settings.vos_pass = pass;
                        try { Config.SaveSettings(_settings); } catch { }
                        SetVosLoginState($"OK: user '{effUser}' -> {saved}", Color.Green);

                        var (ok, msg) = Replayer.VosLoginTemplate.TestLogin(server, _settings.vos_port,
                            pk[0], VosProto.SplitPackets(File.ReadAllBytes(saved))[loginIdx]);
                        SetVosLoginState((ok ? "OK login: " : "!! LOGIN THAT BAI: ") + msg, ok ? Color.Green : Color.Red);
                    });
                }
                catch (Exception e) { SetVosLoginState("!! Loi: " + e.Message, Color.Red); }
            });
        }

        private void TestVosLogin()
        {
            ApplyMapConfig();
            string server = _settings.vos_server;
            int port = _settings.vos_port;
            string session = Bootstrap.ResolveSessionFile(_settings);
            SetVosLoginState("Dang test login...", Color.DarkOrange);

            Task.Run(() =>
            {
                try
                {
                    var pk = VosProto.SplitPackets(File.ReadAllBytes(session));
                    int idx = Replayer.VosLoginTemplate.FindLoginIndex(pk);
                    if (idx < 0)
                    {
                        SetVosLoginState("!! Session khong co packet login.", Color.Red);
                        return;
                    }
                    var cur = Replayer.VosLoginTemplate.Read(pk[idx]);
                    string who = string.IsNullOrEmpty(cur.LoginName) ? "?" : cur.LoginName;
                    var (ok, msg) = Replayer.VosLoginTemplate.TestLogin(server, port, pk[0], pk[idx]);
                    SetVosLoginState($"{(ok ? "OK" : "!!")} [{who}] {msg}", ok ? Color.Green : Color.Red);
                }
                catch (Exception e) { SetVosLoginState("!! Loi: " + e.Message, Color.Red); }
            });
        }

        private void ShowCurrentVosLogin()
        {
            ApplyMapConfig();
            string basePath = _settings.vos_session_file;
            string loginPath = _settings.vos_session_login_file;
            try
            {
                var sb = new System.Text.StringBuilder();
                void Dump(string label, string path)
                {
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    {
                        sb.AppendLine($"{label}: (khong co) {path}");
                        return;
                    }
                    var pk = VosProto.SplitPackets(File.ReadAllBytes(path));
                    int idx = Replayer.VosLoginTemplate.FindLoginIndex(pk);
                    var info = idx >= 0 ? Replayer.VosLoginTemplate.Read(pk[idx]) : null;
                    sb.AppendLine($"{label}: {path}");
                    if (info == null || !info.Recognized)
                        sb.AppendLine("   (khong doc duoc packet login)");
                    else
                        sb.AppendLine($"   user='{info.LoginName}'  pass={(string.IsNullOrEmpty(info.Password) ? "(rong)" : new string('*', Math.Min(info.Password.Length, 10)))}" +
                                      (info.HasCryptoPassword ? "  [co cryptoPassword]" : ""));
                }

                Dump("Session goc (template)", basePath);
                Dump("Session da doi login", loginPath);
                sb.AppendLine();
                sb.AppendLine("Dang dung: " + Bootstrap.ResolveSessionFile(_settings));

                using var dlg = new Form
                {
                    Text = "Tai khoan login VOS3000 trong session",
                    Width = 900,
                    Height = 320,
                    StartPosition = FormStartPosition.CenterParent
                };
                var box = new TextBox
                {
                    Multiline = true,
                    ReadOnly = true,
                    Dock = DockStyle.Fill,
                    ScrollBars = ScrollBars.Both,
                    WordWrap = false,
                    Font = new Font("Consolas", 9),
                    Text = sb.ToString()
                };
                dlg.Controls.Add(box);
                dlg.ShowDialog(this);
            }
            catch (Exception e)
            {
                MessageBox.Show(this, "Loi: " + e.Message, "Tai khoan VOS", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ApplyMapConfig()
        {
            _settings.vos_server = _mapServerBox.Text.Trim();
            _settings.vos_port = (int)_mapPortNum.Value;
            if (string.IsNullOrWhiteSpace(_settings.vos_session_file))
                _settings.vos_session_file = DefaultSessionPath();

            _settings.prefix_api_url = _prefixApiBox.Text.Trim();
            _settings.prefix_api_key = _prefixKeyBox.Text.Trim();
            _settings.prefix_api_field = _prefixFieldBox.Text.Trim();
            _settings.prefix_interval_seconds = (int)_prefixIntervalNum.Value;
            _settings.prefix_auto_start = _prefixAutoChk.Checked;
            _settings.prefix_remove_invalid = _prefixCleanChk?.Checked ?? false;

            // Tai khoan login VOS3000 (1202) - do vao session file khi chay
            if (_vosUserBox != null) _settings.vos_user = _vosUserBox.Text.Trim();
            if (_vosPassBox != null) _settings.vos_pass = _vosPassBox.Text;

            // Monitor Telegram
            if (_tgEnableChk != null)
            {
                _settings.telegram_enabled = _tgEnableChk.Checked;
                _settings.telegram_bot_token = _tgTokenBox.Text.Trim();
                _settings.telegram_chat_id = _tgChatBox.Text.Trim();
                _settings.telegram_include_numbers = _tgNumbersChk.Checked;
                _settings.telegram_only_on_change = _tgOnlyChangeChk.Checked;
                _settings.telegram_max_numbers = (int)_tgMaxNum.Value;
            }

            if (_tlsVerifyChk != null) _settings.verify_tls = _tlsVerifyChk.Checked;
            _settings.Normalize();
            try { Config.SaveSettings(_settings); } catch { }
        }

        private void RunPrefixOnce()
        {
            ApplyMapConfig();
            if (string.IsNullOrWhiteSpace(_settings.prefix_api_url))
            {
                MessageBox.Show(this, "Nhập URL API danh sách số trước.", "Add số",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _prefixSyncer.Configure(_settings, m => MapAppendLog(m));
            SetMapState(Msg.State.CallingApi, Color.DarkOrange);
            Task.Run(() => _prefixSyncer.RunOnce(true));   // chạy tay -> luôn bắn báo cáo Telegram
        }

        private void StartPrefixSchedule()
        {
            ApplyMapConfig();
            if (string.IsNullOrWhiteSpace(_settings.prefix_api_url))
            {
                MessageBox.Show(this, "Nhập URL API danh sách số trước.", "Add số",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _prefixSyncer.Configure(_settings, m => MapAppendLog(m));
            _prefixSyncer.Start(_settings.prefix_interval_seconds);
            UpdateSchedLabel();
        }

        private void StopPrefixSchedule()
        {
            _prefixSyncer.Stop();
            UpdateSchedLabel();
        }

        private void UpdateSchedLabel()
        {
            if (_prefixSyncer.Running)
                _prefixSchedLbl.Text = string.Format(Msg.State.ScheduleNever, _prefixSyncer.IntervalSeconds);
            else
                _prefixSchedLbl.Text = Msg.State.ScheduleOff;
            _prefixSchedLbl.ForeColor = _prefixSyncer.Running ? Color.DarkOrange : Color.Gray;
        }

        private void OnPrefixSyncResult(PrefixSyncResult rr)
        {
            if (IsDisposed) return;
            try
            {
                Invoke(() =>
                {
                    MapAppendLog($"KẾT QUẢ SYNC: {(rr.Ok ? "✔" : "✗")} lấy {rr.Fetched} số | số mới (chỉ lưu {Tables.MissingNumber}) {rr.MissingOnServer} | đã có trên VOS {rr.ExistingOnServer} (chỉ update khóa/mở) | out {rr.OutMappingsUpdated}/{rr.OutMappingsTotal} mapping | in {rr.InRoutingsUpdated}/{rr.InRoutingsTotal} routing | {rr.Message}");
                    foreach (var g in rr.ByAccount.OrderBy(x => x.Key).Take(20))
                        MapAppendLog($"  account #{g.Key} {g.Value.Account}: in={g.Value.InCount}, out={g.Value.OutCount}");
                    if (rr.ByAccount.Count > 0)
                        MapAppendLog("  → Đã lưu danh sách số theo từng account — xem tab \"Số theo account (VOS)\".");
                    if (_prefixSyncer.Running)
                        _prefixSchedLbl.Text = string.Format(Msg.State.ScheduleOn, _prefixSyncer.IntervalSeconds,
                            $"{rr.Time:HH:mm:ss} ({(rr.Ok ? "OK" : "LỖI")})");

                    // Đang mở tab "Số chưa có" thì tự làm mới danh sách sau mỗi vòng sync
                    if (_tabs != null && _tabs.SelectedTab == _missingPage && !_missingLoading)
                        _ = LoadMissingNumbersAsync();

                    // Đang mở tab "Số theo account" thì tự làm mới sau mỗi vòng sync
                    if (_tabs != null && _tabs.SelectedTab == _acctPage && !_acctLoading)
                        _ = LoadAccountNumbersAsync();
                });
            }
            catch { }
            RefreshDbStats();   // cập nhật card Tổng quan từ DB sau mỗi vong sync
        }

        private void SetMapState(string text, Color color)
        {
            if (IsDisposed) return;
            try { Invoke(() => { _mapStateLbl.Text = text; _mapStateLbl.ForeColor = color; }); }
            catch { }
        }

        private void MapAppendLog(string s)
        {
            if (IsDisposed) return;
            try { Invoke(() => _mapLogBox.AppendText(s + Environment.NewLine)); }
            catch { }
        }

        private void RunMapping(string kind)
        {
            ApplyMapConfig();

            string server = _settings.vos_server;
            int port = _settings.vos_port;
            string sessionPath = Bootstrap.ResolveSessionFile(_settings);

            foreach (var b in _mapButtons) b.Enabled = false;
            SetMapState(Msg.NumberLock.StateRunning, Color.DarkOrange);
            MapAppendLog("");
            MapAppendLog(string.Format(Msg.Mapping.Header, kind.ToUpper(), server, port, DateTime.Now.ToString("HH:mm:ss")));

            Task.Run(() =>
            {
                try
                {
                    var client = new PrefixClient(server, port, sessionPath,
                        m => MapAppendLog(m.StartsWith("  ") ? m : "  " + m));
                    string readback = null;
                    bool okLoad = false;
                    if (kind == "load")
                    {
                        readback = client.ReadPrefix();
                        okLoad = readback != null;
                        MapAppendLog(readback == null
                            ? "!! Không đọc được prefix."
                            : $"PREFIX HIỆN TẠI: {readback}");
                        // Ghi DB: mỗi lần chạy Mapping (đọc prefix) = 1 nghiệp vụ
                        try
                        {
                            ToolDb.RecordOperation(_settings, "mapping_load", "", "", okLoad ? "success" : "failed",
                                $"[GET prefix] {server}:{port} → '{readback ?? "(không đọc được)"}'");
                            ToolDb.Log("mapping", okLoad ? "INFO" : "WARN", "", $"load prefix: {(okLoad ? "OK" : "FAIL")}");
                            if (okLoad) ToolDb.UpdateMappedNumbers(readback, "read");
                        }
                        catch { }
                    }
                    SetMapState(readback == null ? "● Lỗi" : "● OK", readback == null ? Color.Red : Color.Green);
                    if (kind == "load") RefreshDbStats();
                }
                catch (Exception e)
                {
                    MapAppendLog("!! Lỗi: " + e.Message);
                    SetMapState("● Lỗi", Color.Red);
                }
                finally
                {
                    if (!IsDisposed)
                        try { Invoke(() => { foreach (var b in _mapButtons) b.Enabled = true; }); } catch { }
                }
            });
        }

        

        private void BuildNumberLockTab(TabPage page)
        {
            var titleLbl = new Label
            {
                Text = Msg.NumberLock.Title,
                Font = new Font("Segoe UI", 14, FontStyle.Bold),
                Dock = DockStyle.Top,
                Height = 32
            };

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, Padding = new Padding(0, 6, 0, 2) };
            _lockStateLbl = new Label
            {
                Text = Msg.NumberLock.StateNotLoaded,
                AutoSize = true,
                Font = new Font("Segoe UI", 11, FontStyle.Bold),
                ForeColor = Color.Gray,
                Margin = new Padding(3, 10, 16, 3)
            };
            var bLoad = new Button { Text = "Tải danh sách số (tất cả account)", AutoSize = true, Margin = new Padding(3) };
            var bLock = new Button { Text = "🔒 Khóa số (tất cả account)", AutoSize = true, Margin = new Padding(3) };
            var bUnlock = new Button { Text = "🔓 Mở khóa (tất cả account)", AutoSize = true, Margin = new Padding(3) };
            var bCommit = new Button { Text = "Cập nhật theo lưới (tất cả account)", AutoSize = true, Margin = new Padding(3) };
            var bPickAll = new Button { Text = "☑ Chọn trang", AutoSize = true, Margin = new Padding(3) };
            var bPickNone = new Button { Text = "☐ Bỏ chọn", AutoSize = true, Margin = new Padding(3) };
            var bClear = new Button { Text = "Xoá log", AutoSize = true, Margin = new Padding(3) };
            bLoad.Click += (_, _) => RunLockTask("load", null);
            bLock.Click += (_, _) => RunLockTask("lock", JoinLockSelection(SelectedLockNumbersOrCurrent()));
            bUnlock.Click += (_, _) => RunLockTask("unlock", JoinLockSelection(SelectedLockNumbersOrCurrent()));
            bCommit.Click += (_, _) => RunLockTask("commit", null);
            bPickAll.Click += (_, _) => SetLockPickAllOnPage(true);
            bPickNone.Click += (_, _) => SetLockPickAllOnPage(false);
            bClear.Click += (_, _) => _lockLogBox.Clear();
            _lockButtons.AddRange(new[] { bLoad, bLock, bUnlock, bCommit, bPickAll, bPickNone });
            bar.Controls.Add(bLoad);
            bar.Controls.Add(bLock);
            bar.Controls.Add(bUnlock);
            bar.Controls.Add(bCommit);
            bar.Controls.Add(bPickAll);
            bar.Controls.Add(bPickNone);
            bar.Controls.Add(bClear);
            bar.Controls.Add(_lockStateLbl);

            _lockGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                RowHeadersWidth = 34,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                MultiSelect = true,
                SelectionMode = DataGridViewSelectionMode.CellSelect,
                Font = new Font("Consolas", 10)
            };
            // cột checkbox: tích để chọn số cần khóa/mở khóa (chọn được NHIỀU số)
            var pickCol = new DataGridViewCheckBoxColumn
            {
                Name = GridColumn.Pick,
                HeaderText = "✔",
                Width = 36,
                FillWeight = 6,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                ToolTipText = "Tích chọn số cần khóa / mở khóa (chọn được nhiều số)",
                FalseValue = false,
                TrueValue = true
            };
            _lockGrid.Columns.Add(pickCol);
            _lockGrid.Columns.Add(GridColumn.Stt, "STT");
            _lockGrid.Columns.Add(GridColumn.Orig, "orig");
            _lockGrid.Columns.Add(GridColumn.Num, "Số");
            _lockGrid.Columns.Add(GridColumn.Status, "Trạng thái");
            _lockGrid.Columns.Add(GridColumn.OpenForm, "Dạng mở khóa (84xxx)");
            _lockGrid.Columns[GridColumn.Stt].FillWeight = 8;
            _lockGrid.Columns[GridColumn.Num].FillWeight = 32;
            _lockGrid.Columns[GridColumn.Status].FillWeight = 22;
            _lockGrid.Columns[GridColumn.OpenForm].FillWeight = 28;
            _lockGrid.Columns[GridColumn.Orig].Visible = false;   // gia tri goc tren server (de ghi lai dung entry)
            _lockGrid.Columns[GridColumn.Stt].ReadOnly = true;
            _lockGrid.Columns[GridColumn.Status].ReadOnly = true;
            _lockGrid.Columns[GridColumn.OpenForm].ReadOnly = true;
            _lockGrid.Columns[GridColumn.Orig].ReadOnly = true;
            _lockGrid.CellValueChanged += (_, e) =>
            {
                if (e.RowIndex < 0) return;
                // cột checkbox "chọn": chỉ đổi lựa chọn, không đụng nội dung số
                if (_lockGrid.Columns[e.ColumnIndex].Name == GridColumn.Pick) return;
                RefreshLockRow(_lockGrid.Rows[e.RowIndex]);
            };
            // click trực tiếp vào ô checkbox -> đổi trạng thái ngay
            _lockGrid.CellClick += (_, e) =>
            {
                if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
                if (_lockGrid.Columns[e.ColumnIndex].Name != GridColumn.Pick) return;
                var cell = _lockGrid.Rows[e.RowIndex].Cells[e.ColumnIndex];
                cell.Value = !(cell.Value is bool b && b);
            };
            _lockGrid.CurrentCellDirtyStateChanged += (_, _) =>
            {
                if (_lockGrid.IsCurrentCellDirty &&
                    _lockGrid.CurrentCell?.OwningColumn?.Name == GridColumn.Pick)
                    _lockGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };

            // ---- thanh tim kiem + phan trang (20 so / trang) ----
            var pager = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 40,
                Padding = new Padding(0, 4, 0, 2),
                FlowDirection = FlowDirection.LeftToRight
            };
            _lockSearchBox = new TextBox
            {
                Width = 240,
                PlaceholderText = "🔍 Tìm số… (vd: 84901 hoặc #090)"
            };
            _lockSearchBox.TextChanged += (_, _) => { _lockPage = 0; RenderLockPage(); };
            var searchLbl = new Label { Text = "Tìm:", AutoSize = true, Margin = new Padding(3, 8, 4, 0) };

            var bFirst = new Button { Text = "⏮", Width = 34, Margin = new Padding(3) };
            var bPrev = new Button { Text = "◀", Width = 34, Margin = new Padding(3) };
            var bNext = new Button { Text = "▶", Width = 34, Margin = new Padding(3) };
            var bLast = new Button { Text = "⏭", Width = 34, Margin = new Padding(3) };
            _lockPageNum = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 1,
                Width = 70,
                Font = new Font("Segoe UI", 10)
            };
            _lockPageNum.ValueChanged += OnLockPageNumChanged;
            _lockPageLbl = new Label
            {
                Text = "Trang 1/1 — chưa có dữ liệu",
                AutoSize = true,
                Margin = new Padding(8, 10, 4, 0),
                Font = new Font("Segoe UI", 9, FontStyle.Bold)
            };
            bFirst.Click += (_, _) => GoLockPage(0);
            bPrev.Click += (_, _) => GoLockPage(_lockPage - 1);
            bNext.Click += (_, _) => GoLockPage(_lockPage + 1);
            bLast.Click += (_, _) => GoLockPage(int.MaxValue);
            pager.Controls.Add(searchLbl);
            pager.Controls.Add(_lockSearchBox);
            pager.Controls.Add(bFirst);
            pager.Controls.Add(bPrev);
            pager.Controls.Add(bNext);
            pager.Controls.Add(bLast);
            pager.Controls.Add(_lockPageNum);
            pager.Controls.Add(_lockPageLbl);

            _lockLogBox = new TextBox
            {
                Dock = DockStyle.Bottom,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 9),
                Height = 150,
                BackColor = Color.FromArgb(18, 18, 18),
                ForeColor = Color.FromArgb(200, 255, 200)
            };

            page.Controls.Add(_lockGrid);
            page.Controls.Add(bar);
            page.Controls.Add(titleLbl);
            page.Controls.Add(pager);
            page.Controls.Add(_lockLogBox);
        }

        // ================= Số API chưa có trên VOS3000 (t_number_missing) =================

        private void BuildMissingNumbersTab(TabPage page)
        {
            var titleLbl = new Label
            {
                Text = Msg.Missing.Title,
                Font = new Font("Segoe UI", 14, FontStyle.Bold),
                Dock = DockStyle.Top,
                Height = 30
            };

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(0, 4, 0, 2) };
            var bLoad = new Button { Text = "⟳ Tải danh sách", AutoSize = true, Margin = new Padding(3) };
            var bExport = new Button { Text = "⇩ Xuất Excel (.xlsx)", AutoSize = true, Margin = new Padding(3) };
            var bClear = new Button { Text = "🗑 Xoá danh sách", AutoSize = true, Margin = new Padding(3) };
            var bLog = new Button { Text = "Xoá log", AutoSize = true, Margin = new Padding(3) };
            bLoad.Click += async (_, _) => await LoadMissingNumbersAsync();
            bExport.Click += (_, _) => ExportMissingExcel();
            bClear.Click += async (_, _) => await ClearMissingNumbersAsync();
            bLog.Click += (_, _) => _missingLogBox.Clear();
            _missingButtons.AddRange(new[] { bLoad, bExport, bClear });
            bar.Controls.Add(bLoad);
            bar.Controls.Add(bExport);
            bar.Controls.Add(bClear);
            bar.Controls.Add(bLog);

            _missingStateLbl = new Label
            {
                Text = Msg.Missing.StateNotLoaded,
                AutoSize = true,
                Font = new Font("Segoe UI", 10, FontStyle.Bold),
                ForeColor = Color.Gray,
                Margin = new Padding(14, 9, 6, 3)
            };
            _missingCountLbl = new Label { Text = "", AutoSize = true, Margin = new Padding(6, 10, 3, 3) };
            bar.Controls.Add(_missingStateLbl);
            bar.Controls.Add(_missingCountLbl);

            var filterBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(0, 4, 0, 2) };
            filterBar.Controls.Add(new Label { Text = "Tìm:", AutoSize = true, Margin = new Padding(3, 8, 4, 0) });
            _missingSearchBox = new TextBox { Width = 280, PlaceholderText = "🔍 tìm theo số…" };
            _missingSearchBox.TextChanged += (_, _) => RenderMissingNumbers();
            filterBar.Controls.Add(_missingSearchBox);

            _missingGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                RowHeadersWidth = 30,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                MultiSelect = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                Font = new Font("Consolas", 10)
            };
            _missingGrid.Columns.Add(GridColumn.MissStt, "STT");
            _missingGrid.Columns.Add(GridColumn.MissNum, "Số (API)");
            _missingGrid.Columns.Add(GridColumn.MissStatus, "Trạng thái API");
            _missingGrid.Columns.Add(GridColumn.MissFirst, "Lần thấy đầu");
            _missingGrid.Columns.Add(GridColumn.MissLast, "Lần thấy cuối");
            _missingGrid.Columns.Add(GridColumn.MissHits, "Số lần");
            _missingGrid.Columns[GridColumn.MissStt].FillWeight = 7;
            _missingGrid.Columns[GridColumn.MissNum].FillWeight = 30;
            _missingGrid.Columns[GridColumn.MissStatus].FillWeight = 14;
            _missingGrid.Columns[GridColumn.MissFirst].FillWeight = 22;
            _missingGrid.Columns[GridColumn.MissLast].FillWeight = 22;
            _missingGrid.Columns[GridColumn.MissHits].FillWeight = 9;
            foreach (DataGridViewColumn col in _missingGrid.Columns) col.ReadOnly = true;

            var hint = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 40,
                Text = Msg.Missing.Hint,
                ForeColor = Color.FromArgb(0x8A, 0x6D, 0x00),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
                Padding = new Padding(2, 8, 0, 0)
            };

            _missingLogBox = new TextBox
            {
                Dock = DockStyle.Bottom,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 9),
                Height = 110,
                BackColor = Color.FromArgb(18, 18, 18),
                ForeColor = Color.FromArgb(200, 255, 200)
            };

            page.Controls.Add(_missingGrid);
            page.Controls.Add(filterBar);
            page.Controls.Add(bar);
            page.Controls.Add(titleLbl);
            page.Controls.Add(hint);
            page.Controls.Add(_missingLogBox);

            // Mở tab lần đầu -> tự tải danh sách
            page.Enter += async (_, _) =>
            {
                if (_missingAll.Count == 0 && !_missingLoading) await LoadMissingNumbersAsync();
            };
        }

        private void MissingLog(string msg)
        {
            if (IsDisposed || _missingLogBox == null || _missingLogBox.IsDisposed) return;
            if (_missingLogBox.InvokeRequired)
            {
                try { _missingLogBox.BeginInvoke(new Action(() => MissingLog(msg))); } catch { }
                return;
            }
            _missingLogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\r\n");
            if (_missingLogBox.TextLength > 200_000) _missingLogBox.Clear();
        }

        private async Task LoadMissingNumbersAsync()
        {
            if (_missingLoading || IsDisposed) return;
            _missingLoading = true;
            foreach (var b in _missingButtons) b.Enabled = false;
            try
            {
                ToolDb.Configure(_settings);
                if (!ToolDb.HasConfig())
                {
                    _missingStateLbl.Text = Msg.Missing.StateDbNotConfig;
                    _missingStateLbl.ForeColor = BrandRed;
                    MissingLog(Msg.Missing.DbNotConfigLog);
                    return;
                }

                _missingStateLbl.Text = Msg.Missing.StateLoading;
                _missingStateLbl.ForeColor = Color.DarkOrange;

                var rows = await ToolDb.LoadMissingNumbersAsync();
                _missingAll = rows;

                if (_missingAll.Count == 0)
                {
                    _missingStateLbl.Text = string.Format(Msg.Missing.StateLoaded, 0);
                    MissingLog(Msg.Missing.Empty);
                }
                else
                {
                    _missingStateLbl.Text = string.Format(Msg.Missing.StateLoaded, _missingAll.Count);
                    MissingLog(string.Format(Msg.Missing.LoadedLog, _missingAll.Count));
                }
                _missingStateLbl.ForeColor = BrandDark;
                RenderMissingNumbers();
            }
            catch (Exception e)
            {
                _missingStateLbl.Text = Msg.Missing.StateError;
                _missingStateLbl.ForeColor = BrandRed;
                MissingLog(string.Format(Msg.Missing.LoadError, e.Message));
                MessageBox.Show(this, string.Format(Msg.Missing.LoadError, e.Message), "Số chưa có (VOS)",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                _missingLoading = false;
                foreach (var b in _missingButtons) b.Enabled = true;
            }
        }

        private IEnumerable<MissingNumberRow> FilteredMissingNumbers()
        {
            IEnumerable<MissingNumberRow> q = _missingAll;
            var key = (_missingSearchBox?.Text ?? "").Trim();
            if (key.Length > 0)
                q = q.Where(x => x.Num.Contains(key, StringComparison.OrdinalIgnoreCase));
            return q;
        }

        private void RenderMissingNumbers()
        {
            if (_missingGrid == null || _missingGrid.IsDisposed) return;
            _missingGrid.Rows.Clear();

            int i = 1;
            foreach (var r in FilteredMissingNumbers())
            {
                string status = string.Equals(r.ApiStatus, "locked", StringComparison.OrdinalIgnoreCase)
                    ? Msg.Missing.StatusLocked
                    : Msg.Missing.StatusOpen;
                int idx = _missingGrid.Rows.Add(
                    i++,
                    r.Num,
                    status,
                    r.FirstSeen == DateTime.MinValue ? "" : r.FirstSeen.ToString("yyyy-MM-dd HH:mm:ss"),
                    r.LastSeen == DateTime.MinValue ? "" : r.LastSeen.ToString("yyyy-MM-dd HH:mm:ss"),
                    r.Hits);
                _missingGrid.Rows[idx].Tag = r;
                var cell = _missingGrid.Rows[idx].Cells[GridColumn.MissStatus];
                cell.Style.ForeColor = status == Msg.Missing.StatusLocked ? BrandRed : BrandDark;
                cell.Style.Font = new Font(_missingGrid.Font, FontStyle.Bold);
            }

            if (_missingCountLbl != null)
                _missingCountLbl.Text = _missingAll.Count == 0
                    ? ""
                    : $"hiển thị {_missingGrid.Rows.Count}/{_missingAll.Count}";
        }

        private void ExportMissingExcel()
        {
            var rows = FilteredMissingNumbers().ToList();
            if (rows.Count == 0)
            {
                MessageBox.Show(this, Msg.Missing.ExcelNoData, "Xuất Excel",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using var dlg = new SaveFileDialog
            {
                Title = "Xuất danh sách số chưa có trên VOS3000",
                Filter = "Excel (*.xlsx)|*.xlsx",
                FileName = $"so_chua_co_vos_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx",
                AddExtension = true,
                DefaultExt = "xlsx",
                OverwritePrompt = true
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                var data = rows.Select((r, i) => new[]
                {
                    (i + 1).ToString(),
                    r.Num,
                    string.Equals(r.ApiStatus, "locked", StringComparison.OrdinalIgnoreCase)
                        ? Msg.Missing.StatusLocked : Msg.Missing.StatusOpen,
                    r.FirstSeen == DateTime.MinValue ? "" : r.FirstSeen.ToString("yyyy-MM-dd HH:mm:ss"),
                    r.LastSeen == DateTime.MinValue ? "" : r.LastSeen.ToString("yyyy-MM-dd HH:mm:ss"),
                    r.Hits.ToString()
                });

                XlsxWriter.Write(dlg.FileName, "Số chưa có",
                    new[] { "STT", "Số (API)", "Trạng thái API", "Lần thấy đầu", "Lần thấy cuối", "Số lần" },
                    data,
                    new double[] { 7, 24, 15, 21, 21, 9 });

                MissingLog($"Đã xuất Excel {rows.Count} dòng: {dlg.FileName}");
                MessageBox.Show(this, string.Format(Msg.Missing.ExcelDone, rows.Count, dlg.FileName),
                    "Xuất Excel", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception e)
            {
                MissingLog(string.Format(Msg.Missing.ExcelError, e.Message));
                MessageBox.Show(this, string.Format(Msg.Missing.ExcelError, e.Message), "Xuất Excel",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async Task ClearMissingNumbersAsync()
        {
            if (_missingAll.Count == 0)
            {
                MessageBox.Show(this, Msg.Missing.ExcelNoData, "Xoá danh sách",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show(this, Msg.Missing.ClearConfirm, "Xoá danh sách",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            try
            {
                ToolDb.Configure(_settings);
                int n = await ToolDb.ClearMissingNumbersAsync();
                _missingAll = new List<MissingNumberRow>();
                RenderMissingNumbers();
                _missingStateLbl.Text = string.Format(Msg.Missing.StateLoaded, 0);
                _missingStateLbl.ForeColor = Color.Gray;
                MissingLog(string.Format(Msg.Missing.ClearDone, n));
            }
            catch (Exception e)
            {
                MissingLog(string.Format(Msg.Missing.LoadError, e.Message));
                MessageBox.Show(this, e.Message, "Xoá danh sách", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BuildAccountNumbersTab(TabPage page)
        {
            var titleLbl = new Label
            {
                Text = Msg.AccountSet.Title,
                Font = new Font("Segoe UI", 14, FontStyle.Bold),
                Dock = DockStyle.Top,
                Height = 30
            };

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(0, 4, 0, 2) };
            var bLoad = new Button { Text = "⟳ Tải danh sách", AutoSize = true, Margin = new Padding(3) };
            var bExport = new Button { Text = "⇩ Xuất Excel (.xlsx)", AutoSize = true, Margin = new Padding(3) };
            var bClear = new Button { Text = "🗑 Xoá bảng (DB tool)", AutoSize = true, Margin = new Padding(3) };
            var bLog = new Button { Text = "Xoá log", AutoSize = true, Margin = new Padding(3) };
            bLoad.Click += async (_, _) => await LoadAccountNumbersAsync();
            bExport.Click += (_, _) => ExportAccountNumbersExcel();
            bClear.Click += async (_, _) => await ClearAccountNumbersAsync();
            bLog.Click += (_, _) => _acctLogBox.Clear();
            _acctButtons.AddRange(new[] { bLoad, bExport, bClear });
            bar.Controls.Add(bLoad);
            bar.Controls.Add(bExport);
            bar.Controls.Add(bClear);
            bar.Controls.Add(bLog);

            _acctStateLbl = new Label
            {
                Text = Msg.AccountSet.StateNotLoaded,
                AutoSize = true,
                Font = new Font("Segoe UI", 10, FontStyle.Bold),
                ForeColor = Color.Gray,
                Margin = new Padding(14, 9, 6, 3)
            };
            _acctCountLbl = new Label { Text = "", AutoSize = true, Margin = new Padding(6, 10, 3, 3) };
            bar.Controls.Add(_acctStateLbl);
            bar.Controls.Add(_acctCountLbl);

            var filterBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(0, 4, 0, 2) };
            filterBar.Controls.Add(new Label { Text = "Account:", AutoSize = true, Margin = new Padding(3, 8, 4, 0) });
            _acctAccountBox = new ComboBox { Width = 250, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 4, 12, 0) };
            _acctAccountBox.Items.Add(Msg.AccountSet.AllAccounts);
            _acctAccountBox.SelectedIndex = 0;
            _acctAccountBox.SelectedIndexChanged += (_, _) => RenderAccountNumbers();
            filterBar.Controls.Add(_acctAccountBox);

            filterBar.Controls.Add(new Label { Text = "Hướng:", AutoSize = true, Margin = new Padding(3, 8, 4, 0) });
            _acctDirectionBox = new ComboBox { Width = 160, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 4, 12, 0) };
            _acctDirectionBox.Items.AddRange(new object[]
            {
                Msg.AccountSet.AllDirections, Msg.AccountSet.DirIn, Msg.AccountSet.DirOut
            });
            _acctDirectionBox.SelectedIndex = 0;
            _acctDirectionBox.SelectedIndexChanged += (_, _) => RenderAccountNumbers();
            filterBar.Controls.Add(_acctDirectionBox);

            filterBar.Controls.Add(new Label { Text = "Tìm:", AutoSize = true, Margin = new Padding(3, 8, 4, 0) });
            _acctSearchBox = new TextBox { Width = 260, PlaceholderText = "🔍 số / account…", Margin = new Padding(0, 4, 0, 0) };
            _acctSearchBox.TextChanged += (_, _) => RenderAccountNumbers();
            filterBar.Controls.Add(_acctSearchBox);

            _acctGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                RowHeadersWidth = 30,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                MultiSelect = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                Font = new Font("Consolas", 10)
            };
            _acctGrid.Columns.Add(GridColumn.AcctStt, "STT");
            _acctGrid.Columns.Add(GridColumn.AcctAccount, "Account");
            _acctGrid.Columns.Add(GridColumn.AcctDirection, "Hướng");
            _acctGrid.Columns.Add(GridColumn.AcctNum, "Số");
            _acctGrid.Columns.Add(GridColumn.AcctStatus, "Trạng thái");
            _acctGrid.Columns.Add(GridColumn.AcctUpdated, "Cập nhật");
            _acctGrid.Columns[GridColumn.AcctStt].FillWeight = 6;
            _acctGrid.Columns[GridColumn.AcctAccount].FillWeight = 24;
            _acctGrid.Columns[GridColumn.AcctDirection].FillWeight = 14;
            _acctGrid.Columns[GridColumn.AcctNum].FillWeight = 24;
            _acctGrid.Columns[GridColumn.AcctStatus].FillWeight = 12;
            _acctGrid.Columns[GridColumn.AcctUpdated].FillWeight = 20;
            foreach (DataGridViewColumn col in _acctGrid.Columns) col.ReadOnly = true;

            var hint = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 40,
                Text = Msg.AccountSet.Hint,
                ForeColor = Color.FromArgb(0x8A, 0x6D, 0x00),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
                Padding = new Padding(2, 8, 0, 0)
            };

            _acctLogBox = new TextBox
            {
                Dock = DockStyle.Bottom,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 9),
                Height = 110,
                BackColor = Color.FromArgb(18, 18, 18),
                ForeColor = Color.FromArgb(200, 255, 200)
            };

            page.Controls.Add(_acctGrid);
            page.Controls.Add(filterBar);
            page.Controls.Add(bar);
            page.Controls.Add(titleLbl);
            page.Controls.Add(hint);
            page.Controls.Add(_acctLogBox);

            // Mở tab lần đầu -> tự tải danh sách đã lưu từ vòng sync gần nhất
            page.Enter += async (_, _) =>
            {
                if (_acctRows.Count == 0 && !_acctLoading) await LoadAccountNumbersAsync();
            };
        }

        private void AccountLog(string msg)
        {
            if (IsDisposed || _acctLogBox == null || _acctLogBox.IsDisposed) return;
            if (_acctLogBox.InvokeRequired)
            {
                try { _acctLogBox.BeginInvoke(new Action(() => AccountLog(msg))); } catch { }
                return;
            }
            _acctLogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\r\n");
            if (_acctLogBox.TextLength > 200_000) _acctLogBox.Clear();
        }

        private async Task LoadAccountNumbersAsync()
        {
            if (_acctLoading || IsDisposed) return;
            _acctLoading = true;
            foreach (var b in _acctButtons) b.Enabled = false;
            try
            {
                ToolDb.Configure(_settings);
                if (!ToolDb.HasConfig())
                {
                    _acctStateLbl.Text = Msg.AccountSet.StateDbNotConfig;
                    _acctStateLbl.ForeColor = BrandRed;
                    AccountLog(Msg.AccountSet.DbNotConfigLog);
                    return;
                }

                _acctStateLbl.Text = Msg.AccountSet.StateLoading;
                _acctStateLbl.ForeColor = Color.DarkOrange;

                var rows = await ToolDb.LoadAccountNumberSetRowsAsync();
                _acctRows = rows ?? new List<AccountNumberSetRow>();

                int accountCount = _acctRows
                    .Select(r => $"{r.AccountId}|{AccountDisplay(r)}")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                int totalNums = _acctRows.Sum(r => r.NumCount);

                if (_acctRows.Count == 0)
                {
                    _acctStateLbl.Text = string.Format(Msg.AccountSet.StateLoaded, 0, 0);
                    AccountLog(Msg.AccountSet.Empty);
                }
                else
                {
                    _acctStateLbl.Text = string.Format(Msg.AccountSet.StateLoaded, totalNums, accountCount);
                    AccountLog(string.Format(Msg.AccountSet.LoadedLog, totalNums, accountCount));
                }
                _acctStateLbl.ForeColor = BrandDark;
                PopulateAccountFilter();
                RenderAccountNumbers();
            }
            catch (Exception e)
            {
                _acctStateLbl.Text = Msg.AccountSet.StateError;
                _acctStateLbl.ForeColor = BrandRed;
                AccountLog(string.Format(Msg.AccountSet.LoadError, e.Message));
                MessageBox.Show(this, string.Format(Msg.AccountSet.LoadError, e.Message), "Số theo account (VOS)",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                _acctLoading = false;
                foreach (var b in _acctButtons) b.Enabled = true;
            }
        }

        private static string AccountDisplay(AccountNumberSetRow r)
        {
            if (r == null) return "";
            if (!string.IsNullOrWhiteSpace(r.Account)) return r.Account.Trim();
            return r.AccountId > 0 ? $"#{r.AccountId}" : "(không rõ account)";
        }

        private static string AccountDirectionDisplay(string dir)
        {
            if (string.Equals(dir, "in", StringComparison.OrdinalIgnoreCase)) return Msg.AccountSet.DirIn;
            if (string.Equals(dir, "out", StringComparison.OrdinalIgnoreCase)) return Msg.AccountSet.DirOut;
            return dir ?? "";
        }

        private void PopulateAccountFilter()
        {
            if (_acctAccountBox == null || _acctAccountBox.IsDisposed) return;
            string selected = _acctAccountBox.SelectedItem?.ToString() ?? Msg.AccountSet.AllAccounts;

            _acctAccountBox.BeginUpdate();
            try
            {
                _acctAccountBox.Items.Clear();
                _acctAccountBox.Items.Add(Msg.AccountSet.AllAccounts);
                foreach (var name in _acctRows
                             .Select(AccountDisplay)
                             .Where(x => !string.IsNullOrWhiteSpace(x))
                             .Distinct(StringComparer.OrdinalIgnoreCase)
                             .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    _acctAccountBox.Items.Add(name);

                int idx = _acctAccountBox.Items.IndexOf(selected);
                _acctAccountBox.SelectedIndex = idx >= 0 ? idx : 0;
            }
            finally { _acctAccountBox.EndUpdate(); }
        }

        private IEnumerable<(AccountNumberSetRow Row, string Number)> FilteredAccountNumbers()
        {
            var rows = _acctRows ?? new List<AccountNumberSetRow>();
            string acc = _acctAccountBox?.SelectedItem?.ToString() ?? Msg.AccountSet.AllAccounts;
            bool allAcc = string.Equals(acc, Msg.AccountSet.AllAccounts, StringComparison.Ordinal);
            string dir = _acctDirectionBox?.SelectedItem?.ToString() ?? Msg.AccountSet.AllDirections;
            bool allDir = string.Equals(dir, Msg.AccountSet.AllDirections, StringComparison.Ordinal);
            bool wantIn = string.Equals(dir, Msg.AccountSet.DirIn, StringComparison.Ordinal);
            string key = (_acctSearchBox?.Text ?? "").Trim();

            foreach (var r in rows
                         .OrderBy(AccountDisplay, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(x => x.Direction, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(x => x.AccountId))
            {
                string accName = AccountDisplay(r);
                if (!allAcc && !string.Equals(accName, acc, StringComparison.OrdinalIgnoreCase)) continue;
                if (!allDir)
                {
                    bool isIn = string.Equals(r.Direction, "in", StringComparison.OrdinalIgnoreCase);
                    if (wantIn != isIn) continue;
                }

                bool accountMatch = key.Length > 0 &&
                                    accName.Contains(key, StringComparison.OrdinalIgnoreCase);

                foreach (var n in (r.Numbers ?? new List<string>())
                             .OrderBy(NumberLockUtil.TailKey, StringComparer.Ordinal))
                {
                    if (key.Length > 0 && !accountMatch &&
                        !n.Contains(key, StringComparison.OrdinalIgnoreCase)) continue;
                    yield return (r, n);
                }
            }
        }

        private void RenderAccountNumbers()
        {
            if (_acctGrid == null || _acctGrid.IsDisposed) return;
            _acctGrid.Rows.Clear();

            int i = 1;
            foreach (var (row, num) in FilteredAccountNumbers())
            {
                bool locked = !NumberLockUtil.IsValid(num);
                string status = locked ? Msg.AccountSet.StatusLocked : Msg.AccountSet.StatusOpen;
                int idx = _acctGrid.Rows.Add(
                    i++,
                    AccountDisplay(row),
                    AccountDirectionDisplay(row.Direction),
                    num,
                    status,
                    row.UpdatedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "");
                var cell = _acctGrid.Rows[idx].Cells[GridColumn.AcctStatus];
                cell.Style.ForeColor = locked ? BrandRed : BrandDark;
                cell.Style.Font = new Font(_acctGrid.Font, FontStyle.Bold);
            }

            if (_acctCountLbl != null)
            {
                int total = _acctRows.Sum(x => x.NumCount);
                _acctCountLbl.Text = total == 0 ? "" : $"hiển thị {_acctGrid.Rows.Count}/{total} số";
            }
        }

        private void ExportAccountNumbersExcel()
        {
            var rows = FilteredAccountNumbers().ToList();
            if (rows.Count == 0)
            {
                MessageBox.Show(this, Msg.AccountSet.ExcelNoData, "Xuất Excel",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using var dlg = new SaveFileDialog
            {
                Title = "Xuất danh sách số trên VOS3000 theo account",
                Filter = "Excel (*.xlsx)|*.xlsx",
                FileName = $"so_theo_account_vos_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx",
                AddExtension = true,
                DefaultExt = "xlsx",
                OverwritePrompt = true
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                var data = rows.Select((x, i) => new[]
                {
                    (i + 1).ToString(),
                    AccountDisplay(x.Row),
                    AccountDirectionDisplay(x.Row.Direction),
                    x.Number,
                    NumberLockUtil.IsValid(x.Number) ? Msg.AccountSet.StatusOpen : Msg.AccountSet.StatusLocked,
                    x.Row.UpdatedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? ""
                });

                XlsxWriter.Write(dlg.FileName, "Số theo account",
                    new[] { "STT", "Account", "Hướng", "Số", "Trạng thái", "Cập nhật" },
                    data,
                    new double[] { 7, 24, 16, 24, 12, 21 });

                AccountLog($"Đã xuất Excel {rows.Count} dòng: {dlg.FileName}");
                MessageBox.Show(this, string.Format(Msg.AccountSet.ExcelDone, rows.Count, dlg.FileName),
                    "Xuất Excel", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception e)
            {
                AccountLog(string.Format(Msg.AccountSet.ExcelError, e.Message));
                MessageBox.Show(this, string.Format(Msg.AccountSet.ExcelError, e.Message), "Xuất Excel",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async Task ClearAccountNumbersAsync()
        {
            if (_acctRows.Count == 0)
            {
                MessageBox.Show(this, Msg.AccountSet.ClearEmpty, "Xoá bảng",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show(this, Msg.AccountSet.ClearConfirm, "Xoá bảng",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            try
            {
                ToolDb.Configure(_settings);
                int n = await ToolDb.ClearAccountNumberSetsAsync();
                _acctRows = new List<AccountNumberSetRow>();
                PopulateAccountFilter();
                RenderAccountNumbers();
                _acctStateLbl.Text = string.Format(Msg.AccountSet.StateLoaded, 0, 0);
                _acctStateLbl.ForeColor = Color.Gray;
                AccountLog(string.Format(Msg.AccountSet.ClearDone, n));
            }
            catch (Exception e)
            {
                AccountLog(string.Format(Msg.AccountSet.LoadError, e.Message));
                MessageBox.Show(this, e.Message, "Xoá bảng", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// Hộp thoại nhập 1 dòng (dùng cho đổi mật khẩu). Trả null nếu huỷ.
        private string PromptInput(string caption, string title, string initial)
        {
            using var dlg = new Form
            {
                Text = title,
                Width = 420,
                Height = 170,
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MinimizeBox = false,
                MaximizeBox = false,
                Font = new Font("Segoe UI", 9.5f)
            };
            var lbl = new Label { Text = caption, Left = 16, Top = 16, Width = 370 };
            var box = new TextBox { Left = 16, Top = 44, Width = 370, Text = initial ?? "", UseSystemPasswordChar = false };
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 216, Top = 84, Width = 80 };
            var no = new Button { Text = "Huỷ", DialogResult = DialogResult.Cancel, Left = 306, Top = 84, Width = 80 };
            dlg.Controls.AddRange(new Control[] { lbl, box, ok, no });
            dlg.AcceptButton = ok;
            dlg.CancelButton = no;
            return dlg.ShowDialog(this) == DialogResult.OK ? box.Text : null;
        }

        private void GoLockPage(int page)
        {
            if (_lockPageNum == null) return;
            int max = (int)_lockPageNum.Maximum;
            int target = Math.Clamp(page, 0, max - 1);
            if ((int)_lockPageNum.Value != target + 1)
                _lockPageNum.Value = target + 1;    // ValueChanged se render
            else
            {
                _lockPage = target;
                RenderLockPage();
            }
        }

        private string SelectedLockNumber()
        {
            if (_lockGrid?.CurrentRow == null) return null;
            var v = _lockGrid.CurrentRow.Cells[GridColumn.Num].Value?.ToString();
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }

        /// Gia tri goc tren server cua dong dang chon (de tac dung chinh xac len server).
        private string SelectedLockOrig()
        {
            if (_lockGrid?.CurrentRow == null) return null;
            var v = _lockGrid.CurrentRow.Cells["orig"].Value?.ToString();
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }

        /// <summary>TẬP SỐ ĐÃ TÍCH CHỌN trên lưới (mỗi số 1 lần, theo "gốc" số) — dùng cho khóa/mở nhiều số.</summary>
        private List<string> SelectedLockNumbers()
        {
            var res = new List<string>();
            if (_lockGrid == null) return res;
            var seen = new HashSet<string>();
            foreach (DataGridViewRow row in _lockGrid.Rows)
            {
                if (!(row.Cells[GridColumn.Pick].Value is bool picked) || !picked) continue;
                string n = NumberLockUtil.Clean(row.Cells[GridColumn.Num].Value?.ToString());
                if (n.Length == 0) continue;
                if (seen.Add(NumberLockUtil.TailKey(n))) res.Add(n);
            }
            return res;
        }

        /// <summary>Nếu chưa tích số nào mà đang chọn 1 dòng -> lấy dòng đang chọn (giữ cách cũ).</summary>
        private List<string> SelectedLockNumbersOrCurrent()
        {
            var picked = SelectedLockNumbers();
            if (picked.Count > 0) return picked;
            var cur = SelectedLockNumber();
            if (!string.IsNullOrWhiteSpace(cur)) picked.Add(cur);
            return picked;
        }

        /// <summary>Ghép danh sách số đã chọn thành 1 chuỗi ("số1;số2;…") để truyền vào RunLockTask.</summary>
        private static string JoinLockSelection(IEnumerable<string> nums)
        {
            var list = (nums ?? Enumerable.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            return list.Count == 0 ? null : string.Join(";", list);
        }

        /// <summary>Tích / bỏ tích TẤT CẢ dòng đang hiển thị (trang hiện tại).</summary>
        private void SetLockPickAllOnPage(bool pick)
        {
            if (_lockGrid == null) return;
            _lockGrid.EndEdit();
            foreach (DataGridViewRow row in _lockGrid.Rows)
                row.Cells[GridColumn.Pick].Value = pick;
        }

        private void RefreshLockRow(DataGridViewRow row)
        {
            string n = NumberLockUtil.Clean(row.Cells[GridColumn.Num].Value?.ToString());
            bool valid = NumberLockUtil.IsValid(n);
            row.Cells[GridColumn.Num].Value = n;
            row.Cells[GridColumn.Status].Value = NumberLockUtil.StatusText(n);
            row.Cells[GridColumn.Status].Style.ForeColor = valid ? Color.Green : Color.Red;
            row.Cells["openform"].Value = valid ? "(đã hợp lệ)" : NumberLockUtil.Unlock(n);
        }

        /// Lap danh sach hien thi (hien co tim kiem) theo trang hien tai.
        private List<string> LockView()
        {
            string q = _lockSearchBox == null ? "" : _lockSearchBox.Text.Trim();
            if (q.Length == 0) return _lockAll;
            q = q.TrimStart('#');
            return _lockAll.Where(x => x.Contains(q) || (x.StartsWith("#") && x.Substring(1).Contains(q))).ToList();
        }

        private void RenderLockPage()
        {
            if (_lockGrid == null) return;
            var view = LockView();
            int pages = Math.Max(1, (int)Math.Ceiling(view.Count / (double)LockPageSize));
            if (_lockPage >= pages) _lockPage = pages - 1;
            if (_lockPage < 0) _lockPage = 0;

            _lockGrid.Rows.Clear();
            int start = _lockPage * LockPageSize;
            int end = Math.Min(start + LockPageSize, view.Count);
            for (int i = start; i < end; i++)
            {
                int r = _lockGrid.Rows.Add();
                _lockGrid.Rows[r].Cells["stt"].Value = (i + 1).ToString();
                _lockGrid.Rows[r].Cells["orig"].Value = view[i];
                _lockGrid.Rows[r].Cells[GridColumn.Num].Value = view[i];
                // giữ lựa chọn khi chuyển trang / đổi bộ lọc tìm kiếm
                _lockGrid.Rows[r].Cells[GridColumn.Pick].Value = _lockPicked.Contains(NumberLockUtil.TailKey(view[i]));
                RefreshLockRow(_lockGrid.Rows[r]);
            }

            string q = _lockSearchBox?.Text.Trim() ?? "";
            string filterNote = q.Length > 0 ? $" (lọc '{q}': {view.Count} số)" : "";
            int outTotal = _lockMappings.Count, inTotal = _lockRoutings.Count;
            int outWithNums = _lockMappings.Count(m => NumberLockUtil.Split(m.Prefix ?? "").Nums.Count > 0);
            int inWithNums = _lockRoutings.Count(r => NumberLockUtil.SplitPlain(r.Prefix).Count > 0);
            string accNote = outTotal + inTotal > 0
                ? $" từ {outWithNums}/{outTotal} account call-out + {inWithNums}/{inTotal} call-in"
                : "";
            string pickNote = _lockPicked.Count > 0 ? $" | ✔ đã chọn {_lockPicked.Count} số" : "";
            _lockPageLbl.Text =
                $"Trang {_lockPage + 1}/{pages} — hiện {Math.Max(0, end - start)} số{filterNote}{pickNote} | " +
                $"Tổng: {_lockAll.Count} số duy nhất{accNote} " +
                $"(✔ {_lockAll.Count(NumberLockUtil.IsValid)} | 🔒 {_lockAll.Count(NumberLockUtil.IsLocked)})";
            SyncPageNum(pages);
        }

        private void SyncPageNum(int pages)
        {
            // cap nhat Maximum/Value ma khong kich hoat render vong lai
            _lockPageNum.ValueChanged -= OnLockPageNumChanged;
            _lockPageNum.Maximum = pages;
            if ((int)_lockPageNum.Value != _lockPage + 1)
                _lockPageNum.Value = _lockPage + 1;
            _lockPageNum.ValueChanged += OnLockPageNumChanged;
        }

        private void OnLockPageNumChanged(object sender, EventArgs e)
        {
            int p = (int)_lockPageNum.Value - 1;
            if (p != _lockPage) { _lockPage = p; RenderLockPage(); }
        }

        /// Nap danh sach GỘP cua tat ca account (call-out + call-in) va hien trang dau tien.
        private void FillLockGrid(List<string> mergedNums, List<MappingEntry> mappings,
            List<RoutingEntry> routings = null)
        {
            _lockMappings = mappings ?? new List<MappingEntry>();
            _lockRoutings = routings ?? new List<RoutingEntry>();
            _lockAll = mergedNums ?? new List<string>();
            // giữ lựa chọn ✔ cho các số còn tồn tại, bỏ số đã biến mất khỏi server
            if (_lockPicked.Count > 0)
            {
                var live = new HashSet<string>(_lockAll.Select(NumberLockUtil.TailKey));
                _lockPicked.IntersectWith(live);
            }
            _lockPage = 0;
            _lockSearchBox.Text = "";
            RenderLockPage();
            int withNums = _lockMappings.Count(m => NumberLockUtil.Split(m.Prefix ?? "").Nums.Count > 0);
            int inNums = _lockRoutings.Count(r => NumberLockUtil.SplitPlain(r.Prefix).Count > 0);
            _lockLogBox.AppendText(string.Format(Msg.NumberLock.LoadedAll,
                _lockAll.Count, withNums, _lockMappings.Count, inNums, _lockRoutings.Count,
                _lockAll.Count(NumberLockUtil.IsValid), _lockAll.Count(NumberLockUtil.IsLocked),
                LockPageSize) + Environment.NewLine);
        }

        private void LockAppendLog(string s)
        {
            if (IsDisposed) return;
            try { Invoke(() => _lockLogBox.AppendText(s + Environment.NewLine)); } catch { }
        }

        private void SetLockState(string text, Color color)
        {
            if (IsDisposed) return;
            try { Invoke(() => { _lockStateLbl.AutoSize = true; _lockStateLbl.Text = text; _lockStateLbl.ForeColor = color; }); } catch { }
        }

        private void RunLockTask(string kind, string selectedNum)
        {
            // dong bo server/port/session tu tab Mapping Prefix (co luu settings)
            ApplyMapConfig();
            string server = _settings.vos_server;
            int port = _settings.vos_port;
            string sessionPath = Bootstrap.ResolveSessionFile(_settings);

            // kết thúc edit đang pending trước khi đọc giá trị lưới
            try { _lockGrid?.EndEdit(); } catch { }

            if (kind == "lock" && string.IsNullOrWhiteSpace(selectedNum))
            {
                MessageBox.Show(this, "Hãy tích ✔ (hoặc click) 1 số trong bảng trước khi khóa.\nCó thể tích NHIỀU số để khóa cùng lúc.",
                    "Khóa số", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (kind == "unlock" && string.IsNullOrWhiteSpace(selectedNum))
            {
                MessageBox.Show(this, "Hãy tích ✔ (hoặc click) 1 số trong bảng trước khi mở khóa.\nCó thể tích NHIỀU số để mở khóa cùng lúc.",
                    "Khóa số", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // "commit" = chi ghi lai O duoc sua: (gia tri goc tren server, gia tri moi tren luoi)
            List<(string From, string To)> edits = null;
            if (kind == "commit")
            {
                edits = new List<(string, string)>();
                foreach (DataGridViewRow row in _lockGrid.Rows)
                {
                    string orig = row.Cells["orig"].Value?.ToString() ?? "";
                    string num = NumberLockUtil.Clean(row.Cells[GridColumn.Num].Value?.ToString());
                    if (num.Length == 0) continue;
                    if (num != orig) edits.Add((orig, num));
                }
                if (edits.Count == 0)
                {
                    MessageBox.Show(this, "Bạn chưa sửa ô 'Số' nào trên lưới.\nSửa tương ứng (vd 0901 → 84901) rồi bấm nút này.",
                        "Khóa số", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
            }

            foreach (var b in _lockButtons) b.Enabled = false;
            SetLockState(Msg.NumberLock.StateRunning, Color.DarkOrange);
            LockAppendLog("");
            LockAppendLog(string.Format(Msg.Mapping.Header, $"KHÓA SỐ [{kind.ToUpper()}]", server, port, DateTime.Now.ToString("HH:mm:ss")));
            if (kind == OperationType.Lock)
                LockAppendLog($"Khóa số: '{string.Join(",", NumberLockUtil.SplitPlain(selectedNum))}' → '{string.Join(",", NumberLockUtil.SplitPlain(selectedNum).Select(NumberLockUtil.Lock))}'");
            if (kind == OperationType.Unlock)
                LockAppendLog($"Mở số: '{string.Join(",", NumberLockUtil.SplitPlain(selectedNum))}' → '{string.Join(",", NumberLockUtil.SplitPlain(selectedNum).Select(NumberLockUtil.Unlock))}'");

            Task.Run(() =>
            {
                try
                {
                    var client = new PrefixClient(server, port, sessionPath,
                        m => LockAppendLog(m.StartsWith("  ") ? m : "  " + m));
                    using var s = client.OpenSession();
                    if (s == null) { SetLockState(Msg.State.ConnectError, Color.Red); return; }

                    // 0) nạp template call-in (gateway routing) — có settlement (callinnn) trong đó
                    client.LoadRoutingTemplates(PrefixApiSyncer.LoadTraffic("vos_routing_c2s.bin"),
                                                PrefixApiSyncer.LoadTraffic("vos_routing_c2s.bin"));

                    // 1) GET live — TAT CA account customer (gateway mapping = call-out) trên server
                    var mappings = client.ReadAllMappings(s);

                    // 1b) GET live — TAT CA gateway routing (call-in / settlement) trên server
                    var routings = client.ReadAllRoutings(s);

                    if (mappings.Count == 0 && routings.Count == 0)
                    {
                        LockAppendLog(Msg.NumberLock.NoAccounts);
                        SetLockState(Msg.NumberLock.StateNotFound, Color.Red);
                        return;
                    }

                    // 2) gộp danh sách số của MỌI account (call-out + call-in) -> mỗi số chỉ hiện 1 lần
                    var merged = NumberLockUtil.DistinctByKey(
                        mappings.SelectMany(m => NumberLockUtil.Split(m.Prefix ?? "").Nums)
                                .Concat(routings.SelectMany(r => NumberLockUtil.SplitPlain(r.Prefix))));

                    if (kind == "load")
                    {
                        SetLockState(Msg.NumberLock.StateOkGrid, Color.Green);
                        try { Invoke(() => FillLockGrid(merged, mappings, routings)); } catch { }
                        // DB: đếm nghiệp vụ tải + cập nhật bảng số đã mapping
                        try
                        {
                            ToolDb.RecordOperation(_settings, OperationType.LockLoad, "", "", Status.Success,
                                string.Format(Msg.NumberLock.RecordLoad, server, port, merged.Count));
                            ToolDb.UpdateMappedNumbers(NumberLockUtil.Join("1;", merged), MappedSource.Read);
                        }
                        catch { }
                        RefreshDbStats();
                        return;
                    }

                    string before = null;    // ghi DB: 1 số (đuôi) hoặc danh sách nhiều số
                    string target2 = null;
                    var applied = new List<(string Before, string After)>();   // từng số đã tác động (ghi DB)

                    if (kind == OperationType.Lock || kind == OperationType.Unlock)
                    {
                        bool wantLock = kind == OperationType.Lock;
                        var targets = (selectedNum ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
                                                        .Select(NumberLockUtil.Clean)
                                                        .Where(x => x.Length > 0)
                                                        .ToList();
                        int okN = 0;
                        string lastBefore = null, lastAfter = null;

                        foreach (var target in targets)
                        {
                            var key = NumberLockUtil.TailKey(target);
                            string cur = merged.FirstOrDefault(x => NumberLockUtil.TailKey(x) == key);
                            string newVal;

                            if (cur == null)
                            {
                                if (!wantLock)
                                {
                                    LockAppendLog(string.Format(Msg.NumberLock.NotOnServer, target));
                                    continue;
                                }
                                newVal = NumberLockUtil.Lock(NumberLockUtil.Unlock(target));   // dạng chuẩn "#84…"
                                merged.Add(newVal);
                                LockAppendLog(string.Format(Msg.NumberLock.NotInAnyAccount, target, newVal));
                            }
                            else
                            {
                                newVal = wantLock ? NumberLockUtil.Lock(cur) : NumberLockUtil.Unlock(cur);
                                if (NumberLockUtil.IsLocked(cur) == wantLock)
                                {
                                    LockAppendLog(string.Format(Msg.NumberLock.AlreadyInState, cur));
                                    continue;
                                }
                                int idx = merged.FindIndex(x => NumberLockUtil.TailKey(x) == key);
                                if (idx >= 0) merged[idx] = newVal;
                                applied.Add((cur, newVal));
                            }
                            okN++;
                            lastBefore = cur;
                            lastAfter = newVal;
                        }

                        if (targets.Count > 1)
                            LockAppendLog(string.Format(Msg.NumberLock.MultiSummary, wantLock ? "khóa" : "mở khóa",
                                okN, targets.Count - okN, targets.Count));

                        if (okN == 0)
                        {
                            SetLockState(targets.Count > 1
                                    ? string.Format(Msg.NumberLock.StateMultiNoChange, targets.Count)
                                    : Msg.NumberLock.StateNoChange,
                                Color.Gray);
                            try { Invoke(() => FillLockGrid(merged, mappings, routings)); } catch { }
                            return;
                        }

                        // ghi DB: nhiều số -> gộp thành 1 dòng (giữ tương thích bảng t_number_action)
                        if (applied.Count > 1)
                        {
                            before = string.Join(",", applied.Select(x => NumberLockUtil.TailKey(x.Before)));
                            target2 = string.Join(",", applied.Select(x => NumberLockUtil.TailKey(x.After)));
                        }
                        else
                        {
                            before = lastBefore;
                            target2 = lastAfter;
                        }
                    }
                    else if (kind == "commit")
                    {
                        LockAppendLog(string.Format(Msg.NumberLock.CommitApplying, edits?.Count ?? 0));
                        foreach (var (from, to) in edits)
                        {
                            var kFrom = NumberLockUtil.TailKey(from);
                            var kTo = NumberLockUtil.TailKey(to);
                            int idx = merged.FindIndex(x => NumberLockUtil.TailKey(x) == kFrom);

                            // giu nguyen trang thai khoa/mo cua so cu, chi doi phan "so"
                            bool lockedFrom = idx >= 0 && merged[idx].StartsWith("#");
                            merged.RemoveAll(x => NumberLockUtil.TailKey(x) == kFrom || NumberLockUtil.TailKey(x) == kTo);
                            int at = idx < 0 ? merged.Count : Math.Min(idx, merged.Count);
                            merged.Insert(at, lockedFrom ? NumberLockUtil.Lock(to) : NumberLockUtil.Unlock(to));

                            if (kFrom != kTo) LockAppendLog($"  '{from}' → '{to}' (tất cả account)");
                        }
                    }

                    // 3) áp tập số gộp lên TẤT CẢ account (mỗi account giữ số riêng)
                    var stateByKey = NumberLockUtil.StateByKey(merged);
                    SetLockState(Msg.NumberLock.StateApplying, Color.DarkOrange);
                    string label = kind == OperationType.Lock ? "khóa" : kind == OperationType.Unlock ? "mở khóa" : "cập nhật";
                    int okC = 0, failC = 0;
                    try
                    {
                        var (mOk, mFail) = client.ApplyStateToAllMappings(s, mappings, stateByKey);
                        var (rOk, rFail) = client.ApplyStateToAllRoutings(s, routings, stateByKey);
                        okC = mOk + rOk;
                        failC = mFail + rFail;
                    }
                    catch (Exception ex)
                    {
                        LockAppendLog(string.Format(Msg.Mapping.ErrorLog, ex.Message));
                        failC = mappings.Count + routings.Count;
                    }

                    bool ok = failC == 0;
                    int changed = okC + failC;
                    // tóm tắt đối tượng tác động (1 số, nhiều số, hoặc cả lưới)
                    string scope = kind == OperationType.Lock || kind == OperationType.Unlock
                        ? $"{NumberLockUtil.SplitPlain(selectedNum).Count} số"
                        : "(lưới)";
                    SetLockState(ok
                        ? string.Format(Msg.NumberLock.AppliedAll, mappings.Count, okC, changed, scope, label)
                        : string.Format(Msg.NumberLock.ApplyFailedAll, scope, label, okC, changed),
                        ok ? Color.Green : Color.Red);

                    // đọc lại toàn bộ account (call-out + call-in) -> dựng lại danh sách gộp chuẩn
                    var finalMaps = client.ReadAllMappings(s);
                    if (finalMaps.Count == 0) finalMaps = mappings;
                    var finalRoutings = client.ReadAllRoutings(s);
                    if (finalRoutings.Count == 0) finalRoutings = routings;
                    var finalMerged = NumberLockUtil.DistinctByKey(
                        finalMaps.SelectMany(m => NumberLockUtil.Split(m.Prefix ?? "").Nums)
                                 .Concat(finalRoutings.SelectMany(r => NumberLockUtil.SplitPlain(r.Prefix))));
                    try { Invoke(() => FillLockGrid(finalMerged, finalMaps, finalRoutings)); } catch { }

                    // Lưu vào DB của tool
                    try
                    {
                        string newSummary = $"gộp {finalMerged.Count} số / {finalMaps.Count} account — ghi OK {okC}, lỗi {failC}";
                        string rbSummary = $"{finalMaps.Count} account (đọc lại)";
                        // nhiều số -> ghi tóm tắt (tránh chuỗi quá dài trong cột DB)
                        string recSel = selectedNum == null
                            ? "(lưới)"
                            : NumberLockUtil.SplitPlain(selectedNum).Count > 3
                                ? $"{NumberLockUtil.SplitPlain(selectedNum).Count} số"
                                : selectedNum;
                        ToolDb.RecordOperation(_settings, kind, "", "", ok ? Status.Success : Status.Failed,
                            string.Format(Msg.NumberLock.RecordOperation, kind, recSel, newSummary, rbSummary));
                        if (kind == OperationType.Lock || kind == OperationType.Unlock)
                            ToolDb.RecordNumberAction(kind, before, target2, ok,
                                ok ? Msg.NumberLock.RecordNumberActionOk : string.Format(Msg.NumberLock.RecordNumberActionFail, failC));
                        ToolDb.Log(LogModule.NumberLock, ok ? LogLevel.Info : LogLevel.Warn, "",
                            string.Format(Msg.NumberLock.RecordLog, kind, ok ? "OK" : "FAIL"));
                        if (ok) ToolDb.UpdateMappedNumbers(NumberLockUtil.Join("1;", finalMerged), kind);
                    }
                    catch { }
                    RefreshDbStats();
                }
                catch (Exception e)
                {
                    LockAppendLog(string.Format(Msg.Mapping.ErrorLog, e.Message));
                    SetLockState(Msg.State.Error, Color.Red);
                }
                finally
                {
                    if (!IsDisposed)
                        try { Invoke(() => { foreach (var b in _lockButtons) b.Enabled = true; }); } catch { }
                }
            });
        }

        private void ApplyHealthConfig()
        {
            _settings.health_domain = _healthDomainBox.Text.Trim();
            _settings.health_endpoint = _healthEndpointBox.Text.Trim();
            _settings.health_interval_seconds = (int)_healthIntervalNum.Value;
            _settings.health_timeout_seconds = (int)_healthTimeoutNum.Value;
            if (_tlsVerifyChk != null) _settings.verify_tls = _tlsVerifyChk.Checked;   // tùy chọn chung (tab Cấu hình)
            _settings.Normalize();
            _health.Configure(_settings);
        }

        private void StartHealth()
        {
            ApplyHealthConfig();
            try { Config.SaveSettings(_settings); } catch { }

            if (string.IsNullOrWhiteSpace(_health.Domain))
            {
                MessageBox.Show(this, "Nhập Domain API nguồn trước.", "Healthcheck",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (_health.Running) return;
            _healthOkCount = 0;
            _healthFailCount = 0;
            _health.Configure(_settings);
            _health.Start();
            if (_sourceDot != null && !_sourceDot.IsDisposed)
            {
                _sourceDot.ForeColor = Color.DarkOrange;
                _statusTip.SetToolTip(_sourceDot, $"Đang ping Server nguồn — {_health.Domain}…");
            }
            SetHealthState(Msg.State.Pinging, Color.DarkOrange);
        }

        private void StopHealth()
        {
            _health.Stop();
            SetHealthState(Msg.State.Stopped, Color.Gray);
        }

        private void PingOnce()
        {
            ApplyHealthConfig();
            if (string.IsNullOrWhiteSpace(_health.Domain))
            {
                MessageBox.Show(this, "Nhập Domain API nguồn trước.", "Healthcheck",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            SetHealthState("● Đang ping…", Color.DarkOrange);
            string url = HealthChecker.BuildUrl(_settings.health_domain, _settings.health_endpoint);
            bool verifyTls = _tlsVerifyChk?.Checked ?? _settings.verify_tls;
            int timeout = _settings.health_timeout_seconds;

            Task.Run(async () =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var result = new HealthResult { Time = DateTime.Now, Url = url };
                try
                {
                    var handler = new HttpClientHandler
                    {
                        ServerCertificateCustomValidationCallback =
                            verifyTls ? null : (sender, cert, chain, errors) => true
                    };
                    using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeout) };
                    using var resp = await http.GetAsync(url);
                    sw.Stop();
                    result.Ok = resp.IsSuccessStatusCode;
                    result.StatusCode = (int)resp.StatusCode;
                    result.ElapsedMs = sw.ElapsedMilliseconds;
                    result.Message = resp.IsSuccessStatusCode
                        ? $"OK ({(int)resp.StatusCode} {resp.ReasonPhrase})"
                        : $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}";
                }
                catch (Exception e)
                {
                    sw.Stop();
                    result.ElapsedMs = sw.ElapsedMilliseconds;
                    result.Message = e is TaskCanceledException ? $"Timeout sau {timeout}s" : e.Message;
                }
                OnHealthResult(result);
            });
        }

        private void OnHealthResult(HealthResult r)
        {
            if (IsDisposed) return;
            try
            {
                Invoke(() => { AddHealthRow(r); UpdateSourceDot(r); });
            }
            catch { }
        }

        /// Đèn trạng thái Server nguồn (chấm thứ 2 trên header).
        private void UpdateSourceDot(HealthResult r)
        {
            if (IsDisposed || _sourceDot == null || _sourceDot.IsDisposed) return;
                _sourceDot.ForeColor = r.Ok ? BrandGreen : BrandRed;
            _sourceDot.Invalidate();
            string tip = r.Ok
                ? $"Server nguồn OK — {r.Url}\nLần cuối: {r.Time:HH:mm:ss} ({r.ElapsedMs} ms)"
                : $"KHÔNG kết nối được Server nguồn — {r.Url}\nLần cuối: {r.Time:HH:mm:ss}\nChi tiết: {r.Message}";
            _statusTip.SetToolTip(_sourceDot, tip);
        }

        private void AddHealthRow(HealthResult r)
        {
            if (r.Ok) _healthOkCount++; else _healthFailCount++;
            _healthLastMs = r.ElapsedMs;

            _healthGrid.Rows.Insert(0,
                r.Time.ToString("yyyy-MM-dd HH:mm:ss"),
                r.Url,
                r.StatusCode?.ToString() ?? "—",
                r.ElapsedMs.ToString(),
                r.Ok ? "OK" : "FAIL",
                r.Message);

            // Giới hạn 500 dòng để không phình bộ nhớ
            while (_healthGrid.Rows.Count > 500)
                _healthGrid.Rows.RemoveAt(_healthGrid.Rows.Count - 1);

            if (_healthGrid.Rows.Count > 0)
            {
                var row = _healthGrid.Rows[0];
                row.DefaultCellStyle.BackColor = r.Ok ? Color.Honeydew : Color.MistyRose;
                row.DefaultCellStyle.ForeColor = r.Ok ? Color.DarkGreen : Color.DarkRed;
            }

            SetHealthState(r.Ok ? Msg.State.Ok : Msg.State.Error, r.Ok ? Color.Green : Color.Red);
            _healthSummaryLbl.Text =
                $"Thành công: {_healthOkCount}   |   Lỗi: {_healthFailCount}   |   " +
                $"Lần cuối: {r.Time:HH:mm:ss} ({_healthLastMs} ms)";
        }

        private void SetHealthState(string text, Color color)
        {
            _healthStateLbl.Text = text;
            _healthStateLbl.ForeColor = color;
        }

        private static DataGridView MakeGrid()
        {
            var g = new DataGridView
            {
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                ReadOnly = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                RowHeadersVisible = false,
                BackgroundColor = SystemColors.Window,
                BorderStyle = BorderStyle.FixedSingle,
                EnableHeadersVisualStyles = false,
                ColumnHeadersHeight = 30
            };
            g.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(240, 244, 248);
            g.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(30, 30, 30);
            g.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 9, FontStyle.Bold);
            g.ColumnHeadersDefaultCellStyle.Padding = new Padding(4, 0, 4, 0);
            g.DefaultCellStyle.Padding = new Padding(4, 0, 4, 0);
            g.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(250, 251, 252);
            return g;
        }

        // ================= Hành vi =================

        private void LoadConfigIntoUi() { /* đã điền khi dựng UI */ }

        private void SaveConfig()
        {
            try
            {
                var s = _settings;

                // ===== VOS3000 server/port (tab Mapping Prefix) =====
                if (_mapServerBox != null)
                {
                    s.vos_server = _mapServerBox.Text.Trim();
                    s.vos_port = (int)_mapPortNum.Value;
                }

                // ===== Nguồn API danh sách số =====
                if (_prefixApiBox != null)
                {
                    s.prefix_api_url = _prefixApiBox.Text.Trim().TrimEnd('/');
                    s.prefix_api_key = _prefixKeyBox.Text.Trim();
                    s.prefix_api_field = _prefixFieldBox.Text.Trim();
                    if (string.IsNullOrEmpty(s.prefix_api_field)) s.prefix_api_field = "data.phone";
                    s.prefix_interval_seconds = (int)_prefixIntervalNum.Value;
                    s.prefix_auto_start = _prefixAutoChk.Checked;
                    s.prefix_remove_invalid = _prefixCleanChk?.Checked ?? false;
                }

                // ===== Monitor Telegram =====
                if (_tgEnableChk != null)
                {
                    s.telegram_enabled = _tgEnableChk.Checked;
                    s.telegram_bot_token = _tgTokenBox.Text.Trim();
                    s.telegram_chat_id = _tgChatBox.Text.Trim();
                    s.telegram_include_numbers = _tgNumbersChk.Checked;
                    s.telegram_only_on_change = _tgOnlyChangeChk.Checked;
                    s.telegram_max_numbers = (int)_tgMaxNum.Value;
                }

                // ===== Tùy chọn API dùng chung =====
                if (_tlsVerifyChk != null)
                    s.verify_tls = _tlsVerifyChk.Checked;

                // ===== Healthcheck =====
                if (_healthDomainBox != null)
                {
                    s.health_domain = _healthDomainBox.Text.Trim();
                    s.health_endpoint = _healthEndpointBox.Text.Trim();
                    s.health_interval_seconds = (int)_healthIntervalNum.Value;
                    s.health_timeout_seconds = (int)_healthTimeoutNum.Value;
                }

                // ===== DB của tool =====
                if (_dbNameBox != null)
                {
                    s.db_host = _mapServerBox != null ? _mapServerBox.Text.Trim() : s.db_host;   // cùng IP với Server VOS3000
                    s.db_port = (int)_dbPortNum.Value;
                    s.db_name = _dbNameBox.Text.Trim();
                    s.vos_db_name = _vosDbNameBox != null ? _vosDbNameBox.Text.Trim() : s.vos_db_name;
                    s.db_user = _dbUserBox.Text.Trim();
                    s.db_pass = _dbPassBox.Text.Trim();
                }

                s.Normalize();

                _settings = s;
                Config.SaveSettings(s);
                ToolDb.Configure(_settings);
                CustomerDb.Configure(_settings);
                _ = ToolDb.SaveConfigToDbAsync(s);   // lưu cấu hình vào t_config
                if (ToolDb.HasConfig()) RefreshDbStats();
                RefreshWatermarkLabel();
                MessageBox.Show(this, "Đã lưu cấu hình", "Cấu hình", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception e)
            {
                MessageBox.Show(this, e.Message, "Cấu hình lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void Simulate(string key)
        {
            var samples = new Dictionary<string, Dictionary<string, object>>
            {
                ["add_number_section"] = new() { ["job_id"] = "demo-add-001", ["type"] = "add_number_section", ["account_id"] = "DEMO001", ["number_start"] = "84901234000", ["number_end"] = "84901234099" },
                ["lock_number"] = new() { ["job_id"] = "demo-lock-001", ["type"] = "lock_number", ["account_id"] = "DEMO001", ["number_start"] = "84901234000", ["number_end"] = "84901234099", ["lock_mode"] = "all" }
            };
            if (!samples.TryGetValue(key, out var job)) return;

            var driver = new GuiDriver(_settings, _profiles, m => AppendDetect(m));
            var res = driver.Execute(job, simulate: true);
            Record(key, job, res.Status, res.Message);
            MessageBox.Show(this, res.Message + "\n\nKhông có dữ liệu VOS3000 nào bị thay đổi.", "Mô phỏng",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void InspectClient()
        {
            Task.Run(() =>
            {
                try
                {
                    string result = GuiDriver.InspectClient();
                    Invoke(() => AppendDetect(result));
                }
                catch (Exception e)
                {
                    Invoke(() => AppendDetect("Lỗi khảo sát: " + e.Message));
                }
            });
        }

        private void OpenProfiles()
        {
            Config.EnsureProfiles();
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = Config.ProfilesPath,
                    UseShellExecute = true
                });
            }
            catch (Exception e)
            {
                MessageBox.Show(this, Config.ProfilesPath + "\n" + e.Message, "GUI profiles");
            }
        }

        private void StartPolling()
        {
            SaveConfig();
            StartPollingInternal();
        }

        /// <summary>Bắt đầu polling không hiện hộp thoại (dùng cho auto-start / test).</summary>
        private void StartPollingInternal()
        {
            if (_worker != null && _worker.Running) return;
            if (string.IsNullOrWhiteSpace(_settings.api_base_url) || string.IsNullOrWhiteSpace(_settings.agent_token))
            {
                SetStatus("Thiếu API URL hoặc token — chưa polling");
                return;
            }

            _worker = new PollWorker(_settings, _profiles, OnWorkerEvent);
            _worker.Start();
            SetStatus("Đang polling");
        }

        private void StopPolling()
        {
            _worker?.Stop();
            SetStatus("Đang dừng…");
        }

        private void OnWorkerEvent(PollWorker.JobEvent e)
        {
            if (IsDisposed) return;
            try
            {
                Invoke(() => HandleEvent(e));
            }
            catch { }
        }

        private void HandleEvent(PollWorker.JobEvent e)
        {
            switch (e.Kind)
            {
                case PollWorker.JobEventKind.Log:
                    AppendDetect(e.Message);
                    break;
                case PollWorker.JobEventKind.Success:
                case PollWorker.JobEventKind.Failed:
                case PollWorker.JobEventKind.Unknown:
                case PollWorker.JobEventKind.Simulated:
                    {
                        var job = e.Job ?? new Dictionary<string, object>();
                        string type = Convert.ToString(job.GetValueOrDefault(JobField.Type)) ?? "";
                        string status = e.Kind switch
                        {
                            PollWorker.JobEventKind.Success => Status.Success,
                            PollWorker.JobEventKind.Failed => Status.Failed,
                            PollWorker.JobEventKind.Unknown => Status.NeedsReconciliation,
                            _ => Status.Simulated
                        };
                        // Chỉ ghi vào grid nghiệp vụ nếu type được hỗ trợ; job lạ chỉ vào Lịch sử.
                        if (Tasks.Any(t => t.Key == type))
                            Record(type, job, status, e.Message);
                        else
                            Record("", job, status, e.Message);
                        break;
                    }
                case PollWorker.JobEventKind.Error:
                case PollWorker.JobEventKind.Worker:
                case PollWorker.JobEventKind.Poll:
                    SetStatus(e.Message);
                    break;
            }
        }

        private void Record(string key, Dictionary<string, object> job, string status, string message)
        {
            string jobType = Convert.ToString(job.GetValueOrDefault(JobField.Type)) ?? "";
            var row = new Dictionary<string, string>
            {
                [RowField.Time] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
                [RowField.Type] = string.IsNullOrEmpty(key) ? (string.IsNullOrEmpty(jobType) ? "(không rõ)" : jobType) : key,
                [RowField.Job] = Convert.ToString(job.GetValueOrDefault(JobField.JobId)) ?? "",
                [RowField.Account] = Convert.ToString(job.GetValueOrDefault(JobField.AccountId)) ?? "",
                [RowField.Status] = status,
                [RowField.Message] = message
            };

            _rows.Add(row);
            _historyGrid.Rows.Insert(0, row[RowField.Time], row[RowField.Type], row[RowField.Job], row[RowField.Account], row[RowField.Status], row[RowField.Message]);
            HighlightNewest(_historyGrid, status);

            // Job lạ (không thuộc 4 nghiệp vụ) chỉ hiện ở Lịch sử.
            if (!string.IsNullOrEmpty(key) && _taskGrids.TryGetValue(key, out var grid))
            {
                grid.Rows.Insert(0, row[RowField.Time], row[RowField.Job], row[RowField.Account],
                    SummarizeJob(job), status, message);
                HighlightNewest(grid, status);
            }

            if (_counts.ContainsKey(status))
            {
                _counts[status]++;
                foreach (var (k, v) in _counts)
                    if (_metricLabels.TryGetValue(k, out var lbl))
                        lbl.Text = v.ToString();
            }

            // Lưu DB (nguồn chính của Lịch sử) — bỏ cache file
            try
            {
                ToolDb.RecordOperation(_settings, row[RowField.Type], row[RowField.Job], row[RowField.Account], row[RowField.Status], row[RowField.Message]);
                ToolDb.Log(LogModule.Job, status, row[RowField.Job], row[RowField.Message]);
            }
            catch { }
            RefreshDbStats();
        }

        /// <summary>Tóm tắt thông số job cho dễ đọc trên grid.</summary>
        private static string SummarizeJob(Dictionary<string, object> job)
        {
            if (job == null) return "";
            var parts = new List<string>();
            foreach (var key in new[] { JobField.NumberStart, JobField.NumberEnd, JobField.LimitValue, JobField.LockMode, JobField.LimitType })
            {
                if (job.TryGetValue(key, out var v) && v != null && Convert.ToString(v) is { Length: > 0 } s)
                    parts.Add($"{key}={s}");
            }
            if (parts.Count == 0)
            {
                // fallback: hiện các key lạ (bỏ các key đã có cột riêng)
                foreach (var kv in job)
                {
                    if (kv.Key is JobField.JobId or JobField.Type or JobField.AccountId) continue;
                    if (kv.Value == null) continue;
                    parts.Add($"{kv.Key}={kv.Value}");
                }
            }
            return string.Join("; ", parts);
        }

        private static void HighlightNewest(DataGridView grid, string status)
        {
            if (grid.Rows.Count == 0) return;
            var row = grid.Rows[0];
            var (back, fore) = status switch
            {
                "success" => (Color.Honeydew, Color.DarkGreen),
                "failed" => (Color.MistyRose, Color.DarkRed),
                "needs_reconciliation" => (Color.LemonChiffon, Color.DarkGoldenrod),
                "simulated" => (Color.AliceBlue, Color.SteelBlue),
                _ => (Color.White, Color.Black)
            };
            row.DefaultCellStyle.BackColor = back;
            row.DefaultCellStyle.ForeColor = fore;
        }

        /// Nạp log nghiệp vụ từ DB (nguồn chính, không dùng cache file).
        private async void LoadHistoryFromDb()
        {
            if (!ToolDb.HasConfig())
            {
                MessageBox.Show(this, "Chưa khai báo DB — vào tab Cấu hình, điền IP/Port/Tên DB rồi Khởi tạo.", "Lịch sử",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            SetStatus("Đang tải log từ DB…");
            var rows = await ToolDb.LoadOperationsAsync(500);
            try { Invoke(() =>
            {
                _historyGrid.Rows.Clear();
                foreach (var r in rows)
                {
                    int i = _historyGrid.Rows.Add(
                        r.Ts.ToString("yyyy-MM-dd HH:mm:ss"),
                        r.Type, r.JobId, r.Account, r.Status, r.Message);
                    HighlightNewest(_historyGrid, r.Status);
                    // HighlightNewest luôn chạm hàng đầu — tô trực tiếp đúng dòng vừa thêm:
                    var row = _historyGrid.Rows[i];
                    var (back, fore) = r.Status switch
                    {
                        "success" => (Color.Honeydew, Color.DarkGreen),
                        "failed" => (Color.MistyRose, Color.DarkRed),
                        "needs_reconciliation" => (Color.LemonChiffon, Color.DarkGoldenrod),
                        "simulated" => (Color.AliceBlue, Color.SteelBlue),
                        _ => (Color.White, Color.Black)
                    };
                    row.DefaultCellStyle.BackColor = back;
                    row.DefaultCellStyle.ForeColor = fore;
                }
                SetStatus($"Đã tải {rows.Count} dòng log từ DB.");
            }); } catch { }
        }

        /// Cập nhật thẻ thống kê (Tổng quan) từ DB.
        private async void RefreshDbStats()
        {
            if (!ToolDb.HasConfig()) { UpdateStatusDot(null); return; }
            var st = await ToolDb.GetStatsAsync();
            UpdateStatusDot(st);
            try { Invoke(() =>
            {
                if (_metricLabels.TryGetValue("ops_db", out var mb))
                    mb.Text = st.OpsTotal.ToString("N0");
                if (_metricLabels.TryGetValue("ok_today_db", out var mo))
                { mo.Text = st.OkToday.ToString("N0"); mo.ForeColor = BrandGreen; }
                if (_metricLabels.TryGetValue("fail_today_db", out var mf))
                { mf.Text = st.FailToday.ToString("N0"); mf.ForeColor = st.FailToday > 0 ? BrandRed : BrandDark; }
                if (_metricLabels.TryGetValue("lock_db", out var ml))
                { ml.Text = st.LockTotal.ToString("N0"); ml.ForeColor = BrandRed; }
                if (_metricLabels.TryGetValue("unlock_db", out var mu))
                { mu.Text = st.UnlockTotal.ToString("N0"); mu.ForeColor = BrandGreen; }
                if (_metricLabels.TryGetValue("sync_db", out var ms))
                    ms.Text = $"{st.SyncAddedTotal:N0}";
                if (_metricLabels.TryGetValue("mapped_db", out var mm))
                    mm.Text = st.MappedTotal.ToString("N0");
                if (_metricLabels.TryGetValue("mapped_today_db", out var mmt))
                    mmt.Text = st.MappedToday.ToString("N0");
                if (_metricLabels.TryGetValue("mapped_history_db", out var mmh))
                    mmh.Text = st.MappedHistory.ToString("N0");
            }); } catch { }
        }

        private void ExportCsv()
        {
            using var dlg = new SaveFileDialog
            {
                DefaultExt = ".csv",
                FileName = $"vos3000_agent_log_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
                Filter = "CSV (*.csv)|*.csv"
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            var sb = new StringBuilder();
            sb.AppendLine("time,type,job,account,status,message");

            int count = 0;
            try
            {
                // Ưu tiên xuất TỪ DB (đủ toàn bộ), không chỉ những dòng đang hiển thị trên lưới
                if (ToolDb.HasConfig())
                {
                    var ops = ToolDb.LoadOperationsAsync(5000).
                        GetAwaiter().GetResult();
                    foreach (var o in ops)
                    {
                        sb.AppendLine(
                            CsvEscape(o.Ts.ToString("yyyy-MM-dd HH:mm:ss")) + "," +
                            CsvEscape(o.Type) + "," + CsvEscape(o.JobId) + "," +
                            CsvEscape(o.Account) + "," + CsvEscape(o.Status) + "," +
                            CsvEscape(o.Message));
                        count++;
                    }
                }
            }
            catch { count = 0; }

            // Không có DB / DB lỗi → xuất những gì đang có trên lưới (an toàn, không throw)
            if (count == 0 && _rows.Count > 0)
            {
                foreach (var r in _rows)
                {
                    string V(string k) => r != null && r.TryGetValue(k, out var v) ? (v ?? "") : "";
                    sb.AppendLine(
                        CsvEscape(V("time")) + "," + CsvEscape(V("type")) + "," +
                        CsvEscape(V("job")) + "," + CsvEscape(V("account")) + "," +
                        CsvEscape(V("status")) + "," + CsvEscape(V("message")));
                    count++;
                }
            }

            try
            {
                File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true)); // UTF-8 BOM cho Excel
                MessageBox.Show(this, $"Đã xuất {count:N0} dòng\n{dlg.FileName}", "Xuất CSV",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Xuất CSV lỗi: " + ex.Message, "Xuất CSV",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static string CsvEscape(string s)
        {
            if (s == null) return "";
            if (s.Contains('"') || s.Contains(',') || s.Contains('\n'))
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        private void AppendDetect(string msg)
        {
            if (_detectBox == null || _detectBox.IsDisposed) return;   // legacy log box (không còn trên UI)
            _detectBox.AppendText(msg + Environment.NewLine);
            _detectBox.SelectionStart = _detectBox.TextLength;
            _detectBox.ScrollToCaret();
        }

        private void DrawStatusDot(object sender, PaintEventArgs e)
        {
            // vòng tròn đặc, giống icon trạng thái kiểu badge
            if (sender is Label l)
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.Clear(l.Parent?.BackColor ?? Color.White);
                using var b = new SolidBrush(l.ForeColor);
                e.Graphics.FillEllipse(b, 2, 2, l.Width - 5, l.Height - 5);
            }
        }

        private void SetStatus(string msg)
        {
            _statusText = msg;
            //红灯 nếu nội dung báo sự cố — ngược lại xanh
            bool bad = msg != null
                && (msg.StartsWith("Lỗi", StringComparison.OrdinalIgnoreCase)
                    || msg.StartsWith("Thiếu", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("lỗi", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("✗")
                    || msg.Contains("!!"));
            if (_statusLabel != null && !_statusLabel.IsDisposed)
            {
                try { Invoke(() =>
                {
                    _statusLabel.ForeColor = bad ? BrandRed : BrandGreen;
                    _statusLabel.Invalidate();
                    _statusTip.SetToolTip(_statusLabel, msg);
                }); } catch { }
            }
        }

        private string _statusText = "Sẵn sàng";

        /// Đèn trạng thái dựa theo phiên kết nối DB gần nhất (gọi từ RefreshDbStats).
        private void UpdateStatusDot(ToolDbStats st)
        {
            if (IsDisposed || _statusLabel == null || _statusLabel.IsDisposed) return;
            try { Invoke(() =>
            {
                bool dbOk = st != null && st.DbOk;
                _statusLabel.ForeColor = dbOk ? BrandGreen : BrandRed;
                _statusLabel.Invalidate();
                string s = ToolDb.HasConfig()
                    ? $"{_settings.db_host}:{_settings.db_port}/{_settings.db_name}"
                    : "(chưa cấu hình DB)";
                string tip = dbOk
                    ? string.Format(Msg.Health.TooltipDbOk, s, _statusText)
                    : string.Format(Msg.Health.TooltipDbFail, s, st?.DbErr ?? "", _statusText);
                _statusTip.SetToolTip(_statusLabel, tip);
            }); } catch { }
        }

        // ================= Entry =================

        [STAThread]
        public static void Main()
        {
            ApplicationConfiguration.Initialize();

            // ===== Thiết lập môi trường lần chạy đầu (bản 1 file .exe) =====
            var boot = Bootstrap.Prepare();
            if (!boot.Ok)
            {
                MessageBox.Show(
                    string.Format(Msg.Bootstrap.DataDirError, boot.Error?.Message),
                    Msg.Bootstrap.DataDirTitle,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            var form = new MainForm();

            // Đăng ký trước khi Run: Application.Run sẽ block tới khi form đóng.
            form.Shown += (_, _) =>
            {
                // Thông báo lần cài đặt đầu tiên (chỉ 1 lần duy nhất)
                if (boot.FirstRun)
                {
                    var detail = boot.Notes.Count > 0 ? "\n\n" + string.Join("\n", boot.Notes) : "";
                    MessageBox.Show(form,
                        string.Format(Msg.Bootstrap.FirstRunOk, Bootstrap.DataDir, detail),
                        Msg.Bootstrap.FirstRunTitle,
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }

                // Healthcheck API nguồn: tự ping khi mở app
                if (form._settings.health_auto_start
                    && !string.IsNullOrWhiteSpace(form._settings.health_domain))
                {
                    form.StartHealth();
                }

                // Tự chạy polling (tương đương app.after(800, _start))
                if (form._settings.auto_poll
                    && !string.IsNullOrWhiteSpace(form._settings.api_base_url)
                    && !string.IsNullOrWhiteSpace(form._settings.agent_token))
                {
                    form.StartPollingInternal();
                }
            };

            Application.Run(form);
        }
    }
}
