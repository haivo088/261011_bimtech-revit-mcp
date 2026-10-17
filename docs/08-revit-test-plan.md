# 08 — Kế hoạch thử trong Revit

Danh sách thử trọn bộ trên Revit thật. Ngoài cột, mọi phần dưới đây mới được build và test ngoài Revit. Làm theo thứ tự: mỗi phần dùng kết quả của phần trước (cột cần trục, dầm cần cột và vách, sàn cần dầm).

Mỗi bước có câu mẫu để nói với Claude, và **"Đạt khi"** là điều cần thấy. Bước nào không đạt: chép nguyên câu trả lời của tool (nhất là phần lỗi), chụp màn hình Revit nếu liên quan, ghi vào bảng ở cuối, rồi đi tiếp sang phần độc lập.

**Nếu `commit_plan` báo timeout:** commit lớn (nhập DWG nhiều tầng, rất nhiều nhóm vách) có thể vượt 120 giây. Claude hết chờ nhưng Revit vẫn ghi tiếp và lưu kết quả vào file kế hoạch. Đợi Revit rảnh tay rồi bảo Claude **gọi lại `commit_plan`** với cùng `planId` và `approvalId`. Nếu lần trước đã ghi xong, lần này chỉ trả kết quả đã lưu (`alreadyCommitted: true`), không ghi trùng; nếu lần trước chưa kịp chạy, lần này mới ghi. **Đừng propose lại**: model đã đổi nên kế hoạch mới sẽ khác, và kế hoạch cũ đã được ghi.

## Lộ trình rút gọn (khoảng 1–1,5 giờ)

Khi ít thời gian, đi đúng con đường này; các mục "Thử thêm" và phần còn lại làm sau.

| # | Việc | Phần | Thời gian |
| --- | --- | --- | --- |
| 1 | Cài bản mới, sửa cấu hình theo lời nhắc của script, `ping_revit` | 0, 1 | 10' |
| 2 | Một tầng có CAD + một tầng trên, chỉ mục chính | 2 | 10' |
| 3 | Trục từ CAD tầng 1 | 3 | 10' |
| 4 | Cột tầng 1 | 5 | 15' |
| 4b | Vách tầng 1 từ hatch (tầng có lõi thang) | 6 | 15' |
| 5 | Dầm chính tầng 2 | 7 | 15' |
| 6 | `send_code` đọc, rồi một lần ghi nhỏ | 10 | 10' |
| 7 | `export_cad_case` cho CAD tầng 1 và tầng 2, tầng 2 sau khi đã sửa tay dầm sai/thiếu (để sửa thuật toán ngoài Revit) | 10 | 5' |
| 8 | `export_cad_case` đọc **file DWG** của một mặt bằng có **lõi thang** (hatch cho vách) | 10 | 5' |

Điều cần ghi lại nhất: **chỗ nào nhận sai và vì sao** (nét vẽ, layer, chữ trong DWG hay Text Note), thời gian chạy, và nguyên văn mọi lỗi.

## 0. Chuẩn bị

- Đóng Revit. Ở gốc repo chạy `.\scripts\install.ps1 -Dev` (bỏ `-Dev` nếu chưa muốn thử bộ dev). Script build lại cả add-in, bộ dev và server, rồi **kiểm tra cấu hình Claude Desktop**. Nếu cuối kết quả có mục vàng **"Check before testing"**, sửa đúng như nó nói (thường là thêm `"BIMTECH_DEV": "1"` vào `env` của `bimtech-revit-mcp` trong `%APPDATA%\Claude\claude_desktop_config.json`, xem `config\claude-desktop.example.json`).
- Khởi động lại Claude Desktop (bắt buộc sau mỗi lần cài).
- Sao lưu `TEST.rvt`, rồi mở bản sao. Chuẩn bị đường dẫn đầy đủ tới 2–3 file DWG mặt bằng kết cấu của các tầng, trong đó ít nhất một file có layer trục.
- Add-Ins → BIMTECH MCP → **MCP Bridge** để bật cầu nối. Settings: tắt chế độ chỉ đọc.

## 1. Kết nối

