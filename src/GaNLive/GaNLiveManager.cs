using UnicornOne.Core.Workbooks; // Shared Core RHEED widget and IRheedSignals (migration boundary)
//using Accord.Math;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using UnicornOne.Core.Controls;
using UnicornOne.Core.Tools.RHEED;
using UnicornOne.Platform.API;
using UnicornOne.Platform.Config;
using UnicornOne.Platform.Data;
using UnicornOne.Platform.Data.Mirror;
using UnicornOne.Platform.Flux;
using UnicornOne.Platform.Imaging;
using UnicornOne.Platform.Recipes;
using UnicornOne.Platform.Theme;
using static UnicornOne.ExternalWorkbooks.GaNLive.SubTaskForm;



/// Summary 
/// This is a III-N temperature and growth type calibration workbook primarily for In and Ga 
/// End of summary
namespace UnicornOne.ExternalWorkbooks.GaNLive
{
    internal class GaNLiveManager : IRheedSignals
    {
        public string WorkbookName { get; set; } = "GaN Calibrate Live";
        private double _sampleTCStartingPoint = double.NaN;
        private double _sampleDelta = double.NaN;
        private double _sampleTCForTarget = double.NaN;

        public double SampleDelta => _sampleDelta;

        public string GrowthType = "Unknown";
        public LiveCalibration Calibration;
        public LiveCalibration PartialCalibration;

        // state engine flags
        private bool Flag_Configuration_Valid = false;
        private bool Flag_DebugWorkbook = false;
        private bool Flag_StartingPointKnown = false;
        private bool Flag_SampleDeltaKnown = false;
        private bool Flag_SampleTCForTargetKnown = false;
        private bool Flag_PlasmaIgnited = false;
        private bool Flag_GrowthParametersKnown = false;


        // Compulsory actions
        public event Action<bool> ConfigurationInvalid;
        public event Action ClearIntensity;
        public event Action<double, string> AddSeriesToIntenstiyChart;
        public event Action<bool> Play;
        public event Action<string, object> UserTaskComplete;
        public event Action<DateTime, string, Color> TaskMessageGenerated;
        public event Action TurboComplete;
        public event Action<bool, string> StartStopRecording;
        public event Action<LiveCalibration> CalibrationUpdated;
        public event Action<bool, CancellationTokenSource> ReinterpretRequested;
        public event Action<long> RunTaskComplete;

        // Unique actions
        // ...none yet
        private void AllActions()
        {
            Play?.Invoke(false);

        }


        private readonly object _intensityLock = new object();
        public object IntensityLock => _intensityLock;

        private List<IntensityPoint> intensityData = new List<IntensityPoint>();
        public List<IntensityPoint> IntensityData
        {
            get => intensityData;
            set => intensityData = value;
        }

        public List<IntensityPoint> CloneIntensityData()
        {
            List<IntensityPoint> data = new List<IntensityPoint>();
            lock (_intensityLock)
                data.AddRange(intensityData);
            return data;
        }

        private LiveConfigClass config;
        public LiveConfigClass Configuration => config;

        private CommandManager commandManager = new CommandManager();

        protected virtual void OnTaskMessageGenerated(string msg, Color color)
        {
            TaskMessageGenerated?.Invoke(DateTime.Now, msg, color);
        }

        public void Initialise()
        {
            config = new LiveConfigClass();
            config.Initialise(WorkbookName);



        }

        public void CheckConfiguration()
        {
            bool Sample_Stage_Ok = SystemState.Instance.systemsConfig.System.Children.ContainsKey(config.SampleStage);
            bool Sample_Heater_Exists = Sample_Stage_Ok ? SystemState.Instance.systemsConfig.System.Children[config.SampleStage].Children.ContainsKey("Heater") : false;
            bool N_Source_Ok = SystemState.Instance.systemsConfig.System.Children.ContainsKey(config.NSource);
            bool N_Shutter_Exists = N_Source_Ok ? SystemState.Instance.systemsConfig.System.Children[config.NSource].Children.ContainsKey("Shutter") : false;
            bool Ga_Source_Ok = SystemState.Instance.systemsConfig.System.Children.ContainsKey(config.IIISource);
            bool Ga_Shutter_Exists = Ga_Source_Ok ? SystemState.Instance.systemsConfig.System.Children[config.IIISource].Children.ContainsKey("Shutter") : false;
            bool Ga_Cell_Exists = Ga_Source_Ok ? SystemState.Instance.systemsConfig.System.Children[config.IIISource].Children.ContainsKey("Cell") : false;
            Flag_Configuration_Valid = Sample_Stage_Ok & Sample_Heater_Exists & N_Source_Ok & N_Shutter_Exists & Ga_Source_Ok & Ga_Shutter_Exists & Ga_Cell_Exists;
            ConfigurationInvalid?.Invoke(Flag_Configuration_Valid);
        }

        private void ApplyDebugSettings()
        {

            Flag_DebugWorkbook = true;
            Flag_StartingPointKnown = true;
            Flag_SampleDeltaKnown = true;
            Flag_SampleTCForTargetKnown = true;
            Flag_PlasmaIgnited = false;
            Flag_GrowthParametersKnown = false;

            _sampleTCStartingPoint = 725.0;
            _sampleDelta = -50;
            _sampleTCForTarget = 750.0;

        }

        public async Task Config(CancellationToken token)
        {
            await SafeDelay(100, token);

            // Show the config form dialog 
            LiveConfigForm configForm = new LiveConfigForm(config);
            configForm.ShowDialog();

            //OnTaskMessageGenerated("Config task complete.", Color.LimeGreen);
            UserTaskComplete.Invoke("Config", Double.NaN);
            CheckConfiguration();
        }

        private static async Task SafeDelay(int ms, CancellationToken token)
        {
            try { await Task.Delay(ms, token); } catch (OperationCanceledException) { }
        }

        public async Task Check(CancellationToken token)
        {

            //bool passed = true;
            await SafeDelay(1000, token);

            // 1) Sample: Is there a sample in the Growth Chamber? Is it a GaN template? Does it have a temperature calibration?
            // 2) Fluxes: Is there Ga incident flux data? Is there Ga desorbing flux data for the sample?
            // 3) AI models: Can we talk to Yolo and Lovo ? / health end points.
            // 4) Initial conditions: What is the current state of the sub T, Ga T and N plasma source compared to the specified starting defaults? -> do we need to ramp Ts and ignite plasma now?
            // 5) RHEED: is the RHEED pipeline working? Can we see an image ?
            // 6) Environment: is this PC capable of running this workbook (GPU, RAM, CPU) etc (Skip for now)

            // Check Environment: CPU, GPU, RAM, etc
            // (Skip for now)...

            // Close all shutters
            OnTaskMessageGenerated("Closing all shutters...", Color.White);
            bool result = await CloseAllShuttersWithConfirmation();
            if (result)
                OnTaskMessageGenerated("All shutters are closed", Color.LimeGreen);
            else
                OnTaskMessageGenerated("Could not close all shutters", Color.OrangeRed);

            // Sample 
            //OnTaskMessageGenerated("Checking sample...", Color.White);
            //var sample = SubstrateServices.currentSample;
            //if (sample != null && sample.Material == "GaN Template")
            //{
            //    OnTaskMessageGenerated("Sample is GaN template", Color.LimeGreen);
            //    if (CheckSampleCalibration())
            //        OnTaskMessageGenerated($"Sample delta is {_sampleDelta:F1} °C", Color.Yellow);
            //    else
            //        OnTaskMessageGenerated("Sample delta is unknown.", Color.Yellow);
            //}     
            //else
            //{
            //    OnTaskMessageGenerated("Sample check failed.", Color.OrangeRed);
            //    passed = false;
            //}

            // Fluxes
            //OnTaskMessageGenerated("Checking flux data...", Color.White);

            // AI models
            //OnTaskMessageGenerated("Checking AI models...", Color.White);

            // RHEED
            //OnTaskMessageGenerated("Checking RHEED...", Color.White);

            OnTaskMessageGenerated("Check task complete.", Color.LimeGreen);
            UserTaskComplete.Invoke("Check", Double.NaN);
        }

        public async Task Run(CancellationToken token)
        {
            Stopwatch sw = new Stopwatch();
            sw.Restart();

            try
            {
                await SafeDelay(1000, token);

                // Steps (ordered with a state - engine so we can resume where we left off)
                // 1) Rough substrate offset search (ramp to 500°C on TC, dump some Ga on the substrate, ramp up by 50°C steps waiting 5 mins each time until Ga desorbs = good starting point for step 2)
                // 2) Temperature calibration: using step #1 as a starting TC temperature, use the t1,t2,t3, t4 intensity envelope -> desorbing flux -> real substrate surface temperature Lovo pipeline to determine the substrate temperatures vs TC temperatures. Q: is the offset fixed (i.e. -51°C) or does it vary?
                // 3) Find the specified target substrate temperature (e.g. 700°C = workbook default)
                // 4) Overlay the expected growth map now we know the substrate temperature onto the output chart based on the specified N flux
                // 5) Use a similar but modified lovo pipeline to step #2 to determine the growth type matches the growth map predictions (i.e. now we have the N flux, Ga flux and real sub T) 6) Grow the specified buffer layer (e.g. 500 nm)

                if (Flag_DebugWorkbook) ApplyDebugSettings();


                await SteadyState(token);

                // What is the state engine state?
                if (!Flag_StartingPointKnown)
                    await FindStartingPoint(token);

                if (!Flag_SampleDeltaKnown)
                    await FindSampleDelta(token);

                if (!Flag_SampleTCForTargetKnown)
                    await FindSampleTCForTarget(token);

                // Optionally: display the growth map now.

                if (!Flag_PlasmaIgnited)
                    await IgnitePlasma(token);

                if (!Flag_GrowthParametersKnown)
                    await FindGrowthParameters(token);
            }
            catch (OperationCanceledException)
            {
                OnTaskMessageGenerated("Task canceled by user.", Color.Yellow);
            }
            catch (Exception ex)
            {
                OnTaskMessageGenerated($"Unexpected error: {ex.Message}", Color.Red);
            }


            long elapsedSeconds = sw.ElapsedMilliseconds / 1000;
            RunTaskComplete?.Invoke(elapsedSeconds);

            if (!token.IsCancellationRequested)
                OnTaskMessageGenerated("Run task complete.", Color.LimeGreen);
            else
                OnTaskMessageGenerated("Run task cancelled.", Color.PaleVioletRed);

            UserTaskComplete.Invoke("Run", Double.NaN);

        }

