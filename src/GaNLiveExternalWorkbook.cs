using System;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using UnicornOne.Abstractions.Workbook;
using UnicornOne.ExternalWorkbooks.GaNLive;

namespace UnicornOne.ExternalWorkbooks
{
    /// <summary>
    /// This DLL contains copies of the real GaNLive form and calibration manager.
    /// Only shared RHEED/UI controls are currently consumed from UnicornOne.Core.
    /// The existing built-in workbook is not replaced or altered.
    /// </summary>
    public sealed class GaNLiveExternalWorkbook : IWorkbookPlugin, IWorkbookController
    {
        private IWorkbookHost _host;
        private GaNLiveForm _view;
        private bool _disposed;

        public string Name { get { return "GaN Calibrate Live (External)"; } }
        public string Version { get { return "1.0.0"; } }

        public void Initialize(string jsonConfig, IWorkbookHost host)
        {
            if (host == null) throw new ArgumentNullException(nameof(host));
            _host = host;

            JObject settings = JObject.Parse(string.IsNullOrWhiteSpace(jsonConfig) ? "{}" : jsonConfig);
            _view = new GaNLiveForm();
            _view.WorkbookName = (string)settings["WorkbookName"] ?? "GaN Calibrate Live";
            _view.RunMode = (string)settings["RunMode"] ?? "Full";

            // A nested form is an ordinary WinForms Control once TopLevel is false.
            // This permits the complete legacy GaNLive layout, including its own
            // RHEED widget, message RichTextBox and menus, to live in the plugin tab.
            _view.TopLevel = false;
            _view.FormBorderStyle = FormBorderStyle.None;
            _view.Dock = DockStyle.Fill;
            _view.FormClosed += OnViewClosed;
        }

        public void BuildUI()
        {
            if (_host == null || _view == null)
                throw new InvalidOperationException("Initialize must be called before BuildUI.");

            _host.AddWidget(_view, "Main");
            _host.SetStatus("GaNLive external workbook loaded. Run in simulation first.");
        }

        public void OnShown()
        {
            if (_view != null && !_view.IsDisposed && !_view.Visible)
                _view.Show();  // Triggers GaNLiveForm.OnShown (normal reset/config check).
        }

        public void OnHidden() { }

        public void Run()
        {
            if (_view == null || _view.IsDisposed)
                throw new InvalidOperationException("GaNLive view is unavailable.");
            if (!_view.Visible)
                throw new InvalidOperationException("Show the GaNLive workbook before running.");
            _view.AutoRun();
        }

        public void RequestCancel()
        {
            if (_view != null && !_view.IsDisposed)
                _view.AutoCancel();
        }

        private void OnViewClosed(object sender, FormClosedEventArgs e)
        {
            // The original GaNLive.AutoRun closes its own form after completion.
            // Tell the host/coordinator that the workbook has completed.
            if (!_disposed && _host != null)
                _host.MarkComplete();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_view != null)
            {
                _view.FormClosed -= OnViewClosed;
                if (!_view.IsDisposed)
                {
                    _view.AutoCancel();
                    _view.Dispose();
                }
                _view = null;
            }
            _host = null;
        }
    }
}
