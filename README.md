# BIMTECH Revit MCP — bắt đầu từ đầu

Đây là dự án mới để học và tự phát triển kết nối **Claude Desktop ↔ Revit 2025**. Mã ứng dụng được viết mới trong thư mục này. Repository GitHub ở thư mục cha chỉ dùng tham khảo; dự án này không import mã hoặc DLL của repository đó.

Các công cụ hiện có (đều chỉ đọc dữ liệu):

- `ping_revit`: kiểm tra cầu nối, phiên bản Revit và danh sách command; tham số `message` được trả lại để kiểm tra đường truyền tham số.
- `get_document_info`: đọc tên, đường dẫn, loại mô hình và view đang mở.
- `get_levels`: danh sách level và cao độ (mm).
- `list_categories`: số phần tử theo category, dùng để biết giá trị lọc hợp lệ.
- `get_elements`: tìm phần tử theo category, view, level, tên; có phân trang.
- `get_selected_elements`: các phần tử đang chọn trong Revit; có phân trang.
- `get_element_parameters`: parameter của tối đa 50 phần tử, lọc theo tên parameter, đơn vị cố định.

Quy ước chung: ID phần tử là chuỗi; danh sách trả `total`, `hasMore`, `nextOffset`; chiều dài mm, diện tích m², thể tích m³, góc độ.

## Bắt đầu đọc từ đâu?

1. Đọc [bản đồ thư mục và file](docs/01-folder-map.md).
2. Đọc [luồng một lệnh đi từ Claude đến Revit](docs/02-request-flow.md).
3. Mở `server/src/index.ts`, sau đó `tools/index.ts`, `tools/define.ts` và `revit-client.ts`.
4. Mở `addin/App.cs`, sau đó `Bridge/TcpBridge.cs`, `Commands/RevitRequestHandler.cs` và `Commands/CommandRegistry.cs`.
5. Build và chạy thử theo phần bên dưới.
6. Khi thêm công cụ, làm theo [03 — Thêm một công cụ mới](docs/03-add-a-tool.md).

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
│   │       ├── schemas.ts          # Schema dùng chung: ID, phân trang, bộ lọc
│   │       ├── diagnostics.ts      # ping_revit
│   │       ├── document.ts         # get_document_info, get_levels, list_categories
│   │       └── elements.ts         # get_elements, get_selected_elements, get_element_parameters
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
│   └── Commands/
│       ├── RevitRequestHandler.cs  # Chạy command qua ExternalEvent
│       ├── CommandRegistry.cs      # Danh sách command theo method
│       ├── IRevitCommand.cs        # Giao diện một command
│       ├── CommandContext.cs       # Document, params, cancellation
│       ├── CommandParams.cs        # Đọc và kiểm tra tham số
│       ├── RevitJson.cs            # Chuyển đổi Revit → JSON
│       ├── Common/                 # Dùng chung: phân trang, bộ lọc, đơn vị, parameter
│       ├── Diagnostics/            # ping
│       ├── Documents/              # get_document_info, get_levels, list_categories
│       └── Elements/               # get_elements, get_selected_elements, get_element_parameters
├── config/claude-desktop.example.json
└── docs/
    ├── 01-folder-map.md
    ├── 02-request-flow.md
    └── 03-add-a-tool.md
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
```

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
- 15 kiểm thử thành công: dữ liệu lớn/Unicode chia gói, lỗi JSON-RPC kèm `data`, sai ID, ngắt kết nối, timeout và thời gian chờ thêm, truyền `params`/`timeoutMs`, schema và annotations MCP, từ chối tham số sai trước khi gọi Revit, chuyển nguyên bộ lọc và phân trang của `get_elements`, từ chối `limit`/`offset`/ID sai, và đối chiếu method giữa server và add-in.
- Chưa cài add-in mới, chưa đổi cấu hình Claude, chưa kiểm thử trên Revit thật.
- Phần C# (`CommandParams`, registry, timeout, `Commands/Common`, 5 command đọc mới) mới được kiểm chứng bằng build, chưa có kiểm thử tự động vì cần Revit API.
- Bộ giao tiếp nội bộ chỉ hỗ trợ một request/response mỗi kết nối và ID dạng chuỗi. Đây là nền tảng học tập, chưa phải bản triển khai đầy đủ MCP/Revit cho sản xuất.

## Mở rộng từng bước

Đã có: tham số và timeout theo request, registry command, khung đọc dữ liệu (phân trang, lọc, đơn vị, giới hạn kích thước response). Tiếp theo: khung ghi (Transaction, dry-run, giới hạn số phần tử). Xem [03 — Thêm một công cụ mới](docs/03-add-a-tool.md) cho quy trình thêm từng công cụ.

Nền tảng API được đối chiếu với tài liệu Autodesk: [Revit 2025 dùng .NET 8](https://help.autodesk.com/cloudhelp/2025/ENU/Revit-WhatsNew/files/GUID-50024FD2-16BE-40BE-96E6-550294D9537D.htm), [ExternalEvent.Raise](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/13bf4411-c400-dcd2-458c-7f09357d9ecb.htm).
