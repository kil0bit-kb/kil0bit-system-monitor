#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]
slint::include_modules!();
use slint::{ComponentHandle, CloseRequestResponse};

mod windowing;
mod config;

use std::sync::{Arc, Mutex};
use std::time::Duration;
use sysinfo::Networks;
use nvml_wrapper::Nvml; // Added Nvml import
use image::GenericImageView;

struct AppState {
    app_ui: Option<AppWindow>,
    overlay_ui: slint::Weak<OverlayWindow>,
}
fn main() -> Result<(), slint::PlatformError> {
    let args: Vec<String> = std::env::args().collect();
    let is_autostart = args.iter().any(|arg| arg == "--autostart");

    windowing::set_dark_mode_for_app();

    let overlay_ui = OverlayWindow::new()?;
    let overlay_handle = overlay_ui.as_weak();

    // ── Load Initial Config ──────────────────────────────────────────────────
    let config = Arc::new(Mutex::new(config::AppConfig::load()));

    // Create App State (UI Thread Only)
    let app_state = std::rc::Rc::new(std::cell::RefCell::new(AppState {
        app_ui: None,
        overlay_ui: overlay_handle.clone(),
    }));

    // Sync OverlayWindow
    {
        let cfg = config.lock().unwrap();
        overlay_ui.set_show_cpu(cfg.show_cpu);
        overlay_ui.set_show_mem(cfg.show_mem);
        overlay_ui.set_show_gpu(cfg.show_gpu);
        overlay_ui.set_show_temp_gpu(cfg.show_temp_gpu);
        overlay_ui.set_show_net_up(cfg.show_net_up);
        overlay_ui.set_show_net_down(cfg.show_net_down);
        overlay_ui.set_font_family(cfg.font_family.clone().into());
        overlay_ui.set_display_variant(cfg.display_variant.clone().into());
        overlay_ui.set_locked_drag(cfg.locked_drag);
        overlay_ui.set_bg_opacity(cfg.bg_opacity);

        if let Ok(color) = parse_hex_color(&cfg.text_color_hex) {
            overlay_ui.set_text_color(color);
        }
        if let Ok(bg) = parse_hex_color(&cfg.bg_color_hex) {
            overlay_ui.set_bg_color(bg);
        }

        // Set Saved Position
        overlay_ui.window().set_position(slint::LogicalPosition::new(cfg.overlay_x as f32, cfg.overlay_y as f32));

        // Registry Robustness: If auto_start is enabled, ensure the registry key is up-to-date with --autostart
        if cfg.auto_start {
            set_auto_start(true);
        }
    }

    // Initialize System Tray
    let tray_menu = tray_icon::menu::Menu::new();
    let settings_item = tray_icon::menu::MenuItem::new("Settings", true, None);
    let task_mgr_item = tray_icon::menu::MenuItem::new("Task Manager", true, None);
    let lock_item = {
        let cfg = config.lock().unwrap();
        tray_icon::menu::CheckMenuItem::new("Lock Position", true, cfg.locked_drag, None)
    };
    let about_item = tray_icon::menu::MenuItem::new("About", true, None);
    let exit_item = tray_icon::menu::MenuItem::new("Exit", true, None);

    let _ = tray_menu.append(&settings_item);
    let _ = tray_menu.append(&task_mgr_item);
    let _ = tray_menu.append(&lock_item);
    let _ = tray_menu.append(&tray_icon::menu::PredefinedMenuItem::separator());
    let _ = tray_menu.append(&about_item);
    let _ = tray_menu.append(&exit_item);

    let icon_bytes = include_bytes!("../ui/assets/icon.png");
    let icon = {
        let image = image::load_from_memory(icon_bytes).expect("Failed to open embedded icon");
        let (width, height) = image.dimensions();
        let rgba = image.to_rgba8().into_raw();
        tray_icon::Icon::from_rgba(rgba, width, height).ok()
    };

    let mut tray_builder = tray_icon::TrayIconBuilder::new()
        .with_tooltip("kil0bit System Monitor")
        .with_menu(Box::new(tray_menu));
    
    if let Some(i) = icon {
        tray_builder = tray_builder.with_icon(i);
    }

    let _tray_icon = tray_builder.build().unwrap();

    // Immediately extract the HWND for OverlayWindow
    use raw_window_handle::HasWindowHandle;
    let slint_handle = overlay_ui.window().window_handle();
    if let Ok(rwh) = slint_handle.window_handle() {
        if let raw_window_handle::RawWindowHandle::Win32(win32) = rwh.as_raw() {
            let hwnd = win32.hwnd.get() as *mut std::ffi::c_void;
            windowing::remove_mica_effect(hwnd);
            windowing::anchor_to_taskbar_owned(hwnd);
        }
    }

    // Wire up Overlay Dragging
    overlay_ui.on_window_moved({
        let overlay_handle = overlay_handle.clone();
        move |offset_x, offset_y| {
            if let Some(ui) = overlay_handle.upgrade() {
                let current_pos = ui.window().position();
                let scale = ui.window().scale_factor();
                
                let slint_handle = ui.window().window_handle();
                if let Ok(rwh) = slint_handle.window_handle() {
                    if let raw_window_handle::RawWindowHandle::Win32(win32) = rwh.as_raw() {
                        let hwnd = win32.hwnd.get() as *mut std::ffi::c_void;
                        let new_x = current_pos.x + (offset_x * scale) as i32;
                        let new_y = current_pos.y + (offset_y * scale) as i32;
                        windowing::move_window(hwnd, new_x, new_y);
                        windowing::punch_hole_in_taskbar(hwnd);
                    }
                }
            }
        }
    });

    let app_state_drop = app_state.clone();
    let config_drop = config.clone();
    overlay_ui.on_window_dropped(move || {
        let s = app_state_drop.borrow();
        if let Some(ui) = s.overlay_ui.upgrade() {
            use raw_window_handle::HasWindowHandle;
            let slint_handle = ui.window().window_handle();
            if let Ok(rwh) = slint_handle.window_handle() {
                if let raw_window_handle::RawWindowHandle::Win32(win32) = rwh.as_raw() {
                    let hwnd = win32.hwnd.get() as *mut std::ffi::c_void;
                    windowing::snap_to_taskbar(hwnd);
                    windowing::punch_hole_in_taskbar(hwnd);

                    // Save new position
                    let pos = ui.window().position();
                    let mut cfg = config_drop.lock().unwrap();
                    cfg.overlay_x = pos.x;
                    cfg.overlay_y = pos.y;
                    cfg.save();
                }
            }
        }
    });

    let app_state_rc = app_state.clone();
    let config_rc = config.clone();
    overlay_ui.on_right_clicked(move || {
        let s = app_state_rc.borrow();
        if let Some(ui) = s.overlay_ui.upgrade() {
            use raw_window_handle::HasWindowHandle;
            let slint_handle = ui.window().window_handle();
            if let Ok(rwh) = slint_handle.window_handle() {
                if let raw_window_handle::RawWindowHandle::Win32(win32) = rwh.as_raw() {
                    let hwnd = win32.hwnd.get() as *mut std::ffi::c_void;
                    let is_locked = config_rc.lock().unwrap().locked_drag;
                    drop(s);
                    let choice = windowing::show_overlay_context_menu(hwnd, is_locked);
                    
                    match choice {
                        1 => show_settings_ui(app_state_rc.clone(), config_rc.clone(), "Settings"),
                        2 => { let _ = std::process::Command::new("taskmgr.exe").spawn(); }
                        3 => {
                            let mut cfg = config_rc.lock().unwrap();
                            let next = !cfg.locked_drag;
                            cfg.locked_drag = next;
                            let s_mut = app_state_rc.borrow();
                            if let Some(ov) = s_mut.overlay_ui.upgrade() { ov.set_locked_drag(next); }
                            if let Some(app) = &s_mut.app_ui {
                                app.set_locked_drag(next);
                            }
                            cfg.save();
                        }
                        4 => show_settings_ui(app_state_rc.clone(), config_rc.clone(), "About"),
                        _ => {}
                    }
                }
            }
        }
    });

    // 1. Initialize sysinfo
    let sys = sysinfo::System::new();
    let networks = Networks::new_with_refreshed_list();
    let sys_lock = Arc::new(Mutex::new(sys));
    let net_lock = Arc::new(Mutex::new(networks));

    // 2. Start a background ticker
    let tray_timer = slint::Timer::default();
    let settings_id_tray = settings_item.id().clone();
    let task_mgr_id_tray = task_mgr_item.id().clone();
    let lock_menu_id_tray = lock_item.id().clone();
    let about_id_tray = about_item.id().clone();
    let exit_id_tray = exit_item.id().clone();
    let lock_item_tray_clone = lock_item.clone();
    let config_tray = config.clone();
    let app_state_tray = app_state.clone();
    
    // Clear stale events that might have accumulated during initialization
    while tray_icon::menu::MenuEvent::receiver().try_recv().is_ok() {}
    while tray_icon::TrayIconEvent::receiver().try_recv().is_ok() {}

    let startup_time = std::time::Instant::now();

    tray_timer.start(slint::TimerMode::Repeated, Duration::from_millis(150), move || {
        if let Ok(event) = tray_icon::menu::MenuEvent::receiver().try_recv() {
            if event.id == settings_id_tray {
                show_settings_ui(app_state_tray.clone(), config_tray.clone(), "Settings");
            } else if event.id == task_mgr_id_tray {
                let _ = std::process::Command::new("taskmgr.exe").spawn();
            } else if event.id == lock_menu_id_tray {
                let mut cfg = config_tray.lock().unwrap();
                let next = !cfg.locked_drag;
                cfg.locked_drag = next;
                let s = app_state_tray.borrow();
                if let Some(overlay) = s.overlay_ui.upgrade() { overlay.set_locked_drag(next); }
                if let Some(app) = &s.app_ui {
                    app.set_locked_drag(next);
                }
                lock_item_tray_clone.set_checked(next);
                cfg.save();
            } else if event.id == about_id_tray {
                show_settings_ui(app_state_tray.clone(), config_tray.clone(), "About");
            } else if event.id == exit_id_tray {
                let _ = slint::quit_event_loop();
            }
        }
        
        {
            let cfg = config_tray.lock().unwrap();
            if lock_item_tray_clone.is_checked() != cfg.locked_drag {
                lock_item_tray_clone.set_checked(cfg.locked_drag);
            }
        }

        if let Ok(tray_icon::TrayIconEvent::Click { button: tray_icon::MouseButton::Left, button_state: tray_icon::MouseButtonState::Up, .. }) = tray_icon::TrayIconEvent::receiver().try_recv() {
            if startup_time.elapsed() > Duration::from_millis(500) {
                show_settings_ui(app_state_tray.clone(), config_tray.clone(), "Settings");
            }
        }
    });

    // Telemetry Thread
    let config_tel = config.clone();
    let overlay_weak_tel = overlay_handle.clone();
    let builder = std::thread::Builder::new().name("kil0bit_sysinfo_thread".into());
    builder.spawn(move || {
        let nvml_opt = Nvml::init().ok();
        loop {
            std::thread::sleep(Duration::from_millis(1000));
            let cfg = config_tel.lock().unwrap().clone();
            
            let mut sys = sys_lock.lock().unwrap();
            let mut net = net_lock.lock().unwrap();

            if cfg.show_cpu { sys.refresh_cpu_all(); }
            if cfg.show_mem { sys.refresh_memory(); }
            if cfg.show_net_up || cfg.show_net_down { net.refresh(true); }

            let cpu_usage = if cfg.show_cpu { sys.global_cpu_usage() } else { 0.0 };
            let mem_usage_percent = if cfg.show_mem {
                let total = sys.total_memory();
                if total > 0 { (sys.used_memory() as f64 / total as f64) * 100.0 } else { 0.0 }
            } else { 0.0 };

            let mut tx_bytes_total = 0;
            let mut rx_bytes_total = 0;
            if cfg.show_net_up || cfg.show_net_down {
                for (name, network) in net.iter() {
                    if cfg.network_adapter == "All Adapters" || cfg.network_adapter == *name {
                        tx_bytes_total += network.transmitted();
                        rx_bytes_total += network.received();
                    }
                }
            }

            let mut gpu_usage_str = "-- %".to_string();
            let mut gpu_temp_str = "-- °C".to_string();
            if cfg.show_gpu || cfg.show_temp_gpu {
                if let Some(nvml) = &nvml_opt {
                    if let Ok(device) = nvml.device_by_index(0) {
                        if cfg.show_gpu {
                            if let Ok(util) = device.utilization_rates() { gpu_usage_str = format!("{:.0}%", util.gpu); }
                        }
                        if cfg.show_temp_gpu {
                            if let Ok(temp) = device.temperature(nvml_wrapper::enum_wrappers::device::TemperatureSensor::Gpu) {
                                gpu_temp_str = format!("{:.0} °C", temp);
                            }
                        }
                    }
                }
            }

            let str_cpu_val = format!("{:.0}%", cpu_usage);
            let str_mem_val = format!("{:.0}%", mem_usage_percent);
            let str_up_val = format_network_speed(tx_bytes_total);
            let str_down_val = format_network_speed(rx_bytes_total);
            
            let overlay_clone = overlay_weak_tel.clone();
            slint::invoke_from_event_loop(move || {
                if let Some(overlay) = overlay_clone.upgrade() {
                    let overlay: OverlayWindow = overlay;
                    overlay.set_str_cpu(format!("CPU: {}", str_cpu_val).into());
                    overlay.set_str_mem(format!("RAM: {}", str_mem_val).into());
                    overlay.set_str_up(format!("UP: {}", str_up_val).into());
                    overlay.set_str_down(format!("DN: {}", str_down_val).into());
                    overlay.set_str_gpu(format!("GPU: {}", gpu_usage_str).into());
                    overlay.set_str_temp_gpu(format!("TEM: {}", gpu_temp_str).into());

                    overlay.set_str_cpu_val(str_cpu_val.into());
                    overlay.set_str_mem_val(str_mem_val.into());
                    overlay.set_str_up_val(str_up_val.into());
                    overlay.set_str_down_val(str_down_val.into());
                    overlay.set_str_gpu_val(gpu_usage_str.into());
                    overlay.set_str_temp_gpu_val(gpu_temp_str.into());
                }
            }).unwrap();
        }
    }).unwrap();

    let app_state_sync = app_state.clone();
    let config_sync = config.clone();
    let sync_timer = slint::Timer::default();
    sync_timer.start(slint::TimerMode::Repeated, Duration::from_millis(250), move || {
        let cfg = config_sync.lock().unwrap();
        if cfg.show_overlay {
            let s = app_state_sync.borrow();
            if let Some(ui) = s.overlay_ui.upgrade() {
                let is_fs = windowing::is_foreground_fullscreen();
                use raw_window_handle::HasWindowHandle;
                let slint_handle = ui.window().window_handle();
                if let Ok(rwh) = slint_handle.window_handle() {
                    if let raw_window_handle::RawWindowHandle::Win32(win32) = rwh.as_raw() {
                        let hwnd = win32.hwnd.get() as *mut std::ffi::c_void;
                        windowing::manage_z_order(hwnd, is_fs);
                        windowing::snap_to_taskbar(hwnd);
                        windowing::force_hide_from_taskbar(hwnd);
                    }
                }
            }
        }
    });

    {
        let cfg = config.lock().unwrap();
        if cfg.show_overlay {
            let s = app_state.borrow();
            if let Some(ui) = s.overlay_ui.upgrade() {
                use raw_window_handle::HasWindowHandle;
                let slint_handle = ui.window().window_handle();
                if let Ok(rwh) = slint_handle.window_handle() {
                    if let raw_window_handle::RawWindowHandle::Win32(win32) = rwh.as_raw() {
                        let hwnd = win32.hwnd.get() as *mut std::ffi::c_void;
                        windowing::anchor_to_taskbar_owned(hwnd);
                        windowing::snap_to_taskbar(hwnd);
                        windowing::punch_hole_in_taskbar(hwnd);
                        let _ = ui.show();
                    }
                }
            }
        }

        if !is_autostart {
            drop(cfg); // Unlock before show_settings_ui
            show_settings_ui(app_state.clone(), config.clone(), "Settings");
        }
    }

    let _ = overlay_ui.run();
    windowing::restore_taskbar_region();
    Ok(())
}

