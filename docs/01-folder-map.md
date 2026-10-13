# 01 — Thư mục và file làm gì?

## Hai chương trình riêng

`server` là chương trình Node.js do Claude khởi chạy. `addin` là thư viện C# do Revit nạp. Chúng trao đổi dữ liệu qua TCP trên cùng máy.

| Thư mục | Nhiệm vụ | Chạy ở đâu? |
| --- | --- | --- |
| `server/` | Định nghĩa công cụ MCP và chuyển lệnh đến Revit | Tiến trình Node.js |
| `addin/` | Nhận lệnh và gọi Revit API | Bên trong tiến trình Revit |
| `config/` | Ví dụ cấu hình ứng dụng AI | Claude đọc sau khi áp dụng cấu hình |
| `docs/` | Tài liệu học cấu trúc và luồng gọi lệnh | Người phát triển đọc |

Mỗi công cụ có một file định nghĩa ở `server/src/tools/` và một class command ở `addin/Commands/`. Hai phía nối với nhau bằng tên method. Chưa cần một hệ thống nạp command DLL động để hiểu kết nối cơ bản.

## File phía server

| File | Nhiệm vụ | Khi nào bạn chỉnh? |
| --- | --- | --- |
| `package.json` | Tên dự án, phiên bản, thư viện và lệnh build/test | Khi thêm thư viện hoặc lệnh npm |
| `package-lock.json` | Khóa phiên bản dependencies cụ thể | npm tự cập nhật khi cài thư viện |
| `tsconfig.json` | Quy tắc biên dịch TypeScript sang JavaScript | Khi đổi cách build |
| `src/index.ts` | Tạo MCP Server, đăng ký công cụ, mở giao tiếp stdio | Khi đổi khởi động hoặc thông tin server |
| `src/config.ts` | Đọc host, port và timeout từ môi trường | Khi đổi thiết lập kết nối |
| `src/tools/define.ts` | Kiểu `RevitTool`, `defineTool` và cách đăng ký công cụ với MCP | Khi đổi cách mọi công cụ chạy |
| `src/tools/index.ts` | Danh sách `allTools` Claude nhìn thấy | Khi thêm công cụ |
| `src/tools/diagnostics.ts`, `document.ts` | Định nghĩa từng công cụ: tên, mô tả, schema, annotations | Khi thêm hoặc sửa công cụ |
| `src/revit-client.ts` | Kết nối TCP, gửi method + params + timeout, ghép dữ liệu nhận được, báo lỗi | Khi thay giao thức kết nối |
| `tests/connection.test.mjs` | Chạy một cầu nối TCP giả lập và kiểm tra MCP | Khi đổi hành vi giao tiếp |
| `tests/contract.test.mjs` | So sánh method của server với command của add-in | Tự chạy cùng `npm test` |

`node_modules/` chứa dependencies được npm cài. `build/` chứa JavaScript được TypeScript sinh ra. Cả hai được bỏ qua trong Git.

## File phía add-in

| File | Nhiệm vụ | Khi nào bạn chỉnh? |
| --- | --- | --- |
| `Bimtech.RevitMcp.Addin.csproj` | Định nghĩa DLL .NET 8, x64 và vị trí Revit API | Khi đổi phiên bản Revit hoặc thư viện |
| `Bimtech.RevitMcp.addin` | Cho Revit biết DLL ở đâu và lớp nào cần gọi | Khi đổi vị trí cài hoặc tên lớp khởi động |
| `App.cs` | Tạo nút ribbon và bắt đầu/dừng cầu nối | Khi đổi giao diện hoặc vòng đời add-in |
| `Bridge/TcpBridge.cs` | Nhận chuỗi JSON qua TCP và gửi kết quả trả về | Khi đổi cách đóng gói hoặc nhận dữ liệu |
| `Protocol/RpcRequest.cs` | Định nghĩa dữ liệu request (method, params, timeoutMs) | Khi mở rộng hợp đồng giao tiếp |
| `Protocol/RpcException.cs` | Bảng mã lỗi `RpcErrorCodes` và `RpcException` | Khi thêm loại lỗi mới |
| `Commands/RevitRequestHandler.cs` | Xếp lệnh vào hàng đợi, chạy trên luồng Revit API, chuyển exception thành mã lỗi | Hiếm khi |
| `Commands/CommandRegistry.cs` | Danh sách command, tra cứu theo tên method | Khi thêm command |
| `Commands/IRevitCommand.cs` | Giao diện mọi command phải cài đặt | Hiếm khi |
| `Commands/CommandContext.cs` | UIApplication, params, cancellation và hàm lấy Document | Khi command cần thêm tiện ích chung |
| `Commands/CommandParams.cs` | Đọc và kiểm tra tham số theo kiểu | Khi cần kiểu tham số mới |
| `Commands/RevitJson.cs` | Chuyển đối tượng Revit sang giá trị JSON | Khi cần chuyển đổi dùng chung |
| `Commands/Diagnostics/`, `Commands/Documents/` | Mỗi command một file, nhóm theo chủ đề | Khi thêm hoặc sửa command |

`bin/` chứa kết quả build; `obj/` chứa dữ liệu trung gian của trình biên dịch. Không viết mã nguồn vào các thư mục này.

## Các tên dễ nhầm

| Khái niệm | Ý nghĩa trong dự án |
| --- | --- |
| MCP tool | Công cụ Claude có thể gọi, ví dụ `ping_revit` |
| Method nội bộ | Tên lệnh add-in xử lý, ví dụ `ping` |
| Add-in | DLL C# chạy trong Revit |
| Manifest `.addin` | File XML đăng ký DLL với Revit |
| ExternalEvent | Cơ chế để Revit xử lý lệnh từ bên ngoài trong ngữ cảnh API phù hợp |
| Transaction | Phạm vi cần có khi thay đổi mô hình; hai lệnh hiện tại chỉ đọc nên không mở Transaction |
| Solution `.sln` | File Visual Studio tập hợp project C#; Node.js vẫn dùng `package.json` |

## Cách thêm một công cụ sau này

Xem [03 — Thêm một công cụ mới](03-add-a-tool.md). Bạn chỉ cần thêm một file định nghĩa phía server và một class command phía add-in; phần kết nối TCP giữ nguyên.
