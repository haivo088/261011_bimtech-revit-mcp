# BIMTECH Revit MCP — bắt đầu từ đầu

Đây là dự án mới để học và tự phát triển kết nối **Claude Desktop ↔ Revit 2025**. Mã ứng dụng được viết mới trong thư mục này. Repository GitHub ở thư mục cha chỉ dùng tham khảo; dự án này không import mã hoặc DLL của repository đó.

Công cụ đọc dữ liệu:

- `ping_revit`: kiểm tra cầu nối, phiên bản Revit và danh sách command; tham số `message` được trả lại để kiểm tra đường truyền tham số.
- `get_document_info`: đọc tên, đường dẫn, loại mô hình và view đang mở.
- `get_levels`: danh sách level và cao độ (mm).
- `list_categories`: số phần tử theo category, dùng để biết giá trị lọc hợp lệ.
- `get_elements`: tìm phần tử theo category, view, level, tên; có phân trang.
- `get_selected_elements`: các phần tử đang chọn trong Revit; có phân trang.
- `get_element_parameters`: parameter của tối đa 50 phần tử, lọc theo tên parameter, đơn vị cố định.

Công cụ đề xuất, duyệt và ghi (xem [04 — Kiến trúc đề xuất và duyệt](docs/04-architecture.md)):

- `propose_set_parameters`: lập kế hoạch gán một giá trị parameter cho các phần tử đang chọn và mở cửa sổ duyệt trong Revit; **không sửa mô hình**.
- `get_plan`, `get_plan_status`, `open_plan_review`, `cancel_plan`: đọc kế hoạch, mở lại cửa sổ duyệt, huỷ kế hoạch (xem [04 — Kiến trúc](docs/04-architecture.md)). Chỉ người dùng duyệt được kế hoạch, trong cửa sổ Revit.
- `commit_plan`: ghi các mục người dùng đã duyệt vào mô hình, cần `approvalId` từ `get_plan_status`; cả lần ghi là một bước Undo. Từ chối khi mô hình đã đổi kể từ lúc propose.

Công cụ dựng model kết cấu từ CAD (xem [05 — Dựng model kết cấu từ CAD](docs/05-cad-to-structure.md)):

- `suggest_levels_from_folder`: đề xuất bảng tầng (tên, cao độ) từ tên các file DWG trong một thư mục.
- `propose_levels`: kế hoạch tạo Level + Structural Plan `KC - <tên>` cho từng tầng và nhập CAD tầng đó vào plan (đổi tên level template cùng cao độ thay vì tạo trùng, đề xuất xoá level thừa); duyệt rồi `commit_plan`.
- `propose_grids`: kế hoạch dựng trục từ layer Grid/Trục/Axis của một CAD và dời CAD các tầng để trục 1-A trùng Project Base Point; duyệt rồi `commit_plan`.
- `scan_cad_walls` / `propose_walls`: vách theo cặp line song song, nhóm theo tên Text + bề dày; mỗi nhóm một mục duyệt; tự tạo Type `BTC-VachKC_<b>` khi thiếu.
- `propose_floors`: ô sàn dò từ tim dầm và vách đã có trên một tầng; mỗi ô một mục duyệt; Type mặc định `BTC_SanKC_150`.
- `list_cad_sources`: các file CAD import/link trong model.
- `load_default_family`: nạp family chuẩn công ty (`kind`: column, beam, pile); với cọc còn tạo Type đài/lót mặc định.
- `scan_cad_columns` / `propose_columns`: cột chữ nhật theo ký hiệu và kích thước, mỗi nhóm một mục duyệt; vị trí làm tròn 5 mm theo trục.
- `scan_cad_beams` / `place_beams`: dầm theo cặp line song song, tên và kích thước đọc từ Text Note.
- `scan_cad_piles` / `propose_piles`: cọc tròn + đài móng, mỗi đài một mục duyệt; chiều dày đài theo ký hiệu bản vẽ hoặc người dùng; làm tròn 5 mm theo trục; lớp lót; join cọc với đài của nó.
- Mọi `place_*` mặc định chỉ xem trước (`dryRun`); chạy thật trả `batchId`.
- `undo_batch`: xóa đúng những gì một lần ghi đã tạo.

