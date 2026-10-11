# 01 — Thư mục và file làm gì?

## Hai chương trình riêng

`server` là chương trình Node.js do Claude khởi chạy. `addin` là thư viện C# do Revit nạp. Chúng trao đổi dữ liệu qua TCP trên cùng máy.

| Thư mục | Nhiệm vụ | Chạy ở đâu? |
| --- | --- | --- |
| `server/` | Định nghĩa công cụ MCP và chuyển lệnh đến Revit | Tiến trình Node.js |
| `addin/` | Nhận lệnh và gọi Revit API | Bên trong tiến trình Revit |
| `config/` | Ví dụ cấu hình ứng dụng AI | Claude đọc sau khi áp dụng cấu hình |
| `docs/` | Tài liệu học cấu trúc và luồng gọi lệnh | Người phát triển đọc |

Trong bản đầu tiên, các lệnh Revit nằm ngay trong add-in. Khi số lệnh tăng lên, có thể tách từng lệnh ra file riêng. Chưa cần một hệ thống nạp command DLL động để hiểu kết nối cơ bản.

## File phía server

| File | Nhiệm vụ | Khi nào bạn chỉnh? |
| --- | --- | --- |
| `package.json` | Tên dự án, phiên bản, thư viện và lệnh build/test | Khi thêm thư viện hoặc lệnh npm |
| `package-lock.json` | Khóa phiên bản dependencies cụ thể | npm tự cập nhật khi cài thư viện |
| `tsconfig.json` | Quy tắc biên dịch TypeScript sang JavaScript | Khi đổi cách build |
| `src/index.ts` | Tạo MCP Server, đăng ký công cụ, mở giao tiếp stdio | Khi đổi khởi động hoặc thông tin server |
| `src/config.ts` | Đọc host, port và timeout từ môi trường | Khi đổi thiết lập kết nối |
| `src/tools.ts` | Tên, mô tả và cách thực thi công cụ Claude nhìn thấy | Khi thêm công cụ hoặc tham số |
| `src/revit-client.ts` | Kết nối TCP, gửi JSON, ghép dữ liệu nhận được, báo lỗi | Khi thay giao thức kết nối |
| `tests/connection.test.mjs` | Chạy một cầu nối TCP giả lập và kiểm tra MCP | Khi đổi hành vi giao tiếp |

`node_modules/` chứa dependencies được npm cài. `build/` chứa JavaScript được TypeScript sinh ra. Cả hai được bỏ qua trong Git.

## File phía add-in

| File | Nhiệm vụ | Khi nào bạn chỉnh? |
| --- | --- | --- |
| `Bimtech.RevitMcp.Addin.csproj` | Định nghĩa DLL .NET 8, x64 và vị trí Revit API | Khi đổi phiên bản Revit hoặc thư viện |
| `Bimtech.RevitMcp.addin` | Cho Revit biết DLL ở đâu và lớp nào cần gọi | Khi đổi vị trí cài hoặc tên lớp khởi động |
| `App.cs` | Tạo nút ribbon và bắt đầu/dừng cầu nối | Khi đổi giao diện hoặc vòng đời add-in |
| `Bridge/TcpBridge.cs` | Nhận chuỗi JSON qua TCP và gửi kết quả trả về | Khi đổi cách đóng gói hoặc nhận dữ liệu |
| `Protocol/RpcRequest.cs` | Định nghĩa dữ liệu request và mã lỗi | Khi mở rộng hợp đồng giao tiếp |
| `Commands/RevitRequestHandler.cs` | Xếp lệnh vào hàng đợi và chạy trên luồng Revit API | Khi thêm thao tác Revit |

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

Ví dụ muốn thêm `get_selected_elements`:

1. Khai báo công cụ và mô tả trong `server/src/tools.ts`.
2. Khai báo method tương ứng trong `addin/Commands/RevitRequestHandler.cs`.
3. Đọc các ID được chọn thông qua `UIApplication.ActiveUIDocument` trong handler.
4. Trả dữ liệu có thể serialize sang JSON.
5. Build hai phía và kiểm tra trên một mô hình thử nghiệm.

Bạn chỉ cần đổi phía định nghĩa công cụ và phía xử lý Revit. Khi giao thức đã ổn định, phần kết nối TCP thường giữ nguyên.