fn show_settings_ui(app_state: std::rc::Rc<std::cell::RefCell<AppState>>, config: Arc<Mutex<config::AppConfig>>, tab: &str) {
    let mut s = app_state.borrow_mut();
    if s.app_ui.is_none() {
        let app = AppWindow::new().expect("Failed to create Settings window");
        
        // Initial Sync
        let cfg = config.lock().unwrap();
        app.set_show_cpu(cfg.show_cpu);
        app.set_show_mem(cfg.show_mem);
        app.set_show_gpu(cfg.show_gpu);
        app.set_show_temp_gpu(cfg.show_temp_gpu);
        app.set_show_net_up(cfg.show_net_up);
        app.set_show_net_down(cfg.show_net_down);
        app.set_font_family_name(cfg.font_family.clone().into());
        app.set_text_color_hex(cfg.text_color_hex.clone().into());
        app.set_bg_opacity_value(cfg.bg_opacity);
        app.set_selected_display_variant(cfg.display_variant.clone().into());
        app.set_locked_drag(cfg.locked_drag);
        app.set_auto_start(cfg.auto_start);
        app.set_app_version(env!("CARGO_PKG_VERSION").into());
        app.set_show_overlay(cfg.show_overlay);

        let mut adapters: Vec<slint::SharedString> = vec!["All Adapters".into()];
        let sys_net = sysinfo::Networks::new_with_refreshed_list();
        for (name, _) in sys_net.iter() { adapters.push(name.clone().into()); }
        app.set_network_adapters(slint::ModelRc::from(std::rc::Rc::new(slint::VecModel::from(adapters))));
        app.set_selected_network_adapter(cfg.network_adapter.clone().into());
        drop(cfg);

        // Callbacks
        let config_cb = config.clone();
        let app_state_cb = app_state.clone();

        app.on_toggle_locked_drag({
            let config_cb = config_cb.clone();
            let app_state_cb = app_state_cb.clone();
            move || {
                let mut cfg = config_cb.lock().unwrap();
                cfg.locked_drag = !cfg.locked_drag;
                let next = cfg.locked_drag;
                let s = app_state_cb.borrow();
                if let Some(ov) = s.overlay_ui.upgrade() { ov.set_locked_drag(next); }
                if let Some(app) = &s.app_ui { app.set_locked_drag(next); }
                cfg.save();
            }
        });

        let config_cb2 = config_cb.clone();
        let app_state_cb2 = app_state_cb.clone();
        app.on_toggle_cpu(move || {
            let mut cfg = config_cb2.lock().unwrap();
            cfg.show_cpu = !cfg.show_cpu;
            let next = cfg.show_cpu;
            let s = app_state_cb2.borrow();
            if let Some(ov) = s.overlay_ui.upgrade() { ov.set_show_cpu(next); }
            if let Some(app) = &s.app_ui { app.set_show_cpu(next); }
            cfg.save();
            refresh_overlay_hole(s.overlay_ui.clone());
        });

        let config_cb3 = config_cb.clone();
        let app_state_cb3 = app_state_cb.clone();
        app.on_toggle_mem(move || {
            let mut cfg = config_cb3.lock().unwrap();
            cfg.show_mem = !cfg.show_mem;
            let next = cfg.show_mem;
            let s = app_state_cb3.borrow();
            if let Some(ov) = s.overlay_ui.upgrade() { ov.set_show_mem(next); }
            if let Some(app) = &s.app_ui { app.set_show_mem(next); }
            cfg.save();
            refresh_overlay_hole(s.overlay_ui.clone());
        });

        let config_cb4 = config_cb.clone();
        let app_state_cb4 = app_state_cb.clone();
        app.on_toggle_gpu(move || {
            let mut cfg = config_cb4.lock().unwrap();
            cfg.show_gpu = !cfg.show_gpu;
            let next = cfg.show_gpu;
            let s = app_state_cb4.borrow();
            if let Some(ov) = s.overlay_ui.upgrade() { ov.set_show_gpu(next); }
            if let Some(app) = &s.app_ui { app.set_show_gpu(next); }
            cfg.save();
            refresh_overlay_hole(s.overlay_ui.clone());
        });

        let config_cb5 = config_cb.clone();
        let app_state_cb5 = app_state_cb.clone();
        app.on_toggle_temp_gpu(move || {
            let mut cfg = config_cb5.lock().unwrap();
            cfg.show_temp_gpu = !cfg.show_temp_gpu;
            let next = cfg.show_temp_gpu;
            let s = app_state_cb5.borrow();
            if let Some(ov) = s.overlay_ui.upgrade() { ov.set_show_temp_gpu(next); }
            if let Some(app) = &s.app_ui { app.set_show_temp_gpu(next); }
            cfg.save();
            refresh_overlay_hole(s.overlay_ui.clone());
        });

        let config_cb6 = config_cb.clone();
        let app_state_cb6 = app_state_cb.clone();
        app.on_apply_display_variant(move |v| {
            let mut cfg = config_cb6.lock().unwrap();
            let s = app_state_cb6.borrow();
            if let Some(ov) = s.overlay_ui.upgrade() { ov.set_display_variant(v.clone()); }
            cfg.display_variant = v.to_string();
            cfg.save();
            refresh_overlay_hole(s.overlay_ui.clone());
        });

        let config_cb7 = config_cb.clone();
        let app_state_cb7 = app_state_cb.clone();
        app.on_toggle_net_up(move || {
            let mut cfg = config_cb7.lock().unwrap();
            cfg.show_net_up = !cfg.show_net_up;
            let next = cfg.show_net_up;
            let s = app_state_cb7.borrow();
            if let Some(ov) = s.overlay_ui.upgrade() { ov.set_show_net_up(next); }
            if let Some(app) = &s.app_ui { app.set_show_net_up(next); }
            cfg.save();
            refresh_overlay_hole(s.overlay_ui.clone());
        });

        let config_cb8 = config_cb.clone();
        let app_state_cb8 = app_state_cb.clone();
        app.on_toggle_net_down(move || {
            let mut cfg = config_cb8.lock().unwrap();
            cfg.show_net_down = !cfg.show_net_down;
            let next = cfg.show_net_down;
            let s = app_state_cb8.borrow();
            if let Some(ov) = s.overlay_ui.upgrade() { ov.set_show_net_down(next); }
            if let Some(app) = &s.app_ui { app.set_show_net_down(next); }
            cfg.save();
            refresh_overlay_hole(s.overlay_ui.clone());
        });

        let config_cb9 = config_cb.clone();
        app.on_apply_network_adapter(move |v| {
            let mut cfg = config_cb9.lock().unwrap();
            cfg.network_adapter = v.into();
            cfg.save();
        });

        let config_cb10 = config_cb.clone();
        let app_state_cb10 = app_state_cb.clone();
        app.on_launch_overlay(move || {
            let mut cfg = config_cb10.lock().unwrap();
            cfg.show_overlay = !cfg.show_overlay;
            let next = cfg.show_overlay;
            let s = app_state_cb10.borrow();
            if let Some(app) = &s.app_ui { app.set_show_overlay(next); }
            cfg.save();
            
            if next {
                if let Some(ui) = s.overlay_ui.upgrade() {
                    use raw_window_handle::HasWindowHandle;
                    if let Ok(rwh) = ui.window().window_handle().window_handle() {
                        if let raw_window_handle::RawWindowHandle::Win32(w) = rwh.as_raw() {
                            let hwnd = w.hwnd.get() as *mut std::ffi::c_void;
                            windowing::anchor_to_taskbar_owned(hwnd);
                            windowing::snap_to_taskbar(hwnd);
                            windowing::punch_hole_in_taskbar(hwnd);
                            let _ = ui.show();
                        }
                    }
                }
            } else {
                windowing::restore_taskbar_region();
                if let Some(ui) = s.overlay_ui.upgrade() { let _ = ui.hide(); }
            }
        });

        let config_cb11 = config_cb.clone();
        let app_state_cb11 = app_state_cb.clone();
        app.on_apply_font(move |v| {
            let mut cfg = config_cb11.lock().unwrap();
            let s = app_state_cb11.borrow();
            if let Some(ov) = s.overlay_ui.upgrade() { ov.set_font_family(v.clone()); }
            cfg.font_family = v.into();
            cfg.save();
        });

        let config_cb12 = config_cb.clone();
        let app_state_cb12 = app_state_cb.clone();
        app.on_apply_text_color(move |c| {
            let mut cfg = config_cb12.lock().unwrap();
            let s = app_state_cb12.borrow();
            if let Some(ov) = s.overlay_ui.upgrade() { ov.set_text_color(c); }
            cfg.text_color_hex = format!("#{:02X}{:02X}{:02X}", c.red(), c.green(), c.blue());
            cfg.save();
        });

        let config_cb13 = config_cb.clone();
        let app_state_cb13 = app_state_cb.clone();
        app.on_apply_bg_color(move |c| {
            let mut cfg = config_cb13.lock().unwrap();
            let s = app_state_cb13.borrow();
            if let Some(ov) = s.overlay_ui.upgrade() { ov.set_bg_color(c); }
            cfg.bg_color_hex = format!("#{:02X}{:02X}{:02X}", c.red(), c.green(), c.blue());
            cfg.save();
        });

        let config_cb14 = config_cb.clone();
        let app_state_cb14 = app_state_cb.clone();
        app.on_apply_bg_opacity(move |o| {
            let mut cfg = config_cb14.lock().unwrap();
            let s = app_state_cb14.borrow();
            if let Some(ov) = s.overlay_ui.upgrade() { ov.set_bg_opacity(o); }
            if let Some(app) = &s.app_ui { app.set_bg_opacity_value(o); }
            cfg.bg_opacity = o;
            cfg.save();
        });

        let config_cb15 = config_cb.clone();
        let app_state_cb15 = app_state_cb.clone();
        app.on_toggle_auto_start(move || {
            let mut cfg = config_cb15.lock().unwrap();
            cfg.auto_start = !cfg.auto_start;
            let next = cfg.auto_start;
            set_auto_start(next);
            cfg.save();
            
            let s = app_state_cb15.borrow();
            if let Some(app) = &s.app_ui { app.set_auto_start(next); }
        });

        app.on_exit_app(|| { let _ = slint::quit_event_loop(); });

        let app_state_cb16 = app_state_cb.clone();
        let config_cb16 = config_cb.clone();
        app.on_save_and_close(move || {
            config_cb16.lock().unwrap().save();
            if let Some(app) = app_state_cb16.borrow_mut().app_ui.take() { let _ = app.hide(); }
        });

        app.on_open_url(|url| { let _ = std::process::Command::new("cmd").args(["/C", "start", "", url.as_str()]).spawn(); });

        // Effects
        use raw_window_handle::HasWindowHandle;
        if let Ok(rwh) = app.window().window_handle().window_handle() {
            if let raw_window_handle::RawWindowHandle::Win32(w) = rwh.as_raw() {
                let hwnd = w.hwnd.get() as *mut std::ffi::c_void;
                windowing::apply_mica_effect(hwnd);
                windowing::center_window(hwnd);
            }
        }

        // Handle Close Button (Red X) -> Fully Unload
        let app_state_close = app_state.clone();
        app.window().on_close_requested(move || {
            app_state_close.borrow_mut().app_ui = None; // Drop the UI handle
            CloseRequestResponse::HideWindow
        });

        s.app_ui = Some(app);
    }

    if let Some(app) = &s.app_ui {
        app.set_active_tab(tab.into());
        let _ = app.show();
    }
}