> Dùng ping_revit.

Đạt khi: `status: ok`, `revitVersion: 2025`, `bridgeVersion`, danh sách `commands` có `propose_levels`, `propose_columns`, `propose_beams`, `propose_walls`, `propose_floors`; `devTools: loaded` nếu đã cài `-Dev`. Nếu `devTools` là `failed to load: ...`: chép nguyên văn, cài lại bằng `install.ps1 -Dev` (không `-NoBuild`).

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
- Tên trục: CAD import thì thêm `dwgPath` (CAD link tự tìm file) → tên đọc từ chữ trong vòng tròn đầu trục của file DWG, kể cả vòng tròn vẽ bằng ellipse, nằm trong block, hay cách xa đầu trục có đường dẫn. Kết quả có `dwg.attached: true`. Thiếu `dwgPath` thì 1, 2, 3... / A, B, C... kèm ghi chú "tên tự đánh".
- Đường trên layer trục mà không có vòng tròn (mép sàn, console) thành `A'`, `B'`... trong `extraLines`, trạng thái **Bỏ** sẵn; giao trục 1-A là trục **có tên** đầu tiên, không phải đường phụ.
- Nhịp vẽ lệch (5998,7) có ghi chú `Nhịp đo 5998.7 mm → 6000 mm`; sau commit đo bằng Dimension trong Revit ra đúng số tròn.
- Mỗi mục dời CAD tầng khác ghi **"Căn theo trục của chính CAD này"**. Thử với hai file tầng vẽ ở hai chỗ khác nhau trong DWG: cả hai vẫn nằm khớp trục sau commit. Mục nào ghi "Dời theo vector của CAD gốc" thì kiểm tra kỹ CAD đó.
- Sau commit: giao điểm trục đầu tiên hai phương trùng Project Base Point; trục và CAD đã **ghim**.
- Gọi lại `propose_grids` → mọi trục bị blocked vì trùng tên, không có mục dời CAD.
- Mục cuối `X1` "Căn phạm vi": sau commit, mở các mặt đứng → mọi trục chạy từ level thấp nhất −1000 tới level cao nhất +1000, mọi level dài quá trục biên 2000 mm (chỉnh bằng `gridMarginMm` / `levelMarginMm`). Thêm tầng bằng `propose_levels` khi đã có trục → kế hoạch cũng có `X1`, trục tự cao thêm tới tầng mới.
- Bản vẽ có layer trục tên lạ: nếu tự nhận sai thì gọi lại với `gridLayer`. Ghi lại style được chọn và điểm (`styleScores`) để bổ sung quy tắc.
- Bản vẽ có trục xiên: kết quả có cảnh báo "đường dài xiên".

## 4. Cọc và đài: `scan_cad_piles` / `propose_piles`

Cọc và đài giờ **không cần chọn layer**: đọc mọi layer và cân bằng chứng (xem [05](05-cad-to-structure.md)). Dựng trục trước.

> Quét cọc và đài từ CAD móng bằng scan_cad_piles (CAD import thì thêm dwgPath "<đường dẫn .dwg>"). Cho mình xem cọc, đài và bằng chứng, rồi propose_piles với capLevelId là Tầng 1 (hoặc level đỉnh đài).

