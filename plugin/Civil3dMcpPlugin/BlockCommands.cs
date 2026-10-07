using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DMcpPlugin;

/// <summary>
/// Reads polylines in model space and lists / inserts block references.
/// Added for TCCE: polygon lookup + block placement.
/// </summary>
public static class BlockCommands
{
  // ── listPolylines ──
  public static Task<object?> ListPolylinesAsync(JsonObject? parameters)
  {
    var layerFilter = PluginRuntime.GetOptionalString(parameters, "layer");
    var closedOnly = PluginRuntime.GetOptionalBool(parameters, "closedOnly") ?? true;
    var maxVertices = PluginRuntime.GetOptionalInt(parameters, "maxVertices") ?? 500;
    var limit = PluginRuntime.GetOptionalInt(parameters, "limit") ?? 200;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
      var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
      var result = new List<object>();

      foreach (ObjectId id in ms)
      {
        if (result.Count >= limit) break;
        if (id.ObjectClass.DxfName != "LWPOLYLINE") continue;
        var pl = tr.GetObject(id, OpenMode.ForRead) as Polyline;
        if (pl == null) continue;
        if (closedOnly && !pl.Closed) continue;
        if (layerFilter != null && !string.Equals(pl.Layer, layerFilter, StringComparison.OrdinalIgnoreCase)) continue;

        var verts = new List<object>();
        var hasArcs = false;
        for (int i = 0; i < pl.NumberOfVertices; i++)
        {
          if (Math.Abs(pl.GetBulgeAt(i)) > 1e-9) hasArcs = true;
          if (i < maxVertices)
          {
            var p = pl.GetPoint2dAt(i);
            verts.Add(new { x = p.X, y = p.Y });
          }
        }

        result.Add(new
        {
          handle = pl.Handle.ToString(),
          layer = pl.Layer,
          closed = pl.Closed,
          area = pl.Closed ? pl.Area : 0.0,
          length = pl.Length,
          vertexCount = pl.NumberOfVertices,
          hasArcs,
          vertices = verts,
        });
      }

      return new { polylines = result, count = result.Count };
    });
  }

  // ── listBlocks ──
  public static Task<object?> ListBlocksAsync(JsonObject? parameters)
  {
    var nameFilter = PluginRuntime.GetOptionalString(parameters, "name");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
      var blocks = new List<object>();

      foreach (ObjectId id in bt)
      {
        var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
        if (btr.IsLayout || btr.IsAnonymous || btr.IsFromExternalReference || btr.IsDependent) continue;
        if (nameFilter != null && btr.Name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;

        double? minX = null, minY = null, maxX = null, maxY = null;
        foreach (ObjectId eid in btr)
        {
          var ent = tr.GetObject(eid, OpenMode.ForRead) as Entity;
          if (ent == null || ent is AttributeDefinition) continue;
          var b = ent.Bounds;
          if (!b.HasValue) continue;
          var e = b.Value;
          minX = minX.HasValue ? Math.Min(minX.Value, e.MinPoint.X) : e.MinPoint.X;
          minY = minY.HasValue ? Math.Min(minY.Value, e.MinPoint.Y) : e.MinPoint.Y;
          maxX = maxX.HasValue ? Math.Max(maxX.Value, e.MaxPoint.X) : e.MaxPoint.X;
          maxY = maxY.HasValue ? Math.Max(maxY.Value, e.MaxPoint.Y) : e.MaxPoint.Y;
        }

        blocks.Add(new
        {
          name = btr.Name,
          origin = new { x = btr.Origin.X, y = btr.Origin.Y },
          hasAttributes = btr.HasAttributeDefinitions,
          isDynamic = btr.IsDynamicBlock,
          // Extents of the definition, relative to the block's own coordinates (base point = origin)
          extents = minX.HasValue
            ? new { minX = minX!.Value, minY = minY!.Value, maxX = maxX!.Value, maxY = maxY!.Value,
                    width = maxX!.Value - minX!.Value, height = maxY!.Value - minY!.Value }
            : null,
        });
      }

      return new { blocks, count = blocks.Count };
    });
  }

  // ── insertBlocks ──
  public static Task<object?> InsertBlocksAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var scale = PluginRuntime.GetOptionalDouble(parameters, "scale") ?? 1.0;
    var defaultRotationDeg = PluginRuntime.GetOptionalDouble(parameters, "rotation") ?? 0.0;
    var layer = PluginRuntime.GetOptionalString(parameters, "layer");
    var points = parameters?["points"] as JsonArray;

    if (points == null || points.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "At least one insertion point is required in 'points'.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
      if (!bt.Has(name))
        throw new JsonRpcDispatchException("CIVIL3D.NOT_FOUND", $"Block '{name}' is not defined in this drawing.");

      var defId = bt[name];
      var def = (BlockTableRecord)tr.GetObject(defId, OpenMode.ForRead);
      var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

      if (layer != null) EnsureLayer(db, tr, layer);

      var attDefs = new List<AttributeDefinition>();
      if (def.HasAttributeDefinitions)
      {
        foreach (ObjectId eid in def)
        {
          if (tr.GetObject(eid, OpenMode.ForRead) is AttributeDefinition ad && !ad.Constant) attDefs.Add(ad);
        }
      }

      var inserted = new List<object>();
      foreach (var node in points)
      {
        var x = node!["x"]!.GetValue<double>();
        var y = node["y"]!.GetValue<double>();
        var z = node["z"] != null ? node["z"]!.GetValue<double>() : 0.0;
        var rotDeg = node["rotation"] != null ? node["rotation"]!.GetValue<double>() : defaultRotationDeg;

        var br = new BlockReference(new Point3d(x, y, z), defId)
        {
          ScaleFactors = new Scale3d(scale),
          Rotation = rotDeg * Math.PI / 180.0,
        };
        if (layer != null) br.Layer = layer;

        ms.AppendEntity(br);
        tr.AddNewlyCreatedDBObject(br, true);

        foreach (var ad in attDefs)
        {
          var ar = new AttributeReference();
          ar.SetAttributeFromBlock(ad, br.BlockTransform);
          ar.TextString = ad.TextString;
          br.AttributeCollection.AppendAttribute(ar);
          tr.AddNewlyCreatedDBObject(ar, true);
        }

        inserted.Add(new { handle = br.Handle.ToString(), x, y, rotation = rotDeg });
      }

      return new { success = true, block = name, count = inserted.Count, inserted };
    });
  }

  // ── createBlock ──
  // Builds a block definition from (a) entity specs given in block coordinates (base point = 0,0)
  // and/or (b) existing model-space entities given by handle (world coords; basePoint is in world coords).
  public static Task<object?> CreateBlockAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var redefine = PluginRuntime.GetOptionalBool(parameters, "redefine") ?? false;
    var eraseOriginals = PluginRuntime.GetOptionalBool(parameters, "eraseOriginals") ?? false;
    var replaceWithReference = PluginRuntime.GetOptionalBool(parameters, "replaceWithReference") ?? false;
    var bp = parameters?["basePoint"];
    var basePoint = new Point3d(
      bp?["x"] != null ? bp["x"]!.GetValue<double>() : 0.0,
      bp?["y"] != null ? bp["y"]!.GetValue<double>() : 0.0,
      0.0);
    var entities = parameters?["entities"] as JsonArray;
    var handles = parameters?["fromHandles"] as JsonArray;

    if ((entities == null || entities.Count == 0) && (handles == null || handles.Count == 0))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Provide 'entities' and/or 'fromHandles'.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
      BlockTableRecord btr;
      ObjectId btrId;
      var redefined = false;

      if (bt.Has(name))
      {
        if (!redefine)
          throw new JsonRpcDispatchException("CIVIL3D.ALREADY_EXISTS", $"Block '{name}' already exists. Pass redefine=true to replace its contents.");
        btrId = bt[name];
        btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForWrite);
        foreach (ObjectId eid in btr) tr.GetObject(eid, OpenMode.ForWrite).Erase();
        redefined = true;
      }
      else
      {
        bt.UpgradeOpen();
        btr = new BlockTableRecord { Name = name, Origin = Point3d.Origin };
        btrId = bt.Add(btr);
        tr.AddNewlyCreatedDBObject(btr, true);
      }

      var created = 0;

      // (a) entity specs, in block coordinates
      if (entities != null)
      {
        foreach (var spec in entities)
        {
          var ent = BuildEntity(spec!);
          var lyr = spec!["layer"]?.GetValue<string>();
          if (lyr != null) { EnsureLayer(db, tr, lyr); ent.Layer = lyr; } else ent.Layer = "0";
          if (spec["color"] != null) ent.ColorIndex = spec["color"]!.GetValue<int>();
          btr.AppendEntity(ent);
          tr.AddNewlyCreatedDBObject(ent, true);
          created++;
        }
      }

      // (b) copy existing entities by handle, moved so basePoint becomes the block origin
      var copied = 0;
      if (handles != null && handles.Count > 0)
      {
        var ids = new ObjectIdCollection();
        foreach (var h in handles)
        {
          var hs = h!.GetValue<string>();
          if (!db.TryGetObjectId(new Handle(Convert.ToInt64(hs, 16)), out var oid) || oid.IsErased)
            throw new JsonRpcDispatchException("CIVIL3D.NOT_FOUND", $"No object with handle '{hs}'.");
          ids.Add(oid);
        }

        var mapping = new IdMapping();
        db.DeepCloneObjects(ids, btrId, mapping, false);
        var move = Matrix3d.Displacement(Point3d.Origin - basePoint);
        foreach (IdPair pair in mapping)
        {
          if (!pair.IsPrimary || !pair.IsCloned) continue;
          if (tr.GetObject(pair.Value, OpenMode.ForWrite) is Entity e) { e.TransformBy(move); copied++; }
        }

        if (eraseOriginals)
          foreach (ObjectId oid in ids) tr.GetObject(oid, OpenMode.ForWrite).Erase();
      }

      string? referenceHandle = null;
      if (replaceWithReference)
      {
        var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
        var br = new BlockReference(basePoint, btrId);
        ms.AppendEntity(br);
        tr.AddNewlyCreatedDBObject(br, true);
        AddAttributes(tr, btr, br);
        referenceHandle = br.Handle.ToString();
      }

      if (redefined) RefreshReferences(tr, btr);

      return new { success = true, block = name, redefined, entitiesCreated = created, entitiesCopied = copied, referenceHandle };
    });
  }

  private static Entity BuildEntity(JsonNode spec)
  {
    var type = spec["type"]?.GetValue<string>()?.ToLowerInvariant()
      ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Each entity needs a 'type'.");
    var pts = spec["points"] as JsonArray;
    Point3d P(JsonNode? n) => new(n?["x"]?.GetValue<double>() ?? 0.0, n?["y"]?.GetValue<double>() ?? 0.0, 0.0);
    double D(string k, double def) => spec[k] != null ? spec[k]!.GetValue<double>() : def;
    double Rad(double deg) => deg * Math.PI / 180.0;

    switch (type)
    {
      case "polyline":
      {
        if (pts == null || pts.Count < 2) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "polyline needs 2+ points.");
        var pl = new Polyline();
        for (int i = 0; i < pts.Count; i++)
        {
          var b = pts[i]!["bulge"] != null ? pts[i]!["bulge"]!.GetValue<double>() : 0.0;
          pl.AddVertexAt(i, new Point2d(pts[i]!["x"]!.GetValue<double>(), pts[i]!["y"]!.GetValue<double>()), b, 0, 0);
        }
        pl.Closed = spec["closed"]?.GetValue<bool>() ?? false;
        return pl;
      }
      case "line":
        if (pts == null || pts.Count != 2) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "line needs exactly 2 points.");
        return new Line(P(pts[0]), P(pts[1]));
      case "circle":
        return new Circle(P(spec["center"]), Vector3d.ZAxis, D("radius", 1.0));
      case "arc":
        return new Arc(P(spec["center"]), D("radius", 1.0), Rad(D("startAngle", 0)), Rad(D("endAngle", 180)));
      case "text":
        return new DBText
        {
          Position = P(spec["position"]),
          TextString = spec["text"]?.GetValue<string>() ?? "",
          Height = D("height", 1.0),
          Rotation = Rad(D("rotation", 0)),
        };
      case "attribute":
        return new AttributeDefinition
        {
          Position = P(spec["position"]),
          Tag = spec["tag"]?.GetValue<string>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "attribute needs a 'tag'."),
          Prompt = spec["prompt"]?.GetValue<string>() ?? spec["tag"]!.GetValue<string>(),
          TextString = spec["text"]?.GetValue<string>() ?? "",
          Height = D("height", 1.0),
          Rotation = Rad(D("rotation", 0)),
        };
      default:
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Unsupported entity type '{type}'. Use polyline, line, circle, arc, text or attribute.");
    }
  }

  private static void AddAttributes(Transaction tr, BlockTableRecord def, BlockReference br)
  {
    if (!def.HasAttributeDefinitions) return;
    foreach (ObjectId eid in def)
    {
      if (tr.GetObject(eid, OpenMode.ForRead) is AttributeDefinition ad && !ad.Constant && !ad.IsErased)
      {
        var ar = new AttributeReference();
        ar.SetAttributeFromBlock(ad, br.BlockTransform);
        ar.TextString = ad.TextString;
        br.AttributeCollection.AppendAttribute(ar);
        tr.AddNewlyCreatedDBObject(ar, true);
      }
    }
  }

  private static void RefreshReferences(Transaction tr, BlockTableRecord btr)
  {
    foreach (ObjectId rid in btr.GetBlockReferenceIds(true, false))
    {
      var br = (BlockReference)tr.GetObject(rid, OpenMode.ForWrite);
      br.RecordGraphicsModified(true);
    }
  }

  // ── importBlock ──
  // Copies a block definition from another DWG. If blockName is omitted, the whole drawing
  // is imported as a block named 'asName' (or the file name).
  public static Task<object?> ImportBlockAsync(JsonObject? parameters)
  {
    var filePath = PluginRuntime.GetRequiredString(parameters, "filePath");
    var blockName = PluginRuntime.GetOptionalString(parameters, "name");
    var asName = PluginRuntime.GetOptionalString(parameters, "asName");
    var replace = PluginRuntime.GetOptionalBool(parameters, "redefine") ?? false;

    if (!File.Exists(filePath))
      throw new JsonRpcDispatchException("CIVIL3D.NOT_FOUND", $"File not found: {filePath}");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, db, tr) =>
    {
      using var src = new Database(false, true);
      src.ReadDwgFile(filePath, FileOpenMode.OpenForReadAndAllShare, true, "");
      src.CloseInput(true);

      if (blockName == null)
      {
        var target = asName ?? Path.GetFileNameWithoutExtension(filePath);
        var bt0 = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        if (bt0.Has(target))
          throw new JsonRpcDispatchException("CIVIL3D.ALREADY_EXISTS", $"Block '{target}' already exists. Use a different 'asName'.");
        var id = db.Insert(target, src, true);
        return new { success = true, block = target, source = filePath, mode = "whole-drawing", handle = id.Handle.ToString() };
      }

      ObjectId srcId;
      using (var str = src.TransactionManager.StartTransaction())
      {
        var sbt = (BlockTable)str.GetObject(src.BlockTableId, OpenMode.ForRead);
        if (!sbt.Has(blockName))
          throw new JsonRpcDispatchException("CIVIL3D.NOT_FOUND", $"Block '{blockName}' not found in {Path.GetFileName(filePath)}.");
        srcId = sbt[blockName];
        str.Commit();
      }

      var btLocal = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
      var existed = btLocal.Has(blockName);
      if (existed && !replace)
        return new { success = true, block = blockName, source = filePath, mode = "skipped", message = "Block already defined; pass redefine=true to overwrite." };

      var ids = new ObjectIdCollection { srcId };
      var map = new IdMapping();
      src.WblockCloneObjects(ids, db.BlockTableId, map,
        existed ? DuplicateRecordCloning.Replace : DuplicateRecordCloning.Ignore, false);

      return new { success = true, block = blockName, source = filePath, mode = existed ? "redefined" : "imported" };
    });
  }

  private static void EnsureLayer(Database db, Transaction tr, string layer)
  {
    var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
    if (lt.Has(layer)) return;
    lt.UpgradeOpen();
    var ltr = new LayerTableRecord { Name = layer };
    lt.Add(ltr);
    tr.AddNewlyCreatedDBObject(ltr, true);
  }
}
