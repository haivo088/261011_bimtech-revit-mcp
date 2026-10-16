# 08 — Kế hoạch thử trong Revit

Danh sách thử trọn bộ trên Revit thật. Ngoài cột, mọi phần dưới đây mới được build và test ngoài Revit. Làm theo thứ tự: mỗi phần dùng kết quả của phần trước, và một tầng có CAD đi qua gần hết code mới.

**Nếu `commit_plan` báo timeout:** commit lớn (nhập DWG nhiều tầng, rất nhiều nhóm vách) có thể vượt 120 giây. Claude hết chờ nhưng Revit vẫn ghi tiếp và lưu kết quả vào file kế hoạch. Đợi Revit rảnh tay rồi bảo Claude **gọi lại `commit_plan`** với cùng `planId` và `approvalId`. Nếu lần trước đã ghi xong, lần này chỉ trả kết quả đã lưu (`alreadyCommitted: true`), không ghi trùng; nếu lần trước chưa kịp chạy, lần này mới ghi. **Đừng propose lại**: model đã đổi nên kế hoạch mới sẽ khác, và kế hoạch cũ đã được ghi.

Mỗi bước có câu mẫu để nói với Claude, và **"Đạt khi"** là điều cần thấy. Bước nào không đạt: chép nguyên câu trả lời của tool (nhất là phần lỗi), chụp màn hình Revit nếu liên quan, ghi vào bảng ở cuối, rồi đi tiếp sang phần độc lập.

## 0. Chuẩn bị

- Đóng Revit. Ở gốc repo chạy `.\scripts\install.ps1 -Dev` (bỏ `-Dev` nếu chưa muốn thử bộ dev).
- Cấu hình Claude Desktop: server `bimtech-revit-mcp` có `"BIMTECH_DEV": "1"` trong `env` (chỉ cần cho phần 9). Khởi động lại Claude Desktop.
- Sao lưu `TEST.rvt`, rồi mở bản sao. Chuẩn bị đường dẫn đầy đủ tới 2–3 file DWG mặt bằng kết cấu của các tầng, trong đó ít nhất một file có layer trục.
- Add-Ins → BIMTECH MCP → **MCP Bridge** để bật cầu nối. Settings: tắt chế độ chỉ đọc.

## 1. Kết nối

> Dùng ping_revit.

Đạt khi: `status: ok`, `revitVersion: 2025`, `bridgeVersion`, danh sách `commands` có `propose_levels`, `propose_walls`, `propose_floors`; `devTools: loaded` nếu đã cài `-Dev`.

## 2. Tầng: `propose_levels`

> Tạo tầng bằng propose_levels: "Tầng 1" cao độ 0 với cadFile "<đường dẫn DWG tầng 1>", "Tầng 2" cao độ 3600 với cadFile "<DWG tầng 2>", "Mái" cao độ 7200 không có CAD.

Đạt khi:
- Cửa sổ duyệt mở trong Revit; cột "Thay đổi" hiện câu dễ đọc, ví dụ `Tạo Level +3600 mm · tạo plan "KC - Tầng 2" · import S-201.dwg`, không phải JSON.
- Duyệt cả 3 mục → bấm **Duyệt kế hoạch** → nói "xong" → Claude gọi `get_plan_status` rồi `commit_plan`.
- Trong Revit có 3 Level đúng cao độ, 3 Structural Plan `KC - <tên>`, CAD nằm trong đúng plan của nó (chỉ hiện trong view đó).
- **Một lần Ctrl+Z** gỡ hết; Ctrl+Y làm lại.

Thử thêm: gọi lại cùng lệnh → các tầng đã đủ được báo `unchangedLevels`, tầng có CAD báo "Plan này đã có CAD cùng tên file". Gọi với một đường dẫn DWG sai → mục đó nằm ở tab câu hỏi (blocked).

Thử đối chiếu theo cao độ (model từ template còn `Level 1` ở 0):
- Xin "Tầng 1" cao độ 0 → mục ghi `Đổi tên Level "Level 1" → "Tầng 1"`, không tạo level thứ hai ở 0. Sau commit, plan của nó (nếu đang tên `Level 1`) thành `KC - Tầng 1`.
- Xin tên có ký tự cấm, ví dụ "Tầng 1: sàn" → mục blocked, lý do nêu đúng ký tự `':'`.
- Xin hai tầng khác tên cùng cao độ → cả hai blocked.

