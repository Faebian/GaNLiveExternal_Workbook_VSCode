using UnicornOne.Core.Workbooks; // Shared Core RHEED widget and IRheedSignals (migration boundary)
//using Accord;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using UnicornOne.Core.Controls;
using UnicornOne.Platform.Data.Media;
using UnicornOne.Platform.Data.Mirror;
using UnicornOne.Platform.Data.Workbooks;
using UnicornOne.Platform.Flux;
using UnicornOne.Platform.Imaging;
using UnicornOne.Platform.Imaging.Export;
using UnicornOne.Platform.Logging;
using UnicornOne.Platform.Sample;
using UnicornOne.Platform.Theme;
using UnicornOne.Platform.TTS;
using static UnicornOne.ExternalWorkbooks.GaNLive.SubTaskForm;
using UnicornOne.Platform.Data;

namespace UnicornOne.ExternalWorkbooks.GaNLive
{
    public partial class GaNLiveForm : Form
    {

        //private string rootPath = @"C:\UnicornOne\Data\Workbooks";

        public string RunMode = "Full";
        public string WorkbookName { get; set; } = "GaN Live";
        private OutputRecordSession _activeWorkbookSession;

        private FlowLayoutPanel flow;
        private RheedWidget rheedWidget;
        private RichTextBox messageBox;
        private Panel outputPanel;
        private FlowLayoutPanel menu;

        private GaNLiveManager liveTaskManager;
        private CancellationTokenSource cts;

        public Dictionary<string, ShinyButton> buttons = new Dictionary<string, ShinyButton>();
        private ToolTip toolTip;

        public GaNLiveForm()
        {

            //ProcessSummaryFile();
            //DecimateData();

            InitializeComponent();
            this.BackColor = Styles.isDarkMode ? Styles.BackColorDark : Styles.BackColorLight;
            this.ForeColor = Styles.isDarkMode ? Styles.ForeColorDark : Styles.ForeColorLight;
            this.DoubleClick += WorkBookForm_DoubleClick;

        }

        // Form-level field to remember the last appended line start
        private int _lastLineStart = -1;

        private CancellationTokenSource ManualCTS;

        public void AutoRun()
        {
            cts = new CancellationTokenSource();
            Task.Run(async () => {
                if (RunMode == "Full")
                    await liveTaskManager.Run(cts.Token);
                else
                    await liveTaskManager.SubTask(cts.Token, RunMode);


                // Give 30s to end the recording...
                await Task.Delay(30000); // back off a little
                this.BeginInvoke(new Action(() => this.Close()));

            });
        }

        public void AutoCancel()
        {
            cts?.Cancel();
            ManualCTS?.Cancel();
        }

        private void AppendLine(RichTextBox rtb, string text, Color color, string msg)
        {
            // Move caret to end
            rtb.SelectionStart = rtb.TextLength;
            rtb.SelectionLength = 0;

            // Remember where this line begins
            _lastLineStart = rtb.SelectionStart;


            // Select color
            if (color == Color.White)
                color = Styles.isDarkMode ? Color.White : Color.Black;

            // Color & append
            rtb.SelectionColor = color;
            rtb.AppendText(text);
            if (!text.EndsWith("\r\n")) rtb.AppendText("\r\n");

            // Reset color and scroll
            rtb.SelectionColor = rtb.ForeColor;
            rtb.SelectionStart = rtb.TextLength;
            rtb.ScrollToCaret();

            //if (color != Color.White) Developer.UniTts.Speak(msg.Replace(".", "..."), false);
            UniTts.Speak(msg.Replace(".", "..."), false);
        }

        private void ReplaceLastLine(RichTextBox rtb, string text, Color color, string msg)
        {
            if (_lastLineStart < 0) { AppendLine(rtb, text, color, msg); return; }

            // Select from last line start to end and replace
            rtb.SelectionStart = _lastLineStart;
            rtb.SelectionLength = rtb.TextLength - _lastLineStart;
            rtb.SelectionColor = color;
            rtb.SelectedText = text + (text.EndsWith("\r\n") ? "" : "\r\n");

            // Reset and keep caret at bottom
            rtb.SelectionColor = rtb.ForeColor;
            rtb.SelectionStart = rtb.TextLength;
            //rtb.ScrollToCaret();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ResetWorkbook();
            liveTaskManager.CheckConfiguration();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);

