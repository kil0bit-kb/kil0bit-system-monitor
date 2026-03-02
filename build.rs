fn main() {
    // 1. Compile Slint UI
    slint_build::compile("ui/settings.slint").unwrap();

    // 2. Generate ICO from PNG if it doesn't exist (using image crate)
    let png_path = std::path::Path::new("ui/assets/icon.png");
    let ico_path = std::path::Path::new("icon.ico");

    let needs_rebuild = if !ico_path.exists() {
        true
    } else {
        let png_meta = png_path.metadata().ok();
        let ico_meta = ico_path.metadata().ok();
        if let (Some(pm), Some(im)) = (png_meta, ico_meta) {
            pm.modified().unwrap_or(std::time::SystemTime::UNIX_EPOCH) > 
            im.modified().unwrap_or(std::time::SystemTime::UNIX_EPOCH)
        } else {
            true
        }
    };

    if png_path.exists() && needs_rebuild {
        if let Ok(img) = image::open(png_path) {
            // Resize to 256x256 (common max size for Windows icons)
            let resized = img.resize_exact(256, 256, image::imageops::FilterType::Lanczos3);
            let _ = resized.save(ico_path);
            println!("cargo:rerun-if-changed=ui/assets/icon.png");
        }
    }

    // 3. Set Executable Icon and Metadata for Windows
    if std::env::var("CARGO_CFG_TARGET_OS").unwrap_or_default() == "windows" {
        let version = env!("CARGO_PKG_VERSION");
        let mut res = winres::WindowsResource::new();

        if ico_path.exists() {
            res.set_icon("icon.ico");
        }

        // Metadata visible in Windows file Properties > Details
        res.set("FileDescription",    "kil0bit System Monitor — Hardware Telemetry Overlay");
        res.set("ProductName",        "kil0bit System Monitor");
        res.set("CompanyName",        "KB - kil0bit");
        res.set("LegalCopyright",     "Copyright © 2026 KB - kil0bit. MIT License.");
        res.set("FileVersion",        version);
        res.set("ProductVersion",     version);
        res.set("OriginalFilename",   "kil0bit-system-monitor.exe");
        res.set("InternalName",       "kil0bit-system-monitor");

        res.compile().unwrap();
    }
}