Thử các nâng cấp:
- `suggest_levels_from_folder` với thư mục DWG thật của bạn → bảng tầng có đúng tên, cao độ không; file nào đọc sai thì ghi lại tên file.
- Một tầng cao độ 36000 sau tầng 3600 → mục có ghi chú chiều cao tầng bất thường.
- `viewTemplate` với tên template Structural Plan có trong model, `scale: 100` → plan mới đúng template và tỉ lệ. Gọi với tên sai → lỗi kèm danh sách template.
- Nhập một DWG vẽ bằng mét (nếu có) với mặc định mm → kết quả commit có `cadWarning`; làm lại với `cadUnit: "m"` → CAD đúng kích thước.
- Model template còn `Level 2` không dùng → có mục `D1` "Xoá Level Level 2", trạng thái **Bỏ** sẵn. Để nguyên rồi commit: Level 2 còn nguyên. Propose lại, duyệt `D1`, commit: Level 2 và các view của nó bị xoá; **một lần Ctrl+Z** lấy lại. Mở view của Level 2 làm view hiện hành rồi propose: không có mục xoá cho nó.

**Kiểm chứng mốc cao độ (C):** trong một bản sao model, dời **cao độ** Project Base Point (ví dụ +1000), rồi propose một tầng "Thử" cao độ 3600 và commit. Ghi lại:
1. Cao độ Revit hiển thị cho level "Thử" (đầu level trong mặt đứng) và giá trị Elevation Base trong Type của level.
2. Gọi lại propose với "Thử" 3600: có báo "Level đã có nhưng cao độ khác" không?

Kết quả này quyết định mốc cao độ cho bản nâng cấp U2 (nhập theo ±0.000 của Project Base Point).

## 3. Trục: `propose_grids`

> Dùng list_cad_sources. Rồi propose_grids với cadId là CAD tầng 1, alignCadIds là CAD các tầng còn lại.

Đạt khi:
- Cửa sổ duyệt có mục `M1`, `M2`... (dời CAD) và `G-1`, `G-2`..., `G-A`... (mỗi trục một mục), mô tả `Trục B (ngang): y = 6000 mm từ gốc, nhịp 6000`.
- Tên trục: nếu text đầu trục là Text Note thì đúng tên trên bản vẽ; không thì 1, 2, 3... / A, B, C... kèm ghi chú "tên tự đánh".
- Nhịp vẽ lệch (5998,7) có ghi chú `Nhịp đo 5998.7 mm → 6000 mm`; sau commit đo bằng Dimension trong Revit ra đúng số tròn.
- Mỗi mục dời CAD tầng khác ghi **"Căn theo trục của chính CAD này"**. Thử với hai file tầng vẽ ở hai chỗ khác nhau trong DWG: cả hai vẫn nằm khớp trục sau commit. Mục nào ghi "Dời theo vector của CAD gốc" thì kiểm tra kỹ CAD đó.
- Sau commit: giao điểm trục đầu tiên hai phương trùng Project Base Point; trục và CAD đã **ghim**.
- Gọi lại `propose_grids` → mọi trục bị blocked vì trùng tên, không có mục dời CAD.
- Bản vẽ có layer trục tên lạ: nếu tự nhận sai thì gọi lại với `gridLayer`. Ghi lại style được chọn và điểm (`styleScores`) để bổ sung quy tắc.
- Bản vẽ có trục xiên: kết quả có cảnh báo "đường dài xiên".

## 4. Cột: `scan_cad_columns` / `propose_columns`

> Quét cột từ CAD tầng 1 bằng scan_cad_columns, rồi propose_columns từ Tầng 1 lên Tầng 2.

Đạt khi:
- Nhóm theo ký hiệu (`C1`, `C2`) nếu ký hiệu là Text Note, không thì theo `300x500` (cạnh nhỏ trước). Nhóm có ký hiệu ghi kích thước khác hình vẽ thì có `tagSizeMismatch`.
- `notRectangles` liệt kê cột chữ L, cột dính vách cùng layer, nét hở; không có "cột" nào từ mũi tên hay nét ghi chú.
- Cửa sổ duyệt: mỗi nhóm một mục, mô tả như `C1 · 300x500 · 12 cột · Type 300x500 (BTC-CotCN_BeTong)`.
- Sau commit: cột đúng tâm, khoảng cách tới trục là bội 5 mm (đo bằng Dimension); cột vẽ lệch nhẹ được đặt thẳng theo trục; **cột chữ nhật đúng hướng b/h** (không bị xoay 90°); tham số Mark có ký hiệu.
- Đặt sẵn một cột của **family khác** ở một vị trí rồi propose: vị trí đó bị bỏ qua (không thành cột chồng cột).
- Layer cột rất nhiều hatch: quét xong nhanh, không timeout.
- Kiểm chứng tên tham số tiết diện của family `BTC-CotCN_BeTong` đúng là `b`/`h`.

## 5. Dầm: `scan_cad_beams` / `place_beams`

> Quét dầm từ CAD tầng 2 bằng scan_cad_beams, extendToColumns với levelId Tầng 1. Đặt lên Tầng 2, xem trước rồi đặt.