Đạt khi:
- Gọi **một lần**, không hỏi layer. `sureCaps` và `surePiles` đúng số đài, số cọc của bản vẽ. Ghi lại: đài/cọc nào **sót**, hình nào **thừa**, cái gì nằm ở diện xem lại.
- **Vòng tròn đầu trục** không thành cọc (nằm trong `droppedPiles` với lý do "vòng tròn đầu trục"). Khung nhà, ô phòng lớn không thành đài.
- `capGroups`: mỗi nhóm có ký hiệu (`Đ1`) và `thicknessMm` nếu bản vẽ ghi (Text Note hoặc chữ DWG), kèm `evidence`. Nhóm thiếu chiều dày thì Claude **hỏi bạn**, không tự chọn.
- Bản vẽ đài vẽ bằng line rời (đã explode): vẫn ra đủ đài chữ nhật.
- Cửa sổ duyệt: mỗi mục đài có dòng "Vì sao: …"; đài chưa chắc ghi "Bằng chứng chưa đủ".
- Cửa sổ duyệt: mỗi đài một mục `C1`, `C2`..., mô tả như `Đài Đ1 2000x2000 · BTC-DaiMong_1200 · cọc 4×D600 · lót`; đài thiếu chiều dày bị chặn với lý do. Cọc lạc ngoài đài nằm trong mục `P-NOCAP` (bị chặn).
- Sau commit: đài đúng chiều dày theo từng loại; đỉnh cọc = đáy đài của **chính đài đó**; cạnh đài là số tròn 5 mm; khoảng cách tâm cọc và tâm đài tới trục là bội 5 mm (đo bằng Dimension).
- Có lớp lót; cọc join với đài và lớp lót của nó; join của các đài khác có sẵn trong model **không bị đổi**.
- Với `pileLengthMm`: cọc đúng chiều dài, hoặc có cảnh báo nếu family không có tham số chiều dài. Ghi lại **gốc chèn** của family `BTC-CocTron_BeTong`: đỉnh cọc có trùng đáy đài không.
- Gọi lại `propose_piles` cùng scan → đài đã dựng nằm trong `alreadyModeledCaps`.

## 5. Cột: `scan_cad_columns` / `propose_columns`

Cột giờ **không cần chọn layer**: đọc mọi layer và cân bằng chứng (xem [05](05-cad-to-structure.md)). Dựng trục trước.

> Quét cột từ CAD tầng 1 bằng scan_cad_columns (CAD import thì thêm dwgPath "<đường dẫn .dwg>"). Cho mình xem các nhóm và bằng chứng, rồi propose_columns từ Tầng 1 lên Tầng 2.

Đạt khi:
- Gọi **một lần**, không hỏi layer. `dwg.attached: true`, `read.hatches` > 0. Ghi lại `read.hatchesWithoutOutline` (hatch không trùng đường bao: hatch của block lật và xoay bị ACadSharp đặt sai, xem mục "còn lại").
- Nhóm chắc chắn đúng các cột của bản vẽ, mỗi nhóm có `evidence` hợp lý (giao trục, hatch, lặp kích thước). Ghi lại: cột nào **sót**, hình nào **thừa** (mảnh vách, ô ký hiệu), nhóm `-maybe` có gì. Các mảnh vách lõi bị cửa cắt phải nằm ở `-maybe` hoặc bị bỏ, không ở nhóm chắc chắn.
- Nhóm theo ký hiệu (`C1`, `C2`) nếu có ký hiệu (Text Note hoặc chữ trong DWG), không thì theo `300x500` (cạnh nhỏ trước). Nhóm có ký hiệu ghi kích thước khác hình vẽ thì có `tagSizeMismatch`.
- Cửa sổ duyệt: nhóm chắc chắn, ở giao trục, Type có sẵn thì **Tự duyệt**; nhóm `-maybe` ghi "Bằng chứng chưa đủ" và ở trạng thái Chờ; mỗi mục có dòng "Vì sao: …".
- Cửa sổ duyệt: mỗi nhóm một mục, mô tả như `C1 · 300x500 · 12 cột · Type 300x500 (BTC-CotCN_BeTong) · tại A-1, A-2…`.
- Sau commit: cột đúng tâm, khoảng cách tới trục là bội 5 mm (đo bằng Dimension); cột vẽ lệch nhẹ được đặt thẳng theo trục; **cột chữ nhật đúng hướng b/h** (không bị xoay 90°); tham số Mark có ký hiệu.
- **Giao trục:** cột vẽ lệch vài mm so với tâm giao trục được đặt đúng tâm; cột biên có mép vẽ gần trục được đặt mép trùng trục. So `tại A-1…` với tham số **Column Location Mark** của Revit. `emptyCrossings` liệt kê đúng các giao trục không có cột. Ghi lại cột nào bị nắn sai (ví dụ cột cố ý lệch 10 mm).
- Đặt sẵn một cột của **family khác** ở một vị trí rồi propose: vị trí đó bị bỏ qua (không thành cột chồng cột).
- Layer cột rất nhiều hatch: quét xong nhanh, không timeout.
- Kiểm chứng tên tham số tiết diện của family `BTC-CotCN_BeTong` đúng là `b`/`h`.

