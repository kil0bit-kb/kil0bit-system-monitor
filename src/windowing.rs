use windows::Win32::Foundation::{HWND, RECT, POINT, LRESULT, WPARAM, LPARAM};
use windows::Win32::UI::WindowsAndMessaging::{
    FindWindowW, GetWindowRect, SetWindowPos, SWP_NOSIZE, HWND_TOPMOST, SWP_SHOWWINDOW,
    GetWindowLongPtrW, SetWindowLongPtrW, GWL_EXSTYLE, WS_EX_TOOLWINDOW, WS_EX_NOACTIVATE,
    WS_EX_APPWINDOW, SWP_FRAMECHANGED, SWP_NOACTIVATE, SetParent,
    GWL_STYLE, WS_POPUP, WS_CAPTION, WS_CHILD, SWP_NOMOVE, SWP_NOZORDER, WS_THICKFRAME,
    WS_MINIMIZEBOX, WS_MAXIMIZEBOX, WS_SYSMENU, WS_EX_LAYERED,
    CreatePopupMenu, AppendMenuW, TrackPopupMenu, GetCursorPos, DestroyMenu,
    TPM_RETURNCMD, TPM_NONOTIFY, TPM_LEFTALIGN, TPM_TOPALIGN, MF_STRING, MF_CHECKED, MF_UNCHECKED,
    SetForegroundWindow, WS_BORDER, WS_DLGFRAME, WS_EX_CLIENTEDGE, WS_EX_STATICEDGE,
    WS_EX_WINDOWEDGE, WS_EX_DLGMODALFRAME, SetClassLongPtrW, GCLP_HBRBACKGROUND,
    GetForegroundWindow, GetDesktopWindow, GetShellWindow, GetClassNameW,
    GWLP_WNDPROC, CallWindowProcW, WNDPROC, WM_NCACTIVATE, WM_NCPAINT, WM_SETTEXT,
    DefWindowProcW, GWLP_HWNDPARENT, SetWindowTextW,
};
use windows::Win32::UI::Controls::MARGINS;
use windows::Win32::Graphics::Gdi::{
    MonitorFromWindow, GetMonitorInfoW, MONITORINFO, MONITOR_DEFAULTTONEAREST,
    CreateRectRgn, CreateRoundRectRgn, CombineRgn, DeleteObject, SetWindowRgn, HRGN, RGN_DIFF,
};

use windows::Win32::Graphics::Dwm::{
    DwmSetWindowAttribute, DwmExtendFrameIntoClientArea,
    DWMWA_SYSTEMBACKDROP_TYPE, DWMWA_NCRENDERING_POLICY, DWMWA_WINDOW_CORNER_PREFERENCE,
    DWMWA_BORDER_COLOR, DWMWA_TRANSITIONS_FORCEDISABLED, DWMWA_USE_IMMERSIVE_DARK_MODE,
    DWMWA_EXCLUDED_FROM_PEEK, DWMWA_DISALLOW_PEEK,
};
use windows::core::w;




