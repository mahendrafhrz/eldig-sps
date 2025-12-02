using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace MedEvacFsmGui
{
    static class Program
    {
        enum FsmState { Normal, Warning, Critical, Acked }

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new FsmForm());
        }

        public class FsmForm : Form
        {
            // UI
            Button btnStart, btnStop, btnReset;
            TextBox stateBox;
            TextBox outputBox;
            Panel wavePanel;

            // Waveform database
            Dictionary<string, List<int>> wave = new Dictionary<string, List<int>>();

            // Simulation
            FsmState currentState = FsmState.Normal;
            int timeIndex = 0;
            Timer tickTimer = new Timer();
            Random rnd = new Random();

            public FsmForm()
            {
                Width = 1200;
                Height = 800;
                Text = "MedEvac FSM Simulation";

                // INIT signal lists
                foreach (var sig in new[] { "W", "C", "ACK", "HP", "HV", "OM", "FS", "AT", "AL", "STATE" })
                    wave[sig] = new List<int>();

                // UI
                btnStart = new Button() { Text = "START", Left = 20, Top = 20, Width = 100 };
                btnStart.Click += StartSim;
                Controls.Add(btnStart);

                btnStop = new Button() { Text = "STOP", Left = 140, Top = 20, Width = 100 };
                btnStop.Click += StopSim;
                Controls.Add(btnStop);

                btnReset = new Button() { Text = "RESET", Left = 260, Top = 20, Width = 100 };
                btnReset.Click += ResetSim;
                Controls.Add(btnReset);

                stateBox = new TextBox()
                {
                    Left = 20,
                    Top = 60,
                    Width = 300,
                    ReadOnly = true
                };
                Controls.Add(stateBox);

                outputBox = new TextBox()
                {
                    Left = 20,
                    Top = 100,
                    Width = 300,
                    Height = 600,
                    Multiline = true,
                    ScrollBars = ScrollBars.Vertical,
                    ReadOnly = true,
                    Font = new Font("Consolas", 10)
                };
                Controls.Add(outputBox);

                wavePanel = new Panel()
                {
                    Left = 350,
                    Top = 20,
                    Width = 800,
                    Height = 700,
                    AutoScroll = true
                };
                wavePanel.Paint += DrawWaveform;
                Controls.Add(wavePanel);

                // Timer for automatic simulation
                tickTimer.Interval = 200; // update setiap 200 ms
                tickTimer.Tick += AutoStep;

                UpdateStateBox();
            }

            // START auto simulation
            void StartSim(object s, EventArgs e)
            {
                tickTimer.Start();
                outputBox.AppendText("=== AUTO SIMULATION START ===\r\n");
            }

            // STOP auto simulation
            void StopSim(object s, EventArgs e)
            {
                tickTimer.Stop();
                outputBox.AppendText("=== STOP ===\r\n");
            }

            // RESET everything
            void ResetSim(object s, EventArgs e)
            {
                tickTimer.Stop();
                timeIndex = 0;
                currentState = FsmState.Normal;
                foreach (var k in wave.Keys)
                    wave[k].Clear();
                outputBox.Clear();
                UpdateStateBox();
                wavePanel.Invalidate();
            }

            // This function is called automatically every timer tick
            void AutoStep(object s, EventArgs e)
            {
                timeIndex++;

                // RANDOM sensors
                bool ST = rnd.Next(2) == 1;
                bool HS = rnd.Next(2) == 1;
                bool OC = rnd.Next(2) == 1;
                bool CS = rnd.Next(2) == 1;
                bool IM = rnd.Next(2) == 1;
                bool WS = rnd.Next(2) == 1;
                bool A = rnd.Next(2) == 1;

                // FLAGS
                bool W = ST || HS;
                bool C = OC || CS || IM || WS;

                // FSM
                bool S1p = C;
                bool S0p = (W && !C) || (C && A);

                if (!S1p && !S0p) currentState = FsmState.Normal;
                else if (!S1p && S0p) currentState = FsmState.Warning;
                else if (S1p && !S0p) currentState = FsmState.Critical;
                else currentState = FsmState.Acked;

                // OUTPUTS
                bool HP = !C && ST;
                bool HV = !C && HS;
                bool OM = C;
                bool FS = C;
                bool AT = C;
                bool AL = C && !A;

                // Log line
                outputBox.AppendText(
                    $"{timeIndex * 20} ns : " +
                    $"W={b(W)} C={b(C)} ACK={b(A)} STATE={currentState} " +
                    $"| HP={b(HP)} HV={b(HV)} OM={b(OM)} FS={b(FS)} AT={b(AT)} AL={b(AL)}\r\n"
                );

                // SAVE WAVEFORM
                wave["W"].Add(b(W));
                wave["C"].Add(b(C));
                wave["ACK"].Add(b(A));
                wave["HP"].Add(b(HP));
                wave["HV"].Add(b(HV));
                wave["OM"].Add(b(OM));
                wave["FS"].Add(b(FS));
                wave["AT"].Add(b(AT));
                wave["AL"].Add(b(AL));
                wave["STATE"].Add((int)currentState);

                wavePanel.Invalidate();
                UpdateStateBox();
            }

            int b(bool v) => v ? 1 : 0;

            void UpdateStateBox()
            {
                stateBox.Text = $"Current State = {currentState}";
            }

            // DRAW WAVEFORM
            private void DrawWaveform(object sender, PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.Clear(Color.White);

                int xStep = 40;
                int rowHeight = 40;
                int y = 10;

                foreach (var sig in wave.Keys)
                {
                    g.DrawString(sig, new Font("Consolas", 10), Brushes.Black, 10, y + 10);

                    List<int> samples = wave[sig];
                    if (samples.Count > 1)
                    {
                        for (int i = 1; i < samples.Count; i++)
                        {
                            int prev = samples[i - 1];
                            int curr = samples[i];

                            int x1 = 120 + (i - 1) * xStep;
                            int x2 = 120 + i * xStep;

                            int mid = y + rowHeight / 2;
                            int highY = mid - 10;
                            int lowY = mid + 10;

                            // Horizontal line
                            g.DrawLine(Pens.Blue,
                                x1, prev == 1 ? highY : lowY,
                                x2, prev == 1 ? highY : lowY);

                            // Vertical transition
                            if (prev != curr)
                            {
                                g.DrawLine(Pens.Red,
                                    x2, prev == 1 ? highY : lowY,
                                    x2, curr == 1 ? highY : lowY);
                            }
                        }
                    }

                    y += rowHeight;
                }

                wavePanel.AutoScrollMinSize = new Size(timeIndex * xStep + 300, wave.Count * rowHeight + 50);
            }
        }
    }
}
