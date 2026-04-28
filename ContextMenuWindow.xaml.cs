using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Kil0bitSystemMonitor.ViewModels;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Helpers;

namespace Kil0bitSystemMonitor
{
    public partial class ContextMenuWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private readonly ConfigService _config;

        public ContextMenuWindow(MainViewModel viewModel, ConfigService config)
        {
            InitializeComponent();
            _viewModel = viewModel;
            _config = config;
            
            this.Loaded += (s, e) => 
            {
                var hWnd = new WindowInteropHelper(this).Handle;
                int exStyle = Win32Helper.GetWindowLong(hWnd, Win32Helper.GWL_EXSTYLE);
                Win32Helper.SetWindowLongPtr(hWnd, Win32Helper.GWL_EXSTYLE, (IntPtr)(exStyle | 0x00000080)); // WS_EX_TOOLWINDOW
            };
        }

        public void ShowContextMenu(int screenX, int screenY)
        {
            this.Left = screenX;
            this.Top = screenY;
            this.Show();

            var menu = new ContextMenu();

            var settingsItem = new MenuItem { Header = "Settings" };
            settingsItem.Click += (s, e) =>
            {
                App.OpenSettings(_viewModel, _config);
                this.Close();
            };

            var taskMgrItem = new MenuItem { Header = "Task Manager" };
            taskMgrItem.Click += (s, e) =>
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("taskmgr") { UseShellExecute = true });
                this.Close();
            };

            var aboutItem = new MenuItem { Header = "About" };
            aboutItem.Click += (s, e) =>
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/kil0bit-kb/kil0bit-system-monitor") { UseShellExecute = true });
                this.Close();
            };

            var exitItem = new MenuItem { Header = "Exit" };
            exitItem.Click += (s, e) => App.Quit();

            menu.Items.Add(settingsItem);
            menu.Items.Add(taskMgrItem);
            menu.Items.Add(new Separator());
            menu.Items.Add(aboutItem);
            menu.Items.Add(new Separator());
            menu.Items.Add(exitItem);

            menu.Closed += (s, e) => this.Close();

            // Open the context menu
            this.ContextMenu = menu;
            menu.IsOpen = true;
        }
    }
}