/// Punch a transparent hole in the taskbar's window region exactly where the overlay sits.
/// The taskbar (and its DComp bridge) won't paint in this region, making the overlay visible.
pub fn punch_hole_in_taskbar(overlay_hwnd: *mut std::ffi::c_void) {
    unsafe {
        let overlay = HWND(overlay_hwnd as *mut _);

        let taskbar = match FindWindowW(w!("Shell_TrayWnd"), None) {
            Ok(h) if !h.0.is_null() => h,
            _ => return,
        };

        let mut tb_rect = RECT::default();
        if GetWindowRect(taskbar, &mut tb_rect).is_err() { return; }

        let mut ov_rect = RECT::default();
        if GetWindowRect(overlay, &mut ov_rect).is_err() { return; }

        let tb_w = tb_rect.right  - tb_rect.left;
        let tb_h = tb_rect.bottom - tb_rect.top;

        // Inset the hole to match the pill background (6px h, 4px v from window edge)
        let h_inset = 0i32;
        let v_inset = 0i32;

        // Convert overlay coords to taskbar-local coords, with pill inset applied
        let hole_l = (ov_rect.left   - tb_rect.left) + h_inset;
        let hole_t = (ov_rect.top    - tb_rect.top)  + v_inset;
        let hole_r = (ov_rect.right  - tb_rect.left) - h_inset;
        let hole_b = (ov_rect.bottom - tb_rect.top)  - v_inset;

        // Full taskbar region
        let full_rgn: HRGN = CreateRectRgn(0, 0, tb_w, tb_h);
        // Rounded hole region — 16x16 ellipse = 8px effective border-radius, matching CSS
        let hole_rgn: HRGN = CreateRoundRectRgn(hole_l, hole_t, hole_r, hole_b, 16, 16);

        // Subtract hole from full region
        CombineRgn(Some(full_rgn), Some(full_rgn), Some(hole_rgn), RGN_DIFF);

        // Apply — SetWindowRgn takes ownership of full_rgn, do NOT DeleteObject it
        SetWindowRgn(taskbar, Some(full_rgn), true);

        // We own hole_rgn, clean it up
        let _ = DeleteObject(hole_rgn.into());
    }
}

/// Restore the taskbar to full painting (remove the hole).
pub fn restore_taskbar_region() {
    unsafe {
        if let Ok(taskbar) = FindWindowW(w!("Shell_TrayWnd"), None) {
            if !taskbar.0.is_null() {
                // None = no clipping = paint entire window
                SetWindowRgn(taskbar, None, true);
            }
        }
    }
}


static mut OLD_WNDPROC: isize = 0;

unsafe extern "system" fn overlay_wndproc(
    hwnd: HWND, msg: u32, wparam: WPARAM, lparam: LPARAM,
) -> LRESULT {
    match msg {
        WM_NCACTIVATE => return LRESULT(1),
        WM_NCPAINT    => return LRESULT(0),
        WM_SETTEXT    => return LRESULT(0),
        _ => {}
    }
    if OLD_WNDPROC != 0 {
        let old: WNDPROC = std::mem::transmute(OLD_WNDPROC);
        CallWindowProcW(old, hwnd, msg, wparam, lparam)
    } else {
        DefWindowProcW(hwnd, msg, wparam, lparam)
    }
}

// ── Mica effect ──────────────────────────────────────────────────────────────
pub fn apply_mica_effect(window_handle: *mut std::ffi::c_void) {
    unsafe {
        let hwnd = HWND(window_handle as *mut _);
        let backdrop: i32 = 2; // DWMSBT_MAINWINDOW (Mica)
        let _ = DwmSetWindowAttribute(
            hwnd, DWMWA_SYSTEMBACKDROP_TYPE,
            &backdrop as *const i32 as *const _, std::mem::size_of::<i32>() as u32,
        );
    }
}

pub fn remove_mica_effect(window_handle: *mut std::ffi::c_void) {
    unsafe {
        let hwnd = HWND(window_handle as *mut _);
        let backdrop: i32 = 1; // None
        let _ = DwmSetWindowAttribute(
            hwnd, DWMWA_SYSTEMBACKDROP_TYPE,
            &backdrop as *const i32 as *const _, std::mem::size_of::<i32>() as u32,
        );
    }
}

// ── Move overlay (screen coords from Slint) ──────────────────────────────────
pub fn move_window(window_handle: *mut std::ffi::c_void, x: i32, y: i32) {
    unsafe {
        let hwnd = HWND(window_handle as *mut _);
        let _ = SetWindowPos(hwnd, None, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOZORDER);
    }
}

