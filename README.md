# BIMTECH Revit MCP — bắt đầu từ đầu

Đây là dự án mới để học và tự phát triển kết nối **Claude Desktop ↔ Revit 2025**. Mã ứng dụng được viết mới trong thư mục này. Repository GitHub ở thư mục cha chỉ dùng tham khảo; dự án này không import mã hoặc DLL của repository đó.

Các công cụ hiện có:

- `ping_revit`: kiểm tra cầu nối, phiên bản Revit và danh sách command; tham số `message` được trả lại để kiểm tra đường truyền tham số.
- `get_document_info`: đọc tên, đường dẫn, loại mô hình và view đang mở.
- `propose_set_parameters`: lập kế hoạch gán một giá trị parameter cho các phần tử đang chọn và mở cửa sổ duyệt trong Revit; **không sửa mô hình**.
- `get_plan`, `get_plan_status`, `open_plan_review`, `cancel_plan`: đọc kế hoạch, mở lại cửa sổ duyệt, huỷ kế hoạch (xem [04 — Kiến trúc](docs/04-architecture.md)). Chỉ người dùng duyệt được kế hoạch, trong cửa sổ Revit.

## Bắt đầu đọc từ đâu?

1. Đọc [bản đồ thư mục và file](docs/01-folder-map.md).
2. Đọc [luồng một lệnh đi từ Claude đến Revit](docs/02-request-flow.md).
3. Mở `server/src/index.ts`, sau đó `tools/index.ts`, `tools/define.ts` và `revit-client.ts`.
4. Mở `addin/App.cs`, sau đó `Bridge/TcpBridge.cs`, `Commands/RevitRequestHandler.cs` và `Commands/CommandRegistry.cs`.
5. Build và chạy thử theo phần bên dưới.
6. Khi thêm công cụ, làm theo [03 — Thêm một công cụ mới](docs/03-add-a-tool.md).
7. Trước khi làm công cụ ghi vào mô hình, đọc [04 — Kiến trúc đề xuất và duyệt](docs/04-architecture.md).

## Cấu trúc

```text
bimtech-revit-mcp/
├── README.md
├── Bimtech.RevitMcp.sln             # Mở phần C# bằng Visual Studio
├── server/                         # Chương trình Node.js giao tiếp với Claude
│   ├── package.json
│   ├── package-lock.json
│   ├── tsconfig.json
│   ├── src/
│   │   ├── index.ts                # Khởi động MCP
│   │   ├── config.ts               # Host, port, timeout
│   │   ├── revit-client.ts         # Gửi method + params + timeout qua TCP
│   │   └── tools/
│   │       ├── define.ts           # Kiểu RevitTool và cách đăng ký với MCP
│   │       ├── index.ts            # Danh sách allTools
│   │       ├── diagnostics.ts      # ping_revit
│   │       └── document.ts         # get_document_info
│   └── tests/
│       ├── connection.test.mjs     # Kiểm thử với cầu nối giả lập
│       └── contract.test.mjs       # Method server khớp command add-in
├── addin/                          # Thư viện C# được Revit nạp
│   ├── Bimtech.RevitMcp.Addin.csproj
│   ├── Bimtech.RevitMcp.addin       # Manifest đăng ký với Revit
│   ├── App.cs                      # Ribbon, mở/đóng cầu nối
│   ├── Bridge/TcpBridge.cs         # Nhận kết nối TCP
│   ├── Protocol/
│   │   ├── RpcRequest.cs           # Cấu trúc request
│   │   └── RpcException.cs         # Mã lỗi và exception
│   ├── Commands/
│   │   ├── RevitRequestHandler.cs  # Chạy command qua ExternalEvent
│   │   ├── CommandRegistry.cs      # Danh sách command theo method
│   │   ├── IRevitCommand.cs        # Giao diện một command
│   │   ├── CommandContext.cs       # Document, params, cancellation
│   │   ├── CommandParams.cs        # Đọc và kiểm tra tham số
│   │   ├── RevitJson.cs            # Chuyển đổi Revit → JSON
│   │   ├── Diagnostics/PingCommand.cs
│   │   ├── Documents/GetDocumentInfoCommand.cs
│   │   ├── Parameters/             # propose_set_parameters
│   │   └── Plans/                  # get_plan, get_plan_status, open_plan_review, cancel_plan
│   ├── Planning/                   # Kế hoạch: dữ liệu, luật trạng thái, lưu JSON
│   └── Ui/                         # Cửa sổ duyệt WPF modeless
├── tests/
│   └── Bimtech.RevitMcp.Planning.Tests/  # xUnit cho Planning/ và ViewModel, chạy không cần Revit
├── config/claude-desktop.example.json
└── docs/
    ├── 01-folder-map.md
    ├── 02-request-flow.md
    ├── 03-add-a-tool.md
    └── 04-architecture.md
```

