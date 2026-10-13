# 03 — Thêm một công cụ mới

Mỗi công cụ gồm hai nửa có cùng tên method:

| Phía | File | Vai trò |
| --- | --- | --- |
| Server | `server/src/tools/<nhóm>.ts` | Tên công cụ, mô tả cho Claude, schema tham số (zod), annotations |
| Add-in | `addin/Commands/<Nhóm>/<Tên>Command.cs` | Đọc tham số, gọi Revit API, trả kết quả |

Ví dụ dưới đây thêm `get_element` đọc tên và category của một phần tử.

## 1. Phía server

Tạo `server/src/tools/elements.ts`:

```ts
import { z } from "zod";
import { defineTool } from "./define.js";

export const getElement = defineTool({
  name: "get_element",
  method: "get_element",
  title: "Get element",
  description: "Read the name and category of one element by its ID.",
  inputSchema: {
    elementId: z.string().regex(/^-?\d+$/).describe("Element ID as a string, e.g. \"12345\"."),
  },
  annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true },
});
```

Thêm `getElement` vào `allTools` trong `server/src/tools/index.ts`.

- `annotations` là bắt buộc. Công cụ sửa mô hình phải có `readOnlyHint: false`, và `destructiveHint: true` nếu xóa hoặc ghi đè dữ liệu.
- `timeoutMs` (tùy chọn) cho tác vụ chậm, tối đa 120000.
- `toParams` (tùy chọn) dùng khi tên tham số Claude thấy khác tên add-in cần.

## 2. Phía add-in

Tạo `addin/Commands/Elements/GetElementCommand.cs`:

```csharp
using Bimtech.RevitMcp.Protocol;

namespace Bimtech.RevitMcp.Commands.Elements;

public sealed class GetElementCommand : IRevitCommand
{
    public string Method => "get_element";

    public object? Execute(CommandContext context)
    {
        var document = context.RequireDocument();
        var id = context.Params.RequireElementId("elementId");
        var element = document.GetElement(id)
            ?? throw RpcException.InvalidParams("elementId", "no element has this ID.");
        return new
        {
            id = RevitJson.Id(element.Id),
            name = element.Name,
            category = element.Category?.Name,
        };
    }
}
```

Thêm `new GetElementCommand()` vào `CommandRegistry.CreateDefault()`.

Không đặt namespace là `Commands.Document` hoặc tên trùng kiểu Revit khác: namespace sẽ che kiểu `Autodesk.Revit.DB.Document`.

## 3. Kiểm tra

```powershell
Push-Location server; npm.cmd test; Pop-Location
dotnet build Bimtech.RevitMcp.sln -c Release
```

`tests/contract.test.mjs` báo lỗi nếu một method chỉ tồn tại ở một phía. Sau đó cài DLL mới và gọi thử trên mô hình mẫu.

## Công cụ có sẵn trong `CommandContext`

| Thành phần | Dùng để |
| --- | --- |
| `RequireDocument()` / `RequireUIDocument()` | Lấy mô hình đang mở, báo lỗi `-32001` nếu chưa mở |
| `Params.GetString/RequireString` | Chuỗi, có giới hạn độ dài |
| `Params.GetInt(name, default, min, max)` | Số nguyên trong khoảng cho phép |
| `Params.GetBool`, `Params.GetEnum<T>` | Boolean, enum Revit theo tên (`"FloorPlan"`) |
| `Params.RequireElementId`, `Params.GetElementIds` | ID phần tử dạng chuỗi hoặc số |
| `Params.GetStringList` | Mảng chuỗi, có giới hạn số phần tử |
| `Cancellation` | Gọi `ThrowIfCancellationRequested()` trong vòng lặp dài |
| `RevitJson.Id(...)` | Trả ElementId dạng chuỗi |

## Khung đọc dữ liệu dùng chung

Công cụ trả danh sách nên dùng lại các phần sau thay vì tự viết:

| Phía | Thành phần | Dùng để |
| --- | --- | --- |
| Server | `schemas.ts`: `paging`, `elementFilter`, `elementId` | Trải vào `inputSchema`: `{ ...elementFilter, ...paging }` |
| Server | `pagingNote`, `unitsNote` | Nối vào `description` để Claude biết cách lật trang và đơn vị |
| Add-in | `Common/Paging`: `PageRequest.From(params)`, `Paging.ToPage(...)` | Đọc `limit`/`offset`, trả `total`, `hasMore`, `nextOffset`, `items` |
| Add-in | `Common/ElementQuery`: `ElementQuery.From(params).Apply(doc)` | Lọc category/view/level/tên, sắp theo ID |
| Add-in | `Common/ElementInfo`: `new ElementInfo(doc).Summary(e)` | Mô tả ngắn một phần tử (tên type, level có cache) |
| Add-in | `Common/ParameterInfo`: `Select`, `From` | Đọc parameter, đổi đơn vị, kèm `display` của Revit |
| Add-in | `Common/UnitConvert` | Feet nội bộ → mm, m², m³, độ |
| Add-in | `Common/CategoryNames.Resolve` | `"OST_Walls"`, `"Walls"` hoặc tên category → ElementId |

Ví dụ một command danh sách hoàn chỉnh chỉ cần vài dòng; xem `Commands/Elements/GetElementsCommand.cs`.

Không đặt tên class trùng kiểu Revit (`Units`, `Categories`, `ElementFilter`...) vì sẽ gây lỗi tham chiếu mơ hồ.

## Mã lỗi

| Mã | Ý nghĩa |
| --- | --- |
| `-32601` | Method không tồn tại (kèm danh sách method có sẵn) |
| `-32602` | Tham số sai; `data.param` cho biết tham số nào |
| `-32603` | Lỗi không lường trước trong add-in |
| `-32001` | Chưa mở mô hình |
| `-32002` | Revit từ chối ExternalEvent |
| `-32003` | Hết thời gian chờ Revit |
| `-32004` | Cầu nối đang dừng |
| `-32005` | Kết quả lớn hơn 1 MiB; giảm `limit` hoặc lọc hẹp hơn |
| `-32010` | Revit API ném lỗi; `data.type` là tên exception |

Ném `RpcException` khi muốn Claude nhận một lỗi cụ thể. Các exception khác được handler chuyển thành `-32010` hoặc `-32603`.