Công cụ cho người phát triển (`run_revit_query`, `analyze_cad`, `run_recognition`, `export_cad_case`) nằm trong DLL riêng, chỉ cài trên máy người phát triển và chỉ hiện khi `BIMTECH_DEV=1`; xem [07 — Bộ công cụ cho người phát triển](docs/07-dev-tools.md).

Phần thuật toán CAD nằm trong lõi `addin/StructuralCore/`, chép từ repo `RevitAddinManager` và từ nay được duy trì riêng trong repo này; build không cần repo nào khác.

Quy ước chung: ID phần tử là chuỗi; danh sách trả `total`, `hasMore`, `nextOffset`; giá trị đo dùng đơn vị SI cố định ghi trong trường `unit` (mm, m², m³, độ, kg, kN, MPa...), kèm `display` theo cài đặt project. Lọc `categoryType: "Model"` để chỉ lấy phần tử vật lý.

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
│   │   ├── availability.ts         # Ẩn tool bị tắt trong Settings của Revit
│   │   └── tools/
│   │       ├── define.ts           # Kiểu RevitTool và cách đăng ký với MCP
│   │       ├── index.ts            # Danh sách allTools
│   │       ├── schemas.ts          # Schema dùng chung: ID, phân trang, bộ lọc
│   │       ├── diagnostics.ts      # ping_revit
│   │       ├── document.ts         # get_document_info, get_levels, list_categories
│   │       ├── elements.ts         # get_elements, get_selected_elements, get_element_parameters
│   │       └── structural.ts       # CAD → kết cấu
│   └── tests/
│       ├── connection.test.mjs     # Kiểm thử với cầu nối giả lập
│       └── contract.test.mjs       # Method server khớp command add-in
├── addin/                          # Thư viện C# được Revit nạp
│   ├── Bimtech.RevitMcp.Addin.csproj
│   ├── Bimtech.RevitMcp.addin       # Manifest đăng ký với Revit
│   ├── App.cs                      # Ribbon (MCP Bridge, Settings), mở/đóng cầu nối
│   ├── Settings/                   # settings.json, CommandPolicy, cửa sổ Settings (WPF)
│   ├── Bridge/TcpBridge.cs         # Nhận kết nối TCP
│   ├── Protocol/
│   │   ├── RpcRequest.cs           # Cấu trúc request
│   │   └── RpcException.cs         # Mã lỗi và exception
│   ├── Commands/
│   │   ├── RevitRequestHandler.cs  # Chạy command qua ExternalEvent, kiểm tra Settings trước khi chạy
│   │   ├── CommandRegistry.cs      # Danh sách command theo method
│   │   ├── IRevitCommand.cs        # Giao diện một command (Method, Access, Execute)
│   │   ├── CommandContext.cs       # Document, params, settings, cancellation
│   │   ├── CommandParams.cs        # Đọc và kiểm tra tham số
│   │   ├── RevitJson.cs            # Chuyển đổi Revit → JSON
│   │   ├── Common/                 # Dùng chung: phân trang, bộ lọc, đơn vị, parameter, WriteSession
│   │   ├── Diagnostics/            # ping
│   │   ├── Documents/              # get_document_info, get_levels, list_categories
│   │   ├── Elements/               # get_elements, get_selected_elements, get_element_parameters
│   │   ├── Parameters/             # propose_set_parameters
│   │   ├── Plans/                  # get_plan, get_plan_status, open_plan_review, cancel_plan, commit_plan
│   │   └── Structural/             # CAD → kết cấu: scan/place cột, dầm, cọc, undo_batch
│   ├── Planning/                   # Kế hoạch: dữ liệu, luật trạng thái, lưu JSON, commit, đếm thay đổi model
│   ├── Settings/                   # settings.json, CommandPolicy, cửa sổ Settings (WPF)
│   └── Ui/                         # Cửa sổ duyệt WPF modeless
├── tests/
│   └── Bimtech.RevitMcp.Planning.Tests/  # xUnit cho Planning/ và ViewModel, chạy không cần Revit
├── config/claude-desktop.example.json
└── docs/
    ├── 01-folder-map.md
    ├── 02-request-flow.md
    ├── 03-add-a-tool.md
    ├── 04-architecture.md
    ├── 05-cad-to-structure.md
    └── 06-settings.md
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