## 6. Vách: `scan_cad_walls` / `propose_walls`

Vách dựng trước dầm: `propose_beams` dùng vách trong model làm gối tựa.

Vách giờ đọc từ **hatch của file DWG** (xem [05](05-cad-to-structure.md)), nên làm sau trục và cột của tầng đó. Chọn tầng có **lõi thang**.

> Quét vách CAD tầng 1 bằng scan_cad_walls (CAD import thì thêm dwgPath "<đường dẫn .dwg>"). Cho mình xem các nhóm và bằng chứng, rồi propose_walls từ Tầng 1 lên Tầng 2.

Đạt khi:
- Kết quả quét có `dwg.matchRate` gần 1, `regionFrom: "grids"`, `model.columns` > 0, và `columnPatternsFrom` là "hatches inside the modeled columns".
- Nhóm `w200`, `w300`… đúng các bề dày vách của bản vẽ; mỗi nhóm có `evidence` hợp lý. Ghi lại: vách nào **sót** (có hatch mà không ra), vách nào **thừa** (tường xây, cột bị nhận nhầm), nhóm `-maybe` có gì.
- Cửa sổ duyệt: mỗi nhóm một mục kèm dòng "Vì sao: …" và "Trên trục: …".
- Sau commit: vách đúng tim, đúng bề dày, góc chữ L/T nối đúng; Type `BTC-VachKC_<b>` được tạo khi thiếu.
- Gọi lại `scan_cad_walls` lần hai: `dwg.cached: true`, nhanh hơn hẳn. Gọi lại `propose_walls` cùng scan → `alreadyModeledGroups`, không tạo vách đôi.
- Ghi lại thời gian lần quét đầu (`dwg.readSeconds`).

## 7. Dầm chính: `propose_beams`

> Cột tầng 1 đã dựng (phần 5). Đề xuất dầm chính Tầng 2 từ CAD tầng 2 bằng propose_beams.

Đạt khi:
- Chạy xong nhanh trên bản vẽ thật (không timeout), dù không chọn layer.
- Mỗi nhịp giữa hai cột liền nhau có dầm; dầm đặt **từ tim cột tới tim cột**; giao trục không có cột thì dầm chạy liền qua, không bị chia.
- Dầm biên lệch trục (mép ngoài bằng mặt cột) đúng vị trí; tim dầm cách trục là bội 5 mm hoặc đúng 0 / mép trùng trục.
- Mục duyệt ghi nhịp kiểu `A-1 → A-2 (trục A)`; ký hiệu dầm (Text Note) gán đúng dầm cùng phương, lan sang các nhịp thẳng hàng; Mark có ký hiệu.
- `report.dangling` liệt kê dầm phụ/console (dựng ở bước 7b); `report.unusedMarks` liệt kê ký hiệu không gán được. Ghi lại dầm chính nào bị sót và vì sao (nét vẽ, khoảng đứt, layer).
- Nhóm không có h (không có ký hiệu kích thước) bị chặn và Claude hỏi chiều cao.
- **Kiểm chứng**: CAD import có Text Note không, hay chữ nằm trong DWG (Revit không đọc được) — khi đó dầm không có ký hiệu.
- Kiểm chứng tên tham số tiết diện của family `BTC-DamCN_BeTong` đúng là `b`/`h`; dầm Revit tự cắt tại mặt cột khi join.

### 7b. Dầm phụ: `propose_beams secondary=true`

> Dầm chính Tầng 2 đã commit (đúng b×h theo ký hiệu). Đề xuất dầm phụ Tầng 2 bằng propose_beams với secondary=true.

