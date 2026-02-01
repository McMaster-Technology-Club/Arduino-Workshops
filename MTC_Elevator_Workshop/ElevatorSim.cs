using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO; // Added for Path checking
using System.IO.Ports;
using System.Windows.Forms;

namespace MTC_Elevator_Workshop
{
    // ==========================================
    // THE MCMASTER VIRTUAL ELEVATOR (MVE)
    // Single-File WinForms Implementation
    // ==========================================
    public class ElevatorSim : Form
    {
        // --- BRAND COLORS ---
        private readonly Color COL_PURPLE = ColorTranslator.FromHtml("#843B97");
        private readonly Color COL_ORANGE = ColorTranslator.FromHtml("#FF8C3C");
        private readonly Color COL_CHARCOAL = ColorTranslator.FromHtml("#414141");
        private readonly Color COL_BG = ColorTranslator.FromHtml("#F8F9FA");
        private readonly Color COL_SURFACE = Color.White;
        private readonly Color COL_ERROR = Color.Red; // For Crash state

        // --- COMPONENTS ---
        private SerialPort serialPort;
        private Timer physicsTimer;
        private ComboBox portSelector;
        private Button btnConnect;
        private RichTextBox logBox;
        private PictureBox visualizer;

        // --- SIMULATION STATE ---
        private float carriageY = 10.0f; // Start at Floor 1 (10.0) so we are valid immediately
        private int direction = 0;   // 1 = Up, -1 = Down, 0 = Stop
        private float speed = 0.8f;  // Speed per tick
        private bool isCrashed = false; // Crash state flag

        // Sensor Positions (Percent of shaft height)
        private readonly float[] sensorPos = { 10.0f, 50.0f, 90.0f }; // Floor 1, 2, 3
        private int lastTriggeredSensor = -1;
        private const float SENSOR_TOLERANCE = 5.0f; // Range to trigger sensor

        public ElevatorSim()
        {
            // 1. FORM SETUP
            this.Text = "MTC Virtual Elevator Stream";
            this.Size = new Size(900, 750);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = COL_BG;
            this.Font = new Font("Segoe UI", 10);
            this.DoubleBuffered = true;

            // 2. ASSET LOADING (Icon & Logo)
            // Logic to find assets if "Assets" folder is a sibling to the project folder
            LoadAssets();

            InitializeUI();
            InitializeLogic();
        }

        // Helper to find assets in current or parent directories
        private string FindAssetPath(string filename)
        {
            string[] possiblePaths = {
                $"Assets/{filename}",           // Same folder
                $"../Assets/{filename}",        // Parent folder (Project/Assets sibling)
                $"../../Assets/{filename}",     // Grandparent (bin/Debug/Assets sibling)
                $"../../../Assets/{filename}"   // Great-grandparent
            };

            foreach (var path in possiblePaths)
            {
                if (File.Exists(path)) return path;
            }
            return null;
        }

        private void LoadAssets()
        {
            // Load Favicon (PNG to Icon conversion)
            string iconPath = FindAssetPath("favicon.png");
            if (iconPath != null)
            {
                try
                {
                    using (Bitmap bmp = new Bitmap(iconPath))
                    {
                        // Convert PNG bitmap to ICO handle for the window
                        this.Icon = Icon.FromHandle(bmp.GetHicon());
                    }
                }
                catch { /* Fail silently if image format is bad */ }
            }
        }

        private void InitializeUI()
        {
            // --- LAYOUT ---
            // Left Panel (Controls & Logs)
            Panel leftPanel = new Panel { Dock = DockStyle.Left, Width = 300, Padding = new Padding(20), BackColor = COL_SURFACE };
            // Right Panel (Visuals)
            Panel rightPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(40), BackColor = COL_BG };

            this.Controls.Add(rightPanel);
            this.Controls.Add(leftPanel);

            // --- LEFT PANEL WIDGETS ---

            // 1. MTC Logo
            PictureBox logoBox = new PictureBox
            {
                Left = 20,
                Top = 20,
                Width = 260,
                Height = 80,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Transparent
            };

            string logoPath = FindAssetPath("MTC.png");
            if (logoPath != null)
            {
                try { logoBox.Image = Image.FromFile(logoPath); } catch { }
            }
            leftPanel.Controls.Add(logoBox);

            // 2. Header
            Label title = new Label
            {
                Text = "VIRTUAL STREAM",
                Font = new Font("Segoe UI", 12, FontStyle.Bold),
                ForeColor = COL_PURPLE,
                AutoSize = true,
                Top = 110,
                Left = 20
            };
            leftPanel.Controls.Add(title);

