using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace Civil3DMcpPlugin;

/// <summary>
/// Earthwork (TCCE): corridor Top/Datum surfaces, feature lines, breaklines/boundaries, pads with daylight
/// slopes to EG (computed geometrically — the .NET API cannot create Grading objects), volumes with factors
/// and boundary polygons, average-end-area volumes by station and mass haul.
/// </summary>
public static class EarthworkCommands
{
  // ════════════════════════════ Corridor Top / Datum ════════════════════════════

  public static Task<object?> CorridorSurfacesAsync(JsonObject? p)
  {
    var corrName = PluginRuntime.GetRequiredString(p, "corridor");
    var makeTop = PluginRuntime.GetOptionalBool(p, "top") ?? true;
    var makeDatum = PluginRuntime.GetOptionalBool(p, "datum") ?? true;
    var extents = PluginRuntime.GetOptionalBool(p, "extentsBoundary") ?? true;
    var replace = PluginRuntime.GetOptionalBool(p, "replace") ?? false;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var corr = FindCorridor(civilDoc, tr, corrName);
      corr.UpgradeOpen();
      var linkCodes = corr.GetLinkCodes();
      var made = new List<object>();
      var warnings = new List<string>();

      void Make(string surfName, string code, OverhangCorrectionType overhang, string? styleName, string? featureCode)
      {
        if (corr.CorridorSurfaces.SurfaceNames().Contains(surfName, StringComparer.OrdinalIgnoreCase))
        {
          if (!replace) { warnings.Add($"'{surfName}' already exists (pass replace=true to rebuild it)."); return; }
          corr.CorridorSurfaces.Remove(surfName);
        }
        if (!linkCodes.Contains(code, StringComparer.OrdinalIgnoreCase))
          warnings.Add($"Link code '{code}' is not produced by this corridor's assemblies (available: {string.Join(", ", linkCodes)}).");
        var cs = styleName != null
          ? corr.CorridorSurfaces.Add(surfName, StyleId(civilDoc.Styles.SurfaceStyles, styleName))
          : corr.CorridorSurfaces.Add(surfName);
        cs.AddLinkCode(code, true);
        if (featureCode != null) cs.AddFeatureLineCode(featureCode);
        cs.OverhangCorrection = overhang;
        if (extents) cs.Boundaries.AddCorridorExtentsBoundary("Corridor Extents");
        made.Add(new { surface = cs.Name, linkCode = code, overhang = overhang.ToString(), surfaceHandle = cs.SurfaceId.Handle.ToString() });
      }

      if (makeTop) Make(PluginRuntime.GetOptionalString(p, "topName") ?? $"{corr.Name} - Top", PluginRuntime.GetOptionalString(p, "topCode") ?? "Top", OverhangCorrectionType.TopLinks, PluginRuntime.GetOptionalString(p, "topStyle"), null);
      if (makeDatum) Make(PluginRuntime.GetOptionalString(p, "datumName") ?? $"{corr.Name} - Datum", PluginRuntime.GetOptionalString(p, "datumCode") ?? "Datum", OverhangCorrectionType.BottomLinks, PluginRuntime.GetOptionalString(p, "datumStyle"), null);

      corr.Rebuild();
      return new { success = made.Count > 0, corridor = corr.Name, surfaces = made, availableLinkCodes = linkCodes, availablePointCodes = corr.GetPointCodes(), warnings };
    });
  }

  public static Task<object?> CorridorCodesAsync(JsonObject? p)
  {
    var corrName = PluginRuntime.GetRequiredString(p, "corridor");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var corr = FindCorridor(civilDoc, tr, corrName);
      return new { corridor = corr.Name, linkCodes = corr.GetLinkCodes(), pointCodes = corr.GetPointCodes(), shapeCodes = corr.GetShapeCodes(), surfaces = corr.CorridorSurfaces.SurfaceNames() };
    });
  }

  // ════════════════════════════ Feature lines ════════════════════════════

  /// From a polyline/3D polyline. Elevations: elevation (constant) | fromSurface | startElevation + grade (percent along the line) | keep (from the source).
  public static Task<object?> CreateFeatureLineAsync(JsonObject? p)
  {
    var handle = PluginRuntime.GetRequiredString(p, "polylineHandle");
    var name = PluginRuntime.GetOptionalString(p, "name") ?? "";
    var elev = PluginRuntime.GetOptionalDouble(p, "elevation");
    var fromSurface = PluginRuntime.GetOptionalString(p, "fromSurface");
    var startElev = PluginRuntime.GetOptionalDouble(p, "startElevation");
    var gradePct = PluginRuntime.GetOptionalDouble(p, "grade");
    var siteName = PluginRuntime.GetOptionalString(p, "site");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var srcId = IdFromHandle(db, handle);
      ObjectId siteId = ObjectId.Null;
      if (siteName != null)
        siteId = civilDoc.GetSiteIds().Cast<ObjectId>().FirstOrDefault(id => ((Site)tr.GetObject(id, OpenMode.ForRead)).Name.Equals(siteName, StringComparison.OrdinalIgnoreCase));
      var flId = siteId.IsNull ? FeatureLine.Create(name, srcId) : FeatureLine.Create(name, srcId, siteId);
      var fl = (FeatureLine)tr.GetObject(flId, OpenMode.ForWrite);
      var styleName = PluginRuntime.GetOptionalString(p, "style");
      if (styleName != null) fl.StyleName = styleName;

      string mode = "source";
      if (fromSurface != null)
      {
        fl.AssignElevationsFromSurface(FindSurface(civilDoc, tr, fromSurface).ObjectId, PluginRuntime.GetOptionalBool(p, "includeIntermediate") ?? true);
        mode = $"surface {fromSurface}";
      }
      else if (elev.HasValue || startElev.HasValue)
      {
        var pts = fl.GetPoints(FeatureLinePointType.AllPoints);
        double along = 0;
        for (int i = 0; i < pts.Count; i++)
        {
          if (i > 0) along += new Point2d(pts[i].X, pts[i].Y).GetDistanceTo(new Point2d(pts[i - 1].X, pts[i - 1].Y));
          var z = elev ?? (startElev!.Value + (gradePct ?? 0) / 100.0 * along);
          fl.SetPointElevation(i, z);
        }
        mode = elev.HasValue ? $"constant {elev}" : $"start {startElev} grade {gradePct ?? 0}%";
      }
      return new { success = true, featureLine = fl.Name, handle = fl.Handle.ToString(), points = fl.PointsCount, minElevation = fl.MinElevation, maxElevation = fl.MaxElevation, elevations = mode };
    });
  }

  // ════════════════════════════ Surface edits ════════════════════════════

  /// handles of polylines / 3D polylines / feature lines. type: standard | proximity | nondestructive
  public static Task<object?> AddBreaklinesAsync(JsonObject? p)
  {
    var surfName = PluginRuntime.GetRequiredString(p, "surface");
    var handles = (p?["handles"] as JsonArray)?.Select(n => n!.GetValue<string>()).ToList()
      ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "'handles' is required.");
    var type = (PluginRuntime.GetOptionalString(p, "type") ?? "standard").ToLowerInvariant();
    var mid = PluginRuntime.GetOptionalDouble(p, "midOrdinate") ?? 0.1;
    var supplement = PluginRuntime.GetOptionalDouble(p, "supplementDistance") ?? 0;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var s = FindSurface(civilDoc, tr, surfName) as TinSurface ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{surfName}' is not a TIN surface.");
      s.UpgradeOpen();
      var ids = new ObjectIdCollection();
      foreach (var h in handles) ids.Add(IdFromHandle(db, h));
      _ = type switch
      {
        "proximity" => s.BreaklinesDefinition.AddProximityBreaklines(ids, mid),
        "nondestructive" => s.BreaklinesDefinition.AddNonDestructiveBreaklines(ids, mid),
        _ => s.BreaklinesDefinition.AddStandardBreaklines(ids, mid, supplement, 0, 0),
      };
      s.Rebuild();
      return new { success = true, surface = s.Name, breaklines = ids.Count, type };
    });
  }

  /// boundary from a polyline handle or points [{x,y}]. type: Outer | Hide | Show | DataClip
  public static Task<object?> AddBoundaryAsync(JsonObject? p)
  {
    var surfName = PluginRuntime.GetRequiredString(p, "surface");
    var typeName = PluginRuntime.GetOptionalString(p, "type") ?? "Outer";
    var nonDestructive = PluginRuntime.GetOptionalBool(p, "nonDestructive") ?? true;
    var mid = PluginRuntime.GetOptionalDouble(p, "midOrdinate") ?? 0.1;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var s = FindSurface(civilDoc, tr, surfName);
      s.UpgradeOpen();
      if (!Enum.TryParse<SurfaceBoundaryType>(typeName, true, out var bt))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "type must be Outer, Hide, Show or DataClip.");
      var handle = PluginRuntime.GetOptionalString(p, "polylineHandle");
      if (handle != null)
        s.BoundariesDefinition.AddBoundaries(new ObjectIdCollection { IdFromHandle(db, handle) }, mid, bt, nonDestructive);
      else
        s.BoundariesDefinition.AddBoundaries(Points2d(p, db, tr), mid, bt, nonDestructive);
      s.Rebuild();
      return new { success = true, surface = s.Name, boundary = bt.ToString() };
    });
  }

  // ════════════════════════════ Pads ════════════════════════════

  /// Pad from a closed polyline at an elevation, daylighting to the target surface with cut/fill slopes (H:V).
  public static Task<object?> CreatePadAsync(JsonObject? p)
  {
    var handle = PluginRuntime.GetRequiredString(p, "polylineHandle");
    var egName = PluginRuntime.GetRequiredString(p, "targetSurface");
    var name = PluginRuntime.GetOptionalString(p, "name") ?? "PAD";
    var cutSlope = PluginRuntime.GetOptionalDouble(p, "cutSlope") ?? 2.0;   // H:V
    var fillSlope = PluginRuntime.GetOptionalDouble(p, "fillSlope") ?? 3.0; // H:V
    var spacing = Math.Max(1.0, PluginRuntime.GetOptionalDouble(p, "spacing") ?? 10.0);
    var maxDist = PluginRuntime.GetOptionalDouble(p, "maxDistance") ?? 500.0;
    var fanDeg = Math.Max(5.0, PluginRuntime.GetOptionalDouble(p, "cornerFanDegrees") ?? 15.0);
    var computeVolume = PluginRuntime.GetOptionalBool(p, "computeVolume") ?? true;
    var cutFactor = PluginRuntime.GetOptionalDouble(p, "cutFactor") ?? 1.0;
    var fillFactor = PluginRuntime.GetOptionalDouble(p, "fillFactor") ?? 1.0;
    var layer = PluginRuntime.GetOptionalString(p, "layer") ?? "C-GRAD-PAD";
    var padElevInput = PluginRuntime.GetOptionalDouble(p, "elevation");
    var balance = PluginRuntime.GetOptionalBool(p, "balance") ?? false;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var pl = tr.GetObject(IdFromHandle(db, handle), OpenMode.ForRead) as Polyline
        ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "polylineHandle must be a closed lightweight polyline.");
      if (!pl.Closed && pl.StartPoint.DistanceTo(pl.EndPoint) > 1e-6)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "The pad polyline must be closed.");
      var eg = FindSurface(civilDoc, tr, egName);

      var ring = Densify(pl, spacing);
      if (SignedArea(ring) < 0) ring.Reverse(); // CCW → outward normal = (dy, -dx)

      double? Eg(double x, double y) { try { return eg.FindElevationAtXY(x, y); } catch { return null; } }

      // Pad elevation: given, or balanced (bisection on net volume), or average EG along the edge.
      var egAlong = ring.Select(q => Eg(q.X, q.Y)).Where(z => z.HasValue).Select(z => z!.Value).ToList();
      if (egAlong.Count == 0) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "The pad is outside the target surface.");
      double zPad = padElevInput ?? egAlong.Average();

      PadResult Build(double z) => BuildPad(ring, z, cutSlope, fillSlope, maxDist, fanDeg, Eg);

      string? balanceNote = null;
      if (balance)
      {
        // Approximate balance using a quick prism estimate over the pad + daylight area sampled on a grid.
        double lo = egAlong.Min() - 50, hi = egAlong.Max() + 50;
        for (int it = 0; it < 30; it++)
        {
          var mid = (lo + hi) / 2;
          var net = QuickNet(ring, mid, Build(mid), Eg, cutFactor, fillFactor, cutSlope, fillSlope, spacing);
          if (net > 0) lo = mid; else hi = mid; // net = cut - fill ; more cut → raise pad
        }
        zPad = (lo + hi) / 2;
        balanceNote = "Pad elevation found by balancing adjusted cut and fill (grid estimate); the volume below is the exact TIN volume.";
      }

      var pad = Build(zPad);

      // Draw pad edge and daylight as 3D polylines (also used as breaklines/boundary).
      EnsureLayer(db, tr, layer);
      var ms = (BlockTableRecord)tr.GetObject(((BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead))[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
      ObjectId Add3d(IEnumerable<Point3d> pts)
      {
        var p3 = new Polyline3d(Poly3dType.SimplePoly, new Point3dCollection(pts.ToArray()), true) { Layer = layer };
        var id = ms.AppendEntity(p3); tr.AddNewlyCreatedDBObject(p3, true); return id;
      }
      var edgeId = Add3d(ring.Select(q => new Point3d(q.X, q.Y, zPad)));
      var dayId = Add3d(pad.Daylight);

      // Surface
      var surfName = UniqueSurfaceName(civilDoc, tr, name);
      var styleName = PluginRuntime.GetOptionalString(p, "style");
      var sid = styleName != null ? TinSurface.Create(surfName, StyleId(civilDoc.Styles.SurfaceStyles, styleName)) : TinSurface.Create(db, surfName);
      var surf = (TinSurface)tr.GetObject(sid, OpenMode.ForWrite);
      surf.BreaklinesDefinition.AddStandardBreaklines(new ObjectIdCollection { edgeId, dayId }, 0.1, 0, 0, 0);
      surf.BoundariesDefinition.AddBoundaries(new ObjectIdCollection { dayId }, 0.1, SurfaceBoundaryType.Outer, true);
      surf.Rebuild();

      object? volume = null;
      if (computeVolume)
        volume = Volume(tr, eg.ObjectId, sid, $"{surfName} VOL", cutFactor, fillFactor, keep: PluginRuntime.GetOptionalBool(p, "keepVolumeSurface") ?? false, boundary: null);

      return new
      {
        success = true,
        surface = surfName,
        padElevation = Math.Round(zPad, 3),
        padArea = Math.Abs(SignedArea(ring)),
        cutSlope = $"{cutSlope}:1",
        fillSlope = $"{fillSlope}:1",
        rays = pad.Rays,
        raysWithoutDaylight = pad.Misses,
        maxCutDepth = Math.Round(pad.MaxCut, 2),
        maxFillDepth = Math.Round(pad.MaxFill, 2),
        padEdgeHandle = edgeId.Handle.ToString(),
        daylightHandle = dayId.Handle.ToString(),
        volume,
        notes = new[]
        {
          balanceNote,
          pad.Misses > 0 ? $"{pad.Misses} rays reached maxDistance or left the target surface; increase maxDistance or check EG coverage." : null,
          "Daylight is computed by projecting the cut/fill slope perpendicular to each edge (fans at convex corners). Check concave corners visually.",
        }.Where(s => s != null),
      };
    });
  }

  private record PadResult(List<Point3d> Daylight, int Rays, int Misses, double MaxCut, double MaxFill);

  private static PadResult BuildPad(List<Point2d> ring, double zPad, double cutSlope, double fillSlope, double maxDist, double fanDeg, Func<double, double, double?> eg)
  {
    var n = ring.Count;
    var day = new List<Point3d>();
    int rays = 0, misses = 0; double maxCut = 0, maxFill = 0;

    Vector2d EdgeNormal(int i) // edge i → i+1, outward for CCW ring
    {
      var a = ring[i]; var b = ring[(i + 1) % n];
      var d = (b - a).GetNormal();
      return new Vector2d(d.Y, -d.X);
    }

    Point3d Ray(Point2d o, Vector2d dir)
    {
      rays++;
      var z0 = eg(o.X, o.Y);
      if (!z0.HasValue) { misses++; return new Point3d(o.X, o.Y, zPad); }
      var cut = z0.Value > zPad;
      var m = cut ? cutSlope : fillSlope;
      double Line(double d) => cut ? zPad + d / m : zPad - d / m;
      double? F(double d) { var e = eg(o.X + dir.X * d, o.Y + dir.Y * d); return e.HasValue ? Line(d) - e.Value : null; }
      if (cut) maxCut = Math.Max(maxCut, z0.Value - zPad); else maxFill = Math.Max(maxFill, zPad - z0.Value);

      double step = 1.0, prev = 0; var fPrev = F(0) ?? 0;
      for (double d = step; d <= maxDist; d += step)
      {
        var f = F(d);
        if (!f.HasValue) { misses++; var dd = prev; return new Point3d(o.X + dir.X * dd, o.Y + dir.Y * dd, Line(dd)); }
        if ((cut && f.Value >= 0) || (!cut && f.Value <= 0))
        {
          double lo = prev, hi = d;
          for (int k = 0; k < 30; k++)
          {
            var mid = (lo + hi) / 2; var fm = F(mid) ?? 0;
            if ((cut && fm >= 0) || (!cut && fm <= 0)) hi = mid; else lo = mid;
          }
          var e = eg(o.X + dir.X * hi, o.Y + dir.Y * hi) ?? Line(hi);
          return new Point3d(o.X + dir.X * hi, o.Y + dir.Y * hi, e);
        }
        prev = d; fPrev = f.Value;
        if (d > 50) step = 2.0;
      }
      misses++;
      return new Point3d(o.X + dir.X * maxDist, o.Y + dir.Y * maxDist, Line(maxDist));
    }

    for (int i = 0; i < n; i++)
    {
      var nPrev = EdgeNormal((i - 1 + n) % n);
      var nNext = EdgeNormal(i);
      var cross = nPrev.X * nNext.Y - nPrev.Y * nNext.X; // >0: convex corner on a CCW ring
      var angle = Math.Acos(Math.Clamp(nPrev.DotProduct(nNext), -1, 1));
      if (cross > 1e-9 && angle > fanDeg * Math.PI / 180)
      {
        var steps = (int)Math.Ceiling(angle / (fanDeg * Math.PI / 180));
        var a0 = Math.Atan2(nPrev.Y, nPrev.X);
        for (int k = 0; k <= steps; k++)
        {
          var a = a0 + angle * k / steps;
          day.Add(Ray(ring[i], new Vector2d(Math.Cos(a), Math.Sin(a))));
        }
      }
      else
      {
        var bis = nPrev + nNext;
        var dir = bis.Length < 1e-9 ? nNext : bis.GetNormal();
        day.Add(Ray(ring[i], dir));
      }
    }
    return new PadResult(day, rays, misses, maxCut, maxFill);
  }

  /// Quick grid estimate of adjusted (cut - fill) for balancing: flat pad inside the edge, slope plane
  /// (cut or fill, decided at the nearest edge point) between the edge and the daylight line.
  private static double QuickNet(List<Point2d> ring, double zPad, PadResult pad, Func<double, double, double?> eg,
    double cf, double ff, double cutSlope, double fillSlope, double spacing)
  {
    var day = pad.Daylight.Select(q => new Point2d(q.X, q.Y)).ToList();
    double minX = day.Min(q => q.X), maxX = day.Max(q => q.X), minY = day.Min(q => q.Y), maxY = day.Max(q => q.Y);
    var cell = Math.Max(spacing / 2, Math.Max(maxX - minX, maxY - minY) / 120.0);
    var edgeCut = ring.Select(r => (eg(r.X, r.Y) ?? zPad) > zPad).ToArray();
    double cut = 0, fill = 0;
    for (var x = minX + cell / 2; x < maxX; x += cell)
      for (var y = minY + cell / 2; y < maxY; y += cell)
      {
        double design;
        if (Inside(ring, x, y)) design = zPad;
        else if (Inside(day, x, y))
        {
          int bi = 0; double bd = double.MaxValue;
          for (int i = 0; i < ring.Count; i++) { var d2 = (ring[i].X - x) * (ring[i].X - x) + (ring[i].Y - y) * (ring[i].Y - y); if (d2 < bd) { bd = d2; bi = i; } }
          var d = Math.Sqrt(bd);
          design = edgeCut[bi] ? zPad + d / cutSlope : zPad - d / fillSlope;
        }
        else continue;
        var e = eg(x, y); if (!e.HasValue) continue;
        var dz = design - e.Value;
        if (dz > 0) fill += dz * cell * cell; else cut += -dz * cell * cell;
      }
    return cut * cf - fill * ff;
  }

  // ════════════════════════════ Volumes ════════════════════════════

  /// Volume between base (e.g. EG) and comparison (e.g. Datum / pad) surfaces, optionally inside one or more boundary polygons.
  public static Task<object?> ComputeVolumesAsync(JsonObject? p)
  {
    var baseName = PluginRuntime.GetRequiredString(p, "baseSurface");
    var compName = PluginRuntime.GetRequiredString(p, "comparisonSurface");
    var cf = PluginRuntime.GetOptionalDouble(p, "cutFactor") ?? 1.0;
    var ff = PluginRuntime.GetOptionalDouble(p, "fillFactor") ?? 1.0;
    var keepName = PluginRuntime.GetOptionalString(p, "keepAs");
    var boundaries = (p?["boundaryHandles"] as JsonArray)?.Select(n => n!.GetValue<string>()).ToList();

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var bs = FindSurface(civilDoc, tr, baseName);
      var cs = FindSurface(civilDoc, tr, compName);
      if (boundaries == null || boundaries.Count == 0)
        return new { regions = new[] { Volume(tr, bs.ObjectId, cs.ObjectId, keepName ?? "_mcp_vol", cf, ff, keepName != null, null) } };

      var regions = new List<object>();
      double tc = 0, tf = 0;
      foreach (var h in boundaries)
      {
        var r = Volume(tr, bs.ObjectId, cs.ObjectId, keepName != null ? $"{keepName} {h}" : "_mcp_vol", cf, ff, keepName != null, IdFromHandle(db, h));
        regions.Add(new { boundary = h, result = r.Item });
        tc += r.AdjCut; tf += r.AdjFill;
      }
      return new { regions, total = new { adjustedCut = tc, adjustedFill = tf, adjustedNet = tc - tf, cutCY = tc / 27, fillCY = tf / 27, netCY = (tc - tf) / 27 } };
    });
  }

  private record VolumeOut(object Item, double AdjCut, double AdjFill);

  private static VolumeOut Volume(Transaction tr, ObjectId baseId, ObjectId compId, string name, double cf, double ff, bool keep, ObjectId? boundary)
  {
    var vid = TinVolumeSurface.Create(keep ? name : "_mcp_tmp_" + Guid.NewGuid().ToString("N"), baseId, compId);
    var vs = (TinVolumeSurface)tr.GetObject(vid, OpenMode.ForWrite);
    vs.CutFactor = cf; vs.FillFactor = ff;
    if (boundary.HasValue)
    {
      vs.BoundariesDefinition.AddBoundaries(new ObjectIdCollection { boundary.Value }, 0.1, SurfaceBoundaryType.Outer, true);
      vs.Rebuild();
    }
    var vp = vs.GetVolumeProperties();
    var item = new
    {
      unadjustedCut = vp.UnadjustedCutVolume,
      unadjustedFill = vp.UnadjustedFillVolume,
      adjustedCut = vp.AdjustedCutVolume,
      adjustedFill = vp.AdjustedFillVolume,
      adjustedNet = vp.AdjustedNetVolume,
      cutFactor = cf,
      fillFactor = ff,
      cutCY = vp.AdjustedCutVolume / 27.0,
      fillCY = vp.AdjustedFillVolume / 27.0,
      netCY = vp.AdjustedNetVolume / 27.0,
      units = "drawing units³ (CY assumes feet)",
      keptAs = keep ? vs.Name : null,
    };
    var result = new VolumeOut(item, vp.AdjustedCutVolume, vp.AdjustedFillVolume);
    if (!keep) vs.Erase();
    return result;
  }

  // ════════════════════════════ Sections / mass haul ════════════════════════════

  /// Average-end-area volumes along an alignment by sampling the two surfaces on perpendicular lines.
  public static Task<object?> SectionVolumesAsync(JsonObject? p)
  {
    var alName = PluginRuntime.GetRequiredString(p, "alignment");
    var baseName = PluginRuntime.GetRequiredString(p, "baseSurface");
    var designName = PluginRuntime.GetRequiredString(p, "designSurface");
    var interval = PluginRuntime.GetOptionalDouble(p, "interval") ?? 25;
    var left = PluginRuntime.GetOptionalDouble(p, "leftWidth") ?? 100;
    var right = PluginRuntime.GetOptionalDouble(p, "rightWidth") ?? 100;
    var step = Math.Max(0.25, PluginRuntime.GetOptionalDouble(p, "sampleStep") ?? 1.0);
    var cf = PluginRuntime.GetOptionalDouble(p, "cutFactor") ?? 1.0;
    var ff = PluginRuntime.GetOptionalDouble(p, "fillFactor") ?? 1.0;
    var csvPath = PluginRuntime.GetOptionalString(p, "outputPath");
    var draw = p?["drawMassHaul"] as JsonObject;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var al = FindAlignment(civilDoc, tr, alName);
      var bs = FindSurface(civilDoc, tr, baseName);
      var ds = FindSurface(civilDoc, tr, designName);
      double? Z(CivilSurface s, double x, double y) { try { return s.FindElevationAtXY(x, y); } catch { return null; } }

      var st0 = PluginRuntime.GetOptionalDouble(p, "startStation") ?? al.StartingStation;
      var st1 = PluginRuntime.GetOptionalDouble(p, "endStation") ?? al.EndingStation;
      var stations = new SortedSet<double> { st0, st1 };
      for (var s = Math.Ceiling(st0 / interval) * interval; s < st1; s += interval) stations.Add(Math.Round(s, 4));

      var rows = new List<(double st, double cutA, double fillA)>();
      foreach (var st in stations)
      {
        double cutA = 0, fillA = 0;
        for (var off = -left + step / 2; off < right; off += step)
        {
          double x = 0, y = 0;
          try { al.PointLocation(st, off, ref x, ref y); } catch { continue; }
          var zb = Z(bs, x, y); var zd = Z(ds, x, y);
          if (!zb.HasValue || !zd.HasValue) continue;
          var dz = zd.Value - zb.Value;
          if (dz > 0) fillA += dz * step; else cutA += -dz * step;
        }
        rows.Add((st, cutA, fillA));
      }

      var table = new List<object>();
      double cumCut = 0, cumFill = 0, mass = 0;
      var massPts = new List<Point2d>();
      for (int i = 0; i < rows.Count; i++)
      {
        double vc = 0, vf = 0;
        if (i > 0)
        {
          var L = rows[i].st - rows[i - 1].st;
          vc = (rows[i].cutA + rows[i - 1].cutA) / 2 * L;
          vf = (rows[i].fillA + rows[i - 1].fillA) / 2 * L;
        }
        cumCut += vc * cf; cumFill += vf * ff; mass += vc * cf - vf * ff;
        massPts.Add(new Point2d(rows[i].st, mass / 27.0));
        table.Add(new
        {
          station = al.GetStationStringWithEquations(rows[i].st),
          rawStation = Math.Round(rows[i].st, 3),
          cutArea = Math.Round(rows[i].cutA, 2),
          fillArea = Math.Round(rows[i].fillA, 2),
          cutCY = Math.Round(vc * cf / 27.0, 1),
          fillCY = Math.Round(vf * ff / 27.0, 1),
          massOrdinateCY = Math.Round(mass / 27.0, 1),
        });
      }

      string? written = null;
      if (csvPath != null)
      {
        var sb = new StringBuilder("station,raw_station,cut_area_sf,fill_area_sf,cut_cy,fill_cy,mass_ordinate_cy\n");
        var ci = CultureInfo.InvariantCulture;
        double m = 0;
        for (int i = 0; i < rows.Count; i++)
        {
          double vc = 0, vf = 0;
          if (i > 0) { var L = rows[i].st - rows[i - 1].st; vc = (rows[i].cutA + rows[i - 1].cutA) / 2 * L * cf; vf = (rows[i].fillA + rows[i - 1].fillA) / 2 * L * ff; }
          m += vc - vf;
          sb.Append(string.Format(ci, "{0},{1:F3},{2:F2},{3:F2},{4:F1},{5:F1},{6:F1}\n", al.GetStationStringWithEquations(rows[i].st), rows[i].st, rows[i].cutA, rows[i].fillA, vc / 27, vf / 27, m / 27));
        }
        File.WriteAllText(csvPath, sb.ToString());
        written = csvPath;
      }

      string? massHandle = null;
      if (draw != null)
      {
        var ox = draw["x"]?.GetValue<double>() ?? 0; var oy = draw["y"]?.GetValue<double>() ?? 0;
        var hs = draw["horizontalScale"]?.GetValue<double>() ?? 1.0; var vsc = draw["verticalScale"]?.GetValue<double>() ?? 0.01;
        var lay = draw["layer"]?.GetValue<string>() ?? "C-GRAD-MASS";
        EnsureLayer(db, tr, lay);
        var ms = (BlockTableRecord)tr.GetObject(((BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead))[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
        var pl = new Polyline { Layer = lay };
        for (int i = 0; i < massPts.Count; i++)
          pl.AddVertexAt(i, new Point2d(ox + (massPts[i].X - st0) * hs, oy + massPts[i].Y * vsc), 0, 0, 0);
        var axis = new Line(new Point3d(ox, oy, 0), new Point3d(ox + (st1 - st0) * hs, oy, 0)) { Layer = lay };
        ms.AppendEntity(pl); tr.AddNewlyCreatedDBObject(pl, true);
        ms.AppendEntity(axis); tr.AddNewlyCreatedDBObject(axis, true);
        massHandle = pl.Handle.ToString();
      }

      var totCut = rows.Count > 1 ? Enumerable.Range(1, rows.Count - 1).Sum(i => (rows[i].cutA + rows[i - 1].cutA) / 2 * (rows[i].st - rows[i - 1].st)) : 0;
      var totFill = rows.Count > 1 ? Enumerable.Range(1, rows.Count - 1).Sum(i => (rows[i].fillA + rows[i - 1].fillA) / 2 * (rows[i].st - rows[i - 1].st)) : 0;
      return new
      {
        alignment = al.Name,
        baseSurface = bs.Name,
        designSurface = ds.Name,
        method = $"average end area, sections every {interval}, sampled every {step} across {left} L / {right} R",
        totals = new { cutCY = Math.Round(totCut * cf / 27, 1), fillCY = Math.Round(totFill * ff / 27, 1), netCY = Math.Round((totCut * cf - totFill * ff) / 27, 1), cutFactor = cf, fillFactor = ff },
        rows = table.Count <= 400 ? table : null,
        rowCount = table.Count,
        csv = written,
        massHaulPolyline = massHandle,
      };
    });
  }

  // ════════════════════════════ Helpers ════════════════════════════

  private static List<Point2d> Densify(Polyline pl, double spacing)
  {
    var pts = new List<Point2d>();
    var len = pl.Length;
    var verts = Enumerable.Range(0, pl.NumberOfVertices).Select(i => pl.GetDistanceAtParameter(i)).ToList();
    var dists = new SortedSet<double>(verts);
    for (var d = 0.0; d < len; d += spacing) dists.Add(d);
    foreach (var d in dists)
    {
      if (d >= len - 1e-6) continue;
      var q = pl.GetPointAtDist(d);
      var p2 = new Point2d(q.X, q.Y);
      if (pts.Count == 0 || pts[^1].GetDistanceTo(p2) > 1e-4) pts.Add(p2);
    }
    if (pts.Count > 2 && pts[0].GetDistanceTo(pts[^1]) < 1e-4) pts.RemoveAt(pts.Count - 1);
    return pts;
  }

  private static double SignedArea(List<Point2d> r)
  {
    double a = 0;
    for (int i = 0; i < r.Count; i++) { var p = r[i]; var q = r[(i + 1) % r.Count]; a += p.X * q.Y - q.X * p.Y; }
    return a / 2;
  }

  private static bool Inside(List<Point2d> poly, double x, double y)
  {
    bool c = false;
    for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
      if (((poly[i].Y > y) != (poly[j].Y > y)) && (x < (poly[j].X - poly[i].X) * (y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)) c = !c;
    return c;
  }

  private static Point2dCollection Points2d(JsonObject? p, Database db, Transaction tr)
  {
    var arr = p?["points"] as JsonArray ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give 'polylineHandle' or 'points'.");
    var c = new Point2dCollection();
    foreach (var n in arr) c.Add(new Point2d(n!["x"]!.GetValue<double>(), n["y"]!.GetValue<double>()));
    if (c.Count > 2 && c[0].GetDistanceTo(c[c.Count - 1]) > 1e-6) c.Add(c[0]);
    return c;
  }

  private static ObjectId IdFromHandle(Database db, string h)
  {
    if (!db.TryGetObjectId(new Handle(Convert.ToInt64(h, 16)), out var id) || id.IsErased)
      throw new JsonRpcDispatchException("CIVIL3D.NOT_FOUND", $"No object with handle '{h}'.");
    return id;
  }

  private static ObjectId StyleId(Autodesk.Civil.DatabaseServices.Styles.StyleCollectionBase coll, string name)
  {
    if (coll.Contains(name)) return coll[name];
    throw new JsonRpcDispatchException("CIVIL3D.NOT_FOUND", $"Style '{name}' not found.");
  }

  private static string UniqueSurfaceName(CivilDocument cd, Transaction tr, string name)
  {
    var existing = cd.GetSurfaceIds().Cast<ObjectId>().Select(id => ((CivilSurface)tr.GetObject(id, OpenMode.ForRead)).Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    if (!existing.Contains(name)) return name;
    for (int i = 2; ; i++) if (!existing.Contains($"{name} ({i})")) return $"{name} ({i})";
  }

  private static void EnsureLayer(Database db, Transaction tr, string name)
  {
    var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
    if (lt.Has(name)) return;
    lt.UpgradeOpen();
    var ltr = new LayerTableRecord { Name = name };
    lt.Add(ltr);
    tr.AddNewlyCreatedDBObject(ltr, true);
  }

  private static CivilSurface FindSurface(CivilDocument cd, Transaction tr, string name)
  {
    foreach (ObjectId id in cd.GetSurfaceIds())
      if (tr.GetObject(id, OpenMode.ForRead) is CivilSurface s && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return s;
    throw new JsonRpcDispatchException("CIVIL3D.NOT_FOUND", $"Surface '{name}' not found.");
  }

  private static Alignment FindAlignment(CivilDocument cd, Transaction tr, string name)
  {
    foreach (ObjectId id in cd.GetAlignmentIds())
      if (tr.GetObject(id, OpenMode.ForRead) is Alignment a && a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return a;
    throw new JsonRpcDispatchException("CIVIL3D.NOT_FOUND", $"Alignment '{name}' not found.");
  }

  private static Corridor FindCorridor(CivilDocument cd, Transaction tr, string name)
  {
    foreach (ObjectId id in cd.CorridorCollection)
      if (tr.GetObject(id, OpenMode.ForRead) is Corridor c && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return c;
    throw new JsonRpcDispatchException("CIVIL3D.NOT_FOUND", $"Corridor '{name}' not found.");
  }
}