            // cancel any tasks running
            cts?.Cancel();
            ManualCTS?.Cancel();

        }

        private void WorkBookForm_DoubleClick(object sender, EventArgs e)
        {
            ResetWorkbook();
        }

        private void ResetWorkbook()
        {
            InitialiseTaskManager();
            InitialiseUI();
            //DummyPopulatePanel();
        }

        private void InitialiseTaskManager()
        {

            liveTaskManager = new GaNLiveManager();
            liveTaskManager.WorkbookName = WorkbookName;
            liveTaskManager.Initialise();

            liveTaskManager.TaskMessageGenerated += (DateTime ts, string msg, Color col) => {

                this.Invoke(new Action(() =>
                {

                    string line = $"{ts:HH:mm:ss}   {msg.TrimStart('\r')}";

                    bool isUpdate = msg.Length > 0 && msg[0] == '\r';
                    if (isUpdate)
                        ReplaceLastLine(messageBox, line, col, msg);
                    else
                        AppendLine(messageBox, line, col, msg);

                }));
            };

            // Status task complete event
            liveTaskManager.UserTaskComplete += (string name, object obj) => {
                this.Invoke(new Action(() =>
                {
                    EnableAllButtons();
                    ResetButtonText(name);
                }));

            };

            liveTaskManager.TurboComplete += () => {
                this.Invoke(new Action(() => {
                    rheedWidget.Rebuild(liveTaskManager.IntensityData);
                    //rheedWidget.FillChart(liveTaskManager.IntensityData);
                }));
            };

            liveTaskManager.StartStopRecording += async (bool start, string calibrationType) =>
            {
                if (start)
                {
                    string wbName = WorkbookName;
                    var alias = SampleManager.GetCurrentSampleAlias();

                    var startedUtc = DateTime.UtcNow;
                    string stamp = TimeUtil.LocalFileStamp(startedUtc, "yyyyMMdd_HHmmss");
                    string runName = $"{alias}_{stamp}";

                    string outputRoot = Platform.Data.PathManager.Instance.WorkbooksRoot;
                    string outputPath = Path.Combine(outputRoot, wbName, runName);

                    // Set up the session output path etc
                    var session = new OutputRecordSession
                    {

                        Origin = OutputOrigin.Workbook,
                        Caller = wbName,
                        RunName = runName,
                        SampleAlias = alias,
                        OutputPath = outputPath,
                        ManifestPath = Path.Combine(outputPath, runName + "_manifest.json"),
                        StartedUtc = startedUtc,
                        FinishedUtc = null,
                        Status = OutputStatus.Started

                       
           
                    };

                    // Set up the recording settings for this workbook
                    var settings = ViewerExportSettings.CreateDefault(); // ...which just happen to be the gloabl defaults
                    rheedWidget.RheedViewer.LocalExportSettings = settings;

                    _activeWorkbookSession = session;
                    await rheedWidget.RheedViewer.StartTraceAsync(session);

                }
                else
                {
                    await rheedWidget.RheedViewer.StopTraceAsync();

                    var session = _activeWorkbookSession;
                    _activeWorkbookSession = null;

                    if (session == null)
                        return;

                    var calibration = CreateCalibrationData(calibrationType, session);
                    PopulateOutPanel(calibration);
                    SaveIntensityData(session);

             

                }
            };


            liveTaskManager.CalibrationUpdated += (LiveCalibration calibration) => {
                this.Invoke(new Action(() => {
                    PopulateOutPanel(calibration);
                }));
            };


            liveTaskManager.ReinterpretRequested += (bool Show, CancellationTokenSource cts) =>
            {
                this.Invoke(new Action(() => {
                    ManualCTS = cts;
                    if (Show)
                        ShowExtraButtons();
                    else
                        HideExtraButtons();
                }));
            };

            liveTaskManager.RunTaskComplete += (long elapsed) => {
                this.Invoke(new Action(() => {

                    LoggingManager.OnNewSystemMessage($"Run Task Completed in {elapsed} seconds.", Styles.isDarkMode ? Styles.MessageSuccessColorDark : Styles.MessageSuccessColorLight);
                    //MessageBox.Show($"Run Task Completed in {elapsed} seconds.");
                }));
            };

            liveTaskManager.ConfigurationInvalid += (bool valid) =>
            {
                DisableOtherButtons("Config");
                if (!valid)
                {
                    MessageBox.Show("The configuration is invalid.\n\nThis workbook requires:\n - a Sample stage with a subloop called Heater.\n - a N source with a Shutter\n - a Ga source with a shutter and a subloop called Cell.\nPlease modify the configuration and retry.", "Configuration Invalid");
                    buttons["Config"].PerformClick();
                    return;
                }
                EnableAllButtons();



            };

      

        }