            // 3. Connection Card
            GroupBox connGroup = new GroupBox
            {
                Text = "Connection",
                Top = 140,
                Left = 20,
                Width = 260,
                Height = 120,
                ForeColor = COL_CHARCOAL
            };

            portSelector = new ComboBox
            {
                Top = 30,
                Left = 15,
                Width = 230,
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat
            };
            RefreshPorts();

            btnConnect = new Button
            {
                Text = "CONNECT",
                Top = 70,
                Left = 15,
                Width = 230,
                Height = 35,
                FlatStyle = FlatStyle.Flat,
                BackColor = COL_PURPLE,
                ForeColor = Color.White,
                Cursor = Cursors.Hand
            };
            btnConnect.FlatAppearance.BorderSize = 0;
            btnConnect.Click += ToggleConnection;

            connGroup.Controls.Add(portSelector);
            connGroup.Controls.Add(btnConnect);
            leftPanel.Controls.Add(connGroup);

            // 4. Log Console
            Label logLabel = new Label { Text = "Serial Monitor", Top = 280, Left = 20, AutoSize = true, Font = new Font("Segoe UI", 9, FontStyle.Bold) };
            logBox = new RichTextBox
            {
                Top = 305,
                Left = 20,
                Width = 260,
                Height = 380,
                BorderStyle = BorderStyle.None,
                BackColor = Color.FromKnownColor(KnownColor.ControlLight),
                Font = new Font("Consolas", 9),
                ReadOnly = true
            };
            leftPanel.Controls.Add(logLabel);
            leftPanel.Controls.Add(logBox);