        public async Task SubTask(CancellationToken token, string task = null, params double[] times)
        {
            try
            {
                await SafeDelay(100, token);

                // Launch a subtask selected from a form
                if (task == null)
                {
                    SubTaskForm subTaskForm = new SubTaskForm();
                    subTaskForm.BringToFront();
                    subTaskForm.ShowDialog();
                    task = subTaskForm.SelectedTask;
                }
                string meta = "Temperature";

                switch (task)
                {

                    case "Find Starting Point":
                        await FindStartingPoint(token);
                        break;

                    case "Find Delta":
                        await FindSampleDelta(token);
                        break;

                    case "Tune to Taget":
                        await FindSampleTCForTarget(token);
                        break;

                    case "Ignite Plasma":
                        await IgnitePlasma(token);
                        break;

                    case "Tune Growth Type":
                        await FindGrowthParameters(token);
                        break;

                    case "Fix":
                        Fix(true);
                        break;

                    case "Reinterpret":
                        //if (times.Length == 4 &&
                        //    times[0] != double.NaN && times[1] != double.NaN && times[2] != double.NaN && times[3] != double.NaN &&
                        //    times[0] != 0 && times[1] != 0 && times[2] != 0 && times[3] != 0)
                        if (times.Length == 4 &&
                            !times.Any(double.IsNaN) &&
                            times.All(x => x != 0))
                        {
                            if (Calibration != null && !string.IsNullOrEmpty(Calibration.growth)) meta = Calibration.growth;
                            Reinterpret(meta, times[0], times[1], times[2], times[3]);
                        }
                        break;

                    case "Calibrations":
                        CalibrationForm calibrationForm = new CalibrationForm();

                        calibrationForm._sampleTCStartingPoint = _sampleTCStartingPoint;
                        calibrationForm._sampleDelta = _sampleDelta;
                        calibrationForm._sampleTCForTarget = _sampleTCForTarget;

                        calibrationForm.Flag_StartingPointKnown = Flag_StartingPointKnown;
                        calibrationForm.Flag_SampleDeltaKnown = Flag_SampleDeltaKnown;
                        calibrationForm.Flag_SampleTCForTargetKnown = Flag_SampleTCForTargetKnown;
                        calibrationForm.Flag_PlasmaIgnited = Flag_PlasmaIgnited;
                        calibrationForm.Flag_GrowthParametersKnown = Flag_GrowthParametersKnown;

                        calibrationForm.ShowDialog();

                        _sampleTCStartingPoint = calibrationForm._sampleTCStartingPoint;
                        _sampleDelta = calibrationForm._sampleDelta;
                        _sampleTCForTarget = calibrationForm._sampleTCForTarget;

                        Flag_StartingPointKnown = calibrationForm.Flag_StartingPointKnown;
                        Flag_SampleDeltaKnown = calibrationForm.Flag_SampleDeltaKnown;
                        Flag_SampleTCForTargetKnown = calibrationForm.Flag_SampleTCForTargetKnown;
                        Flag_PlasmaIgnited = calibrationForm.Flag_PlasmaIgnited;
                        Flag_GrowthParametersKnown = calibrationForm.Flag_GrowthParametersKnown;


                        break;

                    case "Retry":
                        (double desorbingFlux, double subT) = await DetermineSubstrateTemperature(config.TimeOnCalibrate, config.CalibrationGaFlux); // These are instantaneous and have a token 100ms delay

                        if (!double.IsNaN(subT))
                        {
                            (double T, double subTC) = DetermineDelta(subT);
                            _sampleDelta = T;
                            Flag_SampleDeltaKnown = true;
                            CreatePartialCalibration("Temperature", $"{subTC:F1}");
                        }
                        break;

                    case "Test Recording":
                        await TestRecording(token);
                        break;

                    case "Cancel":
                        break;

                }
            }
            catch (OperationCanceledException)
            {
                OnTaskMessageGenerated("Task canceled by user.", Color.Yellow);
            }
            catch (Exception ex)
            {
                OnTaskMessageGenerated($"Unexpected error: {ex.Message}", Color.Red);
            }


            if (token.IsCancellationRequested)
                OnTaskMessageGenerated("Subtask cancelled.", Color.PaleVioletRed);
            else
                OnTaskMessageGenerated("Subtask complete.", Color.LimeGreen);
            UserTaskComplete.Invoke("Subtask", Double.NaN);

        }

        private bool Fix(bool classic)
        {

            bool corrected = UnicornOne.Core.Tools.RHEED.EnvelopeFitter.Fix(intensityData, _intensityLock, classic);
            if (corrected) TurboComplete?.Invoke();
            return corrected;

            //try
            //{
            //    bool corrected = false;
            //    lock (_intensityLock)
            //    {
            //        (var correctedIntensityData, bool correctionApplied) = AnalysisTools.IntensityNormalize.CorrectForTargetDrift(intensityData, classic, 0.5);
            //        intensityData.Clear();
            //        intensityData.AddRange(correctedIntensityData);
            //        corrected = correctionApplied;
            //    }


            //    //intensityData.Clear();
            //    //TurboComplete?.Invoke();
            //    await Task.Delay(100);
            //    return corrected;
            //}
            //catch (OperationCanceledException)
            //{
            //}
            //return false;
        }

        public async Task Smooth(CancellationToken token)
        {
            try
            {
                lock (_intensityLock)
                {
                    var smoothed = AnalysisTools.Smooth.SmoothEmaZeroPhaseTime(intensityData, 1);

                    //var smoothed = AnalysisTools.SmartSmoother.SmoothOnlyFlat(intensityData, 10, 20, 0.15);
                    //smoothed = AnalysisTools.SmartSmoother.SmoothOnlySlopes(smoothed,slopeThresh: 0.25 , ignoreSec: 60);

                    intensityData.Clear();
                    intensityData.AddRange(smoothed);
                }

                //UserTaskComplete.Invoke("Smooth", Double.NaN);
                await Task.Delay(100);
                TurboComplete?.Invoke();
            }
            catch (OperationCanceledException)
            {
                //UserTaskComplete.Invoke("Smooth", Double.NaN);
            }
        }

        public void Reinterpret(string meta, double t0, double t1, double t2, double t3)
        {
            try
            {
                OnTaskMessageGenerated("Reinterpret task starting...", Color.White);


                if (meta == "Temperature")
                {
                    double gaFlux = config.CalibrationGaFlux;
                    (double desorbingFlux, double subT) = EnvelopeFitter.DetermineSubT(t1, t2, t3, gaFlux, config.IIIElement);
                    (double delta, double subTC) = DetermineDelta(subT);

                    _sampleDelta = delta;
                    OnTaskMessageGenerated($"Substrate temperature = {subT:F0}", Color.Yellow);


                    if (Calibration != null)
                    {
                        Calibration.T_sub_cali = $"{subT:F1}";
                        Calibration.phi_des = $"{desorbingFlux:F3}";
                        Calibration.notes = $"Delta = {_sampleDelta:F1} °C";
                        CalibrationUpdated?.Invoke(Calibration);
                    }



                }
                else
                {
                    string growthType = EnvelopeFitter.ReturnGrowthTypeString(intensityData, t0, t1, t2, t3);
                    OnTaskMessageGenerated($"Growth type = {growthType}", Color.Yellow);

                    if (Calibration != null)
                    {
                        Calibration.label = growthType;
                        CalibrationUpdated?.Invoke(Calibration);
                    }

                }

                OnTaskMessageGenerated("Reinterpret task complete.", Color.LimeGreen);
                //UserTaskComplete.Invoke("Reinterpret", Double.NaN);

            }
            catch (OperationCanceledException)
            {
                OnTaskMessageGenerated("Reinterpret task canceled.", Color.Yellow);
                //UserTaskComplete.Invoke("Reinterpret", Double.NaN);
            }


        }

        private async Task TestRecording(CancellationToken token)
        {
            // Offer continue, pivot or exit
            ContinueTaskForm continueTaskForm = new ContinueTaskForm();
            continueTaskForm.ShowDialog();
            string selectedTask = continueTaskForm.SelectedTask;


            if (selectedTask == "Reinterpret")
            {
                CancellationTokenSource cts = new CancellationTokenSource();
                var token2 = cts.Token;
                ReinterpretRequested?.Invoke(true, cts);
                OnTaskMessageGenerated("Waiting for user...", Color.White);
                await Task.Delay(1000, token);
                await WaitForCacellation(60000, token2, "\rWaiting for user ");
                ReinterpretRequested?.Invoke(false, cts);
                await TestRecording(token);
            }
            else if (selectedTask == "Continue")
            {

                OnTaskMessageGenerated("Starting recording...", Color.White);
                StartStopRecording?.Invoke(true, "Temperature");
                await WaitForTime(token, 5000, "\rRecording ");
                StartStopRecording?.Invoke(false, "Temperature");
                OnTaskMessageGenerated("Recording complete.", Color.LimeGreen);
            }

        }

