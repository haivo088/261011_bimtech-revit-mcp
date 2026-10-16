# Command sets

Các đoạn code `send_code` đã chạy tốt, được đóng gói thành script có tên và tham số để dùng lại (chỉ máy người phát triển, `BIMTECH_DEV=1`).

Mỗi thư mục là một bộ: `set.json` (mô tả bộ), và mỗi script gồm `<tên>.cs` (code) + `<tên>.json` (mô tả, `write`, tham số, ghi chú). Tên bộ và script: chữ thường, số và `-`.

Chạy: `send_code` với `script: "<bộ>/<tên>"` và `args`. Lưu: `command_set` với `action: "save"` (Claude đề xuất, bạn đồng ý). Xem định dạng đầy đủ ở [docs/07](../docs/07-dev-tools.md#send-code-và-command-set).