            // --- RIGHT PANEL VISUALIZER ---
            visualizer = new PictureBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent
            };
            visualizer.Paint += RenderFrame;
            rightPanel.Controls.Add(visualizer);
        }

        private void InitializeLogic()
        {
            serialPort = new SerialPort();
            serialPort.BaudRate = 9600;
            serialPort.DataReceived += SerialDataReceived;

            physicsTimer = new Timer { Interval = 16 }; // ~60 FPS
            physicsTimer.Tick += PhysicsLoop;
            physicsTimer.Start();
        }

        private void RefreshPorts()
        {
            portSelector.Items.Clear();
            portSelector.Items.AddRange(SerialPort.GetPortNames());
            if (portSelector.Items.Count > 0) portSelector.SelectedIndex = 0;
        }

        // ==========================================
        // LOGIC & PHYSICS
        // ==========================================

        private void ToggleConnection(object sender, EventArgs e)
        {
            if (serialPort.IsOpen)
            {
                serialPort.Close();
                btnConnect.Text = "CONNECT";
                btnConnect.BackColor = COL_PURPLE;
                Log("Disconnected.");
            }
            else
            {
                if (portSelector.SelectedItem == null) return;
                try
                {
                    serialPort.PortName = portSelector.SelectedItem.ToString();
                    serialPort.Open();
                    btnConnect.Text = "DISCONNECT";
                    btnConnect.BackColor = COL_ORANGE;
                    Log("Connected to " + serialPort.PortName);

                    // Reset sensor state to force an immediate broadcast of current floor
                    lastTriggeredSensor = -1;
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Error: " + ex.Message);
                }
            }
        }

        private void SerialDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                string line = serialPort.ReadLine().Trim().ToUpper();
                this.Invoke(new Action(() => ProcessCommand(line)));
            }
            catch { }
        }

        private void ProcessCommand(string cmd)
        {
            if (isCrashed) return; // Disable control if crashed

            Log("RX: " + cmd);
            switch (cmd)
            {
                case "M_UP":
                    direction = 1;
                    break;
                case "M_DOWN":
                    direction = -1;
                    break;
                case "M_STOP":
                    direction = 0;
                    break;
                case "RESET": // Hidden reset command
                    isCrashed = false;
                    carriageY = 10.0f; // Reset to Floor 1
                    direction = 0;
                    lastTriggeredSensor = -1; // Allow re-trigger
                    Log("System Reset.");
                    break;
            }
        }

        private void CheckCrash()
        {
            // CRASH LOGIC: If moving past limits
            if (direction == 1 && carriageY >= 100)
            {
                Crash("CRITICAL: ROOF COLLISION!");
            }
            else if (direction == -1 && carriageY <= 0)
            {
                Crash("CRITICAL: FLOOR COLLISION!");
            }
        }

        private void Crash(string reason)
        {
            isCrashed = true;
            direction = 0;
            Log(reason);
            MessageBox.Show(reason + "\nResetting System...", "Elevator Crash", MessageBoxButtons.OK, MessageBoxIcon.Error);
            // Auto reset after ack
            isCrashed = false;
            carriageY = 10.0f; // Reset to safe position (Floor 1)
            lastTriggeredSensor = -1;
        }

        private void PhysicsLoop(object sender, EventArgs e)
        {
            if (isCrashed) return;

            // 1. Move Carriage
            if (direction == 1) carriageY += speed;
            if (direction == -1) carriageY -= speed;

            // 2. Check Limits/Crash
            CheckCrash();

            // 3. Clamp (Visual constraint only, crash handles logic)
            carriageY = Math.Max(0, Math.Min(100, carriageY));

            // 4. Sensor Check (Magnet Simulation)
            // We only send data if we are currently touching a sensor 
            // AND we haven't already sent it for this specific pass

            bool touchingAny = false;

            for (int i = 0; i < sensorPos.Length; i++)
            {
                // Check if carriage is overlapping sensor zone
                if (Math.Abs(carriageY - sensorPos[i]) < SENSOR_TOLERANCE)
                {
                    touchingAny = true;
                    if (lastTriggeredSensor != i)
                    {
                        // NEW SENSOR HIT!
                        char sensorChar = (char)('1' + i); // '1', '2', '3'
                        SendSerial(sensorChar.ToString());
                        lastTriggeredSensor = i;
                    }
                }
            }

            if (!touchingAny)
            {
                lastTriggeredSensor = -1; // Reset when leaving zone
            }

            // 5. Redraw
            visualizer.Invalidate();
        }

        private void SendSerial(string data)
        {
            if (serialPort.IsOpen)
            {
                serialPort.Write(data); // Send just the char '1', '2', '3'
                Log("TX: " + data);
            }
        }

        private void Log(string msg)
        {
            logBox.AppendText($"[{DateTime.Now.ToString("HH:mm:ss")}] {msg}\n");
            logBox.ScrollToCaret();
        }

        // ==========================================
        // RENDERING (MATERIAL UI STYLE)
        // ==========================================
        private void RenderFrame(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int w = visualizer.Width;
            int h = visualizer.Height;

            // Margins
            int shaftW = 200;
            int shaftX = (w - shaftW) / 2;
            int padY = 50;
            int shaftH = h - (padY * 2);

            // 1. Draw Shaft (Background)
            using (SolidBrush b = new SolidBrush(Color.FromArgb(230, 230, 230)))
            {
                g.FillRectangle(b, shaftX, padY, shaftW, shaftH);
            }
            // Shaft Border
            using (Pen p = new Pen(Color.Silver, 2))
            {
                g.DrawRectangle(p, shaftX, padY, shaftW, shaftH);
            }

            // 2. Draw Floor Sensors (Limit Switches)
            for (int i = 0; i < 3; i++)
            {
                float normalizedY = 1.0f - (sensorPos[i] / 100.0f); // Invert Y because 0 is top in GDI
                int yPos = padY + (int)(normalizedY * shaftH);

                // Draw Sensor Line
                bool isActive = (i == lastTriggeredSensor);
                Color sensorCol = isActive ? COL_ORANGE : Color.Silver;

                using (Pen p = new Pen(sensorCol, 4))
                {
                    g.DrawLine(p, shaftX - 20, yPos, shaftX + shaftW + 20, yPos);
                }

                // Floor Label
                g.DrawString($"FLOOR {i + 1}", this.Font, Brushes.Gray, shaftX + shaftW + 30, yPos - 10);
            }

            // 3. Draw Carriage
            float normCarriageY = 1.0f - (carriageY / 100.0f);
            int carH = 80;
            int carW = 120;
            int carX = shaftX + (shaftW - carW) / 2;
            int carY = padY + (int)(normCarriageY * shaftH) - (carH / 2);

            // Determine Carriage Color (Purple/Orange Gradient OR Red if crashed)
            Brush carriageBrush;
            if (isCrashed)
            {
                carriageBrush = new SolidBrush(COL_ERROR);
            }
            else
            {
                carriageBrush = new LinearGradientBrush(
                    new Point(carX, carY),
                    new Point(carX + carW, carY + carH),
                    COL_PURPLE, COL_ORANGE);
            }

            using (carriageBrush)
            {
                g.FillRectangle(carriageBrush, carX, carY, carW, carH);
            }

            // 4. Draw Magnet (Visual Indicator)
            int magSize = 15;
            using (SolidBrush b = new SolidBrush(Color.White))
            {
                g.FillEllipse(b, carX + (carW - magSize) / 2, carY + (carH - magSize) / 2, magSize, magSize);
            }

            // 5. Draw Cables
            using (Pen p = new Pen(COL_CHARCOAL, 3))
            {
                g.DrawLine(p, carX + carW / 2, padY, carX + carW / 2, carY);
            }
        }

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new ElevatorSim());
        }
    }
}