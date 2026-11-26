# ADR-002: Refactor to CommunityToolkit MVVM and Generic Host DI

- **Status:** Accepted and implemented
- **Date:** 2026-11-01

## Context

In v1.0.0 all UI logic of `RevitAgent.Host` lived in `MainWindow.xaml.cs`, over **907 lines** of code-behind. This caused:

1. **Single Responsibility violations.** One file opened sockets, rendered UI, captured screenshots via Win32, handled Telegram and saved configuration.
2. **No unit tests.** State was bound to WPF controls (`TextBox.Text`, `Button.IsEnabled`) and could not be tested automatically.
3. **UI freezes.** Named-pipe I/O and P/Invoke calls were sometimes dispatched on the UI thread.

## Decision

Remove the code-behind logic and restructure the Host:

1. **`CommunityToolkit.Mvvm`** with `[ObservableProperty]` and `[RelayCommand]`, split into five ViewModels: `MainViewModel`, `DashboardViewModel`, `SettingsViewModel`, `NotificationsViewModel`, `AuditLogViewModel`.
2. **Generic Host DI (`Microsoft.Extensions.Hosting`)**: the container is built in `App.xaml.cs` and registers ten singleton services (`LocalizationService`, `ThemeService`, `RevitProcessManager`, `SandboxService`, `EncryptedConfigService`, `TelegramNotificationService`, `Win32AutomationService`, `AuditLogService`, `HostIpcService`, `NotificationService`).
3. **Native WPF localization** with dynamic `ResourceDictionary` files for `vi-VN` and `en-US`, instead of hard-coded XAML strings.
4. **Toast notifications replace `MessageBox.Show`.** No modal Win32 dialog blocks the UI thread; the toast queue is synchronized with `BindingOperations.EnableCollectionSynchronization`.

## Consequences

- (+) `MainWindow.xaml.cs` shrank from 907 lines to under 80 (DataContext binding only).
- (+) Business logic is separate from the UI; the test suite grew to 126 passing tests.
- (+) The dashboard stays responsive.
- (-) Strict MVVM discipline: no ad-hoc event handlers in code-behind.