pub fn center_window(window_handle: *mut std::ffi::c_void) {
    unsafe {
        let hwnd = HWND(window_handle as *mut _);
        let monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        let mut mi = MONITORINFO {
            cbSize: std::mem::size_of::<MONITORINFO>() as u32,
            ..Default::default()
        };
        if GetMonitorInfoW(monitor, &mut mi).into() {
            let mut rect = RECT::default();
            if GetWindowRect(hwnd, &mut rect).is_ok() {
                let win_w = rect.right - rect.left;
                let win_h = rect.bottom - rect.top;
                let mon_w = mi.rcWork.right - mi.rcWork.left;
                let mon_h = mi.rcWork.bottom - mi.rcWork.top;
                
                let x = mi.rcWork.left + (mon_w - win_w) / 2;
                let y = mi.rcWork.top + (mon_h - win_h) / 2;
                
                let _ = SetWindowPos(hwnd, None, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            }
        }
    }
}

// ── Snap overlay to vertical centre of taskbar ───────────────────────────────
pub fn snap_to_taskbar(window_handle: *mut std::ffi::c_void) {
    unsafe {
        let my_hwnd = HWND(window_handle as *mut _);

        let taskbar = match FindWindowW(w!("Shell_TrayWnd"), None) {
            Ok(h) if !h.0.is_null() => h,
            _ => return,
        };

        let mut tb_rect = RECT::default();
        if GetWindowRect(taskbar, &mut tb_rect).is_err() { return; }

        let mut my_rect = RECT::default();
        if GetWindowRect(my_hwnd, &mut my_rect).is_err() { return; }

        let my_h     = my_rect.bottom - my_rect.top;
        let snap_y   = tb_rect.top + (tb_rect.bottom - tb_rect.top) / 2 - my_h / 2;
        let current_y = my_rect.top;

        if current_y != snap_y {
            let _ = SetWindowPos(
                my_hwnd, None,
                my_rect.left, snap_y, 0, 0,
                SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOZORDER,
            );
        }
    }
}

// ── Apply all overlay window tricks (standalone topmost approach) ─────────────
pub fn anchor_to_taskbar_owned(window_handle: *mut std::ffi::c_void) {
    unsafe {
        let hwnd = HWND(window_handle as *mut _);

        // Detach from any parent
        let _ = SetWindowLongPtrW(hwnd, GWLP_HWNDPARENT, 0);
        let _ = SetParent(hwnd, None);

        // Install WNDPROC hook to suppress title-bar flicker
        let cur = GetWindowLongPtrW(hwnd, GWLP_WNDPROC);
        if cur != overlay_wndproc as *const () as isize {
            OLD_WNDPROC = cur;
            let _ = SetWindowLongPtrW(hwnd, GWLP_WNDPROC, overlay_wndproc as *const () as isize);
        }

        // Styles: bare WS_POPUP, no decorations
        let style      = GetWindowLongPtrW(hwnd, GWL_STYLE);
        let bad_style  = WS_CAPTION.0 | WS_CHILD.0 | WS_THICKFRAME.0 | WS_MINIMIZEBOX.0
                       | WS_MAXIMIZEBOX.0 | WS_SYSMENU.0 | WS_BORDER.0 | WS_DLGFRAME.0;
        let new_style  = (style & !(bad_style as isize)) | WS_POPUP.0 as isize;
        let _ = SetWindowLongPtrW(hwnd, GWL_STYLE, new_style);

        // Extended styles: toolwindow + noactivate + layered
        let ex         = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
        let bad_ex     = WS_EX_APPWINDOW.0 | WS_EX_DLGMODALFRAME.0 | WS_EX_CLIENTEDGE.0
                       | WS_EX_STATICEDGE.0 | WS_EX_WINDOWEDGE.0;
        let new_ex     = (ex & !(bad_ex as isize))
                       | WS_EX_TOOLWINDOW.0 as isize
                       | WS_EX_NOACTIVATE.0 as isize
                       | WS_EX_LAYERED.0 as isize;
        let _ = SetWindowLongPtrW(hwnd, GWL_EXSTYLE, new_ex);

        // Clear title and background brush
        let _ = SetWindowTextW(hwnd, w!(""));
        let _ = SetClassLongPtrW(hwnd, GCLP_HBRBACKGROUND, 0);

        // Extend DWM frame to cover entire client area (allow alpha)
        let margins = MARGINS { cxLeftWidth: -1, cxRightWidth: -1, cyTopHeight: -1, cyBottomHeight: -1 };
        let _ = DwmExtendFrameIntoClientArea(hwnd, &margins);

        // Disable NC rendering
        let policy: i32 = 1;
        let _ = DwmSetWindowAttribute(hwnd, DWMWA_NCRENDERING_POLICY,
            &policy as *const i32 as *const _, std::mem::size_of::<i32>() as u32);

        // No round corners
        let corner: i32 = 1;
        let _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE,
            &corner as *const i32 as *const _, std::mem::size_of::<i32>() as u32);

        // No border
        let border: u32 = 0xFFFFFFFE;
        let _ = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR,
            &border as *const u32 as *const _, std::mem::size_of::<u32>() as u32);

        // No launch animation
        let no_anim: i32 = 1;
        let _ = DwmSetWindowAttribute(hwnd, DWMWA_TRANSITIONS_FORCEDISABLED,
            &no_anim as *const i32 as *const _, std::mem::size_of::<i32>() as u32);

        // Exclude from Peek / Alt+Tab ghost
        let excl: i32 = 1;
        let _ = DwmSetWindowAttribute(hwnd, DWMWA_EXCLUDED_FROM_PEEK,
            &excl as *const i32 as *const _, std::mem::size_of::<i32>() as u32);
        let _ = DwmSetWindowAttribute(hwnd, DWMWA_DISALLOW_PEEK,
            &excl as *const i32 as *const _, std::mem::size_of::<i32>() as u32);

        // Dark mode so DWM doesn't flash a white border
        let dark: i32 = 1;
        let _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE,
            &dark as *const i32 as *const _, std::mem::size_of::<i32>() as u32);

        // No system backdrop
        let backdrop: i32 = 1;
        let _ = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE,
            &backdrop as *const i32 as *const _, std::mem::size_of::<i32>() as u32);

        // Place window TOPMOST
        let _ = SetWindowPos(hwnd, Some(HWND_TOPMOST), 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
    }
}