## Build

Cần Node.js 20+, .NET SDK hỗ trợ .NET 8, và Revit 2025. Máy hiện tại đã có Node.js 24 và .NET SDK 10.

Từ thư mục **bimtech-revit-mcp**, chạy PowerShell:

```powershell
Push-Location server
npm.cmd ci --no-audit --no-fund
npm.cmd run build
npm.cmd test
Pop-Location

dotnet build Bimtech.RevitMcp.sln -c Release
dotnet test Bimtech.RevitMcp.sln -c Release --no-build
```

`dotnet test` chạy project `tests/Bimtech.RevitMcp.Planning.Tests`. Project này biên dịch lại các file trong `addin/Planning/`, `addin/Protocol/RpcException.cs`, `addin/Ui/ReviewViewModel.cs` và `addin/Ui/PlanItemRow.cs` mà không tham chiếu Revit API, nên kiểm thử được luật duyệt kế hoạch và các nút của cửa sổ duyệt mà không cần mở Revit.

TypeScript được biên dịch thành JavaScript trong `server/build/`. C# tạo DLL trong `addin/bin/Release/net8.0-windows/`. Những thư mục này được sinh tự động; hãy chỉnh mã trong `src` và các file `.cs`.

File `.csproj` tham chiếu `RevitAPI.dll` và `RevitAPIUI.dll` của Revit trên máy. Nếu cài ở đường dẫn khác:

```powershell
dotnet build Bimtech.RevitMcp.sln -c Release '-p:RevitInstallDir=D:\Autodesk\Revit 2025'
```

Build không tự cài hoặc thay add-in trên máy.

## Cài vào Revit để thử

Đóng Revit trước khi chép DLL. Trong `%APPDATA%\Autodesk\Revit\Addins\2025\`, tạo cấu trúc:

```text
2025/
├── Bimtech.RevitMcp.addin          # Chép từ addin/
└── Bimtech.RevitMcp/
    └── Bimtech.RevitMcp.Addin.dll  # Chép từ addin/bin/Release/net8.0-windows/
