# RevitAgent Host: icon files

This folder holds optional PNG icons for the RevitAgent Host dashboard.

## Loading rules

Icons are loaded without locking the file, so a PNG can be copied in or replaced while the dashboard is running.

Probe order:
1. `%LOCALAPPDATA%\RevitAgent\Icons\{IconName}.png` (per-user override)
2. `<install folder>\Resources\Icons\{IconName}.png`
3. The built-in `Resources\Icons\{IconName}.png`
4. **Fallback:** when no PNG exists, the dashboard draws a vector shape (StreamGeometry) or the glyph below. A missing icon never breaks the UI.

## File names

| PNG file | Recommended size | Used for | Fallback glyph |
| :--- | :--- | :--- | :--- |
| `server_start.png` | 16, 24 or 32 px | Start the named-pipe server | ▶️ |
| `server_stop.png` | 16, 24 or 32 px | Stop the named-pipe server | ⏹️ |
| `launch_revit.png` | 16, 24 or 32 px | Launch Autodesk Revit | 🚀 |
| `kill_revit.png` | 16, 24 or 32 px | Stop a Revit process | 🛑 |
| `screenshot.png` | 16, 24 or 32 px | Take a Revit screenshot | 📸 |
| `dismiss_dialog.png` | 16, 24 or 32 px | Dismiss a Win32 modal dialog (#32770) | 🛡️ |
| `emergency_escape.png` | 16, 24 or 32 px | Emergency stop (3× Esc) | ⚡ |
| `preflight.png` | 16, 24 or 32 px | Environment preflight check | 🔍 |
| `sandbox.png` | 16, 24 or 32 px | Open the sandbox folder | 📁 |
| `clear_logs.png` | 16, 24 or 32 px | Clear the displayed log | 🗑️ |
| `export_logs.png` | 16, 24 or 32 px | Export the log to JSON | 💾 |
| `theme_toggle.png` | 16, 24 or 32 px | Switch dark / light / system theme | 🌓 |
| `lang_vi.png` | 16, 24 or 32 px | Switch the UI to Vietnamese | 🇻🇳 |
| `lang_en.png` | 16, 24 or 32 px | Switch the UI to English | 🇬🇧 |

## Image format

- `.png` with a transparent background (alpha channel).
- Square (1:1).