// Helper to beautifully format bytes into KB/s or MB/s
fn format_network_speed(bytes_per_sec: u64) -> String {
    let kb = bytes_per_sec as f64 / 1024.0;
    if kb >= 1024.0 * 1000.0 {
        let gb = kb / (1024.0 * 1024.0);
        format!("{:.1} GB/s", gb)
    } else if kb >= 1024.0 * 100.0 {
        let mb = kb / 1024.0;
        format!("{:.0} MB/s", mb)
    } else if kb > 99.9 {
        let mb = kb / 1024.0;
        format!("{:.1} MB/s", mb)
    } else {
        format!("{:.1} KB/s", kb)
    }
}

// Helper to manually parse a #RRGGBBAA or #RRGGBB string into a slint::Color
fn parse_hex_color(hex: &str) -> Result<slint::Color, &'static str> {
    let hex = hex.trim_start_matches('#');
    let len = hex.len();
    if len != 6 && len != 8 { return Err("Invalid hex length"); }
    let r = u8::from_str_radix(&hex[0..2], 16).map_err(|_| "Invalid hex")?;
    let g = u8::from_str_radix(&hex[2..4], 16).map_err(|_| "Invalid hex")?;
    let b = u8::from_str_radix(&hex[4..6], 16).map_err(|_| "Invalid hex")?;
    let a = if len == 8 { u8::from_str_radix(&hex[6..8], 16).map_err(|_| "Invalid hex")? } else { 255 };
    Ok(slint::Color::from_argb_u8(a, r, g, b))
}