```

Revit cung cấp các DLL API của chính nó khi chạy, nên không chép `RevitAPI.dll` hoặc `RevitAPIUI.dll` vào thư mục add-in.

Mở Revit, mở một mô hình thử nghiệm, vào **Add-Ins → BIMTECH MCP → Start / Stop MCP Bridge**. Khi thông báo cổng `8081` xuất hiện, đóng hộp thoại đó để Revit tiếp tục xử lý các lệnh.

## Kết nối Claude Desktop

Trong `%APPDATA%\Claude\claude_desktop_config.json`, thêm entry `bimtech-revit-mcp` từ [file cấu hình mẫu](config/claude-desktop.example.json). Giữ các cấu hình khác nếu có. Đường dẫn trong mẫu dành cho máy hiện tại; sửa lại khi di chuyển dự án hoặc dùng máy khác.

Khởi động lại Claude Desktop rồi thử:

> Dùng ping_revit để kiểm tra cầu nối BIMTECH với Revit.

> Dùng get_document_info để đọc thông tin mô hình đang mở.

Cổng mặc định của dự án mới là `8081`. Cổng này được khai báo ở `addin/App.cs` và `server/src/config.ts`. Nếu đổi cổng, cập nhật cả hai phía. Biến môi trường `REVIT_MCP_HOST`, `REVIT_MCP_PORT`, `REVIT_MCP_TIMEOUT_MS` điều khiển phía server.

## Đã kiểm chứng và giới hạn

- Build TypeScript thành công.
- Build add-in Revit 2025 thành công. Có hai cảnh báo `MSB3277` về phiên bản dependency Microsoft.VisualBasic và WindowsBase do tham chiếu DLL cài trên máy; chưa xác minh ảnh hưởng trong Revit thật.
- 31 kiểm thử xUnit thành công. 8 kiểm thử cho ViewModel của cửa sổ duyệt: tách mục Blocked sang tab riêng, mô tả phần tử và thay đổi, chỉ bật **Duyệt kế hoạch** khi mọi mục đã có quyết định, giữ dòng đang chọn sau khi bấm nút, hiện lỗi thành thông báo, huỷ từ Claude cập nhật cửa sổ. 23 kiểm thử cho `Planning/`: duyệt/bỏ từng mục, mục Blocked không duyệt được, chỉ duyệt kế hoạch khi mọi mục cần xem đã có quyết định, `approvalId` mất hiệu lực khi sửa lại, huỷ xoá phê duyệt, mã băm các mục đã duyệt giữ nguyên sau khi lưu/đọc tiếng Việt, đọc file kế hoạch phiên bản 1, chặn `planId` dạng đường dẫn.
- Cửa sổ duyệt đã được dựng thử ngoài Revit với dữ liệu mẫu để kiểm tra bố cục. Phần nối với Revit (`ReviewWindowHost`, `FocusHandler`, `OpenPlanReviewCommand`) đã build, chưa chạy trong Revit thật.
- 16 kiểm thử server thành công, gồm một kiểm thử bảo đảm không command nào trong `addin/Commands/` gọi `Approve`. Các mục: dữ liệu lớn/Unicode chia gói, lỗi JSON-RPC kèm `data`, sai ID, ngắt kết nối, timeout và thời gian chờ thêm, truyền `params`/`timeoutMs`, schema và annotations MCP, từ chối tham số sai (kể cả `planId` sai dạng) trước khi gọi Revit, chuyển bộ lọc của `get_plan`, và đối chiếu method giữa server và add-in.
- Phần `Planning/` không phụ thuộc Revit (`Plan`, `PlanService`, `JsonPlanStore`) đã được biên dịch với .NET 8 và chạy thử ngoài Revit: lưu/đọc JSON giữ tiếng Việt, đếm tóm tắt, huỷ, chặn chuyển trạng thái sai, chặn `planId` dạng đường dẫn. Các command mới (`Commands/Plans/`, `Commands/Parameters/`) và `PlanStorageLocation` dùng Revit API đã build thành công với Revit 2025, chưa thử trên Revit thật.
- Chưa cài add-in mới, chưa đổi cấu hình Claude, chưa kiểm thử trên Revit thật.
- Phần C# (`CommandParams`, registry, timeout trong bridge) mới được kiểm chứng bằng build, chưa có kiểm thử tự động vì cần Revit API.
- Bộ giao tiếp nội bộ chỉ hỗ trợ một request/response mỗi kết nối và ID dạng chuỗi. Đây là nền tảng học tập, chưa phải bản triển khai đầy đủ MCP/Revit cho sản xuất.

## Mở rộng từng bước

Đã có: tham số và timeout theo request, registry command. Tiếp theo: khung đọc dữ liệu (phân trang, lọc, đơn vị), rồi khung ghi theo mô hình đề xuất, duyệt, ghi (xem [04 — Kiến trúc](docs/04-architecture.md)). Xem [03 — Thêm một công cụ mới](docs/03-add-a-tool.md) cho quy trình thêm từng công cụ.

Nền tảng API được đối chiếu với tài liệu Autodesk: [Revit 2025 dùng .NET 8](https://help.autodesk.com/cloudhelp/2025/ENU/Revit-WhatsNew/files/GUID-50024FD2-16BE-40BE-96E6-550294D9537D.htm), [ExternalEvent.Raise](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/13bf4411-c400-dcd2-458c-7f09357d9ecb.htm).