        private async Task OpenShutterWithConfirmation(string name)
        {
            string system = SystemState.Instance.systemsConfig.System.Name;

            // Try open once
            await commandManager.ExecuteCommand($"Open({system}.{name})");

            _ = Task.Run(async () =>
            {
                int retries = 0;
                while (retries < 3)
                {
                    await Task.Delay(2500);

                    var current = DataManager.Instance.Cache
                        .GetCurrentValue($"{system}.{name}.MV")?.Value?.ToString();

                    if (current == "1") break; // ✅ success, stop checking

                    retries++;
                    await commandManager.ExecuteCommand($"Open({system}.{name})"); // only retry on failure
                }
            });
        }

        private async Task CloseShutterWithConfirmation(string name)
        {
            string system = SystemState.Instance.systemsConfig.System.Name;

            // Try close once
            await commandManager.ExecuteCommand($"Close({system}.{name})");

            _ = Task.Run(async () =>
            {
                int retries = 0;
                while (retries < 3)
                {
                    await Task.Delay(2500);

                    var current = DataManager.Instance.Cache
                        .GetCurrentValue($"{system}.{name}.MV")?.Value?.ToString();

                    if (current == "0") break; // ✅ success, stop checking

                    retries++;
                    await commandManager.ExecuteCommand($"Close({system}.{name})"); // only retry on failure
                }
            });

        }

        private async Task<bool> CloseAllShuttersWithConfirmation_old()
        {

            var current = DataManager.Instance.Cache.SnapshotCurrentValues();

            bool success = true;
            int i = 0;
            do
            {
                // item here is the full USN path <sytstem>.<parent node>.<child node>.MV e.g. M1.Ga1.Shutter.MV
                // we want to only close shutters whose parent node has an element!

                foreach (var item in current)
                {
                    if (item.Key.Contains(".Shutter.") && item.Value.Value.ToString() != "0")
                        await commandManager.ExecuteCommand($"Close({item.Key.Replace(".MV", "")})");
                }

                await Task.Delay(2500);
                success = true;
                current = DataManager.Instance.Cache.SnapshotCurrentValues();
                foreach (var item in current)
                {
                    if (item.Key.Contains(".Shutter.") && item.Value.Value.ToString() != "0")
                        success = false;
                }

                i++;
                if (i == 3)
                    break;


            } while (!success);

            return success;

        }

        private async Task<bool> CloseAllShuttersWithConfirmation()
        {
            var sys = SystemState.Instance;

            bool IsElementParentShutterKey(string key, out string shutterPathWithoutSuffix)
            {
                shutterPathWithoutSuffix = null;

                // Expect: M1.Ga1.Shutter.MV
                if (!key.EndsWith(".Shutter.MV")) return false;

                var parts = key.Split('.');
                if (parts.Length < 4) return false;

                string systemName = parts[0];
                string parentName = parts[1];

                // Parent must exist and have an Element (Ga, Al, N, etc.)
                if (!sys.TryGetNodeByPath($"{systemName}.{parentName}", out var parentNode))
                    return false;
                if (string.IsNullOrWhiteSpace(parentNode.Element)) return false;

                shutterPathWithoutSuffix = key.Replace(".MV", ""); // -> M1.Ga1.Shutter
                return true;
            }

            var current = DataManager.Instance.Cache.SnapshotCurrentValues();

            bool success;
            int i = 0;

            do
            {
                // Issue close commands for element-source shutters that are not already closed
                foreach (var item in current)
                {
                    if (!IsElementParentShutterKey(item.Key, out var shutterPath)) continue;
                    if (item.Value?.Value?.ToString() == "0") continue;

                    await commandManager.ExecuteCommand($"Close({shutterPath})");
                }

                await Task.Delay(2500);

                // Recheck only the same class of shutters
                success = true;
                current = DataManager.Instance.Cache.SnapshotCurrentValues();

                foreach (var item in current)
                {
                    if (!IsElementParentShutterKey(item.Key, out _)) continue;
                    if (item.Value?.Value?.ToString() != "0")
                    {
                        success = false;
                        break;
                    }
                }

                i++;
                if (i == 3) break;

            } while (!success);

            return success;
        }

        private bool CheckSampleCalibration()
        {
            //Newtonsoft.Json.Linq.JObject Calibrations = SubstrateServices.currentSample.Calibrations;

            //if (Calibrations == null)
            //{
            //    OnTaskMessageGenerated("No infomartion found", Color.Yellow);

            //    SubstrateServices.UpdateSampleCalibraiton(false);
            //    return false;
            //}
            //else
            //{
            //    if (Calibrations != null
            //        && Calibrations.TryGetValue("DeltaCalibrated", out var token3)
            //        && token3.Type == JTokenType.Boolean
            //        && token3.Value<bool>())
            //    {
            //        if (Calibrations.TryGetValue("Delta", out var token2)
            //            && token2.Type == JTokenType.Float
            //            && double.TryParse(token2.Value<float>().ToString(), out double delta))
            //        {
            //            _sampleDelta = delta;
            //            Flag_SampleDeltaKnown = true;
            //            OnTaskMessageGenerated($"Delta is {delta:F1} °C.", Color.White);
            //            OnTaskMessageGenerated("Sample temperature is calibrated. Ready to grow.", Color.LimeGreen);

            //            return true;
            //        }
            //        else
            //        {
            //            OnTaskMessageGenerated("Could not read delta from sample.", Color.Yellow);
            //            return false;
            //        }
            //    }
            //    else
            //    {
            //        Flag_SampleDeltaKnown = false;
            //        OnTaskMessageGenerated("Sample temperature is not calibrated.", Color.Yellow);
            //        return false;
            //    }

            //}

            return false;
        }

        private async Task FindStartingPoint(CancellationToken token)
        {

            GrowthType = "Unknown";

            await SafeDelay(100, token);
            if (token.IsCancellationRequested) return;

            await SteadyState(token);
            if (token.IsCancellationRequested) return;

            await SmartRamp(token, config.CalibrationGaFlux, config.StartingSubstrateTemperature);
            if (token.IsCancellationRequested) return;

            // Clear the intensity chart
            OnTaskMessageGenerated($"Clearing the chart...", Color.White);
            ClearIntensity?.Invoke();
            await SafeDelay(2500, token);
            if (token.IsCancellationRequested) return;

            OnTaskMessageGenerated($"Starting video recording...", Color.White);
            StartStopRecording?.Invoke(true, "Starting");
            await SafeDelay(5000, token);
            if (token.IsCancellationRequested) return;

            // Note the HIGH intensity
            double highIntensity = intensityData.Last().Y;

            // Deposit Ga for t seconds
            bool success = await DepositGa(token, (int)(config.TimeOnStarting));
            if (token.IsCancellationRequested) return;

            if (success)
            {

                // Note the LOW intensity
                double lowIntensity = intensityData.Last().Y;

                // Calculate the mid point intensity
                double midIntensity = (highIntensity + lowIntensity) / 2;

                // Increase sub T and wait 300 s in cycles until intensity is (HIGH + LOW) / 2
                double result = await IntensityRecovery(token, midIntensity, maxWaitSeconds: 1800, holdTime: 150, stepT: 25, nextT: config.StartingSubstrateTemperature, maxT: 800, intensityInterrupt: true);
                if (token.IsCancellationRequested) return;
                CreatePartialCalibration("Starting", result.ToString("F1"));

                await WaitForTime(token, (int)(config.VideoStopDelay * 1000), "\rWaiting for video completion");
                if (token.IsCancellationRequested) return;

                OnTaskMessageGenerated($"Video recording complete", Color.LimeGreen);
                StartStopRecording?.Invoke(false, "Starting");

                OnTaskMessageGenerated($"Starting TC temperarure = {result:F1} °C", Color.Yellow);
                _sampleTCStartingPoint = result;
                Flag_StartingPointKnown = true;
                await SteadyState(token);
            }
            else // retry
            {
                await WaitForTime(token, 30000, "\rWaiting for recovery");
                await FindStartingPoint(token);


            }




        }

        private async Task FindSampleDelta(CancellationToken token)
        {
            GrowthType = "Unknown";
            //double T = -50;
            await Task.Delay(100, token);

            await SteadyState(token);
            if (token.IsCancellationRequested) return;

            // Executes at current sample T
            await SmartRamp(token, config.CalibrationGaFlux, double.NaN);
            if (token.IsCancellationRequested) return;

            // Clear the intensity chart
            OnTaskMessageGenerated($"Clearing the chart...", Color.White);
            ClearIntensity?.Invoke();
            await SafeDelay(2500, token);
            if (token.IsCancellationRequested) return;

            OnTaskMessageGenerated($"Starting video recording...", Color.White);
            StartStopRecording?.Invoke(true, "Temperature");
            await SafeDelay(5000, token);
            if (token.IsCancellationRequested) return;

            // Note the HIGH intensity
            double highIntensity = intensityData.Last().Y;

            // Deposit Ga for t seconds
            bool success = await DepositGa(token, (int)config.TimeOnCalibrate);
            if (token.IsCancellationRequested) return;

            if (success)
            {

                // Note the LOW intensity
                double lowIntensity = intensityData.Last().Y;

                // Calculate the mid point intensity
                double midIntensity = (highIntensity + lowIntensity) / 2;

                await IntensityRecovery(token, midIntensity, maxWaitSeconds: 600, holdTime: 10);
                if (token.IsCancellationRequested) return;


                (double desorbingFlux, double subT) = await DetermineSubstrateTemperature(config.TimeOnCalibrate, config.CalibrationGaFlux); // These are instantaneous and have a token 100ms delay

                if (!double.IsNaN(subT))
                {
                    (double T, double subTC) = DetermineDelta(subT);
                    _sampleDelta = T;
                    Flag_SampleDeltaKnown = true;
                    CreatePartialCalibration("Temperature", $"{subTC:F1}");
                }

                OnTaskMessageGenerated($"Waiting for video completion...", Color.White);
                await WaitForTime(token, (int)(config.VideoStopDelay * 1000), "\rWaiting for video completion");
                OnTaskMessageGenerated($"Video recording complete", Color.LimeGreen);
                StartStopRecording?.Invoke(false, "Temperature");
                await SteadyState(token);
            }
            else
            {
                await WaitForTime(token, 30000, "\rWaiting for recovery");
                await FindSampleDelta(token);
            }


        }

