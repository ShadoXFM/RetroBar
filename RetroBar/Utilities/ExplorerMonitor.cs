using ManagedShell;
using ManagedShell.Common.Helpers;
using ManagedShell.Common.Logging;
using ManagedShell.Interop;
using System;
using System.Windows.Forms;
using System.Windows.Threading;

namespace RetroBar.Utilities
{
    public class ExplorerMonitor : IDisposable
    {
        private ExplorerMonitorWindow _explorerMonitorWindow;

        public void ExplorerMonitorStart(WindowManager windowManagerRef, ShellManager shellManager)
        {
            if (_explorerMonitorWindow != null) { return; } // Prevent multiple monitors.

            _explorerMonitorWindow = new ExplorerMonitorWindow(windowManagerRef, shellManager); // Start monitoring.
        }

        public void Dispose()
        {
            _explorerMonitorWindow?.Dispose();
        }

        private class ExplorerMonitorWindow : NativeWindow, IDisposable
        {
            private readonly ShellManager _shellManager;
            private readonly WindowManager _windowManagerRef;
            private static readonly int WM_TASKBARCREATEDMESSAGE = NativeMethods.RegisterWindowMessage("TaskbarCreated");

            public ExplorerMonitorWindow(WindowManager windowManagerRef, ShellManager shellManager)
            {
                _shellManager = shellManager;
                _windowManagerRef = windowManagerRef;
                CreateHandle(new CreateParams());
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_TASKBARCREATEDMESSAGE)
                {
                    Dispatcher.CurrentDispatcher.BeginInvoke(() => {
                        try
                        {
                            if (EnvironmentHelper.IsAppRunningAsShell)
                            {
                                // Re-evaluate shell status: some apps may delay the Shell process startup.
                                EnvironmentHelper.IsAppRunningAsShell = NativeMethods.GetShellWindow() == IntPtr.Zero;
                                // Trigger the hide taskbar logic again in case shell status has changed
                                _shellManager.ExplorerHelper.HideExplorerTaskbar = true;
                            }
                            else
                            {
                                // The Explorer that has started again has a taskbar of its own, which is not hidden (and reserves its strip of the
                                // screen, so RetroBar's bar was put above it, with a gap under it): hidden again, as it was at the start, and the
                                // bars opened when it has let go of the strip.
                                _shellManager.ExplorerHelper.HideExplorerTaskbar = false;
                                _shellManager.ExplorerHelper.HideExplorerTaskbar = true;
                                var reopen = new DispatcherTimer { Interval = System.TimeSpan.FromSeconds(2) };
                                reopen.Tick += (s, e) =>
                                {
                                    reopen.Stop();
                                    _windowManagerRef.ReopenTaskbars();
                                };
                                reopen.Start();
                            }
                            // Reopen taskbars if explorer.exe is restarted.
                            _windowManagerRef.ReopenTaskbars();
                            // Re-initialize the tasks service to prevent leftover File Explorer windows.
                            _shellManager.Tasks.Dispose();
                            _shellManager.Tasks.Initialize(true);
                        }
                        catch (Exception ex)
                        {
                            ShellLogger.Warning($"Error handling TaskbarCreated message on ExplorerMonitor: {ex.Message}");
                        }
                    });
                }

                base.WndProc(ref m); // Call the base class to process other messages so we dont accidentally cause crashes or bugs.
            }

            public void Dispose()
            {
                DestroyHandle();
            }
        }
    }
}
