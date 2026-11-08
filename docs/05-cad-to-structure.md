# 05 — Dựng model kết cấu từ CAD

Mục tiêu: Claude dựng cột, dầm, vách, sàn, cọc từ bản vẽ CAD đã import/link vào Revit, có người duyệt trước mỗi lần ghi. Đã có **tầng + trục, cột, dầm, vách, sàn, cọc + đài**. Mọi thứ trừ cột chưa được thử trong Revit thật.

## Lõi kết cấu

Thuật toán quét CAD và đặt phần tử nằm trong `addin/StructuralCore/`. Lõi được chép từ repo `RevitAddinManager` (`Tools/_Shared/StructuralCore/`, commit `f2db0d5`) và từ nay được duy trì riêng ở đây. Các hot tool WPF bên `RevitAddinManager` giữ bản của chúng, nên sửa lõi ở repo này không ảnh hưởng tới chúng và ngược lại.

- Các file `.cs` của lõi được **biên dịch thẳng** vào DLL add-in, không tham chiếu DLL riêng.
- Các family chuẩn (`StructuralCore/Families/*.rfa`) được nhúng vào DLL dưới tên tài nguyên `Bimtech.StructuralCore.Families.<tên file>`.

| Thành phần lõi | Vai trò |
| --- | --- |
| `Cad/CadGeometry` | Hình chữ nhật xoay nhỏ nhất, convex hull, tên layer, vùng chữ nhật |
| `Columns/ColumnScanner` | Đọc đường bao cột theo layer từ một ImportInstance |
| `Columns/ColumnPlacer` | Gom nhóm theo kích thước, xem trước và thực hiện đặt cột / tạo Type |
| `Columns/ColumnTypeMatcher` | Khớp Type theo tham số b/h |
| `Beams/` | Ghép cặp line thành dầm, sửa line mồ côi, đọc tag từ Text, nối dầm tới cột, gom nhóm, xem trước + đặt |
| `Piles/` | Quét circle/đường bao, gán cọc vào đài, gom nhóm đường kính, xem trước + đặt cọc/đài, lớp lót, bắt tròn theo lưới, join (`PileWorkflow`) |
| `Common/PlacementModels` | Kết quả đặt dùng chung, đọc tham số kích thước của Type |
| `Families/DefaultFamilies` | Nạp family chuẩn công ty (cột, dầm, cọc) từ tài nguyên nhúng |

## Tầng và trục (đi qua cửa sổ duyệt)

Tầng và trục dùng khuôn propose / duyệt / commit của [04](04-architecture.md) như mọi loại khác. Việc chia làm hai vòng vì trục được đọc từ CAD, mà propose không được ghi nên không thể nhập CAD trước để đọc thử.

```mermaid
flowchart LR
    A[propose_levels] -->|duyệt + commit_plan| B[list_cad_sources]
    B --> C[propose_grids<br/>cadId + alignCadIds]
    C -->|duyệt + commit_plan| D[scan_cad_*]
```