        private async Task FindSampleTCForTarget(CancellationToken token)
        {
            GrowthType = "Unknown";
            //double T = 750;
            await Task.Delay(100, token);

            string system = SystemState.Instance.systemsConfig.System.Name;
            double nextSubTC = double.NaN;
            double.TryParse(DataManager.Instance.Cache.GetCurrentValue($"{system}.{config.SampleStage}.Heater.MV").Value.ToString(), out nextSubTC);
            if (!double.IsNaN(_sampleDelta)) nextSubTC = config.TargetSubstrateTemperature - _sampleDelta;
            double realT = 0;

            await SteadyState(token);
            if (token.IsCancellationRequested) return;

            do
            {

                // Executes at current substrate temperature (if NaN) or updates on each loop
                double.TryParse(DataManager.Instance.Cache.GetCurrentValue($"{system}.{config.SampleStage}.Heater.MV").Value.ToString(), out double currentSubTC);

                if (!double.IsNaN(currentSubTC) && Math.Abs(currentSubTC - nextSubTC) > 2)
                    await SmartRamp(token, config.CalibrationGaFlux, nextSubTC);

                if (token.IsCancellationRequested) return;

                // Clear the intensity chart
                OnTaskMessageGenerated($"Clearing the chart...", Color.White);
                ClearIntensity?.Invoke();
                await SafeDelay(2500, token);
                if (token.IsCancellationRequested) return;

                OnTaskMessageGenerated($"Starting video recording...", Color.White);
                StartStopRecording?.Invoke(true, "Temperature");
                await SafeDelay(5000, token);
                if (token.IsCancellationRequested) return;

                // Note the HIGH intensity
                double highIntensity = intensityData.Last().Y;

                // Deposit Ga for t seconds
                bool success = await DepositGa(token, (int)config.TimeOnCalibrate);
                if (token.IsCancellationRequested) return;

                if (success)
                {

                    // Note the LOW intensity
                    double lowIntensity = intensityData.Last().Y;

                    // Calculate the mid point intensity
                    double midIntensity = (highIntensity + lowIntensity) / 2;

                    await IntensityRecovery(token, midIntensity, maxWaitSeconds: 600, holdTime: 10);
                    if (token.IsCancellationRequested) return;


                    (double desorbingFlux, double subT) = await DetermineSubstrateTemperature(60, config.CalibrationGaFlux); // These are instantaneous and have a token 100ms delay
                    realT = subT;
                    _sampleDelta = realT - nextSubTC;
                    CreatePartialCalibration("Temperature", nextSubTC.ToString("F1"));

                    OnTaskMessageGenerated($"Video recording complete", Color.LimeGreen);
                    StartStopRecording?.Invoke(false, "Temperature");
                    await SafeDelay(1000, token);
                    if (token.IsCancellationRequested) return;
                    string selectedTask = "Continue";

                    // Offer continue, pivot or exit
                    do
                    {
                        ContinueTaskForm continueTaskForm = new ContinueTaskForm();
                        continueTaskForm.ShowDialog();
                        selectedTask = continueTaskForm.SelectedTask;

                        if (selectedTask == "Reinterpret")
                        {
                            CancellationTokenSource cts = new CancellationTokenSource();
                            var token2 = cts.Token;
                            ReinterpretRequested?.Invoke(true, cts);
                            OnTaskMessageGenerated("Waiting for user...", Color.White);
                            await SafeDelay(1000, token);
                            await WaitForCacellation(60000, token2, "\rWaiting for user ");

                        }

                        if (token.IsCancellationRequested) return;

                    } while (selectedTask == "Reinterpret");
                    ReinterpretRequested?.Invoke(false, new CancellationTokenSource());

                    if (selectedTask == "Cancel task")
                        break;

                    nextSubTC = config.TargetSubstrateTemperature - _sampleDelta;
                    if (token.IsCancellationRequested) return;
                }
                else
                {
                    await WaitForTime(token, 30000, "\rWaiting for recovery");
                }

                // automatically repeats lat nextSubTC on fail (!success)

            } while (Math.Abs(realT - config.TargetSubstrateTemperature) > config.TargetTemperatureTolerance);



            _sampleTCForTarget = Math.Round(nextSubTC, 1);
            Flag_SampleTCForTargetKnown = true;
        }

        private async Task IgnitePlasma(CancellationToken token)
        {
            OnTaskMessageGenerated("Igniting plasma...", Color.White);

            bool Completed = false;


            string recipeName = "Ignite Plasma";
            string content = RecipeSQLManager.Instance.LoadVersion(recipeName, -1);
            RecipeEngine _engine = new RecipeEngine(new StringRecipeSource(content));
            Guid _runId = RecipeCoordinator.Instance.Register(_engine, "Ignite Plasma");
            string nameSpace = SystemState.Instance.systemsConfig.System.Name;
            RecipeCoordinator.Instance.Start(_runId, nameSpace, 0);

            GuiTools.OnShowRecipePanel(true, false);

            while (!Completed)
            {
                await Task.Delay(1000, token);
                var runs = RecipeCoordinator.Instance.Snapshot(); // (id, name, state, startedUtc) – extend later if you add more fields
                foreach (var r in runs)
                {
                    if (r.name == "Ignite Plasma" && r.state == RecipeState.Complete)
                        Completed = true;
                    else
                        OnTaskMessageGenerated($"\r{r.name} remaining {r.remaining} s.", Color.White);
                }
                if (token.IsCancellationRequested) return;

            }



            // Unregister the headless recipe
            RecipeCoordinator.Instance.Unregister(_runId);
            GuiTools.OnShowRecipePanel(false, false);

            OnTaskMessageGenerated("Plasma ignited.", Color.LimeGreen);
            Flag_PlasmaIgnited = true;
        }

        private async Task FindGrowthParameters(CancellationToken token)
        {
            GrowthType = "Unknown";
            double subTC = config.TargetSubstrateTemperature - _sampleDelta;


            await SteadyState(token);
            if (token.IsCancellationRequested) return;

            // Executes at current sample temperature (if NaN) or ramps to subTC (if _sampleDelta is known)
            await SmartRamp(token, config.GrowthGaFlux, subTC);
            if (token.IsCancellationRequested) return;

            // Clear the intensity chart
            OnTaskMessageGenerated($"Clearing the chart...", Color.White);
            ClearIntensity?.Invoke();
            await SafeDelay(2500, token);
            if (token.IsCancellationRequested) return;

            OnTaskMessageGenerated($"Starting video recording...", Color.White);
            StartStopRecording?.Invoke(true, "Growth");
            await SafeDelay(5000, token);
            if (token.IsCancellationRequested) return;

            // Note the HIGH intensity
            double highIntensity = intensityData.Last().Y;

            // N on!
            //string system = SystemState.Instance.systemsConfig.System.Name;
            await OpenShutterWithConfirmation("N.Shutter");
            await SafeDelay(250, token);

            // Ga on! + Wait t On Grow -> Ga off!
            bool success = await DepositGa(token, (int)config.TimeOnGrowth, true);
            await SafeDelay(250, token);
            if (token.IsCancellationRequested) return;

            if (success)
            {

                // Note the LOW intensity
                double lowIntensity = intensityData.Last().Y;

                // Calculate the mid point intensity
                double midIntensity = (highIntensity + lowIntensity) / 2;

                // N off!
                await CloseShutterWithConfirmation("N.Shutter");
                await SafeDelay(250, token);
                if (token.IsCancellationRequested) return;

                string growthType = "3D";
                if (HtmlManager.yoloLabel != "Spotty")
                {
                    await IntensityRecovery(token, midIntensity, maxWaitSeconds: 180, holdTime: 10);
                    if (token.IsCancellationRequested) return;
                    growthType = await DetermineGrowthType();
                    OnTaskMessageGenerated($"Growth type = {growthType}", Color.LimeGreen);
                }
                else
                    OnTaskMessageGenerated($"Growth type = {growthType}. You need to recover the surface.", Color.OrangeRed);

                GrowthType = growthType;

                CreatePartialCalibration("Growth", subTC.ToString("F1"));

                OnTaskMessageGenerated($"Video recording complete", Color.LimeGreen);
                StartStopRecording?.Invoke(false, "Growth");

                await SafeDelay(100, token);
            }
            else
            {
                await WaitForTime(token, 15000, "\rWaiting for recovery");
                await CloseShutterWithConfirmation("N.Shutter");
                await WaitForTime(token, 15000, "\rWaiting for recovery");
                await FindGrowthParameters(token);
            }


        }

