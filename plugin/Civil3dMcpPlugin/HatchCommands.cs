using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DMcpPlugin;

/// <summary>
/// Reads hatch objects: pattern, scale, angle, area, layer, color, associativity and boundary loops.
/// Source of hatches (first that applies): 'handles' → current selection (pickfirst) →
/// interactive prompt (prompt=true) → all hatches in model space (all=true, optional layer filter).
/// Added for TCCE.
/// </summary>
public static class HatchCommands
{
  public static Task<object?> GetHatchesAsync(JsonObject? parameters)
  {
    var handles = parameters?["handles"] as JsonArray;
    var prompt = PluginRuntime.GetOptionalBool(parameters, "prompt") ?? false;
    var all = PluginRuntime.GetOptionalBool(parameters, "all") ?? false;
    var layerFilter = PluginRuntime.GetOptionalString(parameters, "layer");
    var includeLoops = PluginRuntime.GetOptionalBool(parameters, "includeLoops") ?? true;
    var maxVertices = PluginRuntime.GetOptionalInt(parameters, "maxVertices") ?? 500;
    var limit = PluginRuntime.GetOptionalInt(parameters, "limit") ?? 200;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var ids = new List<ObjectId>();
      string source;

      if (handles != null && handles.Count > 0)
      {
        source = "handles";
        foreach (var h in handles)
        {
          var hs = h!.GetValue<string>();
          if (!db.TryGetObjectId(new Handle(Convert.ToInt64(hs, 16)), out var oid) || oid.IsErased)
            throw new JsonRpcDispatchException("CIVIL3D.NOT_FOUND", $"No object with handle '{hs}'.");
          ids.Add(oid);
        }
      }
      else
      {
        var ed = doc.Editor;
        var implied = ed.SelectImplied();
        if (implied.Status == PromptStatus.OK && implied.Value.Count > 0)
        {
          source = "selection";
          ids.AddRange(implied.Value.GetObjectIds());
        }
        else if (prompt)
        {
          source = "prompt";
          var opts = new PromptSelectionOptions { MessageForAdding = "\nSeleccione los hatches para Claude: " };
          var filter = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "HATCH") });
          var res = ed.GetSelection(opts, filter);
          if (res.Status != PromptStatus.OK)
            return new { hatches = Array.Empty<object>(), count = 0, source, message = "Selection cancelled." };
          ids.AddRange(res.Value.GetObjectIds());
        }
        else if (all)
        {
          source = "modelspace";
          var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
          var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
          foreach (ObjectId id in ms)
            if (id.ObjectClass.DxfName == "HATCH") ids.Add(id);
        }
        else
        {
          return new
          {
            hatches = Array.Empty<object>(),
            count = 0,
            source = "none",
            message = "No hatches selected. Select them in Civil 3D first, or call again with prompt=true (asks the user to pick), all=true, or handles.",
          };
        }
      }

      var hatches = new List<object>();
      var skipped = 0;
      double totalArea = 0;
      foreach (var id in ids)
      {
        if (hatches.Count >= limit) break;
        if (tr.GetObject(id, OpenMode.ForRead) is not Hatch h) { skipped++; continue; }
        if (layerFilter != null && !string.Equals(h.Layer, layerFilter, StringComparison.OrdinalIgnoreCase)) continue;
        try { totalArea += h.Area; } catch { }
        hatches.Add(Describe(h, tr, includeLoops, maxVertices));
      }

      return new { hatches, count = hatches.Count, totalArea, skippedNonHatch = skipped, source };
    });
  }

  private static object Describe(Hatch h, Transaction tr, bool includeLoops, int maxVertices)
  {
    double? area = null;
    try { area = h.Area; } catch { /* self-intersecting or invalid boundaries */ }

    object? extents = null;
    var b = h.Bounds;
    if (b.HasValue)
      extents = new { minX = b.Value.MinPoint.X, minY = b.Value.MinPoint.Y, maxX = b.Value.MaxPoint.X, maxY = b.Value.MaxPoint.Y };

    var assocHandles = new List<string>();
    if (h.Associative)
    {
      try
      {
        foreach (ObjectId aid in h.GetAssociatedObjectIds())
          if (!aid.IsNull && !aid.IsErased) assocHandles.Add(aid.Handle.ToString());
      }
      catch { }
    }

    List<object>? loops = null;
    if (includeLoops)
    {
      loops = new List<object>();
      for (int i = 0; i < h.NumberOfLoops; i++)
      {
        var loop = h.GetLoopAt(i);
        var flags = loop.LoopType;
        var kind = flags.HasFlag(HatchLoopTypes.External) ? "external"
          : flags.HasFlag(HatchLoopTypes.Outermost) ? "outermost" : "inner";

        if (loop.IsPolyline)
        {
          var verts = new List<object>();
          foreach (BulgeVertex bv in loop.Polyline)
          {
            if (verts.Count >= maxVertices) break;
            verts.Add(new { x = bv.Vertex.X, y = bv.Vertex.Y, bulge = bv.Bulge });
          }
          loops.Add(new { index = i, kind, isPolyline = true, vertexCount = loop.Polyline.Count, vertices = verts });
        }
        else
        {
          var segs = new List<object>();
          foreach (Curve2d c in loop.Curves)
          {
            if (segs.Count >= maxVertices) break;
            switch (c)
            {
              case LineSegment2d ls:
                segs.Add(new { type = "line", start = P(ls.StartPoint), end = P(ls.EndPoint) });
                break;
              case CircularArc2d ca:
                segs.Add(new
                {
                  type = ca.IsClosed() ? "circle" : "arc",
                  center = P(ca.Center),
                  radius = ca.Radius,
                  start = P(ca.StartPoint),
                  end = P(ca.EndPoint),
                  clockwise = ca.IsClockWise,
                });
                break;
              default:
                // ellipse / spline edges → sampled points
                var pts = c.GetSamplePoints(16).Select(P).ToArray();
                segs.Add(new { type = c.GetType().Name, sampled = pts });
                break;
            }
          }
          loops.Add(new { index = i, kind, isPolyline = false, segmentCount = loop.Curves.Count, segments = segs });
        }
      }
    }

    return new
    {
      handle = h.Handle.ToString(),
      layer = h.Layer,
      color = h.Color.ToString(),
      colorIndex = h.ColorIndex,
      patternName = h.PatternName,
      patternType = h.PatternType.ToString(),
      isSolid = h.IsSolidFill,
      isGradient = h.IsGradient,
      patternScale = h.PatternScale,
      patternAngleDeg = h.PatternAngle * 180.0 / Math.PI,
      patternSpace = h.PatternSpace,
      patternDouble = h.PatternDouble,
      hatchStyle = h.HatchStyle.ToString(),
      associative = h.Associative,
      associatedBoundaryHandles = assocHandles,
      area,
      elevation = h.Elevation,
      numberOfLoops = h.NumberOfLoops,
      extents,
      loops,
    };
  }

  private static object P(Point2d p) => new { x = p.X, y = p.Y };
}
