# RevitAgent

[English](README.md) (bản gốc, có giá trị chính thức; bản tiếng Việt này chỉ là bản dịch hỗ trợ)

> Lưu ý: README tiếng Anh là bản chuẩn. Nếu hai bản khác nhau, theo bản tiếng Anh.

**Cổng MCP cho phép bất kỳ AI agent nào điều khiển Autodesk Revit (2022-2026) và các add-in đi kèm, không cần mã nguồn của chúng.**
Bật dashboard (hoặc để AI client tự bật). Mọi agent hỗ trợ MCP, như Claude Code, Claude Desktop, Cursor hay Antigravity,
đều có thể quan sát Revit, thao tác như người dùng, đọc model để kiểm chứng kết quả, rồi chạy theo kịch bản hoặc tự xoay xở.

## Tính năng chính

- **Không phụ thuộc agent:** MCP thuần qua stdio; luật an toàn nằm ở gateway chứ không ở client.
- **Chạy với mọi bản Revit và mọi add-in:** tầng UI Automation không cần cài gì vào Revit; add-in Bridge tuỳ chọn bổ sung đọc ở mức API và ribbon của mọi add-in.
- **Quan sát, hành động, kiểm chứng:** ảnh chụp/snapshot UI, dò hộp thoại, đọc model/schedule để kiểm tra kết quả thao tác UI.
- **An toàn mặc định:** chỉ mở model từ bản sao sandbox, không bao giờ lưu đè; hộp thoại cần người quyết thì AI không bao giờ tự trả lời.
- **Kịch bản và macro:** kịch bản là một thư mục có `scenario.yaml` do gateway chạy; macro là công thức đã kiểm chứng, truy cập qua hai tool.
- **Ghi và phát lại:** ghi một lần AI giải, phát lại không cần AI.
- **Bề mặt tool nhỏ cho model nhỏ:** 67 tool; profile `core` mặc định chỉ liệt kê 18.
- **Có audit:** mọi lời gọi tool đều được ghi; bằng chứng của mỗi lần chạy nằm trong run store.

## Cách hoạt động

```
AI client --stdio MCP--> RevitAgent.Shim --\\.\pipe\revitagent-mcp--> RevitAgent.Host (dashboard WPF + gateway)
                         (hoặc --headless: gateway chạy ngay trong Shim)
                                                    |
        Tầng UI (mọi Revit, không cần cài gì)       |   Tầng API (add-in Bridge, pipe revitagent-bridge-<pid>)
        UI Automation ngoài tiến trình,             |   đọc model và schedule, ribbon của MỌI add-in,
        ảnh chụp, dò hộp thoại Win32                |   PostCommand, mở/đóng tài liệu sandbox
```

| Project | Vai trò |
|---|---|
| `src/RevitAgent.Shim` | Điểm vào MCP stdio cho client; chuyển tiếp tới Host hoặc chạy gateway headless. |
| `src/RevitAgent.Host` | Dashboard WPF (cài Bridge, các Revit đang chạy, quyền, audit) và gateway. |
| `src/RevitAgent.Core` | Logic gateway: tool, policy, UI automation, xử lý tiến trình/hộp thoại Revit, sandbox, kịch bản, macro. |
| `src/RevitAgent.Bridge` | Add-in chạy trong Revit, mỗi năm Revit một bản build (net48 cho 2022-2024, net8 cho 2025-2026). |
| `src/RevitAgent.Contracts` | Hằng số và mã wire dùng chung (netstandard2.0). |
| `tools/RevitAgent.E2E` | Bộ kiểm thử đầu-cuối trên Revit thật; bằng chứng ghi vào `%REVITAGENT_HOME%\runs`. |

## Bắt đầu nhanh

Cần Windows, .NET 10 SDK (thêm .NET 8 SDK để build Bridge cho Revit 2025/2026) và Revit 2022-2026.