        private LiveCalibration CreateCalibrationData(string calibrationType, OutputRecordSession session)
        {
            if (session == null)
                return null;

            string filePath = Path.Combine(
                session.OutputPath,
                session.RunName + "_calibration.json");

            string sampleName = session.SampleAlias;

            var run = WorkbookManager.Instance.GetRun(session.UUID);

            var manifest =
                JsonConvert.DeserializeObject<ManifestBuilder.Manifest>(
                    run.ManifestJson);

            string recName = manifest.Files.Values.First();

            var partial = liveTaskManager.PartialCalibration;
            if (partial == null)
                return null;

            var calibration = new LiveCalibration
            {
                sessionUUID = session.UUID,
                video = recName,
                sample = sampleName,

                element = partial.element,
                source = partial.source,

                t_On = partial.t_On,
                T_sub_set = partial.T_sub_set,
                T_sub_cali = partial.T_sub_cali,

                calibrationType = partial.calibrationType,
                T_III_source = partial.T_III_source,
                phi_incident = partial.phi_incident,
                phi_desorb = partial.phi_desorb,

                growth = partial.growth,
                T_Ga = partial.T_III_source,
                phi_Ga = partial.phi_incident,
                phi_des = partial.phi_desorb,

                label = partial.label,
                notes = partial.notes,
                pressure = partial.pressure
            };

            liveTaskManager.Calibration = calibration;

            try
            {
                string json = JsonConvert.SerializeObject(
                    calibration,
                    Formatting.Indented);

                string partialPath = filePath + ".partial";

                using (DataWriteGate.Instance.EnterWrite())
                {
                    Directory.CreateDirectory(session.OutputPath);

                    File.WriteAllText(partialPath, json, Encoding.UTF8);

                    if (File.Exists(filePath))
                        File.Delete(filePath);

                    File.Move(partialPath, filePath);
                }

                Console.WriteLine($"Calibration file written: {filePath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to write calibration file: {ex.Message}");
            }

            return calibration;
        }

        private void InitialiseUI()
        {

            this.Controls.Clear();
            Margin = new Padding(0, 0, 0, 0);
            int width = this.Width - 500;
            int height = this.Height;
            flow = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(5), Margin = new Padding(0) };
            this.Controls.Add(flow);