Đạt khi: nhóm theo tên đọc từ Text (`D2-X7(220x600)`); dầm đặt đúng tim, đầu dầm kéo tới cột; Type mới tạo trong family dầm mặc định nếu thiếu.

## 6. Vách: `scan_cad_walls` / `propose_walls`

> Quét vách từ CAD tầng 1 bằng scan_cad_walls (chọn layer vách), rồi propose_walls từ Tầng 1 lên Tầng 2.

Đạt khi:
- Mỗi nhóm (tên + bề dày) là một mục, mô tả như `V1 · dày 200 mm · 12 vách (15 đoạn) · Type BTC-VachKC_200`.
- Sau commit: vách đúng tim, đúng chiều cao; Type `BTC-VachKC_<b>` được tạo khi thiếu.
- Gọi lại `propose_walls` cùng scan → `alreadyModeledGroups`, không tạo vách đôi.

## 7. Sàn: `propose_floors`

Cần có dầm (phần 5) hoặc vách trên tầng đó.

> Dùng propose_floors cho Tầng 2.

Đạt khi:
- Mỗi ô sàn là một mục `S1`, `S2`... (S1 ở góc trên trái), có diện tích; ô < 1 m² không xuất hiện (coi là lỗ).
- Sau commit: sàn kết cấu đúng biên tim dầm, Type `BTC_SanKC_150` (tự tạo nếu thiếu).
- Gọi lại `propose_floors` → mục có ghi chú "có thể trùng"; commit lần hai → `result.skipped`, không tạo sàn chồng.

## 8. Cọc và đài: `scan_cad_piles` / `propose_piles`

> Quét cọc và đài từ CAD móng bằng scan_cad_piles, rồi propose_piles với capLevelId là Tầng 1 (hoặc level đỉnh đài).

Đạt khi:
- `scan_cad_piles` lượt hai có `capGroups`: mỗi nhóm có ký hiệu (`Đ1`) và `thicknessMm` nếu bản vẽ ghi (Text Note). Nhóm thiếu chiều dày thì Claude **hỏi bạn**, không tự chọn.
- Bản vẽ đài vẽ bằng line rời (đã explode): vẫn ra đủ đài.
- Cửa sổ duyệt: mỗi đài một mục `C1`, `C2`..., mô tả như `Đài Đ1 2000x2000 · BTC-DaiMong_1200 · cọc 4×D600 · lót`; đài thiếu chiều dày bị chặn với lý do. Cọc lạc ngoài đài nằm trong mục `P-NOCAP` (bị chặn).
- Sau commit: đài đúng chiều dày theo từng loại; đỉnh cọc = đáy đài của **chính đài đó**; cạnh đài là số tròn 5 mm; khoảng cách tâm cọc và tâm đài tới trục là bội 5 mm (đo bằng Dimension).
- Có lớp lót; cọc join với đài và lớp lót của nó; join của các đài khác có sẵn trong model **không bị đổi**.
- Với `pileLengthMm`: cọc đúng chiều dài, hoặc có cảnh báo nếu family không có tham số chiều dài. Ghi lại **gốc chèn** của family `BTC-CocTron_BeTong`: đỉnh cọc có trùng đáy đài không.
- Gọi lại `propose_piles` cùng scan → đài đã dựng nằm trong `alreadyModeledCaps`.

## 9. An toàn khi ghi

- Settings → bật **chế độ chỉ đọc** → nhờ Claude `commit_plan` một kế hoạch đã duyệt → bị từ chối; các `propose_*` vẫn chạy.
- Tắt chỉ đọc. Propose một kế hoạch, duyệt, **sửa tay** model (dời một phần tử), rồi commit → báo kế hoạch cũ (`-32022`), không ghi gì.
- Đóng cửa sổ duyệt giữa chừng → `open_plan_review` mở lại, các mục đã duyệt vẫn còn.

## 10. Bộ dev

Làm theo [07 — Hướng dẫn từng bước](07-dev-tools.md#hướng-dẫn-từng-bước-cho-lần-thử-đầu-tiên). Ba câu hỏi quan trọng nhất cần câu trả lời:

1. `ping_revit` có `devTools: loaded` không (Roslyn nạp được trong Revit)?
2. Linetype được Revit giữ theo từng đối tượng, hay chỉ theo layer?
3. Tên trong vòng tròn đầu trục có đọc được (Text Note) không?

Kết thúc bằng `export_cad_case` cho CAD có trục đã dựng đúng, để có case thật đầu tiên.

## Ghi kết quả

| Phần | Đạt / Không | Ghi chú, lỗi (chép nguyên văn) |
| --- | --- | --- |
| 1. Kết nối | | |
| 2. Tầng | | |
| 3. Trục | | |
| 4. Cột | | |
| 5. Dầm | | |
| 6. Vách | | |
| 7. Sàn | | |
| 8. Cọc, đài | | |
| 9. An toàn | | |
| 10. Bộ dev | | |