Đạt khi:
- `report.supports.beams` bằng số dầm chính đã dựng; `report.secondaryBeams` có số dầm phụ tìm được và số chắc chắn.
- Các dải dầm phụ giữa ô (ví dụ `D2-X15 (220x600)` ở giữa nhịp) ra đủ, **đầu dầm ở tim dầm chính**, không trùng dầm chính đã có.
- Mỗi mục có dòng "Vì sao"; dầm phụ chia đôi nhịp dầm chính có "+1 chia đều nhịp dầm gối". Ghi lại: dầm phụ nào **sót**, đoạn nào **thừa** (tường xây, nét mép sàn), dầm nào bị "−3 to hơn dầm gối" (thường do dầm chính dựng sai kích thước).
- Sau commit: Revit nối dầm phụ vào dầm chính (join), đỉnh dầm phụ bằng đỉnh dầm chính.

## 8. Sàn: `propose_floors`

Cần có dầm (phần 7) hoặc vách trên tầng đó.

> Dùng propose_floors cho Tầng 2.

Đạt khi:
- Mỗi ô sàn là một mục `S1`, `S2`... (S1 ở góc trên trái), có diện tích; ô < 1 m² không xuất hiện (coi là lỗ).
- Sau commit: sàn kết cấu đúng biên tim dầm, Type `BTC_SanKC_150` (tự tạo nếu thiếu).
- Gọi lại `propose_floors` → mục có ghi chú "có thể trùng"; commit lần hai → `result.skipped`, không tạo sàn chồng.

## 9. An toàn khi ghi

- Settings → bật **chế độ chỉ đọc** → nhờ Claude `commit_plan` một kế hoạch đã duyệt → bị từ chối; các `propose_*` vẫn chạy.
- Tắt chỉ đọc. Propose một kế hoạch, duyệt, **sửa tay** model (dời một phần tử), rồi commit → báo kế hoạch cũ (`-32022`), không ghi gì.
- Đóng cửa sổ duyệt giữa chừng → `open_plan_review` mở lại, các mục đã duyệt vẫn còn.

## 10. Bộ dev: `send_code`, `command_set` và case

Cần cài `-Dev` và `BIMTECH_DEV=1` (phần 0).

**Đọc (`write=false`)**

> Dùng send_code đếm số Level, Grid và cột trong model.

Đạt khi: trả về con số đúng, `stage: "done"` (Roslyn nạp được trong Revit). Thử một đoạn code có lỗi cú pháp → `stage: "compile"` kèm số dòng; một vòng lặp vô hạn với `timeoutSeconds: 5` → `stage: "timeout"`, Revit không treo.

**Ghi (`write=true`)**

> Dùng send_code với write=true, purpose "Ghi Comments = test cho 3 cột đầu tiên": đặt tham số Comments của 3 cột đầu tiên thành "test".

Đạt khi:
- Kết quả có `preview` (ví dụ `modified: 3`, `Structural Columns`), model **chưa đổi**, cửa sổ duyệt có mục `CODE-1`. Bấm vào dòng: khung dưới hiện mục đích, kết quả chạy thử và **toàn bộ code**.
- Duyệt → `commit_plan` → 3 cột có Comments "test"; **một lần Ctrl+Z** gỡ hết.
- Làm lại nhưng sửa tay model trước khi commit → `commit_plan` từ chối (kế hoạch cũ).
- Code tự mở `Transaction` với `write=true` → báo lỗi, không tạo kế hoạch.

**Command set**

> Lưu đoạn code vừa rồi thành command set "checks/set-comments" với tham số text (string) và count (number), rồi chạy lại nó bằng send_code với text "ok".

Đạt khi: có file `commandsets/checks/set-comments.cs` và `.json` trong repo; `command_set list` thấy script; chạy bằng `script` + `args` đi qua cửa sổ duyệt như trên; `command_set history` liệt kê các lần chạy (file `logs/code-runs.jsonl`). Không muốn giữ thì xoá thư mục `commandsets/checks`.

