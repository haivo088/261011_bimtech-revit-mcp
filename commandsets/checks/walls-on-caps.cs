var capType = ctx.Has("capType") ? ctx.Str("capType") : "BTC-DaiMong_1500";
var minPct = ctx.Has("minPct") ? ctx.Num("minPct") : 80;
var pbp = BasePoint.GetProjectBasePoint(doc).Position;
var caps = new FilteredElementCollector(doc).OfClass(typeof(Floor)).Cast<Floor>().Where(f => f.FloorType.Name == capType).Select(f => f.get_BoundingBox(null)).ToList();
var rows = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>().Where(w => w.Location is LocationCurve lc && lc.Curve is Line).Select(w => {
  var c = (Line)((LocationCurve)w.Location).Curve; int n = 100, on = 0;
  for (int i = 0; i <= n; i++) { var p = c.Evaluate(i / (double)n, true); if (caps.Any(b => p.X >= b.Min.X && p.X <= b.Max.X && p.Y >= b.Min.Y && p.Y <= b.Max.Y)) on++; }
  var a = c.GetEndPoint(0); var e = c.GetEndPoint(1); var pct = Math.Round(100.0 * on / (n + 1));
  return new { id = w.Id.Value, type = w.WallType.Name, from = $"({QueryContext.Mm(a.X - pbp.X):0},{QueryContext.Mm(a.Y - pbp.Y):0})", to = $"({QueryContext.Mm(e.X - pbp.X):0},{QueryContext.Mm(e.Y - pbp.Y):0})", lengthMm = Math.Round(QueryContext.Mm(c.Length)), onCapPct = pct, flagged = pct < minPct };
}).OrderBy(r => r.onCapPct).ToList();
return new { caps = caps.Count, walls = rows.Count, flagged = rows.Count(r => r.flagged), rows };
