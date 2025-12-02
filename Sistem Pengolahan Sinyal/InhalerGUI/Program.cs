using HelixToolkit.Geometry;
using HelixToolkit.Wpf;                               // HelixViewport3D
using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;
using System.Windows.Forms.Integration;               // ElementHost
using System.Windows.Media.Media3D;
using Color = System.Drawing.Color;
using Media3D = System.Windows.Media.Media3D;         // Alias anti konflik
using WpfControls = System.Windows.Controls;

namespace InhalerGUI
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    public class MainForm : Form
    {
        private double ComputeDynamicPole(double x0, double x1)
        {
            if (Math.Abs(x0) < 1e-9) x0 = 1e-9;
            double a = x1 / x0;

            // clamp nilai biar stabil
            if (a < 0.0001) a = 0.0001;
            if (a > 10) a = 10;

            double pole_s = Math.Log(a) / Dt;
            return pole_s;
        }


        // ------- KONFIGURASI SISTEM UMUM -------
        const int SensorCount = 6;
        const int ActuatorCount = 6;
        const int TotalSignals = SensorCount + ActuatorCount;
        const int BufferSize = 128;
        const double Dt = 0.1; // step waktu simulasi (detik) → 1 detik per update

        enum TabKind { Raw = 0, Noisy = 1, Filtered = 2 }

        // indeks sinyal
        const int IDX_HUMIDITY = 0;
        const int IDX_SPO2 = 1;
        const int IDX_PRESSURE = 2;
        const int IDX_IMU = 3;
        const int IDX_PARTICLE = 4;
        const int IDX_FLOW = 5;

        const int IDX_NOZZLE = 6;
        const int IDX_SOLENOID = 7;
        const int IDX_VIB_FEEDBACK = 8;
        const int IDX_DOSE_DISPLAY = 9;
        const int IDX_HEATER = 10;
        const int IDX_LED = 11;

        private Color[] SignalColors = new Color[]
{
    Color.Red,          // Humidity
    Color.Blue,         // SpO2
    Color.Green,        // Pressure
    Color.Orange,       // IMU
    Color.Purple,       // Particle
    Color.Brown,        // Flow
    Color.Magenta,      // Nozzle
    Color.Cyan,         // Solenoid
    Color.LimeGreen,    // Vibrator
    Color.DarkGoldenrod,// Dose Display
    Color.Sienna,       // Heater
    Color.DeepPink      // LED
};

        private double[,] _axisMin = new double[3, TotalSignals];
        private double[,] _axisMax = new double[3, TotalSignals];
        private bool[,] _axisInit = new bool[3, TotalSignals];

        // ------- DATA SINYAL -------
        private double[,] _raw = new double[TotalSignals, BufferSize];
        private double[,] _noisy = new double[TotalSignals, BufferSize];
        private double[,] _filtered = new double[TotalSignals, BufferSize];

        private double[] _filteredPrev = new double[TotalSignals]; // state filter IIR
        private double[] _controlParam = new double[TotalSignals]; // 0..1, diatur slider
        private double[] _frequencies = new double[TotalSignals];  // frekuensi modulasi internal

        private double _time = 0.0;
        private int _sampleIndex = 0;

        // state tambahan
        private double _vHigh = 0.0;            // untuk pressure sensor
        private double _lastDeltaP = 500.0;     // ΔP terakhir (digunakan di flow sensor)
        private double _nozzleY = 0.0;          // output aktuator nozzle (orde-1)
        private double _doseStartTime = 0.0;    // waktu awal untuk fungsi L(t)

        private Timer _timer;
        private Random _rnd = new Random();


        // CHART: [tab, signalIndex] untuk time dan freq
        private Chart[,] _timeCharts = new Chart[3, TotalSignals];
        private Chart[,] _freqCharts = new Chart[3, TotalSignals];

        // Combined charts per tab (sekarang untuk Laplace pole-zero)
        private Chart[] _combinedSCharts = new Chart[3];
        private Chart[] _combinedZCharts = new Chart[3];

        // tombol global
        private Button _btnStart;
        private Button _btnPause;

        public MainForm()
        {
            Text = "Multi-sensor Inhaler Adherence & Particle Optimization GUI";
            MinimumSize = new Size(1400, 800);
            BackColor = Color.FromArgb(235, 238, 245);
            WindowState = FormWindowState.Maximized;

            InitializeSignals();
            BuildUi();
            InitializeTimer();
        }

        // ---------------------------------------------------------
        // INISIALISASI SINYAL
        // ---------------------------------------------------------
        private void InitializeSignals()
        {
            for (int i = 0; i < TotalSignals; i++)
            {
                _frequencies[i] = 0.1 + 0.1 * i; // 0.1 .. ~1.2 Hz
                _controlParam[i] = 0.5;          // default 0.5 (tengah slider)
            }

            _vHigh = 0.0;
            _lastDeltaP = 500.0;
            _nozzleY = 0.0;
            _doseStartTime = 0.0;
        }

        // ---------------------------------------------------------
        // UI
        // ---------------------------------------------------------
        private void BuildUi()
        {
            // Layout utama: KIRI = parameter, KANAN = tabs
            var mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 1,
                ColumnCount = 2
            };
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300)); // panel kiri fix
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));  // kanan fleksibel

            var leftPanel = BuildLeftPanel();
            mainLayout.Controls.Add(leftPanel, 0, 0);

            var tab = new TabControl
            {
                Dock = DockStyle.Fill,
                Alignment = TabAlignment.Top
            };

            tab.TabPages.Add(BuildTabPage("Raw", TabKind.Raw));
            tab.TabPages.Add(BuildTabPage("Noise", TabKind.Noisy));
            tab.TabPages.Add(BuildTabPage("Filtered", TabKind.Filtered));
            tab.TabPages.Add(Build3DTab());   // <<< TAB 3D BARU
            tab.TabPages.Add(BuildVideoTab());   // <<< TAB VIDEO BARU
            tab.TabPages.Add(BuildEquationTab());

            mainLayout.Controls.Add(tab, 1, 0);
            Controls.Add(mainLayout);
        }

        // ---------------------------------------------------------
        // TAB BARU: EQUATIONS / RUMUS
        // ---------------------------------------------------------
        private TabPage BuildEquationTab()
        {
            var page = new TabPage("Equations");

            var browser = new WebBrowser
            {
                Dock = DockStyle.Fill,
                IsWebBrowserContextMenuEnabled = false, // Matikan klik kanan biar rapi
                AllowWebBrowserDrop = false
            };

            // Kita gunakan HTML sederhana dengan CSS untuk formatting yang cantik
            string css = @"
                <style>
                    body { font-family: 'Segoe UI', sans-serif; padding: 20px; background-color: #f5f5fa; color: #333; }
                    h2 { color: #2c3e50; border-bottom: 2px solid #3498db; padding-bottom: 10px; margin-top: 30px; }
                    h3 { color: #e67e22; margin-top: 20px; margin-bottom: 5px; }
                    .card { background: white; padding: 15px; border-radius: 8px; box-shadow: 0 2px 5px rgba(0,0,0,0.1); margin-bottom: 20px; }
                    .eq-box { background: #e8f6f3; border-left: 4px solid #1abc9c; padding: 10px; margin: 5px 0; font-family: 'Times New Roman', serif; font-size: 1.1em; font-style: italic; }
                    .desc { font-size: 0.9em; color: #666; margin-bottom: 10px; }
                    b { color: #2980b9; }
                    .variable { font-family: 'Courier New', monospace; background: #eee; padding: 2px 4px; border-radius: 4px; }
                </style>";

            string htmlContent = $@"
                <html>
                <head>{css}</head>
                <body>
                    <h1>System Mathematical Models</h1>
                    <p>Dokumentasi lengkap persamaan matematis yang digunakan dalam simulasi sensor, aktuator, dan pemrosesan sinyal.</p>

                    <h2>1. Sensor Models</h2>
                    <div class='card'>
                        <h3>Humidity Sensor</h3>
                        <div class='desc'>Model kapasitif berdasarkan kelembaban relatif.</div>
                        <div class='eq-box'>C_p = &phi; + 10 &middot; sin(2&pi; f t)</div>
                        <div class='eq-box'>RH = (C_p - C_0) / S</div>
                        <div class='desc'>Dimana <b>&phi;</b> adalah setpoint RH, dan <b>S</b> adalah sensitivitas.</div>

                        <h3>SpO2 Sensor (Photoplethysmography)</h3>
                        <div class='desc'>Ratio of Ratios dari sinyal AC/DC Merah dan Infrared.</div>
                        <div class='eq-box'>R = (AC_red / DC_red) / (AC_ir / DC_ir)</div>
                        <div class='eq-box'>SpO2 = A - B &middot; R</div>

                        <h3>Pressure Sensor (Differential)</h3>
                        <div class='desc'>Menggunakan filter orde-1 untuk simulasi respons sensor.</div>
                        <div class='eq-box'>vHigh[n] = &alpha; &middot; vHigh[n-1] + (1 - &alpha;) &middot; p_raw</div>
                        <div class='eq-box'>&Delta;P = vHigh / ScaleFactor</div>

                        <h3>IMU (Accelerometer)</h3>
                        <div class='desc'>Akselerasi statis akibat kemiringan gravitasi.</div>
                        <div class='eq-box'>&theta;(t) = &theta;_max &middot; sin(2&pi; f t)</div>
                        <div class='eq-box'>a_x = g &middot; sin(&theta;) + bias + noise</div>

                        <h3>Flow Sensor (Bernoulli)</h3>
                        <div class='desc'>Hubungan antara aliran dan perbedaan tekanan.</div>
                        <div class='eq-box'>Q = C_d &middot; A_c &middot; &radic;(2 &Delta;P / &rho;_air)</div>
                    </div>

                    <h2>2. Actuator Dynamics</h2>
                    <div class='card'>
                        <h3>Nozzle Spray (First-Order System)</h3>
                        <div class='desc'>Respon lag orde satu terhadap input flow.</div>
                        <div class='eq-box'>&tau;_s &middot; (dy/dt) + y(t) = x(t)</div>
                        <div class='desc'>Discretized: <b>y += Dt * ((-y + x) / &tau;_s)</b></div>

                        <h3>Solenoid Valve</h3>
                        <div class='desc'>Gaya elektromagnetik proporsional terhadap arus.</div>
                        <div class='eq-box'>F = k_s &middot; I(t)</div>

                        <h3>Vibration Feedback (Gaussian)</h3>
                        <div class='desc'>Profil getaran berbentuk lonceng (Gaussian curve).</div>
                        <div class='eq-box'>y = exp( - (k - m)^2 / (2&sigma;^2) )</div>

                        <h3>Dose Display (Gamma-like Decay)</h3>
                        <div class='desc'>Peluruhan luminansi seiring waktu.</div>
                        <div class='eq-box'>L(t) = L_0 &middot; exp( - (t / &tau;)^&beta; )</div>
                    </div>

                    <h2>3. Signal Processing & Domains</h2>
                    <div class='card'>
                        <h3>Noise Injection</h3>
                        <div class='desc'>White Gaussian Noise ditambahkan ke sinyal mentah.</div>
                        <div class='eq-box'>x_noisy = x_raw + A_noise &middot; Random(-1, 1)</div>

                        <h3>IIR Filter (Low Pass)</h3>
                        <div class='desc'>Filter Infinite Impulse Response orde satu (Exponential Smoothing).</div>
                        <div class='eq-box'>y[n] = y[n-1] + &alpha; &middot; (x[n] - y[n-1])</div>
                        <div class='desc'>Dimana <b>&alpha; = 0.02</b> (smoothing factor).</div>

                        <h3>FFT (Frequency Domain)</h3>
                        <div class='desc'>Magnitude spektrum frekuensi diskrit.</div>
                        <div class='eq-box'>X[k] = &Sigma; x[n] &middot; e^(-j 2&pi; k n / N)</div>
                        <div class='eq-box'>Mag[k] = &radic;(Re^2 + Im^2) / N</div>
                    </div>

                    <h2>4. S-Domain & Z-Domain Identification</h2>
                    <div class='card'>
                        <h3>Dynamic Pole Estimation</h3>
                        <div class='desc'>Estimasi pole sistem secara real-time dari rasio input/output.</div>
                        <div class='eq-box'>a = x[n] / x[n-1]</div>
                        <div class='eq-box'>Pole (s-domain) = ln(a) / Dt</div>
                        <div class='eq-box'>Pole (z-domain) = exp(Pole_s &middot; Dt)</div>
                    </div>

                    <br><br>
                    <center style='color:#bdc3c7'>Generated by InhalerGUI System</center>
                </body>
                </html>";

            browser.DocumentText = htmlContent;
            page.Controls.Add(browser);

            return page;
        }
        private TabPage BuildVideoTab()
        {
            var page = new TabPage("Video Demo")
            {
                BackColor = Color.Black
            };

            // Host WPF di WinForms
            var host = new ElementHost
            {
                Dock = DockStyle.Fill
            };

            // MediaElement WPF
            var media = new WpfControls.MediaElement
            {
                LoadedBehavior = WpfControls.MediaState.Manual,
                UnloadedBehavior = WpfControls.MediaState.Manual,
                Stretch = System.Windows.Media.Stretch.Uniform,   // biar proporsional
                Volume = 0.0                                      // mute (kalau mau ada suara: naikin)
            };

            // Looping video
            media.MediaEnded += (s, e) =>
            {
                media.Position = TimeSpan.Zero;
                media.Play();
            };

            // Path ke file video
            string videoPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "Resources",
                "demo.mp4");   // ganti nama file kalau beda

            if (!File.Exists(videoPath))
            {
                MessageBox.Show("Video tidak ditemukan: " + videoPath);
            }
            else
            {
                media.Source = new Uri(videoPath);
                media.Play();
            }

            host.Child = media;
            page.Controls.Add(host);

            return page;
        }

        private TabPage Build3DTab()
        {
            var page = new TabPage("3D Model")
            {
                BackColor = Color.Black
            };

            var host = new ElementHost
            {
                Dock = DockStyle.Fill,
                Child = Build3DView()
            };

            page.Controls.Add(host);
            return page;
        }


        // ---------------------------------------------------------
        // LOGIKA 3D (GANTI BAGIAN INI)
        // ---------------------------------------------------------
        // ---------------------------------------------------------
        // LOGIKA 3D (GANTI BAGIAN INI DI CODING ANDA)
        // ---------------------------------------------------------
        // ---------------------------------------------------------
        // LOGIKA 3D (VERSI PERBAIKAN)
        // ---------------------------------------------------------
        // ---------------------------------------------------------
        // LOGIKA 3D (LATAR BELAKANG ABU-ABU TUA)
        // ---------------------------------------------------------
        private HelixViewport3D Build3DView()
        {
            var view = new HelixViewport3D
            {
                // 1. GANTI LATAR BELAKANG JADI ABU-ABU TUA (BLENDER STYLE)
                // Gunakan Color.FromRgb untuk warna yang spesifik
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(60, 60, 60)),
                ShowCoordinateSystem = true,
                ZoomExtentsWhenLoaded = true
            };

            // Tambahkan DefaultLights
            view.Children.Add(new DefaultLights());

            // Lampu tambahan agar objek terlihat jelas di latar gelap
            var extraLight = new DirectionalLight(
                System.Windows.Media.Colors.White,
                new Media3D.Vector3D(-1, -1, -1));

            view.Children.Add(new ModelVisual3D { Content = extraLight });

            // 1. BODI UTAMA (Abu-abu)
            var body = LoadPart("Resources/body.stl",
                                System.Windows.Media.Colors.LightGray);
            if (body != null) view.Children.Add(body);

            // 2. LAYAR (Hitam)
            var screen = LoadPart("Resources/screen.stl",
                                  System.Windows.Media.Colors.Black);
            if (screen != null) view.Children.Add(screen);

            // 3. TOMBOL (Merah)
            var buttons = LoadPart("Resources/buttons.stl",
                                   System.Windows.Media.Colors.Red);
            if (buttons != null) view.Children.Add(buttons);

            return view;
        }

        // Helper untuk load file STL dan memberi warna spesifik
        // (Method ini TIDAK PERLU DIUBAH dari versi sebelumnya)
        private ModelVisual3D LoadPart(string path, System.Windows.Media.Color color)
        {
            var importer = new StLReader();
            try
            {
                var modelGroup = importer.Read(path);
                var matGroup = new MaterialGroup();

                // Warna Dasar
                matGroup.Children.Add(new DiffuseMaterial(new System.Windows.Media.SolidColorBrush(color)));

                // Efek Kilap (Specular) - Biar terlihat seperti plastik keras
                matGroup.Children.Add(new SpecularMaterial(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.White), 70.0));

                if (modelGroup is Model3DGroup group)
                {
                    foreach (var child in group.Children)
                    {
                        if (child is GeometryModel3D geometry)
                        {
                            geometry.Material = matGroup;
                            geometry.BackMaterial = matGroup;
                        }
                    }
                }
                return new ModelVisual3D { Content = modelGroup };
            }
            catch (Exception)
            {
                return null; // Abaikan jika file belum ada
            }
        }




        // ---------------- LEFT PANEL (SLIDER + LABEL NILAI) ----------------
        private Panel BuildLeftPanel()
        {
            var panel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(10),
                BackColor = Color.White
            };

            // Row 0: tombol Start / Pause
            _btnStart = new Button
            {
                Text = "Start",
                Width = 110,
                Height = 30,
                Location = new Point(10, 10),
                Font = new Font("Segoe UI", 9, FontStyle.Bold)
            };
            _btnStart.Click += (s, e) =>
            {
                _doseStartTime = _time;
                _timer.Start();
            };

            _btnPause = new Button
            {
                Text = "Pause",
                Width = 110,
                Height = 30,
                Location = new Point(150, 10),
                Font = new Font("Segoe UI", 9, FontStyle.Bold)
            };
            _btnPause.Click += (s, e) => _timer.Stop();

            panel.Controls.Add(_btnStart);
            panel.Controls.Add(_btnPause);

            // Flow layout untuk parameter
            var flow = new FlowLayoutPanel
            {
                Location = new Point(0, 50),
                Size = new Size(panel.Width - 5, panel.Height - 60),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true
            };

            // header sensor
            var lblSensor = new Label
            {
                Text = "Sensor Parameters",
                AutoSize = true,
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                Margin = new Padding(10, 5, 3, 3)
            };
            flow.Controls.Add(lblSensor);

            // 6 slider sensor
            for (int i = 0; i < SensorCount; i++)
            {
                int idxSignal = i;
                var group = CreateParamGroup(idxSignal);
                flow.Controls.Add(group);
            }

            // header actuator
            var lblAct = new Label
            {
                Text = "Actuator Parameters",
                AutoSize = true,
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                Margin = new Padding(10, 8, 3, 3)
            };
            flow.Controls.Add(lblAct);

            // 6 slider actuator
            for (int i = 0; i < ActuatorCount; i++)
            {
                int idxSignal = SensorCount + i;
                var group = CreateParamGroup(idxSignal);
                flow.Controls.Add(group);
            }

            panel.Controls.Add(flow);
            return panel;
        }

        // Panel parameter kecil seperti contoh HMI
        private Control CreateParamGroup(int idxSignal)
        {
            var panel = new Panel
            {
                Width = 260,
                Height = 70,
                Margin = new Padding(10, 3, 3, 3),
                BackColor = Color.FromArgb(245, 245, 245),
                BorderStyle = BorderStyle.FixedSingle
            };

            string title = GetSignalDisplayName(idxSignal);
            string paramLabel = GetParameterLabel(idxSignal);

            var lblTitle = new Label
            {
                Text = title,
                Font = new Font("Segoe UI", 8, FontStyle.Bold),
                Location = new Point(6, 4),
                AutoSize = true
            };

            var lblParam = new Label
            {
                Text = paramLabel,
                Font = new Font("Segoe UI", 7),
                Location = new Point(6, 20),
                AutoSize = true
            };

            var tb = new TrackBar
            {
                Minimum = 0,
                Maximum = 100,
                TickStyle = TickStyle.None,
                Width = 150,
                Location = new Point(6, 36),
                Value = 50,
                Tag = idxSignal
            };

            var valueLabel = new Label
            {
                Font = new Font("Segoe UI", 7, FontStyle.Italic),
                Location = new Point(165, 40),
                AutoSize = true,
                Text = GetParamValueText(idxSignal, _controlParam[idxSignal])
            };

            tb.Scroll += (s, e) =>
            {
                var bar = (TrackBar)s;
                int idx = (int)bar.Tag;
                _controlParam[idx] = bar.Value / 100.0;
                valueLabel.Text = GetParamValueText(idx, _controlParam[idx]);
            };

            panel.Controls.Add(lblTitle);
            panel.Controls.Add(lblParam);
            panel.Controls.Add(tb);
            panel.Controls.Add(valueLabel);

            return panel;
        }

        // ---------------------------------------------------------
        // TAB PAGE (RAW / NOISE / FILTERED)
        // ---------------------------------------------------------
        // ---------------------------------------------------------
        // TAB PAGE (RAW / NOISE / FILTERED) – 2 grafik per baris
        // ---------------------------------------------------------
        private TabPage BuildTabPage(string title, TabKind kind)
        {
            var page = new TabPage(title)
            {
                BackColor = Color.FromArgb(245, 245, 250)
            };

            // panel kanan yang bisa di-scroll
            var scrollPanel = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Color.FromArgb(245, 245, 250)
            };

            // flow utama: kiri → kanan, wrap = true
            // hasilnya: 2 grafik per baris (karena width kita set cukup besar)
            var mainFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                AutoSize = true
            };

            int tabIndex = (int)kind;

            // ----- semua sinyal: time + FFT -----
            for (int i = 0; i < TotalSignals; i++)
            {
                string baseName = GetSignalDisplayName(i);
                string yTime = GetYAxisLabel(i);

                // TIME-DOMAIN
                var cTime = CreateSignalChart(baseName + " - Time", "Time (s)", yTime);
                StyleTimeChart(cTime);
                cTime.Width = 750;   // atur lebar supaya 2 grafik pas 1 baris
                cTime.Height = 400;
                _timeCharts[tabIndex, i] = cTime;
                mainFlow.Controls.Add(cTime);

                // FFT
                var cFFT = CreateSignalChart(baseName + " - FFT", "Frequency (Hz)", "Magnitude");
                StyleFftChart(cFFT);
                cFFT.Width = 750;
                cFFT.Height = 400;
                _freqCharts[tabIndex, i] = cFFT;
                mainFlow.Controls.Add(cFFT);
            }

            // ----- combined s-domain -----
            var cS = CreateSignalChart(
                "Combined Laplace Pole (Sensors + Actuators)",
                "Re(s)", "Im(s)");
            StylePoleZeroChart(cS);
            cS.Width = 750;
            cS.Height = 400;
            _combinedSCharts[tabIndex] = cS;
            mainFlow.Controls.Add(cS);

            // ----- combined z-domain -----
            var cZ = CreateSignalChart(
                "Combined Z Pole-Zero (Sensors + Actuators)",
                "Re(s)", "Im(s)");
            StylePoleZeroChart(cZ);
            cZ.Width = 750;
            cZ.Height = 400;
            _combinedZCharts[tabIndex] = cZ;
            mainFlow.Controls.Add(cZ);

            // susun ke dalam tab
            scrollPanel.Controls.Add(mainFlow);
            page.Controls.Add(scrollPanel);

            return page;
        }


        // ---------------------------------------------------------
        // STYLE CHARTS (FONT, FORMAT ANGKA, BORDER)
        // ---------------------------------------------------------
        private Chart CreateSignalChart(string title, string xLabel, string yLabel)
        {
            var chart = new Chart
            {
                Width = 450,
                Height = 300,
                BackColor = Color.White,
                Margin = new Padding(10)
            };

            var area = new ChartArea();
            area.AxisX.MajorGrid.LineColor = Color.FromArgb(230, 230, 230);
            area.AxisY.MajorGrid.LineColor = Color.FromArgb(230, 230, 230);
            area.AxisX.LabelStyle.Font = new Font("Segoe UI", 6);
            area.AxisY.LabelStyle.Font = new Font("Segoe UI", 6);
            area.AxisX.Title = xLabel;
            area.AxisY.Title = yLabel;
            area.AxisX.TitleFont = new Font("Segoe UI", 8);
            area.AxisY.TitleFont = new Font("Segoe UI", 8);

            area.AxisX.Minimum = double.NaN;
            area.AxisX.Maximum = double.NaN;
            area.AxisY.Minimum = double.NaN;
            area.AxisY.Maximum = double.NaN;

            chart.ChartAreas.Add(area);

            chart.Titles.Add(new Title(title)
            {
                Font = new Font("Segoe UI", 8, FontStyle.Bold),
                ForeColor = Color.FromArgb(45, 45, 48)
            });

            chart.BorderlineColor = Color.Silver;
            chart.BorderlineDashStyle = ChartDashStyle.Solid;
            chart.BorderlineWidth = 1;

            return chart;
        }


        private void StyleTimeChart(Chart chart)
        {
            var a = chart.ChartAreas[0];
            a.AxisX.LabelStyle.Format = "0.0"; // time
            a.AxisY.LabelStyle.Format = "0.0";
        }

        private void StyleFftChart(Chart chart)
        {
            var a = chart.ChartAreas[0];
            a.AxisX.LabelStyle.Format = "0";
            a.AxisY.LabelStyle.Format = "0.0E0";
            a.AxisX.Minimum = 0;
            double fs = 1.0 / Dt;
            a.AxisX.Maximum = fs / 2.0;
            a.AxisY.Minimum = 0;
        }

        // Style untuk grafik Laplace pole-zero (s-plane)
        private void StylePoleZeroChart(Chart chart)
        {
            var a = chart.ChartAreas[0];
            a.AxisX.Minimum = -5.0;
            a.AxisX.Maximum = 5.0;
            a.AxisY.Minimum = -5.0;
            a.AxisY.Maximum = 5.0;
            a.AxisX.Crossing = 0;
            a.AxisY.Crossing = 0;
            a.AxisX.MajorGrid.Interval = 1.0;
            a.AxisY.MajorGrid.Interval = 1.0;
            a.AxisX.Title = "Re(s)";
            a.AxisY.Title = "Im(s)";
            a.AxisX.LabelStyle.Format = "0.0";
            a.AxisY.LabelStyle.Format = "0.0";
        }

        // ---------------------------------------------------------
        // LABEL & PARAMETER
        // ---------------------------------------------------------
        private string GetSignalDisplayName(int index)
        {
            switch (index)
            {
                case IDX_HUMIDITY: return "Humidity";
                case IDX_SPO2: return "SpO2";
                case IDX_PRESSURE: return "Pressure";
                case IDX_IMU: return "IMU";
                case IDX_PARTICLE: return "Particle";
                case IDX_FLOW: return "Flow";
                case IDX_NOZZLE: return "Nozzle";
                case IDX_SOLENOID: return "Solenoid";
                case IDX_VIB_FEEDBACK: return "Vibrator";
                case IDX_DOSE_DISPLAY: return "Dose Display";
                case IDX_HEATER: return "Heater";
                case IDX_LED: return "LED";
                default: return "Signal " + index;
            }
        }

        private string GetParameterLabel(int index)
        {
            switch (index)
            {
                case IDX_HUMIDITY:
                    return "Humidity (φ - RH setpoint)";
                case IDX_SPO2:
                    return "SpO2 (B - ratio gain)";
                case IDX_PRESSURE:
                    return "Pressure (pAmp - ΔP amplitude)";
                case IDX_IMU:
                    return "IMU (θmax - tilt angle)";
                case IDX_PARTICLE:
                    return "Particle (K - weighting)";
                case IDX_FLOW:
                    return "Flow (Cd - discharge coeff)";
                case IDX_NOZZLE:
                    return "Nozzle (τs - time constant)";
                case IDX_SOLENOID:
                    return "Solenoid (Imax - coil current)";
                case IDX_VIB_FEEDBACK:
                    return "Vibrator (σ² - spread)";
                case IDX_DOSE_DISPLAY:
                    return "Dose Display (L0 - brightness)";
                case IDX_HEATER:
                    return "Heater (t_ox fraction)";
                case IDX_LED:
                    return "LED (Vj - forward voltage)";
                default:
                    return "Param " + index;
            }
        }

        private string GetParamValueText(int index, double control)
        {
            switch (index)
            {
                case IDX_HUMIDITY:
                    double phi = 20.0 + 60.0 * control;
                    return string.Format("{0:0}% RH", phi);
                case IDX_SPO2:
                    double B = 20.0 + 20.0 * control;
                    return string.Format("B={0:0.0}", B);
                case IDX_PRESSURE:
                    double pAmp = 500.0 + 1500.0 * control;
                    return string.Format("{0:0} Pa", pAmp);
                case IDX_IMU:
                    double thetaDeg = 45.0 * control;
                    return string.Format("{0:0}°", thetaDeg);
                case IDX_PARTICLE:
                    double K = 0.5 + 1.5 * control;
                    return string.Format("K={0:0.00}", K);
                case IDX_FLOW:
                    double Cd = 0.5 + 0.5 * control;
                    return string.Format("Cd={0:0.00}", Cd);
                case IDX_NOZZLE:
                    double tauS = 0.1 + 0.9 * control;
                    return string.Format("τ={0:0.00}s", tauS);
                case IDX_SOLENOID:
                    double iMax = 0.2 + 1.8 * control;
                    return string.Format("{0:0.00} A", iMax);
                case IDX_VIB_FEEDBACK:
                    double spread = 0.1 + 1.4 * control;
                    return string.Format("σ²={0:0.00}", spread);
                case IDX_DOSE_DISPLAY:
                    double L0 = 50.0 + 150.0 * control;
                    return string.Format("L0={0:0}", L0);
                case IDX_HEATER:
                    double fracTox = 0.1 + 0.9 * control;
                    return string.Format("t_ox={0:0.00}", fracTox);
                case IDX_LED:
                    double Vj = 1.5 + 1.0 * control;
                    return string.Format("{0:0.00} V", Vj);
                default:
                    return string.Format("{0:0.00}", control);
            }
        }

        private string GetYAxisLabel(int index)
        {
            switch (index)
            {
                case IDX_HUMIDITY: return "Relative Humidity (%)";
                case IDX_SPO2: return "SpO2 (%)";
                case IDX_PRESSURE: return "ΔP (Pa)";
                case IDX_IMU: return "Acceleration (m/s²)";
                case IDX_PARTICLE: return "Concentration Cm (a.u.)";
                case IDX_FLOW: return "Flow Q (m³/s)";
                case IDX_NOZZLE: return "Nozzle Output (a.u.)";
                case IDX_SOLENOID: return "Force (N)";
                case IDX_VIB_FEEDBACK: return "Vibration Level (a.u.)";
                case IDX_DOSE_DISPLAY: return "Luminance L(t)";
                case IDX_HEATER: return "Conductivity (S/m, scaled)";
                case IDX_LED: return "Current (mA)";
                default: return "Amplitude";
            }
        }

        private void GetTimeYAxisRange(int signalIndex, out double ymin, out double ymax)
        {
            switch (signalIndex)
            {
                case IDX_HUMIDITY: ymin = 0; ymax = 120; break;
                case IDX_SPO2: ymin = 0; ymax = 105; break;
                case IDX_PRESSURE: ymin = 0; ymax = 200; break;
                case IDX_IMU: ymin = -20; ymax = 20; break;
                case IDX_PARTICLE: ymin = 0; ymax = 1000; break;
                case IDX_FLOW: ymin = -0.005; ymax = 0.03; break;
                case IDX_NOZZLE: ymin = -0.005; ymax = 0.03; break;
                case IDX_SOLENOID: ymin = 0; ymax = 40; break;
                case IDX_VIB_FEEDBACK: ymin = 0; ymax = 1.2; break;
                case IDX_DOSE_DISPLAY: ymin = -10; ymax = 40; break;
                case IDX_HEATER: ymin = 0; ymax = 20; break;
                case IDX_LED: ymin = -0.1; ymax = 2; break;
                default: ymin = 0; ymax = 1; break;
            }
        }

        private void GetTimeYAxisRangeForTab(int tab, int signalIndex, out double ymin, out double ymax)
        {
            GetTimeYAxisRange(signalIndex, out ymin, out ymax);
        }

        // ---------------------------------------------------------
        // TIMER
        // ---------------------------------------------------------
        private void InitializeTimer()
        {
            _timer = new Timer();
            _timer.Interval = (int)(Dt * 1000); // sekarang 1 detik
            _timer.Tick += Timer_Tick;
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            UpdateSignals();
            UpdateAllCharts();
        }

        // ---------------------------------------------------------
        // UPDATE SINYAL
        // ---------------------------------------------------------
        private void UpdateSignals()
        {
            _time += Dt;
            _sampleIndex = (_sampleIndex + 1) % BufferSize;

            double alphaFilter = 0.02;
            double noiseAmp = 0.4;

            const double g = 9.81;
            const double rhoAir = 1.2;

            for (int i = 0; i < TotalSignals; i++)
            {
                double modFreq = _frequencies[i];
                double control = _controlParam[i];

                double value = 0.0;

                // ---------------- SENSORS ----------------
                if (i == IDX_HUMIDITY)
                {
                    double phi = 20.0 + 60.0 * control;
                    double cp = phi + 10.0 * Math.Sin(2.0 * Math.PI * modFreq * _time);
                    double c0 = 0.0;

                    double S = (cp - c0) / (100.0 * (phi / 100.0) + 1e-6);
                    double RH = 100.0 * S;
                    value = RH;
                }
                else if (i == IDX_SPO2)
                {
                    double A = 110.0;
                    double B = 20.0 + 20.0 * control;

                    double ACred = 0.8 + 0.2 * Math.Sin(2 * Math.PI * modFreq * _time);
                    double DCred = 1.0;
                    double ACIR = 0.8 + 0.1 * Math.Cos(2 * Math.PI * (modFreq + 0.05) * _time);
                    double DCIR = 1.1;

                    double R = (ACred / DCred) / (ACIR / DCIR + 1e-6);
                    double SpO2 = A - B * R;
                    value = SpO2;
                }
                else if (i == IDX_PRESSURE)
                {
                    double pAmp = 500.0 + 1500.0 * control;
                    double p = pAmp * (0.5 + 0.5 * Math.Sin(2 * Math.PI * modFreq * _time));

                    double alphaA = 0.95;
                    double alphaR = 0.85;

                    if (p >= _vHigh)
                        _vHigh = alphaA * _vHigh + (1.0 - alphaA) * p;
                    else
                        _vHigh = alphaR * _vHigh + (1.0 - alphaR) * p;

                    double S_ticks = _vHigh;
                    double S_scale_factor = 10.0;
                    double deltaP = S_ticks / S_scale_factor;

                    _lastDeltaP = deltaP;
                    value = deltaP;
                }
                else if (i == IDX_IMU)
                {
                    double thetaMax = (Math.PI / 4.0) * control;
                    double theta = thetaMax * Math.Sin(2 * Math.PI * modFreq * _time);

                    double ax = g * Math.Sin(theta);
                    double bax = 0.05;
                    double imuNoise = 0.05 * (2 * _rnd.NextDouble() - 1.0);

                    double aTildeX = ax + bax + imuNoise;
                    value = aTildeX;
                }
                else if (i == IDX_PARTICLE)
                {
                    double K = 0.5 + 1.5 * control;
                    double[] vi = { 0.3, 0.5, 1.0, 2.5 };
                    double Cm = 0.0;

                    for (int c = 0; c < vi.Length; c++)
                    {
                        double phase = c * 0.8;
                        double baseCount = 50 + 30 * Math.Sin(2 * Math.PI * (modFreq + 0.02 * c) * _time + phase);
                        double Nvi = Math.Max(0.0, baseCount);
                        Cm += Nvi * Math.Pow(vi[c], 1.5);
                    }
                    Cm *= K;
                    value = Cm;
                }
                else if (i == IDX_FLOW)
                {
                    double Cd = 0.5 + 0.5 * control;
                    double Ac = 1e-4;
                    double deltaP = Math.Max(10.0, _lastDeltaP);

                    double Q = Cd * Ac * Math.Sqrt(2.0 * deltaP / rhoAir);
                    value = Q;
                }
                // ---------------- ACTUATORS ----------------
                else if (i == IDX_NOZZLE)
                {
                    double tauS = 0.1 + 0.9 * control;
                    double rhoS = 1.0;
                    double x = _raw[IDX_FLOW, _sampleIndex];

                    _nozzleY += Dt * ((-rhoS * _nozzleY + x) / tauS);
                    value = _nozzleY;
                }
                else if (i == IDX_SOLENOID)
                {
                    double iMax = 0.2 + 1.8 * control;
                    double current = iMax * (0.5 + 0.5 * Math.Sin(2 * Math.PI * modFreq * _time));
                    double ks = 10.0;
                    double F = ks * current;
                    value = F;
                }
                else if (i == IDX_VIB_FEEDBACK)
                {
                    double m = 1.0;
                    double kVal = 1.0 + 0.5 * Math.Sin(2 * Math.PI * modFreq * _time);
                    double gPlusSigma2 = 0.1 + 1.5 * control;
                    double y = Math.Exp(-Math.Pow(kVal - m, 2.0) / (gPlusSigma2 + 1e-6));
                    value = y;
                }
                else if (i == IDX_DOSE_DISPLAY)
                {
                    double L0 = 50.0 + 150.0 * control;
                    double tau = 1.5;
                    double beta = 1.2;

                    double tDose = _time;

                    double L = L0 * Math.Exp(-Math.Pow(tDose / tau, beta));

                    L += 0.03 * L0 * Math.Sin(2 * Math.PI * 0.3 * _time);
                    L += 0.01 * L0 * (2 * _rnd.NextDouble() - 1);

                    value = L;
                }
                else if (i == IDX_HEATER)
                {
                    double fracTox = 0.1 + 0.9 * control;

                    double tTotal = 100e-9;
                    double t_ox = fracTox * tTotal;
                    double t_nit = (1 - fracTox) * tTotal;

                    double sigmaOx = 1e4;
                    double sigmaNit = 5e3;

                    double sigmaTot = (t_ox * sigmaOx + t_nit * sigmaNit) / (t_ox + t_nit + 1e-18);

                    double heaterValue = sigmaTot / 1000.0;
                    heaterValue *= (1.0 + 0.5 * Math.Sin(2 * Math.PI * modFreq * _time));
                    value = heaterValue;
                }
                else if (i == IDX_LED)
                {
                    double I0 = 1e-12;
                    double q = 1.602e-19;
                    double kB = 1.38064852e-23;
                    double T = 300.0;
                    double n = 2.0;

                    double Vj = 1.5 + 1.0 * control;
                    Vj += 0.05 * Math.Sin(2 * Math.PI * modFreq * _time);

                    double exponent = q * Vj / (n * kB * T);
                    exponent = Math.Min(exponent, 20.0);

                    double I = I0 * (Math.Exp(exponent) - 1.0);
                    double I_mA = I * 1e3;
                    value = I_mA;
                }

                
                _raw[i, _sampleIndex] = value;

                
                double noise = noiseAmp * (2.0 * _rnd.NextDouble() - 1.0);
                double noisy = value + noise;
                _noisy[i, _sampleIndex] = noisy;

               
                double prev = _filteredPrev[i];
                double filtered = prev + alphaFilter * (noisy - prev);
                _filteredPrev[i] = filtered;
                _filtered[i, _sampleIndex] = filtered;
            }
        }

        // ---------------------------------------------------------
        // UPDATE CHART
        // ---------------------------------------------------------
        private void UpdateAllCharts()
        {
            UpdateChartsForTab(TabKind.Raw);
            UpdateChartsForTab(TabKind.Noisy);
            UpdateChartsForTab(TabKind.Filtered);
        }

        private void UpdateChartsForTab(TabKind kind)
        {
            int tab = (int)kind;
            double[,] src;

            if (kind == TabKind.Raw) src = _raw;
            else if (kind == TabKind.Noisy) src = _noisy;
            else src = _filtered;

            for (int i = 0; i < TotalSignals; i++)
            {
                UpdateTimeChart(_timeCharts[tab, i], src, i, tab);
                UpdateFreqChart(_freqCharts[tab, i], src, i);
            }

            UpdatePoleZeroChart(_combinedSCharts[tab], src);
            UpdateZPlaneChart(_combinedZCharts[tab], src);

        }

        private void UpdateTimeChart(Chart chart, double[,] data, int signalIndex, int tab)
        {
            if (chart == null) return;
            chart.Series.Clear();

            var series = new Series
            {
                ChartType = SeriesChartType.Line,
                BorderWidth = 1,
                IsXValueIndexed = false,
                Name = GetSignalDisplayName(signalIndex)
            };

            double tEnd = _time;
            double tStart = tEnd - BufferSize * Dt;

            for (int k = 0; k < BufferSize; k++)
            {
                int idx = (_sampleIndex + k + 1) % BufferSize;
                double t = tStart + k * Dt;
                series.Points.AddXY(t, data[signalIndex, idx]);
            }

            chart.Series.Add(series);

            var area = chart.ChartAreas[0];
            area.AxisX.Minimum = tStart;
            area.AxisX.Maximum = tEnd;

            double ymin, ymax;

            if (tab == (int)TabKind.Raw)
            {
                GetTimeYAxisRange(signalIndex, out ymin, out ymax);
            }
            else
            {
                ymin = double.MaxValue;
                ymax = double.MinValue;

                for (int i = 0; i < BufferSize; i++)
                {
                    double v = data[signalIndex, (_sampleIndex + i) % BufferSize];
                    if (v < ymin) ymin = v;
                    if (v > ymax) ymax = v;
                }

                if (ymax <= ymin)
                    ymax = ymin + 1;

                double margin = 0.1 * (ymax - ymin);
                ymin -= margin;
                ymax += margin;
            }

            if (!_axisInit[tab, signalIndex])
            {
                _axisMin[tab, signalIndex] = ymin;
                _axisMax[tab, signalIndex] = ymax;
                _axisInit[tab, signalIndex] = true;
            }
            else
            {
                _axisMin[tab, signalIndex] = 0.9 * _axisMin[tab, signalIndex] + 0.1 * ymin;
                _axisMax[tab, signalIndex] = 0.9 * _axisMax[tab, signalIndex] + 0.1 * ymax;
            }

            area.AxisY.Minimum = _axisMin[tab, signalIndex];
            area.AxisY.Maximum = _axisMax[tab, signalIndex];
        }

        private void UpdateFreqChart(Chart chart, double[,] data, int signalIndex)
        {
            if (chart == null) return;
            chart.Series.Clear();

            int nfft = BufferSize;
            int half = nfft / 2;
            double[] tempSamples = new double[nfft];
            double[] mag = new double[half];

            for (int k = 0; k < nfft; k++)
            {
                int idx = (_sampleIndex + k + 1) % BufferSize;
                tempSamples[k] = data[signalIndex, idx];
            }

            ComputeFftMagnitude(tempSamples, mag);

            double maxMag = 0.0;
            for (int i = 0; i < half; i++)
                if (mag[i] > maxMag) maxMag = mag[i];
            if (maxMag <= 0.0) maxMag = 1.0;

            var series = new Series
            {
                ChartType = SeriesChartType.Line,
                BorderWidth = 1,
                IsXValueIndexed = false,
                Name = GetSignalDisplayName(signalIndex)
            };

            double fs = 1.0 / Dt;
            for (int k = 0; k < half; k++)
            {
                double freq = k * fs / nfft;
                double yNorm = mag[k] / maxMag;
                series.Points.AddXY(freq, yNorm);
            }

            chart.Series.Add(series);

            var area = chart.ChartAreas[0];
            area.AxisX.Minimum = 0;
            area.AxisX.Maximum = fs / 2.0;
            area.AxisY.Minimum = 0.0;
            area.AxisY.Maximum = 1.05;
        }

        // ---------------------------------------------------------
        // LAPPLACE POLE-ZERO UNTUK COMBINED S & Z
        // ---------------------------------------------------------

        // Definisi pole & zero sederhana per sinyal (bisa Anda sesuaikan modelnya)
        private void GetPoleZeroForSignal(int index, out double pole, out double zero)
        {
            pole = -1.0;
            zero = double.NaN; // default: tidak ada zero

            switch (index)
            {
                // --------- SENSORS ----------
                case IDX_HUMIDITY:
                    pole = -0.5;
                    break;
                case IDX_SPO2:
                    pole = -1.0;
                    break;
                case IDX_PRESSURE:
                    pole = -0.3;
                    break;
                case IDX_IMU:
                    pole = -2.0;
                    break;
                case IDX_PARTICLE:
                    pole = -0.2;
                    break;
                case IDX_FLOW:
                    pole = -1.2;
                    break;

                // --------- ACTUATORS ----------
                case IDX_NOZZLE:
                    // pakai τ dari slider (time constant)
                    double tau = 0.1 + 0.9 * _controlParam[index];
                    pole = -1.0 / tau;
                    break;

                case IDX_SOLENOID:
                    pole = -3.0;
                    zero = -0.5;
                    break;

                case IDX_VIB_FEEDBACK:
                    pole = -1.5;
                    break;

                case IDX_DOSE_DISPLAY:
                    pole = -0.7;
                    break;

                case IDX_HEATER:
                    pole = -1.0;
                    zero = -0.1;
                    break;

                case IDX_LED:
                    pole = -4.0;
                    break;
            }
        }

        // di dalam UpdateCombinedCharts
        private void UpdateCombinedCharts(Chart chartS, Chart chartZ, double[,] data)
        {
            UpdatePoleZeroChart(chartS, data);   // ← PASTI dua argumen
            UpdateZPlaneChart(chartZ, data);    // ← PASTI dua argumen juga
        }

        private void UpdateZPlaneChart(Chart chart, double[,] data)
        {
            if (chart == null) return;

            chart.Series.Clear();

            // Unit circle
            var circle = new Series { ChartType = SeriesChartType.Line, Color = Color.Black, BorderWidth = 2 };
            for (int i = 0; i <= 360; i++)
                circle.Points.AddXY(Math.Cos(i * Math.PI / 180), Math.Sin(i * Math.PI / 180));
            chart.Series.Add(circle);

            var poles = new Series { ChartType = SeriesChartType.Point, MarkerStyle = MarkerStyle.Cross, MarkerSize = 8 };
            var zeros = new Series { ChartType = SeriesChartType.Point, MarkerStyle = MarkerStyle.Circle, MarkerSize = 6 };

            for (int i = 0; i < TotalSignals; i++)
            {
                int idx0 = (_sampleIndex - 1 + BufferSize) % BufferSize;
                int idx1 = _sampleIndex;

                double x0 = data[i, idx0];
                double x1 = data[i, idx1];

                double p_s = ComputeDynamicPole(x0, x1);
                double p_z = Math.Exp(p_s * Dt);     // s → z transform

                // Clamp radius biar tidak meledak
                if (p_z < -2) p_z = -2;
                if (p_z > 2) p_z = 2;

                Color c = SignalColors[i];
                string name = GetSignalDisplayName(i) + (i < SensorCount ? " (Sensor)" : " (Actuator)");

                int pid = poles.Points.AddXY(p_z, 0);
                poles.Points[pid].Color = c;
                poles.Points[pid].ToolTip = name;
            }

            chart.Series.Add(poles);
            chart.Series.Add(zeros);

            // legend
            if (chart.Legends.Count == 0)
                chart.Legends.Add(new Legend("LegendZ") { Docking = Docking.Right });

            var L = chart.Legends["LegendZ"];
            L.CustomItems.Clear();

            for (int i = 0; i < TotalSignals; i++)
                L.CustomItems.Add(new LegendItem
                {
                    Name = GetSignalDisplayName(i) + (i < SensorCount ? " (Sensor)" : " (Actuator)"),
                    Color = SignalColors[i]
                });
        }



        private void UpdatePoleZeroChart(Chart chart, double[,] data)
        {
            if (chart == null) return;

            chart.Series.Clear();

            var poles = new Series { ChartType = SeriesChartType.Point, MarkerStyle = MarkerStyle.Cross, MarkerSize = 8 };
            var zeros = new Series { ChartType = SeriesChartType.Point, MarkerStyle = MarkerStyle.Circle, MarkerSize = 6 };

            for (int i = 0; i < TotalSignals; i++)
            {
                int idx0 = (_sampleIndex - 1 + BufferSize) % BufferSize;
                int idx1 = _sampleIndex;

                double x0 = data[i, idx0];
                double x1 = data[i, idx1];

                double p = ComputeDynamicPole(x0, x1);

                Color c = SignalColors[i];
                string name = GetSignalDisplayName(i) + (i < SensorCount ? " (Sensor)" : " (Actuator)");

                int pid = poles.Points.AddXY(p, 0);
                poles.Points[pid].Color = c;
                poles.Points[pid].ToolTip = name;
            }

            chart.Series.Add(poles);
            chart.Series.Add(zeros);

            // legend
            if (chart.Legends.Count == 0)
                chart.Legends.Add(new Legend("LegendS") { Docking = Docking.Right });

            var L = chart.Legends["LegendS"];
            L.CustomItems.Clear();

            for (int i = 0; i < TotalSignals; i++)
                L.CustomItems.Add(new LegendItem
                {
                    Name = GetSignalDisplayName(i) + (i < SensorCount ? " (Sensor)" : " (Actuator)"),
                    Color = SignalColors[i]
                });
        }






        // ---------------------------------------------------------
        // FFT
        // ---------------------------------------------------------
        private void ComputeFftMagnitude(double[] samples, double[] magOut)
        {
            int n = samples.Length;
            int half = n / 2;
            for (int k = 0; k < half; k++)
            {
                double re = 0.0;
                double im = 0.0;
                for (int t = 0; t < n; t++)
                {
                    double angle = -2.0 * Math.PI * k * t / n;
                    re += samples[t] * Math.Cos(angle);
                    im += samples[t] * Math.Sin(angle);
                }
                magOut[k] = Math.Sqrt(re * re + im * im) / n;
            }
        }
    }
}