```powershell
dotnet build RevitAgent.slnx -c Debug
dotnet test tests\RevitAgent.Tests\RevitAgent.Tests.csproj     # test UI/hộp thoại cần desktop đã mở khoá
src\RevitAgent.Host\bin\Debug\net10.0-windows\RevitAgent.Host.exe   # dashboard: cài Bridge, xem Revit, kết nối AI
```

1. **Cài Bridge (tuỳ chọn nhưng nên làm):** nút *Install Bridge* trên dashboard, hoặc tool `bridge_install`. Build đủ các năm bằng `scripts\build-bridge.ps1`. Revit đang mở phải khởi động lại.
2. **Kết nối AI client:** mọi client chạy Shim qua stdio, ví dụ

   ```powershell
   claude mcp add revitagent -- <repo>\src\RevitAgent.Shim\bin\Debug\net10.0-windows\RevitAgent.Shim.exe --profile core
   ```

   Cấu hình cho Claude Code, Claude Desktop, Cursor, Antigravity (agy), Codex CLI, client MCP khác và model mở, cùng cách chọn profile theo model: mục 1 của [docs/agent-guide/README.md](docs/agent-guide/README.md).
3. **Profile tool:** `--profile core|full` (hoặc biến môi trường `REVITAGENT_PROFILE`; mặc định `core`). `core` liệt kê 18 tool, `full` liệt kê cả 67. Gọi thẳng một tool ngoài profile sẽ bị từ chối (`NOT_FOUND`); macro và kịch bản vẫn dùng được các tool đó ở bên trong.
4. **Nghiệm thu trên Revit thật:** mở Revit 2024, rồi `powershell -ExecutionPolicy Bypass -File scripts\acceptance.ps1`.

## Nhóm tool

| Nhóm | Tool | Dùng để |
|---|---|---|
| `gateway_*`, `bridge_*` | gateway_get_info; bridge_status, list_installs, install, uninstall | Tự mô tả gateway, cài Bridge cho từng năm Revit. |
| `revit_*` | list_installed, list_instances, launch, close, get_state, wait_ready, list_dialogs, handle_dialog | Tìm, mở, đóng Revit; cổng "sẵn sàng"; hộp thoại. |
| `ui_*` | list_windows, snapshot, find, inspect, click, type, select, toggle, expand, keys, scroll, wait, expect, read_table, screenshot, run_script | Điều khiển Revit và cửa sổ add-in. |
| `doc_*`, `model_*`, `schedule_*`, `view_*`, `selection_*` | info, open, close, query_elements, get_element, schedule_read, schedule_create, ... | Đọc model qua Bridge; `schedule_create` chỉ trên bản sao sandbox. |
| `ribbon_list_items`, `command_post` | | Thấy và chạy nút ribbon của bất kỳ add-in nào. |
| `sandbox_*` | create_run, copy_model, list_runs | Bản sao model kèm băm SHA-256 chứng minh bản gốc không đổi. |
| `scenario_*`, `recorder_*` | validate, run, list_runs; start, stop, export | Kịch bản có kiểm tra và báo cáo; ghi và phát lại. |
| `macro_list`, `macro_run` | `create-schedule`, `run-addin-command`, `run-revit-command`, `read-schedule` có sẵn | Công thức nhiều bước đã kiểm chứng ([macros](docs/agent-guide/macros.md)). |
| `human_ask` | | Hỏi chủ máy một câu. |

Danh mục đầy đủ: [docs/agent-guide/tool-catalog.md](docs/agent-guide/tool-catalog.md).

## Nguyên tắc an toàn

Do gateway thực thi, đúng cho mọi client:

1. **Hộp thoại cần người quyết thì AI không bao giờ tự trả lời:** bảo mật, add-in chưa ký, license, khôi phục, xoá/ghi đè đều trả `PERMISSION_DENIED`. Chỉ có các ngoại lệ đã ghi tài liệu:
   - Với Revit **do gateway tự mở**, hộp thoại "Security - Unsigned Add-In" được trả lời theo danh sách add-in được phép của chủ máy (sửa ở dashboard): có trong danh sách thì `Load Once`, ngoài danh sách thì `Do Not Load`, không bao giờ `Always Load`. AI không có tool nào sửa danh sách; mỗi lần bấm đều được audit kèm ảnh chụp.
   - Gateway có thể tự đóng an toàn các hộp thoại vô hại đang chặn chính thao tác của nó trong Revit do nó mở (xem [revit-process-and-dialogs.md](docs/agent-guide/revit-process-and-dialogs.md)).
   - Tuỳ chọn: khi chạy không người trực, hộp thoại bị cấm có thể được chuyển cho chủ máy, người này chọn nút.
2. **Chỉ đóng/kill Revit do gateway mở.** Với Revit của chính người dùng, AI luôn được đọc, nhưng chỉ được thao tác (click, gõ, chạy lệnh, mở/đóng tài liệu) khi chủ máy bật **"Let AI act on my Revit"** trên dashboard. Quyền này có thời hạn, chỉ lưu trong bộ nhớ Host, thu hồi được bất cứ lúc nào, và mỗi lần dùng đều được audit.
3. **Model chỉ mở từ sandbox** (`%REVITAGENT_HOME%\runs`, mặc định `%LOCALAPPDATA%\RevitAgent\runs`) và không bao giờ lưu đè bản gốc.
4. **Mọi lời gọi tool đều được audit** (`%LOCALAPPDATA%\RevitAgent\audit\*.jsonl`); trần quyền (Read/Interact/Write/Admin) chỉnh ở dashboard.
5. **Không có tool nào chạy mã tuỳ ý.**
6. **Cài Bridge cần chủ máy duyệt.** `bridge_install` ghi manifest của add-in và tin cậy trước AddInId của Bridge trong khoá registry CodeSigning theo người dùng của Revit, nên Revit không hiện hộp thoại add-in chưa ký cho Bridge. Việc này cần chủ máy duyệt, và `bridge_uninstall` xoá mục tin cậy đó.

## Tài liệu

| Ở đâu | Nội dung |
|---|---|
| [docs/README.md](docs/README.md) | Mục lục. |
| [docs/agent-guide/](docs/agent-guide/README.md) | Cho AI agent và người viết kịch bản: kết nối client, recipes, UI automation, hộp thoại, kịch bản, macro. |
| [docs/architecture/decisions/](docs/architecture/decisions/) | Các quyết định kiến trúc (ADR). |
| [docs/components/bridge-v2.md](docs/components/bridge-v2.md) | Add-in Bridge. |
| [docs/contributing/](docs/contributing/index.md) | Quy tắc Git, code, test, PR. |
| [examples/scenarios/](examples/scenarios/README.md) | Kịch bản mẫu. |

## Trạng thái

Đã kiểm chứng trực tiếp trên Revit 2024.3 ngày 2026-11-26 với Claude Code (Haiku và Opus) và Antigravity CLI (Gemini 3.8 Flash).
Bản phát hành sớm, chỉ chạy trên Windows. Đã chạy thử trực tiếp trên Revit 2024.3; Bridge build được cho Revit 2022-2026.

Bridge biên dịch với các gói NuGet reference-assembly của cộng đồng `Nice3point.Revit.Api.*`, nên build không cần cài Revit, và không có DLL nào của Autodesk được đóng kèm trong repo hay trong kết quả build.

## Giấy phép

Giấy phép: [MIT](LICENSE). Gói bên thứ ba: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Báo lỗi bảo mật: [SECURITY.md](SECURITY.md). Đóng góp: [CONTRIBUTING.md](CONTRIBUTING.md).

## Nhãn hiệu

Autodesk và Revit là nhãn hiệu đã đăng ký của Autodesk, Inc. RevitAgent là dự án độc lập, không liên kết, tài trợ hay được Autodesk chứng thực.