        private async Task SmartRamp(CancellationToken token, double gaFlux, double sampleTC)
        {

            // check IF we need to ramp at all...
            string system = SystemState.Instance.systemsConfig.System.Name;
            double.TryParse(DataManager.Instance.Cache.GetCurrentValue($"{system}.{config.SampleStage}.Heater.MV").Value.ToString(), out double sampleTC_Now);
            double.TryParse(DataManager.Instance.Cache.GetCurrentValue($"{system}.{config.IIISource}.Cell.MV").Value.ToString(), out double gaT_Now);
            double gaFlux_Now = FluxManager.EstimateFlux($"{config.IIISource}", gaT_Now); // Incident flux NOT desorbing flux

            // Calculate the deltas
            double gaT_1 = gaT_Now; // FluxManager.SolveMvForLogFlux("Ga", gaFlux_Now, 500, 1200);
            double gaT_2 = FluxManager.SolveMvForLogFlux($"{config.IIISource}", gaFlux, 500, 1200); // Incident flux NOT desorbing flux
            double delta_Ga = Math.Abs(gaT_1 - gaT_2);
            double delta_Sample = double.IsNaN(sampleTC) ? 0 : Math.Abs(sampleTC - sampleTC_Now);

            // Only ramp if needed!
            if (delta_Ga < 0.5 && delta_Sample < config.TargetTemperatureTolerance) return;

            // Calculate the wait time
            double delta_biggest = Math.Max(delta_Ga, delta_Sample);
            int waitMS = (int)(Math.Max(30000, Math.Min(delta_biggest * 5000, 300000)));  // wait 30s to 300s depending on delta

            OnTaskMessageGenerated("Ramping to initial conditions...", Color.White);
            await RampToFluxAndSubT(gaFlux, sampleTC);
            OnTaskMessageGenerated("\rWaiting for ramps to end", Color.White);
            await WaitForRampsToComplete(token, $"{config.SampleStage}.Heater", $"{config.IIISource}.Cell");
            if (token.IsCancellationRequested) return;
            await WaitForTime(token, waitMS, "\rAwaiting temperature stability after ramping ");
            OnTaskMessageGenerated("\rReady!", Color.LimeGreen);

        }

        private async Task RampToFluxAndSubT(double gaFlux, double sampleTC)
        {
            // Ramp Ga to calibation flux
            OnTaskMessageGenerated("Ramping to initial conditions...", Color.White);
            double gaT = FluxManager.SolveMvForLogFlux($"{config.IIISource}", gaFlux, 500, 1200); // Incident flux NOT desorbing flux
            string system = SystemState.Instance.systemsConfig.System.Name;
            if (!double.IsNaN(sampleTC)) await commandManager.ExecuteCommand($"Ramp({system}.{config.SampleStage}.Heater, {sampleTC:F1}, {config.SampleRampRate})");
            if (!double.IsNaN(gaT)) await commandManager.ExecuteCommand($"Ramp({system}.{config.IIISource}.Cell, {gaT:F1}, {config.GaRampRate})");
            await Task.Delay(1000); // short pause so ramps are loaded
        }

        private async Task RampWait(CancellationToken token, string loopName, double endPoint, double rampRate)
        {
            string system = SystemState.Instance.systemsConfig.System.Name;
            await commandManager.ExecuteCommand($"Ramp({system}.{loopName}, {endPoint:F1}, {rampRate:F1})");
            await WaitForRampsToComplete(token, $"{config.SampleStage}.Heater");
        }

        private async Task<bool> DepositGa(CancellationToken token, int tOn, bool AiInterrupt = false)
        {
            string system = SystemState.Instance.systemsConfig.System.Name;
            OnTaskMessageGenerated($"Supplying {config.IIIElement}...", Color.White);
            await OpenShutterWithConfirmation($"{config.IIISource}.Shutter");
            bool success = await WaitForTime(token, tOn * 1000, $"\rSupplying {config.IIIElement} ");
            await CloseShutterWithConfirmation($"{config.IIISource}.Shutter");
            return success;
        }

        private bool IntensityStable(double periodSeconds)
        {

            if (intensityData.Last().X - intensityData.First().X < periodSeconds)
                return false;

            double targetTime = intensityData.Last().X - periodSeconds;

            // Find the most recent point at or before targetTime
            var item = intensityData
                .Where(p => p.X <= targetTime)
                .OrderByDescending(p => p.X)
                .FirstOrDefault();

            if (item == null)
                return false; // no data that far back

            return Math.Abs(item.Y - intensityData.Last().Y) <= 1;
        }

        private async Task SteadyState(CancellationToken token, int stableSeconds = 5)
        {

            double latestIntensity = intensityData.Last().Y;
            double nextIntensity;
            var sw = Stopwatch.StartNew();

            if (IntensityStable(stableSeconds)) return;

            do
            {

                OnTaskMessageGenerated("Intensity not stable...", Color.White);
                await SafeDelay(500, token);
                nextIntensity = latestIntensity;
                await WaitForTime(token, stableSeconds * 1000, "\rWaiting for stable intensity ");
                latestIntensity = intensityData.Last().Y;
                double elapsedSeconds = sw.ElapsedMilliseconds / 1000;
                if (elapsedSeconds > 300) break;
                if (token.IsCancellationRequested) return;

            } while (Math.Abs(nextIntensity - latestIntensity) > 5);

        }

        private async Task<double> IntensityRecovery(CancellationToken token, double thresholdIntensity, int maxWaitSeconds, int holdTime = 150, double stepT = 0, double nextT = 0, double maxT = 800, bool intensityInterrupt = false)
        {
            var sw = new Stopwatch();
            sw.Restart();
            while (intensityData.Last().Y < thresholdIntensity)
            {
                if (stepT != 0)
                {
                    nextT += stepT;
                    OnTaskMessageGenerated("\rRamping substrate...", Color.White);
                    await RampWait(token, $"{config.SampleStage}.Heater", nextT, double.Parse(config.SampleRampRate.ToString()));
                    if (nextT + stepT > maxT) break;
                }



                await WaitForTime(token, (int)holdTime * 1000, "\rWaiting for intensity recovery ", intensityInterrupt: intensityInterrupt, thresholdIntensity: thresholdIntensity);
                double elapsedSeconds = sw.ElapsedMilliseconds / 1000;
                if (elapsedSeconds > maxWaitSeconds) break;
                if (token.IsCancellationRequested) return 0;

            }

            double finalElapsedSeconds = sw.ElapsedMilliseconds / 1000;
            while (finalElapsedSeconds < 30)
            {
                await WaitForTime(token, 10000, "\rWaiting for minimum duration ");
                finalElapsedSeconds = sw.ElapsedMilliseconds / 1000;
                if (finalElapsedSeconds > maxWaitSeconds) break;
                if (token.IsCancellationRequested) return 0;
            }

            return nextT;

        }

        private async Task WaitForRampsToComplete(CancellationToken token, params string[] keys)
        {
            string system = SystemState.Instance.systemsConfig.System.Name;
            bool running = true;
            string prefix = "\rWaiting for ramps to end";
            string suffix = ".";

            while (running)
            {
                running = false;
                var snapshot = RampCoordinator.Instance.Snapshot();

                foreach (var ramp in snapshot)
                {
                    // match system + key
                    if (keys.Any(k => ramp.key.Contains($"{system}.{k}")))
                    {
                        if (ramp.state == RampState.Running || ramp.state == RampState.Paused)
                        {
                            running = true;
                            break;
                        }
                    }
                }

                if (running)
                {
                    await Task.Delay(1000, token); // poll interval
                    suffix = suffix + ".";
                    if (suffix.Length == 4) suffix = ".";
                    OnTaskMessageGenerated($"{prefix}{suffix}", Color.White);

                }

                if (token.IsCancellationRequested) return;

            }

            var finalSnapshot = RampCoordinator.Instance.Snapshot();

        }

        private async Task<bool> WaitForTime(CancellationToken token, int msWait, string prefix = "\rWaiting ", bool AiInterrupt = false, bool intensityInterrupt = false, double thresholdIntensity = -1)
        {
            long elapsedSeconds = 0;
            int i = 0;
            bool corrected = false;
            bool deposition = prefix == "\rSupplying Ga ";

            var sw = Stopwatch.StartNew();

            while (sw.ElapsedMilliseconds < msWait)
            {
                if (token.IsCancellationRequested) return false;
                await SafeDelay(100, token); // poll interval
                i++;

                if (i > 9)
                {
                    i = 0;
                    elapsedSeconds = sw.ElapsedMilliseconds / 1000;
                    if (config.ActiveFixing) corrected = Fix(false);

                    if (corrected && deposition && elapsedSeconds < 15) // if it was growing and corrected in the first 15 seconds...
                        return false;


                    int dots = (int)((elapsedSeconds) % 3 + 1); // cycle 1–3 dots
                    string suffix = new string('.', dots);
                    //OnTaskMessageGenerated($"{prefix}{suffix}", Color.White);
                    OnTaskMessageGenerated($"{prefix} {elapsedSeconds} / {msWait / 1000} seconds{suffix}", Color.White);

                    if (AiInterrupt)
                    {
                        string yoloLabel = HtmlManager.yoloLabel;
                        if (yoloLabel == "Spotty") break;

                    }
                    else if (intensityInterrupt)
                    {
                        if (intensityData.Last().Y > thresholdIntensity) break;
                    }

                }
            }
            return true;
        }

        private async Task WaitForCacellation(int msWait, CancellationToken token, string prefix = "\rWaiting ")
        {
            long elapsedSeconds = 0;
            //int i = 0;

            var sw = Stopwatch.StartNew();

            while (sw.ElapsedMilliseconds < msWait)
            {
                await SafeDelay(1000, token); // poll interval
                elapsedSeconds = sw.ElapsedMilliseconds / 1000;
                OnTaskMessageGenerated($"{prefix} {elapsedSeconds} / {msWait / 1000} seconds", Color.White);

                if (token.IsCancellationRequested) break;

            }
        }