0. (Tuỳ chọn) `suggest_levels_from_folder` với thư mục DWG: đọc **tên file** (không mở bản vẽ) để đề xuất bảng tầng, ví dụ `KC-03 MB TANG 2 (+3.600).dwg` → Tầng 2, +3600 mm. Nhận Tầng N, Hầm N, Móng, Tầng trệt, Tầng lửng, Sân thượng, Mái, Tum; cao độ theo mét (`+3.600`, `-1,500`, `±0.000`, `COS 7.20`). Số hiệu bản vẽ kiểu `KC-2.01` không bị đọc thành cao độ. Mặt cắt, chi tiết, bố trí thép được liệt kê riêng; mặt bằng điển hình không có tên tầng. Không đoán: thiếu tên hoặc cao độ thì để trống để hỏi người dùng. Có cảnh báo khi một tầng có nhiều file, thứ tự tên ngược thứ tự cao độ, hoặc chiều cao tầng bất thường.
1. `propose_levels` với danh sách `{ name, elevationMm, cadFile?, mode?, cadUnit? }`, cùng `viewTemplate?`, `scale?` cho các plan mới. Mỗi tầng là một mục `create_level`: tạo Level (hoặc dùng lại Level cùng tên, cùng cao độ), tạo Structural Plan `KC - <tên>` nếu chưa có, nhập hoặc link DWG vào plan đó tại gốc toạ độ. Không có `cadFile` thì chỉ tạo Level + plan. Tầng đã đủ và không có `cadFile` bị bỏ qua.
   - **Đối chiếu theo cao độ:** không có Level cùng tên nhưng có đúng một Level khác tên ở cùng cao độ (thường là `Level 1` của template) thì **đổi tên** Level đó thay vì tạo Level trùng cao độ; plan của nó mang tên cũ của Level (tên Revit tự đặt) cũng đổi theo thành `KC - <tên>`. Muốn giữ tên cũ thì bỏ mục đó trong cửa sổ duyệt.
   - Thành `blocked`: tên có ký tự Revit cấm (`\ : { } [ ] | ; < > ? ` ~`), thiếu file DWG, Level cùng tên nhưng khác cao độ, nhiều Level khác tên ở cùng cao độ, nhiều tầng trong danh sách cùng tên hoặc cùng cao độ, model không có loại view Structural Plan.
   - Chỉ ghi chú (vẫn duyệt được): Level đã có; Level sẽ được đổi tên; tên `KC - <tên>` đã bị view khác dùng nên plan mới thành `KC - <tên> (2)`; plan đã có CAD cùng tên file (nhập nữa sẽ thành 2 bản).
   - Tên plan hiện trong cửa sổ duyệt là đúng tên sẽ tạo.
   - **Chiều cao tầng** giữa hai tầng liên tiếp ngoài khoảng 2–8 m được ghi chú (thường là gõ nhầm, ví dụ 36000 thay vì 3600).
   - **Project Base Point** dời cao độ: mỗi mục có ghi chú "mốc cao độ chưa kiểm chứng" cho tới khi thử xong (docs/08, phần 2).
   - **Đơn vị DWG** (`cadUnit`): mặc định mm; `m`, `cm`, hoặc `auto` (theo file). Sau khi nhập, kết quả commit của mỗi mục có `cadSizeMm`; CAD rộng dưới 2 m hoặc trên 2 km có `cadWarning` (thường do sai đơn vị), khi đó Ctrl+Z rồi propose lại với `cadUnit` khác.
   - **View template và tỉ lệ** (`viewTemplate` là tên template Structural Plan, `scale` ví dụ 100): chỉ áp cho plan mới tạo. Sai tên template thì lỗi kèm danh sách template có trong model.
   - **Level thừa**: level không có phần tử nào phụ thuộc ngoài các view của chính nó, không view nào nằm trên sheet hay đang mở, và không được giữ hoặc đổi tên trong kế hoạch, được đề xuất xoá thành mục `D1`, `D2`... **mặc định Bỏ**; người dùng duyệt mục đó thì mới xoá (kèm các view của nó). Lúc commit kiểm tra lại, có gì phụ thuộc thì không xoá. Tắt bằng `proposeLevelCleanup: false`.
2. Người dùng duyệt trong Revit → `get_plan_status` → `commit_plan`. Mỗi tầng một Transaction, cả lượt một Ctrl+Z.
3. `list_cad_sources` lấy ID các CAD vừa nhập.
4. `propose_grids` với `cadId` là CAD có trục và `alignCadIds` là CAD các tầng còn lại.
   - **Tìm trục** (`addin/Recognition/GridRecognizer.cs`): line dài đứng/ngang (phủ ít nhất 30% bản vẽ) theo từng graphics style, chấm điểm theo **line pattern** (nét tâm, chấm gạch) trước, rồi **vòng tròn đầu trục**, cuối cùng **tên layer** (GRID, TRUC/TRỤC, AXIS). Chọn sai thì truyền `gridLayer`. Không tìm được trục ở cả hai phương thì báo lỗi kèm điểm của từng style.
   - **Tên trục** lấy từ text trong vòng tròn đầu trục khi đọc được (Text Note), giữ được `1'`, `C1`; không thì trục đứng 1, 2, 3… trái sang phải, trục ngang A, B, C… dưới lên. Hai trục cùng tên thì trục sau bị chặn.
   - **Sửa nhịp lệch** (`GridLayout.Snap`): mỗi nhịp giữa hai trục liền nhau được làm tròn về bội của `snapModuleMm` (mặc định 50) khi lệch không quá `snapToleranceMm` (mặc định 10): 5998,7 → 6000. Vị trí trục là tổng các nhịp đã làm tròn tính từ trục đầu, nên sai số không cộng dồn. Nhịp lệch quá ngưỡng giữ số đo (làm tròn 1 mm) và có ghi chú. `snapModuleMm: 0` chỉ làm tròn 1 mm.
   - **Trục đầu tiên** mỗi phương (trái nhất, dưới nhất) nằm trên Project Base Point.
   - **Căn từng CAD theo trục của chính nó** (`GridLayout.Match`): tìm trục trong từng CAD của `alignCadIds` và đối chiếu khoảng cách trục với CAD gốc (sai số 20 mm, cho phép bản vẽ tầng chỉ có một phần các trục). Khớp duy nhất thì CAD đó dời theo vector riêng; không tìm được trục hoặc khớp nhiều chỗ (lưới đều) thì dời theo vector của CAD gốc và mục `move_cad` ghi rõ để người duyệt kiểm tra.
   - **Trục xiên** chưa dựng: được đếm và cảnh báo, không bỏ qua lặng lẽ.
   - Mục `move_cad` (M1, M2…) và `create_grid` (G-1, G-A…), mô tả như `Trục B (ngang): y = 6000 mm từ gốc, nhịp 6000`. Trục và CAD được **ghim** sau khi ghi (`pin: false` để không ghim). Trục trùng tên Grid đã có thì bị chặn.
5. Duyệt → `commit_plan`. Bỏ mục `move_cad` thì trục vẫn dựng tại Project Base Point nhưng CAD đó lệch khỏi trục.

Sau khi nhập CAD (`propose_levels`), kết quả commit có `cadWarning` khi CAD nằm cách gốc toạ độ trên 10 km (bản vẽ dùng toạ độ thực tế như VN-2000). `propose_grids` sẽ dời nó về trục.

`GridBuilder` (lõi cũ, chỉ theo tên layer) vẫn dùng cho tool WPF qua `LevelSetup.ApplyRow`, không còn dùng trong MCP.

## Trục từ dữ liệu sạch (không cần CAD import)

Hướng thay thế cho việc import CAD vào Revit: CAD được đọc **bên ngoài Revit** bởi bộ trích xuất (hiện là dự án Python `261107_InnoCad-mcp`, sau này chuyển vào add-in). Kết quả là file `<tên>_grid.json` theo chuẩn `grid/1.0`, đã kiểm tra dim, dãy tên, đơn vị, kèm handle CAD và quyết định của người dùng (`_fix.json`). Revit chỉ còn **dựng**.

`propose_grids_from_data(dataPath)`:

1. Đọc và kiểm tra file (`addin/Data/GridDataReader`): sai `schema` (khác `grid/1.x`), đơn vị khác mm, hệ trục xoay → từ chối, không đoán.
2. Mỗi trục một mục `create_grid` (`addin/Data/GridDataPlanner`) — **cùng params với `propose_grids`**, nên cửa sổ duyệt, nét cam xem trước và `commit_plan` dùng chung. Gốc dữ liệu (`meta.origin`, vd A-1) trùng Project Base Point.
3. Trục chưa có tên, trùng tên trong dữ liệu, hoặc tên đã có trong model → **Blocked**. Dữ liệu qua tự kiểm tra (`stats.self_check`) → độ tin cậy 90% (tự duyệt); không qua/không có → 40% (phải xem).
4. Truy vết: file kế hoạch giữ handle CAD, file nguồn và hash của từng trục cùng `gridId` đã tạo. Grid của Revit **không có** Comments/Mark (đã kiểm chứng), nên `CreateGridAction` trả `commented: false`; muốn lưu trên chính phần tử thì cần Extensible Storage (chưa làm).
5. Mục cuối `X1` căn phạm vi trục/level như `propose_grids`.

Không có CAD trong model nên không có mục dời CAD. Muốn có nền để nhìn, link DWG rồi dời theo `meta.origin_cad` (dời chính xác, không cần khớp nét) — chưa tự động.

Phần đọc và lập mục không dùng Revit API, có kiểm thử trong `tests/Bimtech.RevitMcp.Planning.Tests/GridDataTests.cs` với file thật `Data/MB_KC_Tang2_grid.json` (17 trục).

## Cột từ dữ liệu sạch (không cần CAD import)

`propose_columns_from_data(dataPath)` đọc `<tên>_column.json` (chuẩn `column/1.0`) do bộ trích xuất bên ngoài sinh ra. Toạ độ cột tính **từ gốc lưới** của `grid/1.0`, nên phải dựng trục bằng `propose_grids_from_data` trước.

1. Đọc và kiểm tra (`addin/Data/ColumnDataReader`): sai `schema`, đơn vị khác mm, góc khác 0/90, kích thước ≤ 0 thì từ chối.
2. **Kiểm tra gốc:** hai trục của `meta.origin` (vd `A` và `1`) phải đi qua Project Base Point (sai số 1 mm). Nếu lệch hoặc thiếu trục, mọi mục còn 40% (phải xem) kèm lý do. Nếu không kiểm tra, cột sẽ lệch cả loạt.
3. **Level:** `baseLevelId` / `topLevelId`, hoặc theo tên `meta.level.base`; đỉnh `next` = level kế trên theo cao độ.
4. **Mỗi loại một mục** `create_columns` (`addin/Data/ColumnDataPlanner`), **cùng params với `propose_columns`**: `angleDeg = 0`, `lengthMm` theo X, `widthMm` theo Y; `ColumnPlacer` tự xoay Type b×h cho khớp.
5. **Loại cột:** `visible` dựng; `planted` (cột cấy) là mục riêng `K-<type>-cay`, luôn phải xem; `hidden` không dựng, chỉ liệt kê; không rõ loại thì phải xem.
6. **Type:** `types` (ID hoặc `"new"`) → Type có sẵn đúng b/h → Type b×h mới trong `templateFamilyId` hoặc `BTC-CotCN_BeTong` → Blocked. Mục sắp tạo Type mới có độ tin cậy 45% (phải xem); dùng Type có sẵn và dữ liệu đã kiểm tra thì 90% (tự duyệt).
7. Cột đã có trong model tại đó (mọi family, cùng khoảng cao độ) bỏ qua lúc propose và lúc commit.

Kết quả trả về kèm `dataIssues`, `emptyCrossings`, `hidden`, `wallLikeLeftForWalls` (đường bao giống vách để bước vách). Phần đọc và lập mục không dùng Revit API, có kiểm thử trong `tests/Bimtech.RevitMcp.Planning.Tests/ColumnDataTests.cs` với file thật `Data/MB_KC_Tang2_column.json` (51 cột).

## Vách từ dữ liệu sạch (không cần CAD import)

`propose_walls_from_data(dataPath)` đọc `<tên>_wall.json` (chuẩn `wall/1.0`). Bộ trích xuất đọc bản vẽ **trực tiếp qua InnoCAD**, tách đường bao bê tông thành các đoạn vách thẳng (đường tim + bề dày) và kéo đầu vách tới tim vách giao.

1. Đọc và kiểm tra (`addin/Data/WallDataReader`): sai `schema`, đơn vị khác mm, bề dày ≤ 0, đoạn dài 0 thì từ chối. Đọc thêm bảng `outlines` để biết **đường bao nào tách chưa khớp diện tích**; chỉ đoạn thuộc đường bao đó phải xem, không hạ cả file.
2. **Kiểm tra gốc** và **level**: dùng chung `Commands/Structural/DataPlacement` với cột.
3. **Mỗi loại bề dày một mục** `create_walls` (`addin/Data/WallDataPlanner`), **cùng params với `propose_walls`**: `lines [[x0, y0, x1, y1]]` theo toạ độ model, `thicknessMm`, level. Đoạn có cờ (`sliver`, `odd_thickness`, `overlaps_column`) hoặc thuộc đường bao chưa khớp vào mục `W-<type>-xem` (40%).
4. **Type:** `types` (ID hoặc `"new"`) → Basic Wall đúng bề dày (`Recognition.WallTypes`) → `BTC-VachKC_<b>` tạo mới lúc ghi từ `templateTypeId` hoặc Type bê tông gần nhất → Blocked. Mục sẽ tạo Type mới có độ tin cậy 45%, Type có sẵn 90%.
5. Lúc ghi, `WallPlacer` bỏ phần tim vách đã có vách.
6. **Cửa (wall/1.1):** khe trên bản vẽ là cửa thang máy/thang bộ (vách bị cắt tại cao độ cửa nên không hatch) đã được bộ trích xuất lấp thành **vách liền** và ghi vào bảng `openings`. Mục duyệt mang `openings [[x0, y0, x1, y1, sill, head]]`; sau khi dựng vách, `WallOpenings` khoét **lỗ chữ nhật** (`NewOpening`) trên vách chứa nó, từ chân vách + sill tới head. Phần vách trên cửa (lanh tô) có sẵn. Cửa chưa có chiều cao thì vách vẫn liền nhưng không khoét, mục ghi lý do.

Kiểm thử: `tests/Bimtech.RevitMcp.Planning.Tests/WallDataTests.cs` với file thật `Data/MB_KC_Tang2_wall.json` (26 đoạn).

## Dầm từ dữ liệu sạch (không cần CAD import)

`propose_beams_from_data(dataPath)` đọc `<tên>_beam.json` (chuẩn `beam/1.0`). Bộ trích xuất đọc mặt bằng dầm **trực tiếp qua InnoCAD**: ghép cặp cạnh dầm (nét khuất, mép sàn liền nét, hai nét liền qua giếng thang), nối qua chỗ giao, cắt tại tâm cột, gắn tên dầm, kéo đầu dầm tới tâm cột / tim vách / tim dầm đỡ (gối lấy từ `_column.json`, `_wall.json`).

1. Đọc và kiểm tra (`addin/Data/BeamDataReader`): sai `schema`, đơn vị khác mm, b/h ≤ 0, dầm dài 0 thì từ chối. `b`/`h` được phép null (bản vẽ không ghi kích thước).
2. **Kiểm tra gốc** như cột; **level** = `levelId` hoặc `meta.level.base` theo tên (`DataPlacement.ResolveBaseLevel`); mặt dầm = level + `meta.top_offset_mm`.
3. **Mỗi loại b×h một mục** `create_beams` (`addin/Data/BeamDataPlanner`), **cùng params với `propose_beams`**; mỗi dầm mang `mark` riêng = tên dầm trên bản vẽ (`CreateBeamsAction` ghi vào Mark, ưu tiên mark của từng dầm). Dầm có cờ (`width_mismatch`, `multi_label`, `inner_edge`) vào mục `B-<type>-xem` (40%).
4. **Không dựng:** dầm không có kích thước (`unsized`, trả về để hỏi người dùng) và dải không phải dầm (`small` = lỗ sàn, `beside_beam` = bậc sàn).
5. **Type:** `types` → Type dầm đúng b/h → Type b×h tạo trong `templateFamilyId` hoặc `BTC-DamCN_BeTong` → Blocked. Type có sẵn 90%, tạo mới 45%.
6. Lúc ghi, dầm trùng trục với dầm đã có bị bỏ qua.

Kiểm thử: `tests/Bimtech.RevitMcp.Planning.Tests/BeamDataTests.cs` với file thật `Data/MB_KC_Tang2_beam.json` (141 nhịp, 123 dựng được).

## Sàn từ dữ liệu sạch (không cần CAD import)

`propose_floors_from_data(dataPath)` đọc `<tên>_slab.json` (chuẩn `slab/1.x`; `1.1`: sàn chính + mỗi vùng hatch một sàn). Bộ trích xuất lấy **đường bao sàn** là mọi thứ mép sàn bao lấy (loang từ ngoài vào, nét liền, vách và cột chặn; nét khuất không chặn vì sàn chạy qua dầm) và **lỗ sàn** là các ô có dấu X (giếng thang máy, thang bộ, hộp kỹ thuật). Bề dày, cao độ lấy từ `_fix.json` vì bản vẽ không ghi.

1. Đọc và kiểm tra (`addin/Data/SlabDataReader`): sai `schema`, đơn vị khác mm, bề dày ≤ 0, đường bao dưới 3 điểm thì từ chối.
2. **Kiểm tra gốc** như cột; **level** = `levelId` hoặc `meta.level.base` theo tên; mặt sàn = level + `top_offset`.
3. **Mỗi sàn một mục** `create_floor` (`addin/Data/SlabDataPlanner`): `points` (vòng ngoài), `holes` (các vòng trong của file `1.1`: lỗ mở và sàn vùng nằm trong; file `1.0`: các lỗ) theo toạ độ model, `thicknessMm`, `comment` = "Vùng <mẫu hatch>" cho sàn vùng (ghi vào Comments, bản vẽ không ghi công năng: đổi Type sau). `CreateFloorAction` nhận `holes`, `thicknessMm`, `comment` (vẫn tương thích `propose_floors`).
4. Lỗ chạm mép sàn không khoét được (là phần khuyết của mép) → không đưa vào `holes`, trả `notCut`, mục còn 40%.
5. **Type:** `floorTypeId` → `BTC_SanKC_<bề dày>` có sẵn → tạo `BTC_SanKC_<bề dày>` lúc ghi từ Floor Type có cấu tạo lớp (`FloorDefaults.CreateCompanyTypeInTransaction`) → Blocked. Có sẵn 90%, tạo mới 45%.
6. Lúc ghi, `FloorPlacer` xoá lại sàn vừa tạo nếu chồng sàn đã có (báo `skipped`).

Kiểm thử: `tests/Bimtech.RevitMcp.Planning.Tests/SlabDataTests.cs` với file thật `Data/MB_KC_Tang2_slab.json` (1 sàn, 32 lỗ).

## Vách và sàn (đi qua cửa sổ duyệt)

**Vách:** `scan_cad_walls` → `propose_walls` → duyệt → `commit_plan`.

1. `scan_cad_walls` đọc vách **từ hatch của chính file DWG** (đọc từ ổ đĩa vì Revit không cho đọc hatch; CAD link tự lấy đường dẫn, CAD import truyền `dwgPath`). File được đặt lên CAD bằng cách khớp line, không tin cách Revit đặt. Mỗi vùng rời của một hatch được đọc riêng (một hatch lõi thường gồm nhiều mảnh: khối chữ U, các mảnh vách bị cửa cắt, phần chữ H bên cạnh) và cắt thành các nhánh (lõi chữ L, T, U, H ra mỗi nhánh một vách); một nhánh chỉ là vách khi **hai bên dọc theo nó là khoảng trống**, dải nằm trong một vùng hatch lớn (vùng sàn, khu vực DOLMIT, ANSI…) không phải vách, dải cùng bề dày trên một đường được gộp, đầu vách chạm mặt vách vuông góc được kéo về tim (như Revit nối vách). Hatch có cạnh dài ≤ 1 m là **cột**, trừ khi nó **thẳng hàng với một vách cùng bề dày và cách vách đó không quá một ô cửa** (≤ 2,5 m): đó là mảnh vách bị cửa cắt (vd. 300x1000 giữa hai cửa lõi thang).
   Mỗi vách được **chấm điểm như kỹ sư** trên thang tin cậy chung (từ 50%), mỗi điểm kèm lý do: +10% hatch kiểu bê tông (BETONG, CONC, BTCT); +20% cùng kiểu hatch với cột đã dựng (+10% nếu chỉ đoán); +10% dày đúng cỡ vách bê tông (150–500, bội 50); +10% nằm trên trục; +10% nối vào cột; +20% trùng vách tầng dưới; **+20% nằm trên đài móng** (ít nhất 80% tim vách nằm trên đường bao một đài đã dựng, ở bất kỳ độ sâu nào); +10% thuộc cụm vách (lõi); +10% có chữ thang máy; ±5% tên layer; −20% dày cỡ tường xây (100/110/220…, **trừ khi hatch là bê tông**); −10% ngắn so với bề dày (cột dẹt); −30% layer đang tắt. **Vách phải đứng trên đài móng hoặc trên vách tầng dưới**: khi model đã có đài, vách không nằm trên đài nào và không trên vách nào bị **loại trừ** (giữ ≤ 40%, luôn phải xem); chưa có đài thì chỉ −20% khi không nối cột và không có vách dưới. Dải hatch nằm trong cột đã dựng (hatch của cột dài 350x1400) là cột, không phải vách. Từ 50%: nhóm `"w300"` (đề xuất mặc định); 20–50%: nhóm `"w300-maybe"` (phải `include`); dưới 20%: loại, vẫn báo lý do. Thứ tự dựng: **trục → đài cọc → cột → vách**. Vùng đọc mặc định là phạm vi trục trong model + 3 m (một file DWG thường có nhiều mặt bằng), nên **dựng trục và cột trước**.
   Bản vẽ không có hatch: truyền `layers` để dùng cách cũ (ghép cặp line song song, tên từ Text Note).
2. `propose_walls` với `scanId`, `baseLevelId`, `topLevelId` (offset tuỳ chọn). Nhóm `wNNN` (hoặc có tên, cách cũ) được đề xuất mặc định, nhóm còn lại phải nằm trong `include`. Mỗi mục trong cửa sổ duyệt có dòng **"Vì sao"** đếm bằng chứng của nhóm (vd `+1 nằm trên trục (12/14)`) và các trục vách nằm trên. Tuỳ chọn như tool WPF: `onlySupported` bỏ vách không có vách/móng đỡ trong 500 mm bên dưới; `extendToColumns` kéo đầu vách tới cột gần nhất.
3. **Mỗi nhóm là một mục** `create_walls` (không phải mỗi bức vách), để cửa sổ duyệt không có hàng trăm dòng. Muốn bỏ một nhóm thì bỏ mục đó, hoặc gọi lại với `exclude`.
4. Type (luật Revit-free `addin/Recognition/WallTypes.cs`, có kiểm thử):
   1. mục trong `types`: ID một Basic Wall Type, hoặc `"new"` để nhóm đó dùng Type công ty `BTC-VachKC_<b>` dù đã có Type khác cùng bề dày;
   2. `BTC-VachKC_<b>` đúng bề dày đó;
   3. Basic Wall **bê tông** lệch ≤ 1 mm: lớp Structure có vật liệu bê tông (class hoặc tên), hoặc tên Type có *bê tông / concrete / BTCT / vách*;
   4. không có thì tạo `BTC-VachKC_<b>` lúc commit, nhân bản từ `templateTypeId` (mặc định: Type `BTC-VachKC_` có sẵn, không thì Type bê tông, gần bề dày nhất).

   Type mẫu của template Revit (`CW 102-85-100p`, Brick, Masonry, Stud, Render, Block, Generic, Partition, gạch…) **không bao giờ** là `exactType`, dù đúng bề dày; nhiều nhất là `nearestType`. (Model 261016: nhóm w300 từng bị gán `CW 102-85-100p` dày 299 mm thay vì `BTC-VachKC_300`.) Các nhóm cùng bề dày dùng chung Type mới.
5. Lúc commit, phần tim đã có vách cùng trục bị trừ đi (kể cả vách vừa tạo ở mục trước), chỉ tạo đoạn còn thiếu ≥ 50 mm.

**Sàn:** `propose_floors` → duyệt → `commit_plan`. Sàn không đọc từ CAD mà từ **model**: tim dầm có Reference Level là tầng đó và tim vách thẳng cắt qua cao độ tầng (theo view), dò thành các ô kín; ô dưới 1 m² coi là lỗ mở. Vì vậy cần dựng dầm, vách trước.

- **Mỗi ô là một mục** `create_floor` (S1 ở góc trên trái, đọc như bản vẽ).
- Type: `floorTypeId` → `BTC_SanKC_150` → tạo `BTC_SanKC_150` (150 mm) lúc commit nếu chưa có.
- Ô có tâm nằm trong khung bao của sàn đã có được ghi chú "có thể trùng"; lúc commit, Revit kiểm tra giao nhau thật và ô chồng lên sàn cũ bị bỏ qua (ghi trong `result.skipped`), không tính là lỗi.

## Quy trình (cột)

```mermaid
flowchart LR
    A[list_cad_sources] --> B[scan_cad_columns<br/>mọi layer → scanId]
    B -->|chưa có Type b/h| L[load_default_family]
    L --> D
    B --> D[propose_columns]
    D -->|duyệt trong Revit| E[commit_plan]
```

1. `list_cad_sources`: ID các CAD trong model. CAD gắn riêng một view (`viewSpecific`) sẽ được quét trong view đó. Dựng **trục** (và cột, đài móng tầng dưới) trước: chúng là bằng chứng.
2. `scan_cad_columns` (`ColumnRecognizer`): **không cần chọn layer**, đọc như kỹ sư.
   - **Ứng viên** từ mọi layer: hình chữ nhật thật (PolyLine hoặc line rời) cạnh 100–2000 mm, mọi đầu mút nằm trên biên và bốn cạnh vẽ đủ ít nhất 90%. Cột chữ L, cột vẽ dính vách không thành ứng viên.
   - **Hatch** đọc từ chính file DWG (CAD link tự lấy đường dẫn, CAD import truyền `dwgPath`) chỉ **xác nhận** một đường bao trùng khít; hatch đứng một mình không thành cột. Không đọc được file thì vẫn quét, ít bằng chứng hơn.
   - Cùng một cột vẽ hai lần (đường bao và hatch, hay hai layer) tính một.
   - **Bằng chứng** (có dấu, lý do ghi lại cho cửa sổ duyệt): +20% ở giao trục (+10% nằm trên 1 trục), +20% có hatch bên trong, +10% kích thước lặp lại ≥ 3 lần, +10% có ký hiệu cột bên cạnh, +20% có cột hoặc đài móng tầng dưới (`levelId`, mặc định tầng của view), +5% tên layer giống cột; −20% nằm trong hatch vách (không rộng hơn vách), **loại trừ** (giữ ≤ 40%): thẳng hàng với mảnh cùng bề dày cách nhau cỡ ô cửa, 600–2500 mm (**đoạn vách bị cửa cắt**; hai mảnh ở hai giao trục khác nhau là hai cột, không xét); có hai đường chéo X mà **không tô**, không có cột tầng dưới, trên bản vẽ có tô các cột vẽ X (≥ 3 cột X có hatch) (**ký hiệu lỗ mở sàn**; cột vẽ X có tô, hay đứng trên cột tầng dưới, không bị ảnh hưởng; không đọc được hatch thì không xét), −5% tên layer giống thứ khác (cửa, thiết bị, kích thước), −10% đơn lẻ (không trục, không hatch, không ký hiệu, kích thước không lặp).
   - Thang tin cậy chung (xem [04](04-architecture.md)): bắt đầu 50%; **từ 50% tự duyệt**; 20–50% **xem lại** (nhóm `"300x500-maybe"`); dưới 20% bỏ, chỉ báo số lượng và lý do.
   - **Ký hiệu cột** đọc từ Text Note và chữ trong file DWG, gần nhất trong 1500 mm (`C1 300x500`); mỗi text chỉ gán cho cột gần nó nhất.
   - Nhóm theo ký hiệu và kích thước: khóa `"C1"`, hoặc `"C1 300x500"` khi một ký hiệu có nhiều kích thước, hoặc kích thước **b×h cạnh nhỏ trước** `"300x500"` khi không có ký hiệu (trùng tên Type tạo mới). Ký hiệu ghi kích thước khác hình vẽ thì có `tagSizeMismatch`.
   - Mỗi nhóm có số đếm bằng chứng (`evidence`), Type khớp đúng (`exactType`) hoặc gần nhất (`nearestType`), và `scanId`. `layers` chỉ còn để giới hạn khi cần. Hình học ở lại trong add-in.
   - Test hồi quy: mọi case trong `cases/` có cột trong model phải tìm lại ≥ 95% số cột đó ở mức chắc chắn (`CaseLibraryTests`).
4. Nếu model chưa có Type cột nào có tham số b/h: `load_default_family` với `kind=column` nạp `BTC-CotCN_BeTong` và trả `familyId`.
5. `propose_columns` với base/top level: **mỗi nhóm là một mục duyệt**, kèm "Vì sao" (số cột có từng bằng chứng). Nhóm `-maybe` (dưới 50%) ghi "Bằng chứng chưa đủ" và không tự duyệt. Mục lấy độ tin cậy của cột kém chắc nhất; mục sắp tạo Type mới luôn phải xem.
   - **Cột thường đứng ở giao hai trục.** Xét từng phương: trục đi qua cột hoặc cách mặt cột không quá `gridMarginMm` (mặc định 300) là cột nằm trên trục đó. Khi đó độ lệch cách **đồng tâm** hoặc **mép trùng trục** không quá `gridToleranceMm` (mặc định 15) được đặt đúng vào đó; độ lệch khác **làm tròn 5 mm** (`snapMm`). Cột không nằm trên trục nào chỉ được làm tròn 5 mm.
   - Cột trùng vị trí và kích thước với cột đã **bỏ** ở tầng dưới (các kế hoạch cột đã ghi kết thúc ở base level; vị trí dựng bù sau đó thì thôi) tách thành mục riêng đã **tự bỏ**; kết quả có `rejectedAsBelow`. Dựng từng tầng từ dưới lên để quyết định truyền lên.
   - Mục duyệt ghi các giao trục của nhóm (`tại A-1, A-2…`); cột chỉ nằm trên một trục (cột phụ, cột thang?) hoặc không nằm trên trục nào có ghi chú. Kết quả trả `emptyCrossings`: **giao trục không có cột** trong vùng các cột (ô thông tầng, hay cột vẽ ở layer khác) để người dùng kiểm tra.
   - Cột vẽ lệch dưới 2° so với phương trục được **đặt thẳng theo trục** (chọn hướng trục gần nhất, không đổi chỗ b/h).
   - Cột vẽ trùng (cùng tâm) chỉ đặt một lần; cột đã có trong model ở đó (**mọi family**, cao độ chồng nhau) bị bỏ qua.
   - Ký hiệu được ghi vào tham số **Mark** (`writeMark`, mặc định có).
6. Duyệt → `commit_plan`: mỗi nhóm một Transaction, cả lượt một Ctrl+Z.

Type cho mỗi nhóm: mục trong `types` → Type có sẵn khớp b/h → Type `b×h` tạo trong `templateFamilyId` → chưa có Type (mục bị chặn). Giá trị `"new"` trong `types` bỏ qua bước khớp: nhóm dùng Type `b×h` trong `templateFamilyId` (mặc định `BTC-CotCN_BeTong` nếu đã nạp), dùng lại nếu family đó đã có, không thì tạo lúc commit; dù family khác đã có Type cùng kích thước. Cột tròn chưa hỗ trợ.

## Đổi Type phần tử đã dựng

`propose_change_type` → duyệt → `commit_plan`. Dùng khi phần tử đã dựng sai Type (trước đây phải dùng `send_code`).

- Tham số: `elementIds` (tối đa 1000) và `typeId` (Type đích). Vách cần Wall Type, phần tử family (cột, dầm…) cần Family Type **cùng category**; Type khác có category (sàn…) cũng được, miễn Revit cho phép (`Element.GetValidTypes`).
- **Mỗi Type hiện tại là một mục** `change_type`, cột Phần tử ghi `Walls · CW 102-85-100p → BTC-VachKC_300 (14)`. Vách đổi bề dày có ghi chú `Bề dày đổi 299 → 300 mm`.
- Phần tử đã đúng Type bị bỏ qua (`alreadyOfType`); phần tử khác category, không có Type hoặc Revit không cho đổi bị từ chối và liệt kê trong `refused` kèm lý do. Không còn phần tử nào đổi được thì báo lỗi.
- Lúc commit đổi Type từng phần tử (`ChangeTypeId`) trong Transaction của mục; phần tử đã bị xoá đếm vào `missing`, phần tử Revit từ chối chỉ lỗi riêng nó. Cả lượt một Ctrl+Z.
- Cách sắp xếp phần tử vào mục là luật Revit-free `addin/Commands/Elements/TypeChangeGroups.cs`, có kiểm thử.

## Dầm

`propose_beams` → duyệt → `commit_plan` (dầm chính), rồi `propose_beams secondary=true` → duyệt → `commit_plan` (dầm phụ). Dầm console làm sau.

**Lan từ cột, trục làm thước.** Dựng cột (và vách) bên dưới tầng trước: chúng là gối tựa. Không cần bước quét riêng, không cần chọn layer (`layers` chỉ để giới hạn khi cần).

1. Gối tựa: cột và vách **trong model** có chiều cao chạm cao độ tầng (cột tầng dưới và tầng trên cùng chỗ chỉ tính một). Đường bao lấy từ hình khối thật của family.
2. Line CAD: chỉ giữ line theo phương trục (lệch ≤ 2°; line xiên được đếm); line nằm trọn trong cột hoặc vách (đường bao, hatch) bị bỏ.
3. Ở mỗi mặt cột: cặp line song song **xuất phát trong 300 mm** (`probeMm`) từ mặt cột, cách nhau 100–1000 mm (`minWidthMm`/`maxWidthMm`), tim nằm trong bề rộng cột, là đầu một dầm. Line nằm đúng giữa hai line khác là **nét tim**, không phải mép dầm.
4. Dò dọc hai mép, nhảy qua chỗ đứt tới 1500 mm (`maxGapMm`, chỗ dầm khác cắt ngang), tới **cột hoặc vách đầu tiên** cùng dải. Hai mép phải vẽ đủ ít nhất 60% nhịp (`minCoverage`).
5. Dầm đặt **từ tim cột tới tim cột** (vách: tới tim vách). **Chỉ chia tại gối tựa**, giao trục không có cột thì dầm chạy liền qua.
6. Mép dầm đi ra từ cột mà không tới gối tựa nào → **đầu dầm lơ lửng** (`report.dangling`): dầm phụ hoặc console, chưa dựng.

**Ký hiệu dầm** (`D1 (220x600)`, `220X600`, `DC1 250*500`, `DP3`) đọc từ Text Note trong view, kể cả mã định dạng MText:
- Mỗi ký hiệu gán cho dầm **cùng phương với chữ** gần nhất trong 1000 mm (`tagDistanceMm`), rồi **lan sang các dầm thẳng hàng** (cùng bề rộng, chung gối tựa) tới khi gặp ký hiệu khác.
- b lấy theo ký hiệu, không có thì đo từ nét (làm tròn 5 mm); ký hiệu ghi b khác nét đo quá 10 mm thì có ghi chú. **h chỉ lấy từ ký hiệu** hoặc `heightsMm`; không có thì mục **bị chặn** để hỏi người dùng, không đoán.
- Ký hiệu không gán được dầm nào nằm trong `report.unusedMarks`.

**Trục**: độ lệch tim dầm so với trục đặt như cột (đồng tâm hoặc mép trùng trục trong `gridToleranceMm` thì đặt đúng, còn lại làm tròn `snapMm`). Mỗi dầm ghi `A-1 → A-2 (trục A)`; dầm không nằm trên trục nào có ghi chú.

**Mục duyệt**: mỗi nhóm (ký hiệu + kích thước; khóa `"D1"`, `"D1 220x600"` khi một ký hiệu có nhiều kích thước, `"220x600"`, hoặc `"b220"` khi chưa biết h) là một mục, ghi các nhịp, số dầm lấy ký hiệu từ dầm bên cạnh, dầm nét đứt nhiều (phủ dưới 90%). Dầm đã có trong model trên cùng trục bị bỏ qua. Cao độ đỉnh dầm = tầng + `offsetMm`. Ký hiệu ghi vào **Mark** (`writeMark`).

Type mỗi nhóm: mục trong `types` → Type có sẵn khớp b×h → Type `b×h` tạo trong `templateFamilyId` (mặc định `BTC-DamCN_BeTong` nếu đã nạp; `load_default_family kind=beam`) → chưa có Type (mục bị chặn).

**Giới hạn đã biết**: Revit API không đọc được chữ bên trong file DWG import/link, chỉ đọc Text Note của Revit. Nếu bản vẽ chưa có Text Note, dầm vẫn được nhận nhưng không có ký hiệu (cần `heightsMm` hoặc `types`).

### Dầm phụ (`secondary=true`)

**Lan dần từ gối, theo cách truyền lực**: dầm chính gác lên cột và vách, dầm phụ gác lên dầm chính. Làm **hai bước**: duyệt và commit dầm chính trước, rồi mới gọi `propose_beams secondary=true`, để dầm phụ chỉ gác lên dầm người dùng đã duyệt.

1. Gối tựa: cột, vách, và **dầm đã có trong model ở tầng đó** (mỗi dầm là hình chữ nhật dọc trục, rộng b, sâu h theo Type). Mép của chính các dầm này nằm trong thân dầm nên bị bỏ.
2. Dầm phụ **xuất phát từ mặt bên của dầm gối** (dầm chạy ngang phương đang dò) hoặc từ mặt vách. Không xuất phát từ cột, vì ở vòng này dầm chính sẽ bị cắt ngắn tại dầm đầu tiên nó gặp; dầm phụ một đầu gác lên cột vẫn được tìm, dò từ đầu bên dầm sang.
3. Dò hai mép như dầm chính tới gối đầu tiên (dầm, cột, vách). Đoạn nằm dọc thân một dầm đã có thì bỏ.
4. Đầu dầm phụ đặt ở **tim dầm gối**, đỉnh bằng tầng + `offsetMm` như dầm chính. Ký hiệu nhận cả `DP1` lẫn `D2-X3 (220x600)`, từ Text Note và chữ trong DWG.
5. **Bằng chứng** (`SecondaryBeams`): +20% hai đầu tựa lên gối, +10% có ký hiệu, +10% chia đều nhịp dầm gối (1/2, 1/3, 1/4) hoặc nằm trên trục, +10% vẽ nét khuất, +10% nối tiếp thành dải với dầm phụ khác; **loại trừ** (giữ ≤ 40%): **to hơn dầm gối** (b hay h lớn hơn), −20% trùng hatch tường hay vách (cặp nét tường xây 220 trông như dầm 220), −10% nhịp dưới 1 m, −10% nét đứt nhiều. Từ 50% tự duyệt; dầm phụ dưới 50% vào nhóm `-maybe` riêng, ghi "bằng chứng chưa đủ". Mục duyệt có dòng "Vì sao".
6. Chưa làm: dầm console (một đầu tự do), dầm phụ cấp 2 trong cùng một lượt (gọi lại `secondary=true` sau khi commit cấp 1).

## Cọc và đài

`scan_cad_piles` → `propose_piles` → duyệt → `commit_plan`. Chỉ hỗ trợ **cọc tròn**; cọc vuông chưa hỗ trợ.

**Quét** (`scan_cad_piles`, `PileRecognizer`): **không cần chọn layer**, một lượt, đọc như kỹ sư. Dựng trục trước.
- **Ứng viên** từ mọi layer: cọc là hình tròn đường kính 200–2000 mm (hai vòng đồng tâm gộp thành một, vòng nhỏ là ký hiệu); đài là PolyLine khép kín, hoặc hình chữ nhật vẽ bằng **line rời** (bản vẽ đã explode), cạnh 400 mm–20 m. Hai đường bao vẽ trùng gộp thành một.
- **Bằng chứng đài**: +20% có cọc bên trong, +20% chứa giao trục (+10% có trục đi qua), +10% có ký hiệu đài, +10% kích thước lặp lại, +5% tên layer giống đài; −20% không có cọc bên trong, **loại trừ** (giữ ≤ 40%): **bao lấy đài khác** (khung nhà, ô phòng; đường bao lót chỉ lớn hơn đài dưới 400 mm thì không tính), **loại trừ** (bỏ): **nằm trong đài có cọc** mà không có cọc riêng (cột, cổ móng vẽ trên đài), −5% tên layer giống thứ khác.
- **Bằng chứng cọc**: +20% nằm trong đài, +10% đường kính lặp lại ≥ 3 lần, +10% có ký hiệu cọc (nét chéo, hatch hay vòng thứ hai), +5% tên layer giống cọc; −60% **vòng tròn đầu trục** (ở đầu một trục, hay chứa tên trục), −5% tên layer giống thứ khác, −10% đơn lẻ.
- Thang tin cậy chung: bắt đầu 50%, **từ 50% tự duyệt**, 20–50% xem lại (đài ghi "Bằng chứng chưa đủ"), dưới 20% bỏ. Thêm **loại trừ** (bỏ): dải mảnh (tường lõi vẽ trên mặt bằng móng). Mục đài lấy độ tin cậy thấp nhất giữa đài và các cọc của nó; mục tên theo vị trí: `Đài #37 · 4900x4500 · B-2`. `pileLayers` / `capLayers` chỉ còn để giới hạn khi cần.
- Nhóm cọc theo đường kính (`D600`).
- **Cọc thuộc đài** chứa nó; cọc ngoài biên trong 300 mm vẫn thuộc đài gần nhất (vẽ lệch nhẹ); **xa hơn thì không thuộc đài nào** và được báo ra, không bị gán bừa cho một đài ở xa.
- **Ký hiệu đài** đọc từ Text Note hoặc chữ trong file DWG (`dwgPath` cho CAD import), trong hoặc cạnh đài: `Đ1 (2000x2000x1200)`, `ĐM2 h=1500`, `ĐC3 - 1500X2400 - H=1000`. Đài gom thành nhóm (`capGroups`) theo ký hiệu, không có ký hiệu thì theo kích thước (`2000x2000`), kèm chiều dày đọc được và số đếm bằng chứng.
- Đài vẽ bằng line rời chỉ nhận **hình chữ nhật**; đài đa giác (đài 3 cọc tam giác) phải là PolyLine khép kín.

**Đề xuất** (`propose_piles`): **mỗi đài là một mục** gồm đài, các cọc của nó và lớp lót.
- **Chiều dày đài theo từng nhóm**, theo thứ tự: `capTypes` → `capThicknessMm` → chiều dày ghi trong ký hiệu bản vẽ → `capFloorTypeId` (chung) → **chặn**, để Claude hỏi người dùng, không đoán. Có chiều dày mà chưa có Type thì lúc commit tạo `BTC-DaiMong_<h>` từ `capTemplateTypeId` (mặc định `BTC-DaiMong_1500`). Đỉnh cọc = cao độ level − chiều dày **của đài chứa nó**.
- **Làm tròn 5 mm** (`snapMm`): cạnh đài được làm vuông theo phương hệ trục rồi làm tròn bội 5 mm (1498,7 → 1500); đài và cọc được dời để khoảng cách tới trục gần nhất là bội 5 mm. Tính trước khi dựng nên đài (là Floor) cũng được làm tròn, không chỉ cọc.
- Lớp lót dưới mỗi đài (`withLot`, mặc định có, rộng thêm `lotOffsetMm` = 50).
- **Join chỉ giữa cọc với đài và lớp lót của chính nó**, không đụng join có sẵn trong model.
- `pileLengthMm`: đặt chiều dài cọc vào tham số `Length` (hoặc `Pile Length`, `Chiều dài`, `L`) nếu family có; không có thì kết quả có cảnh báo.
- Đài đã có ở vị trí đó **và cỡ gần bằng** (lệch ≤ 300 mm hay 20%) thì bỏ qua (`alreadyModeledCaps`); hình nhỏ cùng tâm (cột vẽ trên đài) không chặn đài thật. Cọc đã có thì bỏ qua từng cọc.
- Cọc không thuộc đài nào gom thành một mục `P-NOCAP` bị chặn, liệt kê vị trí để kiểm tra.

`load_default_family kind=pile` nạp `BTC-CocTron_BeTong` và tạo `BTC-DaiMong_1500` / `BTC-BeTongLot_100` khi chưa có (cần sẵn ít nhất 1 Type Foundation Slab làm mẫu). Type cọc thiếu chỉ được tạo khi `createMissingTypes=true`, nhân bản từ Type gần nhất, tên theo đường kính. Gốc chèn của family cọc (đỉnh hay mũi cọc) cần kiểm chứng trong Revit.

## An toàn khi ghi

| Cơ chế | Tác dụng |
| --- | --- |
| `propose_*` chỉ đọc | Mọi thay đổi đi qua cửa sổ duyệt; chỉ `commit_plan` ghi, và chỉ những mục người dùng đã duyệt |
| `RequireWritableDocument()` | Từ chối family document, model chỉ đọc, hoặc khi Revit đang trong transaction khác (`-32006`) |
| `FailureGuard` | Cảnh báo Revit được ghi lại và bỏ qua; lỗi thì rollback và báo về — không có hộp thoại nào làm treo bridge |
| `TransactionGroup` | Mỗi lần ghi là một bước Undo; lỗi giữa chừng thì rollback toàn bộ |
| Chống trùng | Vị trí đã có cột cùng family, cùng khoảng cao độ, bị bỏ qua; chạy lại không tạo cột đôi |
| Giới hạn | Tối đa 5000 cột mỗi lần gọi |
| `undo_batch` | Không xóa Type/family còn được dùng (kể cả Type sàn móng); phần giữ lại vẫn gỡ được sau bằng cùng `batchId` |
| `WriteSession` | Khung chung của mọi lệnh ghi: FailureGuard + TransactionGroup + lưu batch |

`scanId` và `batchId` chỉ sống trong bộ nhớ của bridge (tối đa 20 mỗi loại) và gắn với document đã tạo ra chúng. Tắt bridge thì mất; khi đó dùng Ctrl+Z trong Revit.

## Thêm loại phần tử mới

Loại mới nên theo khuôn của tầng, trục, vách và sàn: lệnh `propose_*` chỉ đọc, phần ghi là các `IPlanAction` trong `addin/Planning/Actions/` (đăng ký ở `PlanActions.CreateDefault`), mỗi mục có `params.summary` để cửa sổ duyệt hiện một câu dễ đọc.

1. Nếu lõi chưa có loại đó: viết scanner/placer trong `addin/StructuralCore/<Loại>/`, không phụ thuộc UI (có thể tham khảo tool WPF bên `RevitAddinManager` nhưng không sửa repo đó). Thêm hàm `Plan*` xem trước cho mỗi thao tác ghi.
2. Thêm `Scan<Loại>Command` và `Place<Loại>Command` trong `addin/Commands/Structural/` theo mẫu cột/dầm: kết quả quét lưu bằng `context.Sessions.Add`, lần chạy thật bọc trong `WriteSession`.
3. Khai báo công cụ trong `server/src/tools/structural.ts`; `tests/contract.test.mjs` kiểm tra hai phía khớp tên.