// ── Fullscreen detection ──────────────────────────────────────────────────────
pub fn is_foreground_fullscreen() -> bool {
    unsafe {
        let hwnd = GetForegroundWindow();
        if hwnd.0.is_null() { return false; }
        if hwnd == GetDesktopWindow() || hwnd == GetShellWindow() { return false; }

        let mut cls = [0u16; 256];
        let len = GetClassNameW(hwnd, &mut cls);
        if len > 0 {
            let name = String::from_utf16_lossy(&cls[..len as usize]);
            if name.starts_with("WorkerW") || name.starts_with("Progman") {
                return false;
            }
        }

        let mut rect = RECT::default();
        if GetWindowRect(hwnd, &mut rect).is_ok() {
            let monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            let mut mi = MONITORINFO {
                cbSize: std::mem::size_of::<MONITORINFO>() as u32,
                ..Default::default()
            };
            if GetMonitorInfoW(monitor, &mut mi).into() {
                let m = mi.rcMonitor;
                if rect.left <= m.left && rect.right >= m.right
                    && rect.top <= m.top && rect.bottom >= m.bottom {
                    return true;
                }
            }
        }
        false
    }
}

// ── Z-order manager (called every 100 ms) ────────────────────────────────────
static mut LAST_FS: bool = false;

