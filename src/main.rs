slint::include_modules!();
use slint::{ComponentHandle, CloseRequestResponse};

mod windowing;
mod config;

use std::sync::{Arc, Mutex};
use std::time::Duration;
use sysinfo::Networks;
use nvml_wrapper::Nvml; // Added Nvml import
use image::GenericImageView;
fn main() -> Result<(), slint::PlatformError> {
    windowing::set_dark_mode_for_app();
    let app_ui = AppWindow::new()?;
    let app_handle = app_ui.as_weak();

    let overlay_ui = OverlayWindow::new()?;
    let overlay_handle = overlay_ui.as_weak();

    // ── Sync Initial State from Config ───────────────────────────────────────
    let config = config::AppConfig::load();
    let config_lock = Arc::new(Mutex::new(config.clone()));

    // Sync AppWindow (Settings)
    app_ui.set_show_cpu(config.show_cpu);
    app_ui.set_show_mem(config.show_mem);
    app_ui.set_show_gpu(config.show_gpu);
    app_ui.set_show_temp_gpu(config.show_temp_gpu);
    app_ui.set_show_net_up(config.show_net_up);
    app_ui.set_show_net_down(config.show_net_down);
    app_ui.set_font_family_name(config.font_family.clone().into());
    app_ui.set_text_color_hex(config.text_color_hex.clone().into());
    app_ui.set_bg_opacity_value(config.bg_opacity);
    app_ui.set_selected_display_variant(config.display_variant.clone().into());
    app_ui.set_locked_drag(config.locked_drag);
    app_ui.set_auto_start(config.auto_start);
    app_ui.set_app_version(env!("CARGO_PKG_VERSION").into());

    // Sync OverlayWindow
    overlay_ui.set_show_cpu(config.show_cpu);
    overlay_ui.set_show_mem(config.show_mem);
    overlay_ui.set_show_gpu(config.show_gpu);
    overlay_ui.set_show_temp_gpu(config.show_temp_gpu);
    overlay_ui.set_show_net_up(config.show_net_up);
    overlay_ui.set_show_net_down(config.show_net_down);
    overlay_ui.set_font_family(config.font_family.clone().into());
    overlay_ui.set_display_variant(config.display_variant.clone().into());
    overlay_ui.set_locked_drag(config.locked_drag);
    overlay_ui.set_bg_opacity(config.bg_opacity);

    if let Ok(color) = parse_hex_color(&config.text_color_hex) {
        overlay_ui.set_text_color(color);
    }
    if let Ok(bg) = parse_hex_color(&config.bg_color_hex) {
        overlay_ui.set_bg_color(bg);
    }

    // Initialize System Tray
    let tray_menu = tray_icon::menu::Menu::new();
    let settings_item = tray_icon::menu::MenuItem::new("Settings", true, None);
    let task_mgr_item = tray_icon::menu::MenuItem::new("Task Manager", true, None);
    let lock_item = tray_icon::menu::CheckMenuItem::new("Lock Position", true, config.locked_drag, None);
    let about_item = tray_icon::menu::MenuItem::new("About", true, None);
    let exit_item = tray_icon::menu::MenuItem::new("Exit", true, None);

    let _ = tray_menu.append(&settings_item);
    let _ = tray_menu.append(&task_mgr_item);
    let _ = tray_menu.append(&lock_item);
    let _ = tray_menu.append(&tray_icon::menu::PredefinedMenuItem::separator());
    let _ = tray_menu.append(&about_item);
    let _ = tray_menu.append(&exit_item);

    // Load the icon for the tray
    let icon_path = std::path::Path::new("ui/assets/icon.png");
    let icon = if icon_path.exists() {
        let image = image::open(icon_path).expect("Failed to open icon");
        let (width, height) = image.dimensions();
        let rgba = image.to_rgba8().into_raw();
        tray_icon::Icon::from_rgba(rgba, width, height).ok()
    } else {
        None
    };

    let mut tray_builder = tray_icon::TrayIconBuilder::new()
        .with_tooltip("kil0bit System Monitor")
        .with_menu(Box::new(tray_menu));
    
    if let Some(i) = icon {
        tray_builder = tray_builder.with_icon(i);
    }

    let _tray_icon = tray_builder.build().unwrap();

    // Handle incoming tray events
    let _menu_channel = tray_icon::menu::MenuEvent::receiver();
    let _tray_channel = tray_icon::TrayIconEvent::receiver();

    // Map the close button to Hide Window instead of Quit
    let app_hide_handle = app_ui.as_weak();
    app_ui.window().on_close_requested(move || {
        if let Some(app) = app_hide_handle.upgrade() {
            let _ = app.hide();
        }
        CloseRequestResponse::KeepWindowShown
    });

    // Construct Network Adapters List
    let mut adapters_vec: Vec<slint::SharedString> = vec!["All Adapters".into()];
    let sys_net = sysinfo::Networks::new_with_refreshed_list();
    for (name, _) in sys_net.iter() {
        adapters_vec.push(name.clone().into());
    }
    app_ui.set_network_adapters(slint::ModelRc::from(std::rc::Rc::new(slint::VecModel::from(adapters_vec))));
    app_ui.set_selected_network_adapter(config.network_adapter.clone().into());

    // Immediately extract the HWND and apply the Mica/Acrylic effect upon startup
    use raw_window_handle::HasWindowHandle;
    
    // Apply Mica/Acrylic to AppWindow (Settings)
    let app_slint_handle = app_ui.window().window_handle();
    if let Ok(rwh) = app_slint_handle.window_handle() {
        if let raw_window_handle::RawWindowHandle::Win32(win32) = rwh.as_raw() {
            let hwnd = win32.hwnd.get() as *mut std::ffi::c_void;
            windowing::apply_mica_effect(hwnd);
            windowing::center_window(hwnd);
        }
    }

    // Apply Mica/Acrylic & Taskbar Anchoring to OverlayWindow based on current config
    let slint_handle = overlay_ui.window().window_handle();
    if let Ok(rwh) = slint_handle.window_handle() {
        if let raw_window_handle::RawWindowHandle::Win32(win32) = rwh.as_raw() {
            let hwnd = win32.hwnd.get() as *mut std::ffi::c_void;
            
            windowing::remove_mica_effect(hwnd);
            
            windowing::anchor_to_taskbar_owned(hwnd);
        }
    }

    // Wire up Window Dragging
    overlay_ui.on_window_moved({
        let overlay_handle = overlay_handle.clone();
        move |offset_x, offset_y| {
            if let Some(ui) = overlay_handle.upgrade() {
                let current_pos = ui.window().position();
                let scale = ui.window().scale_factor();
                
                use raw_window_handle::HasWindowHandle;
                let slint_handle = ui.window().window_handle();
                if let Ok(rwh) = slint_handle.window_handle() {
                    if let raw_window_handle::RawWindowHandle::Win32(win32) = rwh.as_raw() {
                        let hwnd = win32.hwnd.get() as *mut std::ffi::c_void;
                        let new_x = current_pos.x + (offset_x * scale) as i32;
                        let new_y = current_pos.y + (offset_y * scale) as i32;
                        windowing::move_window(hwnd, new_x, new_y);
                        // Update the hole to follow the drag
                        windowing::punch_hole_in_taskbar(hwnd);
                    }
                }
            }
        }
    });

    overlay_ui.on_window_dropped({
        let overlay_handle = overlay_handle.clone();
        move || {
            if let Some(ui) = overlay_handle.upgrade() {
                 use raw_window_handle::HasWindowHandle;
                 let slint_handle = ui.window().window_handle();
                 if let Ok(rwh) = slint_handle.window_handle() {
                     if let raw_window_handle::RawWindowHandle::Win32(win32) = rwh.as_raw() {
                         let hwnd = win32.hwnd.get() as *mut std::ffi::c_void;
                         windowing::snap_to_taskbar(hwnd);
                         // Refresh hole after snap repositions the overlay
                         windowing::punch_hole_in_taskbar(hwnd);
                     }
                 }
            }
        }
    });

    overlay_ui.on_right_clicked({
        let app_handle = app_handle.clone();
        let overlay_handle = overlay_handle.clone();
        let config_lock_clone = config_lock.clone();
        move || {
            if let Some(overlay) = overlay_handle.upgrade() {
                use raw_window_handle::HasWindowHandle;
                let slint_handle = overlay.window().window_handle();
                if let Ok(rwh) = slint_handle.window_handle() {
                    if let raw_window_handle::RawWindowHandle::Win32(win32) = rwh.as_raw() {
                        let hwnd = win32.hwnd.get() as *mut std::ffi::c_void;
                        let is_locked = overlay.get_locked_drag();
                        let choice = windowing::show_overlay_context_menu(hwnd, is_locked);
                        
                        match choice {
                            1 => {
                                // Settings
                                if let Some(app) = app_handle.upgrade() {
                                    app.set_active_tab("Settings".into());
                                    let _ = app.show();
                                }
                            }
                            2 => {
                                // Task Manager
                                let _ = std::process::Command::new("taskmgr.exe").spawn();
                            }
                            3 => {
                                // Toggle Lock
                                let next_lock = !is_locked;
                                overlay.set_locked_drag(next_lock);
                                if let Some(app) = app_handle.upgrade() {
                                    app.set_locked_drag(next_lock);
                                }
                                let mut c = config_lock_clone.lock().unwrap();
                                c.locked_drag = next_lock;
                                c.save();
                            }
                            4 => {
                                // About
                                if let Some(app) = app_handle.upgrade() {
                                    app.set_active_tab("About".into());
                                    let _ = app.show();
                                }
                            }
                            _ => {}
                        }
                    }
                }
            }
        }
    });

    let cfg_lock = config_lock.clone();
    app_ui.on_toggle_locked_drag({
        let app_handle = app_handle.clone();
        let overlay_handle = overlay_handle.clone();
        move || {
            if let (Some(app), Some(overlay)) = (app_handle.upgrade(), overlay_handle.upgrade()) {
                let next = !app.get_locked_drag();
                app.set_locked_drag(next);
                overlay.set_locked_drag(next);

                let mut c = cfg_lock.lock().unwrap();
                c.locked_drag = next;
                c.save();
            }
        }
    });

    let cfg_cpu = config_lock.clone();
    app_ui.on_toggle_cpu({
        let app_handle = app_handle.clone();
        let overlay_handle = overlay_handle.clone();
        move || {
            if let (Some(app), Some(overlay)) = (app_handle.upgrade(), overlay_handle.upgrade()) {
                let next = !app.get_show_cpu();
                app.set_show_cpu(next);
                overlay.set_show_cpu(next);
                
                let mut c = cfg_cpu.lock().unwrap();
                c.show_cpu = next;
                c.save();
                refresh_overlay_hole(overlay_handle.clone());
            }
        }
    });

    let cfg_mem = config_lock.clone();
    app_ui.on_toggle_mem({
        let app_handle = app_handle.clone();
        let overlay_handle = overlay_handle.clone();
        move || {
            if let (Some(app), Some(overlay)) = (app_handle.upgrade(), overlay_handle.upgrade()) {
                let next = !app.get_show_mem();
                app.set_show_mem(next);
                overlay.set_show_mem(next);

                let mut c = cfg_mem.lock().unwrap();
                c.show_mem = next;
                c.save();
                refresh_overlay_hole(overlay_handle.clone());
            }
        }
    });

    let cfg_gpu = config_lock.clone();
    app_ui.on_toggle_gpu({
        let app_handle = app_handle.clone();
        let overlay_handle = overlay_handle.clone();
        move || {
            if let (Some(app), Some(overlay)) = (app_handle.upgrade(), overlay_handle.upgrade()) {
                let next = !app.get_show_gpu();
                app.set_show_gpu(next);
                overlay.set_show_gpu(next);

                let mut c = cfg_gpu.lock().unwrap();
                c.show_gpu = next;
                c.save();
                refresh_overlay_hole(overlay_handle.clone());
            }
        }
    });

    let cfg_temp_gpu = config_lock.clone();
    app_ui.on_toggle_temp_gpu({
        let app_handle = app_handle.clone();
        let overlay_handle = overlay_handle.clone();
        move || {
            if let (Some(app), Some(overlay)) = (app_handle.upgrade(), overlay_handle.upgrade()) {
                let next = !app.get_show_temp_gpu();
                app.set_show_temp_gpu(next);
                overlay.set_show_temp_gpu(next);

                let mut c = cfg_temp_gpu.lock().unwrap();
                c.show_temp_gpu = next;
                c.save();
                refresh_overlay_hole(overlay_handle.clone());
            }
        }
    });

    let cfg_variant = config_lock.clone();
    app_ui.on_apply_display_variant({
        let overlay_handle = overlay_handle.clone();
        move |variant| {
            if let Some(overlay) = overlay_handle.upgrade() {
                overlay.set_display_variant(variant.clone());
                let mut c = cfg_variant.lock().unwrap();
                c.display_variant = variant.to_string();
                c.save();

                refresh_overlay_hole(overlay_handle.clone());
            }
        }
    });

    let cfg_net_up = config_lock.clone();
    app_ui.on_toggle_net_up({
        let app_handle = app_handle.clone();
        let overlay_handle = overlay_handle.clone();
        move || {
            if let (Some(app), Some(overlay)) = (app_handle.upgrade(), overlay_handle.upgrade()) {
                let next = !app.get_show_net_up();
                app.set_show_net_up(next);
                overlay.set_show_net_up(next);

                let mut c = cfg_net_up.lock().unwrap();
                c.show_net_up = next;
                c.save();
                refresh_overlay_hole(overlay_handle.clone());
            }
        }
    });

    let cfg_net_down = config_lock.clone();
    app_ui.on_toggle_net_down({
        let app_handle = app_handle.clone();
        let overlay_handle = overlay_handle.clone();
        move || {
            if let (Some(app), Some(overlay)) = (app_handle.upgrade(), overlay_handle.upgrade()) {
                let next = !app.get_show_net_down();
                app.set_show_net_down(next);
                overlay.set_show_net_down(next);

                let mut c = cfg_net_down.lock().unwrap();
                c.show_net_down = next;
                c.save();
                refresh_overlay_hole(overlay_handle.clone());
            }
        }
    });
    let cfg_net = config_lock.clone();
    app_ui.on_apply_network_adapter(move |adapter_name| {
        let mut c = cfg_net.lock().unwrap();
        c.network_adapter = adapter_name.into();
        c.save();
    });

    app_ui.on_launch_overlay({
        let overlay_handle = overlay_handle.clone();
        let app_handle = app_handle.clone();
        move || {
            if let (Some(overlay), Some(app)) = (overlay_handle.upgrade(), app_handle.upgrade()) {
                let current = app.get_show_overlay();
                let next = !current;
                app.set_show_overlay(next);
                
                if next {
                    // Re-apply taskbar hiding styles every time we show it
                    use raw_window_handle::HasWindowHandle;
                    let slint_handle = overlay.window().window_handle();
                    if let Ok(rwh) = slint_handle.window_handle() {
                        if let raw_window_handle::RawWindowHandle::Win32(win32) = rwh.as_raw() {
                            let hwnd = win32.hwnd.get() as *mut std::ffi::c_void;

                            windowing::anchor_to_taskbar_owned(hwnd);
                            windowing::snap_to_taskbar(hwnd);

                            // Punch a transparent hole where the overlay sits
                            windowing::punch_hole_in_taskbar(hwnd);

                            let _ = overlay.show();

                            // Reinforce styles after show() since Slint may reset them
                            windowing::anchor_to_taskbar_owned(hwnd);
                            // Refresh the hole after show() re-positions the window
                            windowing::punch_hole_in_taskbar(hwnd);
                        }
                    }
                } else {
                    // Restore taskbar's normal visual appearance when overlay is hidden
                    windowing::restore_taskbar_region();
                    let _ = overlay.hide();
                }
            }
        }
    });

    // Customization Callbacks
    let cfg_font = config_lock.clone();
    app_ui.on_apply_font({
        let overlay_handle = overlay_handle.clone();
        move |font_name| {
            if let Some(overlay) = overlay_handle.upgrade() {
                overlay.set_font_family(font_name.clone());
                let mut c = cfg_font.lock().unwrap();
                c.font_family = font_name.into();
                c.save();
            }
        }
    });

    let cfg_tc = config_lock.clone();
    app_ui.on_apply_text_color({
        let overlay_handle = overlay_handle.clone();
        move |color| {
            if let Some(overlay) = overlay_handle.upgrade() {
                overlay.set_text_color(color);
                let hex_code = format!("#{:02X}{:02X}{:02X}", color.red(), color.green(), color.blue());
                let mut c = cfg_tc.lock().unwrap();
                c.text_color_hex = hex_code;
                c.save();
            }
        }
    });

    let cfg_bg_color = config_lock.clone();
    app_ui.on_apply_bg_color({
        let overlay_handle = overlay_handle.clone();
        move |color| {
            if let Some(overlay) = overlay_handle.upgrade() {
                overlay.set_bg_color(color);
                let hex_code = format!("#{:02X}{:02X}{:02X}", color.red(), color.green(), color.blue());
                let mut c = cfg_bg_color.lock().unwrap();
                c.bg_color_hex = hex_code;
                c.save();
            }
        }
    });

    let cfg_bg_opacity = config_lock.clone();
    app_ui.on_apply_bg_opacity({
        let overlay_handle = overlay_handle.clone();
        let app_handle_opacity = app_ui.as_weak();
        move |opacity| {
            if let Some(overlay) = overlay_handle.upgrade() {
                overlay.set_bg_opacity(opacity);
            }
            if let Some(app) = app_handle_opacity.upgrade() {
                app.set_bg_opacity_value(opacity);
            }
            let mut c = cfg_bg_opacity.lock().unwrap();
            c.bg_opacity = opacity;
            c.save();
        }
    });

    let cfg_as = config_lock.clone();
    let app_handle_as = app_ui.as_weak();
    app_ui.on_toggle_auto_start(move || {
        if let Some(app) = app_handle_as.upgrade() {
            let mut c = cfg_as.lock().unwrap();
            c.auto_start = !c.auto_start;
            app.set_auto_start(c.auto_start); // Fix UI Sync
            set_auto_start(c.auto_start);
            c.save();
        }
    });


    app_ui.on_exit_app(move || {
        let _ = slint::quit_event_loop();
    });

    let app_handle_save = app_ui.as_weak();
    let cfg_save = config_lock.clone();
    app_ui.on_save_and_close(move || {
        if let Some(app) = app_handle_save.upgrade() {
            let c = cfg_save.lock().unwrap();
            c.save();
            let _ = app.hide();
        }
    });

    app_ui.on_open_url(|url| {
        let _ = std::process::Command::new("cmd")
            .args(["/C", "start", "", url.as_str()])
            .spawn();
    });

    // 1. Initialize sysinfo
    let sys = sysinfo::System::new_all();
    let networks = Networks::new_with_refreshed_list();

    // We need to keep sysinfo alive to calculate diffs (like Network speed and CPU usage over time)
    let sys_lock = Arc::new(Mutex::new(sys));
    let net_lock = Arc::new(Mutex::new(networks));

    // 2. Start a background ticker
    let sysinfo_overlay_handle = overlay_handle.clone();
    let cfg_ticker = config_lock.clone();
    
    // Create UI thread timer to poll the tray icon channels (since slint blocks the main thread)
    let tray_timer = slint::Timer::default();
    
    let settings_id = settings_item.id().clone();
    let task_mgr_id = task_mgr_item.id().clone();
    let lock_menu_id = lock_item.id().clone();
    let about_id = about_item.id().clone();
    let exit_id = exit_item.id().clone();
    
    let lock_item_tray = lock_item.clone();
    let config_lock_tray = config_lock.clone();
    let overlay_handle_tray = overlay_handle.clone();
    let app_tray_handle = app_ui.as_weak();

    tray_timer.start(slint::TimerMode::Repeated, Duration::from_millis(50), move || {
        if let Ok(event) = tray_icon::menu::MenuEvent::receiver().try_recv() {
            if event.id == settings_id {
                if let Some(app) = app_tray_handle.upgrade() {
                    app.set_active_tab("Settings".into());
                    let _ = app.show();
                }
            } else if event.id == task_mgr_id {
                let _ = std::process::Command::new("taskmgr.exe").spawn();
            } else if event.id == lock_menu_id {
                if let (Some(app), Some(overlay)) = (app_tray_handle.upgrade(), overlay_handle_tray.upgrade()) {
                    let next = !app.get_locked_drag();
                    app.set_locked_drag(next);
                    overlay.set_locked_drag(next);
                    lock_item_tray.set_checked(next);

                    let mut c = config_lock_tray.lock().unwrap();
                    c.locked_drag = next;
                    c.save();
                }
            } else if event.id == about_id {
                if let Some(app) = app_tray_handle.upgrade() {
                    app.set_active_tab("About".into());
                    let _ = app.show();
                }
            } else if event.id == exit_id {
                let _ = slint::quit_event_loop();
            }
        }
        
        // Background sync: Ensure tray checkbox matches actual state (if toggled elsewhere)
        if let Some(app) = app_tray_handle.upgrade() {
            let current_lock = app.get_locked_drag();
            if lock_item_tray.is_checked() != current_lock {
                lock_item_tray.set_checked(current_lock);
            }
        }
        if let Ok(tray_icon::TrayIconEvent::Click { button: tray_icon::MouseButton::Left, button_state: tray_icon::MouseButtonState::Up, .. }) = tray_icon::TrayIconEvent::receiver().try_recv() {
            if let Some(app) = app_tray_handle.upgrade() {
                let _ = app.show();
            }
        }
    });

    let builder = std::thread::Builder::new().name("kil0bit_sysinfo_thread".into());
    builder.spawn(move || {
        // Initialize the nvml-wrapper for GPU and Temp telemetry
        let nvml_opt = match Nvml::init() {
            Ok(n) => Some(n),
            Err(e) => {
                println!("NVML Initialization Failed! {:?}", e);
                None
            }
        };
        // Initialize background loop
        loop {
            std::thread::sleep(Duration::from_millis(1000)); // Update every 1 second

            let mut sys = sys_lock.lock().unwrap();
            let mut net = net_lock.lock().unwrap();

            // Refresh required metrics
            sys.refresh_cpu_all();
            sys.refresh_memory();
            net.refresh(true);

            // Gather metrics
            let cpu_usage = sys.global_cpu_usage();
            
            let total_mem = sys.total_memory();
            let used_mem = sys.used_memory();
            let mem_usage_percent = if total_mem > 0 {
                (used_mem as f64 / total_mem as f64) * 100.0
            } else {
                0.0
            };

            // Calculate Network Speeds (bytes per second)
            let mut tx_bytes_total = 0;
            let mut rx_bytes_total = 0;
            let selected_adapter = cfg_ticker.lock().unwrap().network_adapter.clone();
            
            for (name, network) in net.iter() {
                if selected_adapter == "All Adapters" || selected_adapter == *name {
                    tx_bytes_total += network.transmitted();
                    rx_bytes_total += network.received();
                }
            }

            // Collect GPU Telemetry via NVML
            let gpu_usage_str;
            let gpu_temp_str;
            if let Some(ref nvml) = nvml_opt {
                if let Ok(device) = nvml.device_by_index(0) {
                    gpu_usage_str = match device.utilization_rates() {
                        Ok(util) => format!("{:.0}%", util.gpu),
                        Err(_) => "-- %".to_string(),
                    };
                    gpu_temp_str = match device.temperature(nvml_wrapper::enum_wrappers::device::TemperatureSensor::Gpu) {
                        Ok(temp) => format!("{:.0} °C", temp),
                        Err(_) => "-- °C".to_string(),
                    };
                } else {
                    gpu_usage_str = "-- %".to_string();
                    gpu_temp_str = "-- °C".to_string();
                }
            } else {
                gpu_usage_str = "-- %".to_string();
                gpu_temp_str = "-- °C".to_string();
            }
            


            // Format Data
            let str_cpu_val = format!("{:.0}%", cpu_usage);
            let str_mem_val = format!("{:.0}%", mem_usage_percent);
            let str_cpu = format!("CPU: {}", str_cpu_val);
            let str_mem = format!("RAM: {}", str_mem_val);
            let str_up_val = format_network_speed(tx_bytes_total);
            let str_down_val = format_network_speed(rx_bytes_total);
            let str_up = format!("UP: {}", str_up_val);
            let str_down = format!("DN: {}", str_down_val);
            
            let str_gpu_val = gpu_usage_str;
            let str_temp_gpu_val = gpu_temp_str;
            let str_gpu = format!("GPU: {}", str_gpu_val);
            let str_temp_gpu = format!("TEM: {}", str_temp_gpu_val);

            // 3. Dispatch to the Slint UI thread safely
            let overlay_handle_clone = sysinfo_overlay_handle.clone();
            slint::invoke_from_event_loop(move || {
                if let Some(overlay) = overlay_handle_clone.upgrade() {
                    overlay.set_str_cpu(str_cpu.into());
                    overlay.set_str_mem(str_mem.into());
                    overlay.set_str_up(str_up.into());
                    overlay.set_str_down(str_down.into());
                    overlay.set_str_gpu(str_gpu.into());
                    overlay.set_str_temp_gpu(str_temp_gpu.into());

                    overlay.set_str_cpu_val(str_cpu_val.into());
                    overlay.set_str_mem_val(str_mem_val.into());
                    overlay.set_str_up_val(str_up_val.into());
                    overlay.set_str_down_val(str_down_val.into());
                    overlay.set_str_gpu_val(str_gpu_val.into());
                    overlay.set_str_temp_gpu_val(str_temp_gpu_val.into());


                }
            }).unwrap();
        }
    }).unwrap();
    


    // 4. Start 250ms Sync Timer for strict Taskbar Z-Order without flickering
    let sync_timer = slint::Timer::default();
    let overlay_handle_sync = overlay_handle.clone();
    let app_handle_sync = app_handle.clone();
    
    sync_timer.start(slint::TimerMode::Repeated, Duration::from_millis(100), move || {
        if let (Some(overlay), Some(app)) = (overlay_handle_sync.upgrade(), app_handle_sync.upgrade()) {
            if app.get_show_overlay() {
                let is_fs = windowing::is_foreground_fullscreen();
                use raw_window_handle::HasWindowHandle;
                let slint_handle = overlay.window().window_handle();
                if let Ok(rwh) = slint_handle.window_handle() {
                    if let raw_window_handle::RawWindowHandle::Win32(win32) = rwh.as_raw() {
                        let hwnd = win32.hwnd.get() as *mut std::ffi::c_void;
                        // Relentlessly assert TOPMOST over the taskbar and handle fullscreen games
                        windowing::manage_z_order(hwnd, is_fs);
                        windowing::snap_to_taskbar(hwnd);
                        // Note: hole is only re-punched on drag/drop, not every tick
                    }
                }
            }
        }
    });

    // Hide the overlay by default on boot since the Settings window is our entrypoint now
    let _ = overlay_ui.hide();
    let result = app_ui.run();
    // Cleanup: restore taskbar to normal appearance when app exits
    windowing::restore_taskbar_region();
    result
}