**Nhận diện và case** — làm theo [07 — Hướng dẫn từng bước](07-dev-tools.md#hướng-dẫn-từng-bước-cho-lần-thử-đầu-tiên). Câu hỏi quan trọng nhất:

1. Linetype được Revit giữ theo từng đối tượng, hay chỉ theo layer?
2. Tên trong vòng tròn đầu trục, ký hiệu cột, ký hiệu dầm có đọc được (Text Note) không?

Kết thúc bằng `export_cad_case` cho CAD tầng 1 (có trục đã dựng đúng) và CAD tầng 2 với `levelId` Tầng 2. Với tầng 2: **trước khi xuất, sửa tay cho dầm trong model đúng** (thêm dầm bị sót, xoá dầm thừa), vì dầm trong model được lưu làm đáp án. Kết quả có số cột, vách và `truthBeams`; `dotnet test` sẽ chỉ ra dầm nào thuật toán còn sót hoặc thừa.

**Đọc thẳng file DWG (hatch, chữ).** Revit không cho đọc hatch và chữ trong DWG, và explode trong Revit đôi khi làm mất hatch; nên add-in đọc chính file DWG trên ổ đĩa. Chọn một mặt bằng **có lõi thang** (vách được hatch):

> Dùng export_cad_case với CAD <tên>, notes "lõi thang, thử đọc hatch". (CAD import, không phải link: thêm dwgPath "<đường dẫn đầy đủ tới file .dwg>".)

Đạt khi phần `dwg` trong kết quả có `attached: true`, `matchRate` gần 1 (≥ 0,7), `placement` hợp lý (`scale` 1, `rotationDeg` bằng góc xoay CAD trong Revit), và `hatchesByLayer` có các layer/mẫu hatch của vách và cột. Ghi lại: CAD là **link hay import**, `readSeconds`, `matchRate`, và nguyên văn `problem` nếu có. Nếu không khớp, ghi thêm CAD được đặt thế nào khi nhập (Origin to Origin / Center to Center / xoay, đơn vị nhập).

## Ghi kết quả

Lần thử 16–17/10/2026 trên `261016.rvt` (Revit 2025), DWG `MB_KC_*.dwg` của dự án thử.

| Phần | Đạt / Không | Ghi chú, lỗi (chép nguyên văn) |
| --- | --- | --- |
| 1. Kết nối | Đạt | Revit 2025, bridge 0.5.0, `devTools: loaded`. Sau khi cài lại, Claude vẫn giữ mô tả tool cũ (thiếu `dwgPath`) nhưng server mới vẫn nhận tham số. |
| 2. Tầng | Đạt (sau sửa) | 19 tầng (Móng −3600, Tầng 1–18 bước 3600) + CAD từng tầng, ~52 s, không timeout. `Level 1` → `Tầng 1` (đổi tên, không trùng). Lỗi đã sửa: `DaiMong`/`TangKyThuat`/`TangTum` viết liền không nhận; file dải tầng `Tang4-5` chỉ ra 1 tầng; `Level 2` thừa không có mục xoá `D1` (viewport ẩn và work plane grid của view bị coi là đang dùng). Mới: mục `X1` kéo trục theo dải level và level theo trục biên. Chưa thử: Ctrl+Z, ký tự cấm, `viewTemplate`, `cadUnit`, mốc cao độ (C). |
| 3. Trục | Đạt (sau sửa) | 17 trục, layer `kata_grid` nét `IMPORT-CENTER`, không phải làm tròn nhịp. Lỗi đã sửa: tên trục tự đánh vì chữ đầu trục nằm trong DWG → `propose_grids` đọc file (`dwgPath`); vòng tròn đầu trục là Ellipse (trước bị bỏ); chữ trong block lồng `truc/toado` lấy sai điểm căn; vòng tròn trục ngang cách đầu trục 3,5 m; đường 1800 không tên thành trục A làm lệch tên → giờ là `A'` (đường phụ, Bỏ sẵn), 1-A là trục có tên đầu tiên. File khai Inches nhưng vẽ mm: căn vị trí tự xử lý (tỉ lệ 1/25,4). Mỗi CAD dời theo trục của chính nó, CAD Móng khớp. |
| 4. Cọc, đài | Một phần | 128 cọc (D800×54, D1000×74), 57 đài + 57 lót, đỉnh cọc = đáy đài + 100, join đúng. Lỗi: chiều dài cọc không đặt được (`Length` của `BTC-CocTron_BeTong` chỉ đọc, cọc 6,1 m thay vì 30 m); 13 đường bao không cọc trên `DCE_BaoBeTong` thành đài (6 dựng, 7 lồng trong đài khác, có "floors overlap"); chiều dày đài không đọc được (ký hiệu trong DWG). Số hiệu mục `C…` không theo thứ tự quét. Cần: đài = đường bao chứa cọc, không phụ thuộc layer. |
| 5. Cột | Đạt, cần nâng cấp | 51 cột (350x1300×4, 350x1400×28, 500x1700×1, 1000x1000×18), cách trục bội 5 mm, Column Location Mark khớp, vị trí đúng (người dùng kiểm tra). `taggedColumns: 0` (ký hiệu trong DWG). Vẫn phải chọn layer — trái hướng "nhận như kỹ sư": hatch `01 Hatch colum` SOLID ×51 đúng bằng số cột, nên nhận cột từ hatch là khả thi. |
| 6. Vách | Một phần | Đọc DWG khớp 95,1%, `regionFrom: grids`, mẫu cột SOLID học từ cột đã dựng, quét lại `cached: true`, propose lại → `alreadyModeledGroups`. Dựng 17 vách (w300×13, w330×4), 6 vách sai đã xoá: chỉ có đài đỡ 0–67% chiều dài (dải mép trục A/D là dầm), 11 vách lõi nằm 100% trên đài. Lỗi: nhóm w350 trùng cột dẹt đã dựng; type khớp theo bề dày (`CW 102-85-100p`, `Exterior - Brick on Mtl. Stud`) — phải tạo `BTC-VachKC_300/330` bằng send_code. Cần luật: vách phải có đỡ liên tục bên dưới; mẫu hatch BETONG/AR-CONC là bằng chứng bê tông. |
| 7. Dầm | Đạt (sau sửa) | Dò từ 51 cột + 11 vách: 87 dầm chính. Lần đầu cả 12 nhóm bị chặn (thiếu h, ký hiệu trong DWG) → `propose_beams` đọc ký hiệu từ file (`dwgPath`, khớp 80,5%): 24 nhóm theo ký hiệu thật, dựng 78 dầm, b/h đúng ký hiệu, đỉnh +3600, có Mark, join cột. Còn lại: 251 ký hiệu dầm phụ 220x600 chưa dựng (dầm phụ chưa hỗ trợ); 3 nhóm `D2-X14 b275`, `D2-X6A b100`, `b350` (ký hiệu lan sang cặp nét đo bề rộng khác); 2 dầm treo (A-8 dọc trục 8 1800, C-7 dọc trục 7 5340). |
| 8. Sàn | Chưa đủ | Thử khi chỉ có vách lõi: 2 ô trong lõi thang rộng 2,5 m — là giếng, không phải sàn; không dựng. Cần: ô chỉ bao bởi vách lõi → gợi ý giếng, Bỏ sẵn. Thử lại sau khi có dầm. |
| 9. An toàn | Đạt | Chỉ đọc: `commit_plan` → `-32008`, `propose_*` vẫn chạy. Sửa tay model → `-32022`, không ghi gì. `open_plan_review` mở lại, giữ mục đã duyệt. Lưu ý: sau khi bật chỉ đọc, server ẩn lệnh ghi và trả `-32602 Tool commit_plan disabled` cho tới khi ping lại. |
| 10. Bộ dev | Đạt | `send_code` đọc/ghi qua cửa sổ duyệt (kéo trục/level, xoá vách, tạo type vách), một Ctrl+Z. Mô tả ghi `ctx.Mm` nhưng phải là `QueryContext.Mm` (đã sửa). Command set `checks/walls-on-caps` lưu, chạy bằng `script`+`args`, `history` đủ. `export_cad_case` Tầng 1: CAD import, AC1032, Inches, khớp 1658/1743 nét (95,1%), `problem: null`, hatch `01 Hatch colum` SOLID×51, `DCE_Hatch` BRASS×35, BETONG×16, EARTH×6, AR-CONC×4. |
