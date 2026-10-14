# 06 — Cài đặt và quản lý lệnh

Nút **Settings** trên panel BIMTECH MCP (tab Add-Ins) mở cửa sổ cài đặt. Nút ghi luôn chế độ hiện tại ("Chỉ đọc" hoặc "Cho phép ghi"), nút **MCP Bridge** ghi cầu nối đang chạy hay đã dừng.

## Nội dung cài đặt

Lưu tại `%APPDATA%\BIMTECH\RevitMcp\settings.json`, dùng chung cho mọi model trên máy.

| Mục | Mặc định | Tác dụng |
| --- | --- | --- |
| Chế độ chỉ đọc | tắt | Chặn mọi lệnh sửa/xóa model. Lệnh có chế độ xem trước (`place_columns` với `dryRun=true`) vẫn chạy được |
| Cổng TCP | 8081 | Cổng của cầu nối. Server đọc cùng file nên không cần sửa ở nơi khác. Đổi cổng khi cầu nối đang chạy thì cầu nối tự mở lại |
| Giới hạn phần tử mỗi lần ghi | 5000 | Số phần tử tối đa một lần gọi được tạo (tối đa 20000) |
| Tự bật cầu nối khi mở Revit | tắt | Không cần bấm nút MCP Bridge mỗi lần |
| Lệnh cho Claude | tất cả bật | Bỏ chọn để tắt từng lệnh; `ping` luôn bật để server đọc được trạng thái |

```json
{
  "version": 1,
  "port": 8081,
  "autoStartBridge": false,
  "readOnlyMode": false,
  "maxElementsPerWrite": 5000,
  "disabledCommands": ["undo_batch"]
}
```

File hỏng hoặc giá trị ngoài khoảng cho phép thì add-in dùng giá trị mặc định và báo trong cửa sổ cài đặt. File chỉ lưu danh sách lệnh **bị tắt**, nên lệnh mới thêm vào các bản sau mặc định được bật.

## Lệnh bị tắt được chặn như thế nào

Có hai lớp, lớp đầu là lớp bảo đảm:

1. **Add-in luôn chặn.** `CommandPolicy.EnsureAllowed` chạy trên luồng TCP trước khi request vào hàng đợi của Revit. Lệnh bị tắt, hoặc lệnh sẽ sửa model khi đang ở chế độ chỉ đọc, nhận lỗi `-32008` kèm `data.reason` = `disabled` hoặc `readOnlyMode`. Thay đổi cài đặt có hiệu lực ngay ở request kế tiếp.
2. **Server ẩn tool cho gọn.** `ping` trả `commands[].available`. Server hỏi khi khởi động, mỗi 30 giây, và ngay sau khi gặp lỗi `-32008`/`-32601`, rồi ẩn/hiện tool bằng `tools/list_changed`. Không kết nối được Revit thì giữ nguyên trạng thái cũ. Một số client (như Claude Desktop) có thể chưa làm mới danh sách ngay; lớp 1 vẫn chặn.

## Quyền của mỗi lệnh

Mỗi command khai báo `Access` bắt buộc trong C#:

| Access | Ý nghĩa | Annotation phía server |
| --- | --- | --- |
| `Read` | Không bao giờ sửa model | `readOnlyHint: true` |
| `Write` | Thêm hoặc sửa phần tử | `readOnlyHint: false`, `destructiveHint: false` |
| `Delete` | Xóa phần tử | `readOnlyHint: false`, `destructiveHint: true` |

`tests/contract.test.mjs` báo lỗi nếu hai phía không khớp. Ngoại lệ duy nhất: `cancel_plan` là `Read` (không đụng model, chế độ chỉ đọc vẫn cho huỷ kế hoạch) nhưng annotation là `readOnlyHint: false` vì nó sửa file kế hoạch.

Với kế hoạch ([04](04-architecture.md)): `propose_*`, `get_plan`, `get_plan_status`, `open_plan_review` là `Read`; `commit_plan` là `Delete` (kế hoạch có thể chứa mục ghi đè hoặc xoá). Ở chế độ chỉ đọc, Claude vẫn lập được kế hoạch và người dùng vẫn duyệt được trong Revit, nhưng `commit_plan` bị chặn cho tới khi tắt chế độ chỉ đọc. Lệnh ghi có chế độ xem trước thì override `ModifiesModel(params)` để chế độ chỉ đọc vẫn cho xem trước (xem `PlaceColumnsCommand`).

## Biến môi trường phía server

| Biến | Tác dụng |
| --- | --- |
| `REVIT_MCP_PORT` | Ghi đè cổng trong `settings.json` |
| `REVIT_MCP_SETTINGS` | Đường dẫn khác tới `settings.json` |
| `REVIT_MCP_TIMEOUT_MS` | Timeout mặc định cho mỗi lệnh |
| `REVIT_MCP_AVAILABILITY_POLL_MS` | Chu kỳ hỏi trạng thái lệnh (mặc định 30000); `0` tắt hẳn việc đồng bộ |