            menu = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 100, Padding = new Padding(0), Margin = new Padding(0) };
            this.Controls.Add(menu);

            int flowWidth = flow.Width - flow.Padding.Right - flow.Padding.Left;
            int flowHeight = flow.Height - flow.Padding.Top - flow.Padding.Bottom;

            rheedWidget = new RheedWidget(new Size(flowWidth, flowHeight / 2),  liveTaskManager, live: true, autoScale: true);
            rheedWidget.EnableDragDropSeries = true;
            flow.Controls.Add(rheedWidget);

            outputPanel = new Panel { Size = new Size((int)(flowWidth / 2) - 10, (int)(flowHeight / 2) - 10), };
            flow.Controls.Add(outputPanel);

            int padding = 25;
            messageBox = new RichTextBox
            {
                BackColor = this.BackColor,
                ForeColor = this.ForeColor,
                //Text = $"{DateTime.Now:HH:mm:ss}   Workbook ready\n",
                Margin = new Padding(padding),
                Size = new Size((int)(flowWidth / 2) - (padding * 2) - 10, (int)(flowHeight / 2) - (padding * 2) - 10),
                Font = new Font(Font.FontFamily, 12)
            };
            flow.Controls.Add(messageBox);


            AppendLine(messageBox, $"{DateTime.Now:HH:mm:ss}   Workbook loaded", Color.White, "Workbook loaded");

            string[] buttonsNames = { "Config", "Check", "Run", "Subtask", "Reset" };

            toolTip = new ToolTip
            {
                AutoPopDelay = 5000,
                InitialDelay = 500,
                ReshowDelay = 100,
                ShowAlways = true
            };

            foreach (string buttonName in buttonsNames)
            {
                CreateMenuButton(buttonName);
            }

            //LoadConfiguration();

            //buttons["Run"].Enabled = false;

        }

        private void ShowExtraButtons()
        {
       
            string[] buttonsNames = { "Reinterpret", "Continue"};
            foreach (string buttonName in buttonsNames)
                CreateMenuButton(buttonName);

        }

        private void HideExtraButtons()
        {
            string[] buttonsNames = { "Reinterpret", "Continue" };

            foreach (string buttonName in buttonsNames)
            {
                if (buttons.TryGetValue(buttonName, out var btn))
                {
                    menu.Controls.Remove(btn);
                    btn.Dispose(); // optional: frees resources
                    buttons.Remove(buttonName);
                }
            }
        }

        private void PopulateOutPanel(object calibration)
        {
            this.Invoke(new Action(() =>
            {
                outputPanel.Controls.Clear();

                //int H = 400;
                UOGroupBox interpretGbx = new UOGroupBox
                {
                    Width = outputPanel.Width - 20,
                    Height = 302,
                    Location = new Point(10, 13),
                    Text = "Intensity analysis results",
                    ForeColor = Styles.isDarkMode ? Styles.ForeColorDark : Styles.ForeColorLight,
                    Font = new Font(Font.FontFamily, 12),
                    
                };
                outputPanel.Controls.Add(interpretGbx);

                Panel scrollPanel = new Panel();
                scrollPanel.Dock = DockStyle.Fill;
                scrollPanel.AutoScroll = true;
                interpretGbx.Controls.Add(scrollPanel);

                int Y = 20, step = 25;
            
                //double desorbingNM = desorbingFlux * 3 / 2.19997;

                // Iterate over all properties of the anonymous object
                foreach (var prop in calibration.GetType().GetProperties())
                {
                    string name = prop.Name;
                    object value = prop.GetValue(calibration);

                    if (name == "growth" || name == "T_Ga" || name == "phi_Ga" || name == "phi_des") continue;

                    var lbl = new Label
                    {
                        Text = $"{name}:",
                        AutoSize = true,
                        Margin = new Padding(0, 4, 0, 4),
                        Location = new Point(10, Y),
                        Font = new Font(Font.FontFamily, 12)
                    };
                    scrollPanel.Controls.Add(lbl);

                    if (name == "video")
                        value = value.ToString().Split('\\').Last();

                    var lbl2 = new Label
                    {
                        Text = $"{value}",
                        AutoSize = true,
                        Margin = new Padding(0, 4, 0, 4),
                        Location = new Point(125, Y),
                        Font = new Font(Font.FontFamily, 12)
                    };
                    scrollPanel.Controls.Add(lbl2);


                    Y += step;
            }
            }));
        }

        private void CreateMenuButton(string name)
        {
            ShinyButton shinyButton = new ShinyButton
            {
                Size = new Size(menu.Width - 5, 50),
                ButtonText = name,
                Bright = true
            };
            shinyButton.Click += (s, e) =>
            {

                if (shinyButton.ButtonText == name)
                {
                    LaunchButtonTask(name);
                }
                else
                    CancelButtonTask(name);

            };
            menu.Controls.Add(shinyButton);
            buttons[name] = shinyButton;
            SetMenuButtonTooltip(name);
        }

        private void SetMenuButtonTooltip(string name)
        {

            switch (name)
            {

                case "Config":
                    toolTip.SetToolTip(buttons[name], "View the workbook configuration.");
                    break;

                case "Check":
                    toolTip.SetToolTip(buttons[name], "Perform prechecks that confirm the workbook can run.");
                    break;

                case "Run":
                    toolTip.SetToolTip(buttons[name], "Run the workbook.");
                    break;

                case "Subtask":
                    toolTip.SetToolTip(buttons[name], "Run an individual subtask from the workbook.");
                    break;

                case "Reset":
                    toolTip.SetToolTip(buttons[name], "Reset the workbook.");
                    break;

              

            }
        }

        public void LaunchButtonTask(string text)
        {
            if (text != "Reinterpret" && text != "Continue")
            {
                buttons[text].ButtonText = buttons[text].ButtonText == text ? "Cancel" : text;
                cts = new CancellationTokenSource();
                DisableOtherButtons(text);
            }

            double t0 = 0;
            double t1 = 0;
            double t2 = 0;
            double t3 = 0;

            switch (text)
            {
        
                case "Config":
                    Task.Run(async () => { await liveTaskManager.Config(cts.Token); });
                    break;

                case "Check":
                    Task.Run(async () => { await liveTaskManager.Check(cts.Token); });
                    break;

                case "Run":
                    Task.Run(async () => { await liveTaskManager.Run(cts.Token); });
                    break;

                case "Subtask":
                    t0 = rheedWidget.GetTimeSeries("t0");
                    t1 = rheedWidget.GetTimeSeries("t1");
                    t2 = rheedWidget.GetTimeSeries("t2");
                    t3 = rheedWidget.GetTimeSeries("t3");

                    Task.Run(async () => { await liveTaskManager.SubTask(cts.Token, null, t0, t1, t2, t3); });
                    break;

                case "Reset":
                    ResetWorkbook();
                    break;

                case "Reinterpret":
                    t0 = rheedWidget.GetTimeSeries("t0");
                    t1 = rheedWidget.GetTimeSeries("t1");
                    t2 = rheedWidget.GetTimeSeries("t2");
                    t3 = rheedWidget.GetTimeSeries("t3");
                    Task.Run(() => { liveTaskManager.Reinterpret("Temperature", t0, t1, t2, t3); });
                    break;

                case "Continue":
                    ManualCTS.Cancel();
                    break;

            }
        }

        private void CancelButtonTask(string text)
        {
            cts.Cancel();
            EnableAllButtons();
            buttons[text].ButtonText = buttons[text].ButtonText == text ? "Cancel" : text;

        }

        private void DisableOtherButtons(string buttonName)
        {

            foreach (ShinyButton button in buttons.Values)
                button.Enabled = false;

            buttons[buttonName].Enabled = true;

        }

        private void EnableAllButtons()
        {
            foreach (ShinyButton button in buttons.Values) button.Enabled = true;
        }

        private void ResetButtonText(string text)
        {
            buttons[text].ButtonText = text;
        }

        private void SaveIntensityData(OutputRecordSession session)
        {
            if (session == null)
                return;

            if (liveTaskManager.IntensityData == null ||
                liveTaskManager.IntensityData.Count == 0)
                return;

            string finalPath = Path.Combine(
                session.OutputPath,
                session.RunName + "_intensity.txt");

            string partialPath = finalPath + ".partial";

            List<IntensityPoint> cloneIntensityData =
                liveTaskManager.CloneIntensityData();

            var utf8NoBom = new UTF8Encoding(false);
            var ic = CultureInfo.InvariantCulture;

            using (DataWriteGate.Instance.EnterWrite())
            {
                Directory.CreateDirectory(session.OutputPath);

                using (var sw = new StreamWriter(partialPath, false, utf8NoBom))
                {
                    foreach (var pt in cloneIntensityData)
                    {
                        sw.Write(pt.X.ToString(ic));
                        sw.Write(',');
                        sw.Write(pt.Y.ToString(ic));

                        if (!double.IsNaN(pt.TargetX) || !double.IsNaN(pt.TargetY))
                        {
                            sw.Write(',');
                            sw.Write(pt.TargetX.ToString(ic));
                            sw.Write(',');
                            sw.Write(pt.TargetY.ToString(ic));
                        }

                        sw.WriteLine();
                    }
                }

                if (File.Exists(finalPath))
                    File.Delete(finalPath);

                File.Move(partialPath, finalPath);
            }
        }


    }

}