// Helper to beautifully format bytes into KB/s or MB/s
fn format_network_speed(bytes_per_sec: u64) -> String {
    let kb = bytes_per_sec as f64 / 1024.0;
    if kb >= 1024.0 * 1000.0 {
        // Switch to GB/s after 1000 MB/s
        let gb = kb / (1024.0 * 1024.0);
        format!("{:.1} GB/s", gb)
    } else if kb >= 1024.0 * 100.0 {
        // Drop decimal for MB/s >= 100 to save space
        let mb = kb / 1024.0;
        format!("{:.0} MB/s", mb)
    } else if kb > 99.9 {
        // Use MB/s with decimal below 100 MB/s
        let mb = kb / 1024.0;
        format!("{:.1} MB/s", mb)
    } else {
        // Standard KB/s
        format!("{:.1} KB/s", kb)
    }
}

// Helper to manually parse a #RRGGBBAA or #RRGGBB string into a slint::Color
fn parse_hex_color(hex: &str) -> Result<slint::Color, &'static str> {
    let hex = hex.trim_start_matches('#');
    let len = hex.len();
    
    if len != 6 && len != 8 {
        return Err("Invalid hex length (must be 6 or 8)");
    }

    let r = u8::from_str_radix(&hex[0..2], 16).map_err(|_| "Invalid hex")?;
    let g = u8::from_str_radix(&hex[2..4], 16).map_err(|_| "Invalid hex")?;
    let b = u8::from_str_radix(&hex[4..6], 16).map_err(|_| "Invalid hex")?;
    
    let a = if len == 8 {
        u8::from_str_radix(&hex[6..8], 16).map_err(|_| "Invalid hex")?
    } else {
        255
    };

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
        // Use KEY_ALL_ACCESS and RegCreateKeyExW to ensure we have permissions and the key exists
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
                    let exe_str = format!("\"{}\"", exe_path.to_string_lossy());
                    let mut exe_wide: Vec<u16> = exe_str.encode_utf16().collect();
                    exe_wide.push(0);
                    
                    println!("[Registry] Setting auto-start: {}", exe_str);
                    let res = RegSetValueExW(
                        hkey,
                        app_name,
                        Some(0),
                        REG_SZ,
                        Some(std::slice::from_raw_parts(exe_wide.as_ptr() as *const u8, exe_wide.len() * 2)),
                    );
                    if res.is_ok() {
                        println!("[Registry] Successfully enabled auto-start.");
                    } else {
                        println!("[Registry] Failed to set registry value: {:?}", res);
                    }
                }
            } else {
                println!("[Registry] Removing auto-start entry.");
                let _ = RegDeleteValueW(hkey, app_name);
            }
            let _ = windows::Win32::System::Registry::RegCloseKey(hkey);
        } else {
            println!("[Registry] Failed to open/create registry key.");
        }
    }
}

fn refresh_overlay_hole(overlay_handle: slint::Weak<OverlayWindow>) {
    // Re-punch hole after Slint resizes the window (one-shot timer for layout sync)
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