Cách nhanh: đóng Revit rồi chạy `.\scripts\install.ps1` ở gốc repo. Script build add-in và server rồi chép đúng file vào thư mục add-in; thêm `-Dev` để cài cả bộ công cụ cho người phát triển ([07](docs/07-dev-tools.md)). Danh sách thử trọn bộ: [08 — Kế hoạch thử trong Revit](docs/08-revit-test-plan.md).

Cài tay: đóng Revit trước khi chép DLL. Trong `%APPDATA%\Autodesk\Revit\Addins\2025\`, tạo cấu trúc:

```text
2025/
├── Bimtech.RevitMcp.addin          # Chép từ addin/
└── Bimtech.RevitMcp/
    └── Bimtech.RevitMcp.Addin.dll  # Chép từ addin/bin/Release/net8.0-windows/
```

Revit cung cấp các DLL API của chính nó khi chạy, nên không chép `RevitAPI.dll` hoặc `RevitAPIUI.dll` vào thư mục add-in.

Mở Revit, mở một mô hình thử nghiệm, vào **Add-Ins → BIMTECH MCP → MCP Bridge**. Nhãn nút đổi thành "Đang chạy" khi cầu nối đã mở. Nút **Settings** bật chế độ chỉ đọc, tắt từng lệnh, đổi cổng, hoặc cho cầu nối tự bật khi mở Revit; xem [06 — Cài đặt và quản lý lệnh](docs/06-settings.md).

## Kết nối Claude Desktop

Trong `%APPDATA%\Claude\claude_desktop_config.json`, thêm entry `bimtech-revit-mcp` từ [file cấu hình mẫu](config/claude-desktop.example.json). Giữ các cấu hình khác nếu có. Đường dẫn trong mẫu dành cho máy hiện tại; sửa lại khi di chuyển dự án hoặc dùng máy khác.

Khởi động lại Claude Desktop rồi thử:

> Dùng ping_revit để kiểm tra cầu nối BIMTECH với Revit.

> Dùng get_document_info để đọc thông tin mô hình đang mở.

Cổng mặc định là `8081`, đổi trong cửa sổ Settings của Revit. Server đọc cùng file `%APPDATA%\BIMTECH\RevitMcp\settings.json` nên không cần sửa ở nơi khác; khởi động lại Claude Desktop sau khi đổi cổng. Biến môi trường phía server được liệt kê trong [docs/05](docs/06-settings.md).

## Đã kiểm chứng và giới hạn

- Build TypeScript thành công.
- Build add-in Revit 2025 thành công. Có hai cảnh báo `MSB3277` về phiên bản dependency Microsoft.VisualBasic và WindowsBase do tham chiếu DLL cài trên máy; chưa xác minh ảnh hưởng trong Revit thật.
- 41 kiểm thử xUnit thành công. 10 kiểm thử cho `commit_plan` (`PlanCommitter` với writer giả): chỉ ghi mục đã duyệt theo thứ tự duyệt, commit lần hai trả kết quả cũ, từ chối kế hoạch chưa duyệt, `approvalId` cũ và mục bị sửa sau khi duyệt, chuyển `Stale` khi mô hình đổi hoặc Revit khởi động lại, kế hoạch không có `modelStamp` coi là cũ, một dòng audit mỗi lần ghi, `overrides` đè lên `params`. Còn lại: 8 kiểm thử cho ViewModel của cửa sổ duyệt: tách mục Blocked sang tab riêng, mô tả phần tử và thay đổi, chỉ bật **Duyệt kế hoạch** khi mọi mục đã có quyết định, giữ dòng đang chọn sau khi bấm nút, hiện lỗi thành thông báo, huỷ từ Claude cập nhật cửa sổ. 23 kiểm thử cho `Planning/`: duyệt/bỏ từng mục, mục Blocked không duyệt được, chỉ duyệt kế hoạch khi mọi mục cần xem đã có quyết định, `approvalId` mất hiệu lực khi sửa lại, huỷ xoá phê duyệt, mã băm các mục đã duyệt giữ nguyên sau khi lưu/đọc tiếng Việt, đọc file kế hoạch phiên bản 1, chặn `planId` dạng đường dẫn.
- Cửa sổ duyệt đã được dựng thử ngoài Revit với dữ liệu mẫu để kiểm tra bố cục. Phần nối với Revit (`ReviewWindowHost`, `FocusHandler`, `OpenPlanReviewCommand`) đã build, chưa chạy trong Revit thật.
- 16 kiểm thử server thành công, gồm một kiểm thử bảo đảm không command nào trong `addin/Commands/` gọi `Approve`. Các mục: dữ liệu lớn/Unicode chia gói, lỗi JSON-RPC kèm `data`, sai ID, ngắt kết nối, timeout và thời gian chờ thêm, truyền `params`/`timeoutMs`, schema và annotations MCP, từ chối tham số sai (kể cả `planId` sai dạng) trước khi gọi Revit, chuyển bộ lọc của `get_plan`, và đối chiếu method giữa server và add-in.
- Phần `Planning/` không phụ thuộc Revit (`Plan`, `PlanService`, `JsonPlanStore`) đã được biên dịch với .NET 8 và chạy thử ngoài Revit: lưu/đọc JSON giữ tiếng Việt, đếm tóm tắt, huỷ, chặn chuyển trạng thái sai, chặn `planId` dạng đường dẫn. Các command mới (`Commands/Plans/`, `Commands/Parameters/`) và `PlanStorageLocation` dùng Revit API đã build thành công với Revit 2025, chưa thử trên Revit thật.
- Kiểm thử server cho phần đọc dữ liệu, Settings và kết cấu (thêm 3 cho công cụ kết cấu: dryRun mặc định, annotations ghi/xóa, từ chối tham số sai): dữ liệu lớn/Unicode chia gói, lỗi JSON-RPC kèm `data`, sai ID, ngắt kết nối, timeout và thời gian chờ thêm, truyền `params`/`timeoutMs`, schema và annotations MCP, từ chối tham số sai trước khi gọi Revit, chuyển nguyên bộ lọc và phân trang của `get_elements`, từ chối `limit`/`offset`/ID sai, và đối chiếu method giữa server và add-in.
- Chưa cài add-in mới, chưa đổi cấu hình Claude, chưa kiểm thử trên Revit thật.
- Phần C# (`CommandParams`, registry, timeout, `Commands/Common`, 5 command đọc mới) mới được kiểm chứng bằng build, chưa có kiểm thử tự động vì cần Revit API.
- Bộ giao tiếp nội bộ chỉ hỗ trợ một request/response mỗi kết nối và ID dạng chuỗi. Đây là nền tảng học tập, chưa phải bản triển khai đầy đủ MCP/Revit cho sản xuất.

## Mở rộng từng bước

Đã có: tham số và timeout theo request, registry command, khung đọc dữ liệu (phân trang, lọc, đơn vị, giới hạn kích thước response), Settings và chế độ chỉ đọc, dựng cột/dầm/cọc từ CAD (`dryRun` + `undo_batch`), và khung đề xuất → duyệt trong Revit → `commit_plan`. Tiếp theo: chuyển cột/dầm/cọc sang `propose_*` + `IPlanAction`, rồi level/lưới, vách, sàn (xem [04](docs/04-architecture.md) và [05](docs/05-cad-to-structure.md)). Xem [03 — Thêm một công cụ mới](docs/03-add-a-tool.md) cho quy trình thêm từng công cụ.

Nền tảng API được đối chiếu với tài liệu Autodesk: [Revit 2025 dùng .NET 8](https://help.autodesk.com/cloudhelp/2025/ENU/Revit-WhatsNew/files/GUID-50024FD2-16BE-40BE-96E6-550294D9537D.htm), [ExternalEvent.Raise](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/13bf4411-c400-dcd2-458c-7f09357d9ecb.htm).
