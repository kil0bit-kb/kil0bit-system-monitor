using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Kil0bitSystemMonitor.Helpers
{
    public static class Win32Helper
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string lpszWindow);

        [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern int SetCurrentProcessExplicitAppUserModelID(string AppID);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDesktopWindow();

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        public static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        {
            if (IntPtr.Size == 8)
                return SetWindowLongPtr64(hWnd, nIndex, dwNewLong);
            else
                return new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
        }

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MARGINS
        {
            public int cxLeftWidth;
            public int cxRightWidth;
            public int cyTopHeight;
            public int cyBottomHeight;
        }

        public const int GWL_EXSTYLE = -20;
        public const int GWL_STYLE = -16;
        public const int GWL_HWNDPARENT = -8;

        public const long WS_EX_TOOLWINDOW = 0x00000080L;
        public const long WS_EX_TOPMOST = 0x00000008L;
        public const long WS_EX_TRANSPARENT = 0x00000020L;
        public const long WS_EX_LAYERED = 0x00080000L;
        public const long WS_EX_NOACTIVATE = 0x08000000L;

        public const int WS_POPUP = unchecked((int)0x80000000);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint SW_SHOW = 5;

        [DllImport("user32.dll")]
        public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        public const uint LWA_COLORKEY = 0x00000001;
        public const uint LWA_ALPHA = 0x00000002;

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr LoadImage(IntPtr hinst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        public const uint WM_SETICON = 0x0080;
        public const int ICON_BIG = 1;
        public const int ICON_SMALL = 0;
        public const uint IMAGE_ICON = 1;
        public const uint LR_LOADFROMFILE = 0x0010;

        /// <summary>
        /// Forces a window to use a custom icon by loading a PNG/ICO at runtime.
        /// </summary>
        public static void SetAppIcon(IntPtr hWnd, string imagePath)
        {
            if (string.IsNullOrEmpty(imagePath) || !System.IO.File.Exists(imagePath)) return;

            try
            {
                // If it's a PNG, we must convert at runtime to get a valid HICON
                if (imagePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                {
                    using (var bitmap = new System.Drawing.Bitmap(imagePath))
                    {
                        IntPtr hIcon = bitmap.GetHicon();
                        if (hIcon != IntPtr.Zero)
                        {
                            SendMessage(hWnd, WM_SETICON, (IntPtr)ICON_BIG, hIcon);
                            SendMessage(hWnd, WM_SETICON, (IntPtr)ICON_SMALL, hIcon);
                            // Do NOT DestroyIcon(hIcon) here; the window needs it for the taskbar icon.
                            // It will leak 1-2 handles per application window, but this is necessary for WinUI 3 compatibility.
                        }
                    }
                }
                else if (imagePath.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                {
                    IntPtr hIcon = LoadImage(IntPtr.Zero, imagePath, IMAGE_ICON, 32, 32, LR_LOADFROMFILE);
                    IntPtr hIconSm = LoadImage(IntPtr.Zero, imagePath, IMAGE_ICON, 16, 16, LR_LOADFROMFILE);
                    if (hIcon != IntPtr.Zero) SendMessage(hWnd, WM_SETICON, (IntPtr)ICON_BIG, hIcon);
                    if (hIconSm != IntPtr.Zero) SendMessage(hWnd, WM_SETICON, (IntPtr)ICON_SMALL, hIconSm);
                }
            }
            catch { }
        }

        [DllImport("dwmapi.dll")]
        public static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarInset);

        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        public const int DWMWA_BORDER_COLOR = 34;
        public const int DWMWA_CAPTION_COLOR = 35;
        public const int DWMWA_TEXT_COLOR = 36;
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

        public const int DWMWCP_DONOTROUND = 1;
        public const int DWMWCP_ROUND = 2;
        public const int DWMWCP_ROUNDSMALL = 3;

        public const int DWMSBT_NONE = 1;
        public const int DWMSBT_MAINWINDOW = 2;
        public const int DWMSBT_TRANSIENTWINDOW = 3;
        public const int DWMSBT_TABBEDWINDOW = 4;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        public const int SW_RESTORE = 9;

        public const uint MB_YESNO = 0x00000004;
        public const uint MB_ICONQUESTION = 0x00000020;
        public const int IDYES = 6;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr hIcon);
    }
}