        private async Task<(double, double)> DetermineSubstrateTemperature(double tOn, double incidentGaFlux)
        {

            //if (intensityData.Count() == 0) return (double.NaN, double.NaN);

            //for (int i = 0; i < 2; i++) // Run fix twice
            //    await Fix(true); // These are instantaneous and have a token 100ms delay

            //await Smooth(CancellationToken.None); // These are instantaneous and have a token 100ms delay

            //(double t0, double t1, double t2, double t3) = (double.NaN, double.NaN, double.NaN, double.NaN);

            //if (!config.UseAI)
            //    (t0, t1, t2, t3) = Determinictic_DetermineSubstrateTemperature(tOn);
            //else
            //    (t0, t1, t2, t3) = await AIInterpret(tOn);

            //(double desorbingFlux, double subT) = (double.NaN, double.NaN);

            //if (!double.IsNaN(t0) && !double.IsNaN(t1) && !double.IsNaN(t2) && !double.IsNaN(t3))
            //    (desorbingFlux, subT) = await DetermineSubT(t1, t2, t3, incidentGaFlux);

            if (config.UseAI) OnTaskMessageGenerated("AiInterpret task starting...", Color.White);

            EnvelopeResult result = await EnvelopeFitter.DetermineSubstrateTemperature(intensityData, _intensityLock, tOn, incidentGaFlux, config.UseAI, config.IIIElement, config.UseSmooth);
            TurboComplete?.Invoke();

            // if we request AI and it failed... inform the user
            if (config.UseAI && !result.AI_Success) OnTaskMessageGenerated(result.AI_Message, Color.PaleVioletRed);

            if (!result.Success) // determinisitc failed | fallback deterministic failed AFTER AI failed
            {
                OnTaskMessageGenerated(result.Deterministic_Message, Color.PaleVioletRed);
            }
            else // success!!!
            {
                AddSeriesToIntenstiyChart?.Invoke(result.t0, "t0");
                AddSeriesToIntenstiyChart?.Invoke(result.t1, "t1");
                AddSeriesToIntenstiyChart?.Invoke(result.t2, "t2");
                AddSeriesToIntenstiyChart?.Invoke(result.t3, "t3");
                OnTaskMessageGenerated($"Substrate temperature = {result.SubT:F0} °C", Color.Yellow);
                await Task.Delay(4000);
            }
            return (result.DesFlux, result.SubT);

        }

        private (double, double) DetermineDelta(double realT)
        {
            string system = SystemState.Instance.systemsConfig.System.Name;
            if (double.TryParse(DataManager.Instance.Cache.GetCurrentValue($"{system}.{config.SampleStage}.Heater.MV").Value.ToString(), out double result))
            {
                return (realT - result, result);
            }
            return (0, 0);

        }

        private async Task<string> DetermineGrowthType()
        {
            //await Task.Delay(100);
            //if (intensityData.Count() == 0) return "Layer by layer";

            //for (int i = 0; i < 2; i++) // Run fix twice
            //    await Fix(true); // These are instantaneous and have a token 100ms delay

            //await Smooth(CancellationToken.None);

            //(double t0, double t1, double t2, double t3) = (double.NaN, double.NaN, double.NaN, double.NaN);
            //if (!config.UseAI)
            //    (t0, t1, t2, t3) = Deterministic_DetermineGrowthType();
            //else
            //    (t0, t1, t2, t3) = await AIInterpret(config.TimeOnGrowth);

            //string growthType = "Layer by layer";
            //if (!double.IsNaN(t0) && !double.IsNaN(t1) && !double.IsNaN(t2) && !double.IsNaN(t3))
            //    growthType = ReturnGrowthTypeString(t0, t1, t2, t3);

            if (config.UseAI) OnTaskMessageGenerated("AiInterpret task starting...", Color.White);

            EnvelopeResult result = await EnvelopeFitter.DetermineGrowthType(intensityData, _intensityLock, config.TimeOnGrowth, config.UseAI, config.UseSmooth);
            TurboComplete?.Invoke();

            // if we request AI and it failed... inform the user
            if (config.UseAI && !result.AI_Success) OnTaskMessageGenerated(result.AI_Message, Color.PaleVioletRed);

            if (!result.Success) // determinisitc failed | fallback deterministic failed AFTER AI failed
            {
                OnTaskMessageGenerated(result.Deterministic_Message, Color.PaleVioletRed);
            }
            else
            {
                AddSeriesToIntenstiyChart?.Invoke(result.t0, "t0");
                AddSeriesToIntenstiyChart?.Invoke(result.t1, "t1");
                AddSeriesToIntenstiyChart?.Invoke(result.t2, "t2");
                AddSeriesToIntenstiyChart?.Invoke(result.t3, "t3");
                // the user is informed about the growth type in the calling function atm, might want to clean that up later. 
            }

            return result.GrowthType;

        }

        private List<IntensityPoint> SmoothEmaZeroPhaseTime(List<IntensityPoint> src, double tauSec)
        {
            var n = src?.Count ?? 0;
            var res = new List<IntensityPoint>(n);
            if (n == 0 || tauSec <= 0) return new List<IntensityPoint>(src ?? new List<IntensityPoint>());

            var t = new double[n];
            var y = new double[n];
            for (int i = 0; i < n; i++) { t[i] = src[i].X; y[i] = src[i].Y; }

            // forward EMA: y_f[i] = a_i*y[i] + (1-a_i)*y_f[i-1], a_i = 1-exp(-dt/tau)
            var yf = new double[n];
            yf[0] = y[0];
            for (int i = 1; i < n; i++)
            {
                double dt = Math.Max(0.0, t[i] - t[i - 1]);
                double a = 1.0 - Math.Exp(-(dt / tauSec));
                yf[i] = a * y[i] + (1.0 - a) * yf[i - 1];
            }

            // backward EMA: apply same on reversed series to remove phase lag
            var yb = new double[n];
            yb[n - 1] = yf[n - 1];
            for (int i = n - 2; i >= 0; i--)
            {
                double dt = Math.Max(0.0, t[i + 1] - t[i]);
                double a = 1.0 - Math.Exp(-(dt / tauSec));
                yb[i] = a * yf[i] + (1.0 - a) * yb[i + 1];
            }

            for (int i = 0; i < n; i++) res.Add(new IntensityPoint(t[i], yb[i]));
            return res;
        }

        private void CreatePartialCalibration(string calibrationType, string SubTC)
        {


            string tON = calibrationType == "Temperature" ? Configuration.TimeOnCalibrate.ToString() : Configuration.TimeOnGrowth.ToString();
            if (calibrationType == "Starting") tON = (Configuration.TimeOnStarting).ToString("F0");
            string phiGa = calibrationType == "Temperature" || calibrationType == "Starting" ? Configuration.CalibrationGaFlux.ToString("F2") : Configuration.GrowthGaFlux.ToString("F2");

            string system = SystemState.Instance.systemsConfig.System.Name;
            string TGa = DataManager.Instance.Cache.GetCurrentValue($"{system}.{config.IIISource}.Cell.MV").Value.ToString();

            double delta = SampleDelta;
            double value = 0;
            string SubT = !double.IsNaN(delta) && double.TryParse(SubTC, out value) ? (value + delta).ToString("F1") : "Unknown";

            string desorbingFlux = SubT != "Unknown" && double.TryParse(SubT, out value) ? FluxManager.EstimateFlux(config.IIIElement, value, FluxManager.fluxFitDictionary[config.IIIElement]).ToString("F2") : "Unknown";
            string growthType = GrowthType;

            string GCpressure = DataManager.Instance.Cache.GetCurrentValue($"{system}.GC IG.Gauge.MV").Value.ToString();

            PartialCalibration = new LiveCalibration
            {

                source = config.IIISource,
                element = config.IIIElement,


                calibrationType = calibrationType,
                growth = calibrationType,

                t_On = tON,     // seconds

                T_Ga = TGa,   // °C
                T_III_source = TGa,

                phi_Ga = phiGa,     // arbitrary flux units
                phi_incident = phiGa,

                T_sub_set = SubTC,

                phi_des = desorbingFlux,
                phi_desorb = desorbingFlux,

                T_sub_cali = SubT,
                label = growthType,
                notes = $"Delta = {delta:F1} °C",
                pressure = GCpressure
            };







        }

    }

    [AttributeUsage(AttributeTargets.Property)]
    public class ConfigDisplayAttribute : Attribute
    {
        public string Label { get; }
        public string Unit { get; }
        public object Default { get; }

        public ConfigDisplayAttribute(string label, string unit = "", object @default = null)
        {
            Label = label;
            Unit = unit;
            Default = @default;
        }
    }

    public class LiveConfigClass
    {

        [ConfigDisplay("Sample stage", "", "Sample stage")]
        public string SampleStage { get; set; } = "Manipulator";

        [ConfigDisplay("N source", "", "N source")]
        public string NSource { get; set; } = "N";

        [ConfigDisplay("III source", "", "III source")]
        public string IIISource { get; set; } = "Ga1";

        [ConfigDisplay("III element", "", "III element")]
        public string IIIElement { get; set; } = "Ga";

        [ConfigDisplay("Starting Substrate Temperature", "°C", 620.0)]
        public double StartingSubstrateTemperature { get; set; } = 620.0;

        [ConfigDisplay("Target Substrate Temperature", "°C", 690.0)]
        public double TargetSubstrateTemperature { get; set; } = 690.0;

        [ConfigDisplay("Target Temperature Tolerance", "°C", 2.0)]
        public double TargetTemperatureTolerance { get; set; } = 2.0;

        [ConfigDisplay("Time On Starting", "s", 60.0)]
        public double TimeOnStarting { get; set; } = 60.0;

        [ConfigDisplay("Time On Calibrate", "s", 60.0)]
        public double TimeOnCalibrate { get; set; } = 60.0;

        [ConfigDisplay("Time On Growth", "s", 120.0)]
        public double TimeOnGrowth { get; set; } = 120.0;