pub fn manage_z_order(window_handle: *mut std::ffi::c_void, is_fullscreen: bool) {
    unsafe {
        let hwnd = HWND(window_handle as *mut _);

        if is_fullscreen {
            // Only push behind once when entering fullscreen
            if !LAST_FS {
                LAST_FS = true;
                let _ = SetWindowPos(hwnd,
                    Some(windows::Win32::UI::WindowsAndMessaging::HWND_BOTTOM),
                    0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE,
                );
            }
        } else {
            LAST_FS = false;

            // Detect if the taskbar or Start shell is currently active 
            // (the moment where the overlay gets pushed behind)
            let fg = GetForegroundWindow();
            let mut cls = [0u16; 256];
            let len = GetClassNameW(fg, &mut cls);
            let fg_is_taskbar = if len > 0 {
                let name = String::from_utf16_lossy(&cls[..len as usize]);
                name.starts_with("Shell_TrayWnd")
                    || name.starts_with("Windows.UI.Core.CoreWindow")
                    || name.starts_with("Shell_Lightweight")
            } else {
                false
            };

            // Always assert TOPMOST, without optimization flags, so DWM actually applies it.
            // When triggered by taskbar/Start activity, fire it twice for good measure.
            let flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE;
            let _ = SetWindowPos(hwnd, Some(HWND_TOPMOST), 0, 0, 0, 0, flags);
            if fg_is_taskbar {
                // Second call forces compositor to re-evaluate immediately
                let _ = SetWindowPos(hwnd, Some(HWND_TOPMOST), 0, 0, 0, 0, flags | SWP_FRAMECHANGED);
            }
        }
    }
}

// ── Dark Mode Context Menus ──────────────────────────────────────────────────
pub fn set_dark_mode_for_app() {
    unsafe {
        use windows::Win32::System::LibraryLoader::{GetModuleHandleW, GetProcAddress};
        use windows::core::PCSTR;

        if let Ok(uxtheme) = GetModuleHandleW(w!("uxtheme.dll")) {
            // Ordinal 135: SetPreferredAppMode
            // 0 = Default, 1 = AllowDark, 2 = ForceDark, 3 = ForceLight, 4 = Max
            if let Some(set_preferred_app_mode) = GetProcAddress(uxtheme, PCSTR(135 as *const u8)) {
                let func: extern "system" fn(i32) -> i32 = std::mem::transmute(set_preferred_app_mode);
                func(2); // ForceDark
            }

            // Ordinal 136: FlushMenuThemes
            if let Some(flush_menu_themes) = GetProcAddress(uxtheme, PCSTR(136 as *const u8)) {
                let func: extern "system" fn() = std::mem::transmute(flush_menu_themes);
                func();
            }
        }
    }
}

// ── Context menu ─────────────────────────────────────────────────────────────
pub fn show_overlay_context_menu(hwnd: *mut std::ffi::c_void, is_locked: bool) -> u32 {
    unsafe {
        let hwnd = HWND(hwnd as *mut _);
        let hmenu = CreatePopupMenu().unwrap_or_default();
        if hmenu.is_invalid() { return 0; }

        let _ = AppendMenuW(hmenu, MF_STRING, 1, w!("Settings"));
        let _ = AppendMenuW(hmenu, MF_STRING, 2, w!("Task Manager"));
        let lock_flag = if is_locked { MF_STRING | MF_CHECKED } else { MF_STRING | MF_UNCHECKED };
        let _ = AppendMenuW(hmenu, lock_flag, 3, w!("Lock Position"));
        let _ = AppendMenuW(hmenu, windows::Win32::UI::WindowsAndMessaging::MF_SEPARATOR, 0, None);
        let _ = AppendMenuW(hmenu, MF_STRING, 4, w!("About"));

        let mut pt = POINT::default();
        let _ = GetCursorPos(&mut pt);
        let _ = SetForegroundWindow(hwnd);

        let cmd = TrackPopupMenu(
            hmenu,
            TPM_RETURNCMD | TPM_NONOTIFY | TPM_LEFTALIGN | TPM_TOPALIGN,
            pt.x, pt.y,
            Some(0), hwnd, None,
        );
        let _ = DestroyMenu(hmenu);
        cmd.0 as u32
    }
}