fn set_auto_start(enabled: bool) {
    use windows::Win32::System::Registry::{
        RegCreateKeyExW, RegDeleteValueW, RegSetValueExW, HKEY_CURRENT_USER, REG_SZ, KEY_ALL_ACCESS, REG_OPTION_NON_VOLATILE,
    };
    use windows::core::w;

    let subkey = w!("Software\\Microsoft\\Windows\\CurrentVersion\\Run");
    let app_name = w!("kil0bit System Monitor");

    unsafe {
        let mut hkey = windows::Win32::System::Registry::HKEY::default();
        if RegCreateKeyExW(
            HKEY_CURRENT_USER,
            subkey,
            Some(0),
            None,
            REG_OPTION_NON_VOLATILE,
            KEY_ALL_ACCESS,
            None,
            &mut hkey,
            None,
        ).is_ok() {
            if enabled {
                if let Ok(exe_path) = std::env::current_exe() {
                    let exe_str = format!("\"{}\" --autostart", exe_path.to_string_lossy());
                    let mut exe_wide: Vec<u16> = exe_str.encode_utf16().collect();
                    exe_wide.push(0);
                    let _ = RegSetValueExW(
                        hkey,
                        app_name,
                        Some(0),
                        REG_SZ,
                        Some(std::slice::from_raw_parts(exe_wide.as_ptr() as *const u8, exe_wide.len() * 2)),
                    );
                }
            } else {
                let _ = RegDeleteValueW(hkey, app_name);
            }
            let _ = windows::Win32::System::Registry::RegCloseKey(hkey);
        }
    }
}

fn refresh_overlay_hole(overlay_handle: slint::Weak<OverlayWindow>) {
    let t = Box::leak(Box::new(slint::Timer::default()));
    t.start(slint::TimerMode::SingleShot, std::time::Duration::from_millis(100), move || {
        if let Some(ov) = overlay_handle.upgrade() {
            use raw_window_handle::HasWindowHandle;
            if let Ok(rwh) = ov.window().window_handle().window_handle() {
                if let raw_window_handle::RawWindowHandle::Win32(w) = rwh.as_raw() {
                    let hwnd = w.hwnd.get() as *mut std::ffi::c_void;
                    windowing::snap_to_taskbar(hwnd);
                    windowing::punch_hole_in_taskbar(hwnd);
                }
            }
        }
    });
}