        [ConfigDisplay("Calibration III Flux", "atoms/nm²/s", 4.00)]
        public double CalibrationGaFlux { get; set; } = 4.00; // ≈ 5.45 nm/min

        [ConfigDisplay("Growth III Flux", "atoms/nm²/s", 7.33)]
        public double GrowthGaFlux { get; set; } = 7.33; // ≈ 10 nm/min

        [ConfigDisplay("Growth N Flux", "atoms/nm²/s", 5.86)]
        public double GrowthNFlux { get; set; } = 5.86; // ≈ 8 nm/min

        [ConfigDisplay("Sample ramp rate", "°C/s", 0.33)]
        public double SampleRampRate { get; set; } = 0.33;

        [ConfigDisplay("Ga ramp rate", "°C/s", 0.33)]
        public double GaRampRate { get; set; } = 0.33;

        [ConfigDisplay("Active fixing", "", false)]
        public bool ActiveFixing { get; set; } = false;

        [ConfigDisplay("Ignition recipe", "", "Ignite Plasma")]
        public string IgnitionRecipe { get; set; } = "Ignite Plasma";

        [ConfigDisplay("Use AI analysis", "", true)]
        public bool UseAI { get; set; } = true;

        [ConfigDisplay("Use smooth", "", true)]
        public bool UseSmooth { get; set; } = true;

        [ConfigDisplay("Video stop delay", "s", 15.0)]
        public double VideoStopDelay { get; set; } = 15.0;
        //[ConfigDisplay("Ignition RF Power", "W", 300.0)]
        //public double IgnitionRFPower { get; set; } = 300.0;

        //[ConfigDisplay("Ignition Mass Flow", "sccm", 4.0)]
        //public double IgnitionMassFlow { get; set; } = 4.0;

        //[ConfigDisplay("Growth RF Power", "W", 400.0)]
        //public double GrowthRFPower { get; set; } = 400.0;

        //[ConfigDisplay("Growth Mass Flow", "sccm", 0.5)]
        //public double GrowthMassFlow { get; set; } = 0.5;

        private string filePath;

        public void Initialise(string wbName)
        {

            string settingsRoot = PathManager.Instance.InstanceSettingsPath;
            string wbRoot = PathManager.Instance.GetSettingsPath("WorkBooks", wbName);
            using (DataWriteGate.Instance.EnterWrite())
            {
                Directory.CreateDirectory(wbRoot);
            }
            var fileName = "GaN Calibrate Live.json";
            filePath = Path.Combine(wbRoot, fileName);


            //Directory.CreateDirectory(".\\Data");
            //var config = SystemState.Instance.systemsConfig;
            //var uoUUID = config.UUID;
            //var dataRoot = Path.Combine(".\\Data", uoUUID);
            //Directory.CreateDirectory(dataRoot);

            //var workbookRoot = Path.Combine(".\\Data", uoUUID, "WorkBooks");
            //Directory.CreateDirectory(dataRoot);
            //var liveRoot = Path.Combine(".\\Data", uoUUID, "WorkBooks", "GaN Live");
            //Directory.CreateDirectory(liveRoot);
            //var fileName = "GaNLive.json";
            //filePath = Path.Combine(".\\Data", uoUUID, "WorkBooks", "GaN Live", fileName);

            if (File.Exists(filePath))
                LoadJson();

            else
                SaveJson();
        }

        public void LoadJson()
        {
            try
            {
                var json = File.ReadAllText(filePath);
                var loaded = JsonConvert.DeserializeObject<LiveConfigClass>(json);

                if (loaded != null)
                {
                    // Copy values from loaded object into this one
                    foreach (var prop in typeof(LiveConfigClass).GetProperties())
                    {
                        var value = prop.GetValue(loaded);
                        prop.SetValue(this, value);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error loading config: " + ex.Message);
            }
        }

        public void SaveJson()
        {
            try
            {
                var json = JsonConvert.SerializeObject(this, Formatting.Indented);

                string partialPath = filePath + ".partial";

                using (DataWriteGate.Instance.EnterWrite())
                {
                    File.WriteAllText(partialPath, json);

                    if (File.Exists(filePath))
                        File.Delete(filePath);

                    File.Move(partialPath, filePath);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error saving config: " + ex.Message);
            }
        }

    }

    class LiveConfigForm : Form
    {
  

        private System.ComponentModel.IContainer components = null;
        private LiveConfigClass _config;
        private int _width = 525;
        private int _height = 900;

        private Dictionary<System.Reflection.PropertyInfo, RegexMaskedTextBox> propertyBindings = new Dictionary<System.Reflection.PropertyInfo, RegexMaskedTextBox>();

        public LiveConfigForm(LiveConfigClass config)
        {

            _config = config;

            this.components = new System.ComponentModel.Container();
            this.BackColor = Styles.isDarkMode ? Styles.BackColorDark : Styles.BackColorLight;
            this.ForeColor = Styles.isDarkMode ? Styles.ForeColorDark : Styles.ForeColorLight;
            this.Size = new Size(_width, _height);
            this.ClientSize = new System.Drawing.Size(_width, _height);
            this.Padding = new Padding(2);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.None;
            this.Font = new Font(Font.FontFamily, 12);

        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            CreateControls();
            BringToFront();
            TopMost = true;
        }

        private void CreateControls()
        {

            int Y = 10;

            Label titleLabel = new Label { Text = "Workbook configuration", Location = new Point(10, Y), AutoSize = true, ForeColor = Color.LimeGreen };
            this.Controls.Add(titleLabel);
            Y += 40;

            foreach (var prop in typeof(LiveConfigClass).GetProperties())
            {
                var attr = prop.GetCustomAttribute<ConfigDisplayAttribute>();
                if (attr != null)
                {
                    string labelText = string.IsNullOrEmpty(attr.Unit) ? $"{attr.Label}" : $"{attr.Label} ({attr.Unit})";


                    object defaultValue = prop.GetValue(_config) ?? attr.Default;

                    Label label = new Label { Text = labelText, Location = new Point(10, Y), Size = new Size(300, 35) };
                    this.Controls.Add(label);

                    RegexMaskedTextBox regexMaskedTextBox = new RegexMaskedTextBox { Text = defaultValue.ToString(), Location = new Point(310, Y), Size = new Size(175, 35) };
                    propertyBindings[prop] = regexMaskedTextBox;
                    this.Controls.Add(regexMaskedTextBox);

                    Y += 40;
                }
            }

            ShinyButton closeButton = new ShinyButton { ButtonText = "Close", Bright = true, Location = new Point(10, Y), Width = this.Width - 20 };
            this.Controls.Add(closeButton);
            // Then update the Close button:
            closeButton.Click += (s, e) =>
            {
                foreach (var kvp in propertyBindings)
                {
                    var prop = kvp.Key;
                    var tb = kvp.Value;
                    var text = tb.Text;

                    try
                    {
                        object convertedValue;
                        if (prop.PropertyType == typeof(double))
                            convertedValue = double.Parse(text);
                        else if (prop.PropertyType == typeof(int))
                            convertedValue = int.Parse(text);
                        else if (prop.PropertyType == typeof(bool))
                            convertedValue = bool.Parse(text);
                        else
                            convertedValue = text; // fallback for string

                        prop.SetValue(_config, convertedValue);
                    }
                    catch
                    {
                        // You could log or show an error here; for now ignore invalid input
                    }
                }

                _config.SaveJson();

                this.Close();
            };

        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Color color = Styles.isGreenMode ? Styles.GradientStartGreen : Styles.GradientStartBlue;
            using (var pen = new Pen(color, 2)) // pick your border color & thickness
            {
                // -1 so the border is fully inside the client area
                e.Graphics.DrawRectangle(pen, 1, 1, this.ClientSize.Width - 1, this.ClientSize.Height - 1);
            }
        }

    }

    class SubTaskForm : Form
    {
    
        private System.ComponentModel.IContainer components = null;
        //private LiveConfigClass _config;
        private int _width = 250;
        private int _height = 545;

        public string SelectedTask = "Cancel";

        public SubTaskForm()
        {

            this.components = new System.ComponentModel.Container();
            this.BackColor = Styles.isDarkMode ? Styles.BackColorDark : Styles.BackColorLight;
            this.ForeColor = Styles.isDarkMode ? Styles.ForeColorDark : Styles.ForeColorLight;
            this.Size = new Size(_width, _height);
            this.ClientSize = new System.Drawing.Size(_width, _height);
            this.Padding = new Padding(2);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.None;
            this.Font = new Font(Font.FontFamily, 12);

        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            CreateControls();
            BringToFront();
            TopMost = true;
        }

        private void CreateControls()
        {

            int Y = 10;

            Label titleLabel = new Label { Text = "Select subtask", Location = new Point(10, Y), AutoSize = true, ForeColor = Color.LimeGreen };
            this.Controls.Add(titleLabel);
            Y += 30;

            string[] buttonNames = { "Find Starting Point", "Find Delta", "Tune to Taget", "Ignite Plasma", "Tune Growth Type", "Fix", "Reinterpret", "Calibrations", "Retry", "Cancel" };



            foreach (string buttonName in buttonNames)
            {


                ShinyButton shinyButton = new ShinyButton { ButtonText = buttonName, Bright = true, Location = new Point(10, Y), Width = this.Width - 20 };
                this.Controls.Add(shinyButton);
                // Then update the Close button:
                shinyButton.Click += (s, e) =>
                {
                    SelectedTask = shinyButton.ButtonText;
                    this.Close();
                };

                Y += 50;

            }

        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Color color = Styles.isGreenMode ? Styles.GradientStartGreen : Styles.GradientStartBlue;
            using (var pen = new Pen(color, 2)) // pick your border color & thickness
            {
                // -1 so the border is fully inside the client area
                e.Graphics.DrawRectangle(pen, 1, 1, this.ClientSize.Width - 1, this.ClientSize.Height - 1);
            }
        }

        public class LiveCalibration
        {
            public string sessionUUID { get; set; }
            public string video { get; set; }
            public string sample { get; set; }

            public string source { get; set; }
            public string element { get; set; }

            public string t_On { get; set; }          // seconds
            public string T_sub_set { get; set; }
            public string T_sub_cali { get; set; }

            // Nrew
            public string calibrationType { get; set; }
            public string T_III_source { get; set; }          // °C
            public string phi_incident { get; set; }        // arbitrary flux units        
            public string phi_desorb { get; set; }       // desorbing flux

            // Legacy
            public string growth { get; set; }
            public string T_Ga { get; set; }          // °C
            public string phi_Ga { get; set; }        // arbitrary flux units        
            public string phi_des { get; set; }       // desorbing flux

            public string label { get; set; }
            public string notes { get; set; } =
                "Automatic calibration file for GaN live workbook";
            public string pressure { get; set; }
        }

    }

    class ContinueTaskForm : Form
    {
        private System.ComponentModel.IContainer components = null;
        //private LiveConfigClass _config;
        private int _width = 250;
        private int _height = 200;

        private Label titleLabel;
        private System.Windows.Forms.Timer timer;
        private int duration = 15;
        public string SelectedTask = "Continue";

        public ContinueTaskForm()
        {

            this.components = new System.ComponentModel.Container();
            this.BackColor = Styles.isDarkMode ? Styles.BackColorDark : Styles.BackColorLight;
            this.ForeColor = Styles.isDarkMode ? Styles.ForeColorDark : Styles.ForeColorLight;
            this.Size = new Size(_width, _height);
            this.ClientSize = new System.Drawing.Size(_width, _height);
            this.Padding = new Padding(2);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.None;
            this.Font = new Font(Font.FontFamily, 12);

        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            CreateControls();
            BringToFront();
            TopMost = true;

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 1000;
            timer.Tick += (s, e2) =>
            {
                duration--;
                titleLabel.Text = $"Select (resumes in {duration} s)";
                if (duration <= 0)
                    this.Close();

            };
            timer.Start();
        }

        private void CreateControls()
        {

            int Y = 10;

            titleLabel = new Label { Text = "Select (resumes in 15 s)", Location = new Point(10, Y), AutoSize = true, ForeColor = Color.LimeGreen };
            this.Controls.Add(titleLabel);
            Y += 30;

            string[] buttonNames = { "Continue", "Reinterpret", "Cancel task" };


            foreach (string buttonName in buttonNames)
            {


                ShinyButton shinyButton = new ShinyButton { ButtonText = buttonName, Bright = true, Location = new Point(10, Y), Width = this.Width - 20 };
                this.Controls.Add(shinyButton);
                // Then update the Close button:
                shinyButton.Click += (s, e) =>
                {
                    SelectedTask = shinyButton.ButtonText;
                    this.Close();
                };

                Y += 50;

            }

        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Color color = Styles.isGreenMode ? Styles.GradientStartGreen : Styles.GradientStartBlue;
            using (var pen = new Pen(color, 2)) // pick your border color & thickness
            {
                // -1 so the border is fully inside the client area
                e.Graphics.DrawRectangle(pen, 1, 1, this.ClientSize.Width - 1, this.ClientSize.Height - 1);
            }
        }

    }

    class CalibrationForm : Form
    {
        private int _width = 500;
        private int _height = 400;

        private CheckBox chkStartingPoint;
        private TextBox txtStartingPoint;

        private CheckBox chkDelta;
        private TextBox txtDelta;

        private CheckBox chkTarget;
        private TextBox txtTarget;

        private CheckBox chkPlasmaIgnited;
        private CheckBox chkGrowthParams;

        // backing fields
        public double _sampleTCStartingPoint = double.NaN;
        public double _sampleDelta = double.NaN;
        public double _sampleTCForTarget = double.NaN;

        public bool Flag_StartingPointKnown = false;
        public bool Flag_SampleDeltaKnown = false;
        public bool Flag_SampleTCForTargetKnown = false;
        public bool Flag_PlasmaIgnited = false;
        public bool Flag_GrowthParametersKnown = false;

        public CalibrationForm()
        {
            this.BackColor = Styles.isDarkMode ? Styles.BackColorDark : Styles.BackColorLight;
            this.ForeColor = Styles.isDarkMode ? Styles.ForeColorDark : Styles.ForeColorLight;
            this.Size = new Size(_width, _height);
            this.ClientSize = new Size(_width, _height);
            this.Padding = new Padding(10);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.None;
            this.Font = new Font(Font.FontFamily, 12);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            CreateControls();
            SetValues();
            BringToFront();
            TopMost = true;
        }

        private void CreateControls()
        {
            int Y = 20;

            Label title = new Label { Text = "Calibration Settings", Location = new Point(10, Y), AutoSize = true, ForeColor = Color.LimeGreen };
            this.Controls.Add(title);
            Y += 40;

            // --- Starting Point ---
            chkStartingPoint = new CheckBox { Text = "Sample TC Starting Point", Location = new Point(10, Y), Width = 250 };
            txtStartingPoint = new TextBox { Location = new Point(270, Y), Width = 150, Enabled = false };
            chkStartingPoint.CheckedChanged += (s, e) =>
            {
                txtStartingPoint.Enabled = chkStartingPoint.Checked;
                if (!chkStartingPoint.Checked) txtStartingPoint.Text = string.Empty;
            };
            this.Controls.Add(chkStartingPoint);
            this.Controls.Add(txtStartingPoint);
            Y += 40;

            // --- Delta ---
            chkDelta = new CheckBox { Text = "Sample Δ", Location = new Point(10, Y), Width = 250 };
            txtDelta = new TextBox { Location = new Point(270, Y), Width = 150, Enabled = false };
            chkDelta.CheckedChanged += (s, e) =>
            {
                txtDelta.Enabled = chkDelta.Checked;
                if (!chkDelta.Checked) txtDelta.Text = string.Empty;
            };
            this.Controls.Add(chkDelta);
            this.Controls.Add(txtDelta);
            Y += 40;

            // --- TC For Target ---
            chkTarget = new CheckBox { Text = "Sample TC For Target", Location = new Point(10, Y), Width = 250 };
            txtTarget = new TextBox { Location = new Point(270, Y), Width = 150, Enabled = false };
            chkTarget.CheckedChanged += (s, e) =>
            {
                txtTarget.Enabled = chkTarget.Checked;
                if (!chkTarget.Checked) txtTarget.Text = string.Empty;
            };
            this.Controls.Add(chkTarget);
            this.Controls.Add(txtTarget);
            Y += 40;

            // --- PlasmaIgnited ---
            chkPlasmaIgnited = new CheckBox { Text = "Plasma Ignited", Location = new Point(10, Y), Width = 250 };
            this.Controls.Add(chkPlasmaIgnited);
            Y += 40;

            // --- Growth Parameters ---
            chkGrowthParams = new CheckBox { Text = "Growth Parameters Known", Location = new Point(10, Y), Width = 250 };
            this.Controls.Add(chkGrowthParams);
            Y += 60;

            // --- Save/Close button ---
            ShinyButton btnClose = new ShinyButton { ButtonText = "Save and Close", Bright = true, Location = new Point(10, Y), Width = this.Width - 40 };
            btnClose.Click += (s, e) =>
            {
                try
                {
                    if (chkStartingPoint.Checked)
                    {
                        _sampleTCStartingPoint = double.TryParse(txtStartingPoint.Text, out var d) ? d : double.NaN;
                        Flag_StartingPointKnown = true;
                    }
                    else { _sampleTCStartingPoint = double.NaN; Flag_StartingPointKnown = false; }

                    if (chkDelta.Checked)
                    {
                        _sampleDelta = double.TryParse(txtDelta.Text, out var d) ? d : double.NaN;
                        Flag_SampleDeltaKnown = true;
                    }
                    else { _sampleDelta = double.NaN; Flag_SampleDeltaKnown = false; }

                    if (chkTarget.Checked)
                    {
                        _sampleTCForTarget = double.TryParse(txtTarget.Text, out var d) ? d : double.NaN;
                        Flag_SampleTCForTargetKnown = true;
                    }
                    else { _sampleTCForTarget = double.NaN; Flag_SampleTCForTargetKnown = false; }

                    Flag_PlasmaIgnited = chkPlasmaIgnited.Checked;
                    Flag_GrowthParametersKnown = chkGrowthParams.Checked;
                }
                catch { /* optional: log */ }

                this.Close();
            };
            this.Controls.Add(btnClose);
        }

        public void SetValues()
        {
            chkStartingPoint.Checked = Flag_StartingPointKnown;
            txtStartingPoint.Text = double.IsNaN(_sampleTCStartingPoint) ? "" : _sampleTCStartingPoint.ToString("F1");

            chkDelta.Checked = Flag_SampleDeltaKnown;
            txtDelta.Text = double.IsNaN(_sampleDelta) ? "" : _sampleDelta.ToString("F1");

            chkTarget.Checked = Flag_SampleTCForTargetKnown;
            txtTarget.Text = double.IsNaN(_sampleTCForTarget) ? "" : _sampleTCForTarget.ToString("F1");

            chkPlasmaIgnited.Checked = Flag_PlasmaIgnited;
            chkGrowthParams.Checked = Flag_GrowthParametersKnown;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Color color = Styles.isGreenMode ? Styles.GradientStartGreen : Styles.GradientStartBlue;
            using (var pen = new Pen(color, 2))
            {
                e.Graphics.DrawRectangle(pen, 1, 1, this.ClientSize.Width - 1, this.ClientSize.Height - 1);
            }
        }
    }

}
