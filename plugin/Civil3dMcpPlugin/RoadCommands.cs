using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace Civil3DMcpPlugin;

/// <summary>
/// Road modeling workflow (TCCE): styles, alignments, profiles, profile views, assemblies,
/// corridors, corridor surfaces, sample lines, section views, superelevation and design checks.
/// Signatures verified against the Civil 3D 2027 managed API (AeccDbMgd 13.9).
/// </summary>
public static class RoadCommands
{
  // ════════════════════════════ Styles / inventory ════════════════════════════

  public static Task<object?> ListRoadStylesAsync(JsonObject? parameters)
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var s = civilDoc.Styles;
      return new
      {
        alignmentStyles = Names(s.AlignmentStyles, tr),
        alignmentLabelSets = Names(s.LabelSetStyles.AlignmentLabelSetStyles, tr),
        profileStyles = Names(s.ProfileStyles, tr),
        profileLabelSets = Names(s.LabelSetStyles.ProfileLabelSetStyles, tr),
        profileViewStyles = Names(s.ProfileViewStyles, tr),
        profileViewBandSets = Names(s.ProfileViewBandSetStyles, tr),
        assemblyStyles = Names(s.AssemblyStyles, tr),
        codeSetStyles = Names(s.CodeSetStyles, tr),
        corridorStyles = Names(s.CorridorStyles, tr),
        surfaceStyles = Names(s.SurfaceStyles, tr),
        sampleLineStyles = Names(s.SampleLineStyles, tr),
        sectionViewStyles = Names(s.SectionViewStyles, tr),
        sectionViewBandSets = Names(s.SectionViewBandSetStyles, tr),
        surfaces = civilDoc.GetSurfaceIds().Cast<ObjectId>().Select(id => ((CivilSurface)tr.GetObject(id, OpenMode.ForRead)).Name).ToList(),
        alignments = civilDoc.GetAlignmentIds().Cast<ObjectId>().Select(id => ((Alignment)tr.GetObject(id, OpenMode.ForRead)).Name).ToList(),
        assemblies = civilDoc.AssemblyCollection.Select(id => ((Assembly)tr.GetObject(id, OpenMode.ForRead)).Name).ToList(),
        corridors = civilDoc.CorridorCollection.Select(id => ((Corridor)tr.GetObject(id, OpenMode.ForRead)).Name).ToList(),
      };
    });
  }

  // ════════════════════════════ Alignments ════════════════════════════

  public static Task<object?> CreateAlignmentFromPolylineAsync(JsonObject? p)
  {
    var name = PluginRuntime.GetRequiredString(p, "name");
    var handle = PluginRuntime.GetRequiredString(p, "polylineHandle");
    var addCurves = PluginRuntime.GetOptionalBool(p, "addCurvesBetweenTangents") ?? false;
    var erase = PluginRuntime.GetOptionalBool(p, "eraseExisting") ?? false;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var plId = IdFromHandle(db, handle);
      var opts = new PolylineOptions { PlineId = plId, AddCurvesBetweenTangents = addCurves, EraseExistingEntities = erase };
      var id = Alignment.Create(civilDoc, opts, UniqueAlignmentName(name), ObjectId.Null,
        LayerId(db, tr, Str(p, "layer")),
        StyleId(civilDoc.Styles.AlignmentStyles, Str(p, "style")),
        StyleId(civilDoc.Styles.LabelSetStyles.AlignmentLabelSetStyles, Str(p, "labelSet")));
      var al = (Alignment)tr.GetObject(id, OpenMode.ForWrite);
      ApplyAlignmentSettings(al, p);
      return Summary(al);
    });
  }

  /// PIs: [{x,y,radius?,spiralIn?,spiralOut?}] — first/last are start/end points; interior ones get a curve.
  public static Task<object?> CreateAlignmentByPIsAsync(JsonObject? p)
  {
    var name = PluginRuntime.GetRequiredString(p, "name");
    var pis = p?["pis"] as JsonArray;
    if (pis == null || pis.Count < 2)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "'pis' needs at least 2 points (start and end).");
    var defaultRadius = PluginRuntime.GetOptionalDouble(p, "defaultRadius");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var id = Alignment.Create(civilDoc, UniqueAlignmentName(name), ObjectId.Null,
        LayerId(db, tr, Str(p, "layer")),
        StyleId(civilDoc.Styles.AlignmentStyles, Str(p, "style")),
        StyleId(civilDoc.Styles.LabelSetStyles.AlignmentLabelSetStyles, Str(p, "labelSet")));
      var al = (Alignment)tr.GetObject(id, OpenMode.ForWrite);

      var pts = pis.Select(n => new Point3d(n!["x"]!.GetValue<double>(), n["y"]!.GetValue<double>(), 0)).ToList();
      var lines = new List<AlignmentLine>();
      for (int i = 0; i < pts.Count - 1; i++)
        lines.Add(al.Entities.AddFixedLine(pts[i], pts[i + 1]));

      var curves = new List<object>();
      for (int i = 1; i < pts.Count - 1; i++)
      {
        var node = pis[i]!;
        var r = node["radius"] != null ? node["radius"]!.GetValue<double>() : defaultRadius;
        if (r == null || r <= 0) continue; // angle point without curve
        var spIn = node["spiralIn"] != null ? node["spiralIn"]!.GetValue<double>() : 0.0;
        var spOut = node["spiralOut"] != null ? node["spiralOut"]!.GetValue<double>() : spIn;
        var prev = lines[i - 1].EntityId;
        var next = lines[i].EntityId;
        if (spIn > 0 || spOut > 0)
        {
          var scs = al.Entities.AddFreeSCS(prev, next, spIn, spOut, SpiralParamType.Length, r.Value, false, SpiralType.Clothoid);
          curves.Add(new { pi = i, type = "SCS", radius = r, spiralIn = spIn, spiralOut = spOut, entityId = scs.EntityId });
        }
        else
        {
          var arc = al.Entities.AddFreeCurve(prev, next, r.Value, CurveParamType.Radius, false, CurveType.Compound);
          curves.Add(new { pi = i, type = "Arc", radius = r, entityId = arc.EntityId, length = arc.Length });
        }
      }

      ApplyAlignmentSettings(al, p);
      return new { summary = Summary(al), curves };
    });
  }

  public static Task<object?> CreateOffsetAlignmentAsync(JsonObject? p)
  {
    var name = PluginRuntime.GetRequiredString(p, "name");
    var parent = PluginRuntime.GetRequiredString(p, "alignment");
    var offset = PluginRuntime.GetRequiredDouble(p, "offset");
    var st0 = PluginRuntime.GetOptionalDouble(p, "startStation");
    var st1 = PluginRuntime.GetOptionalDouble(p, "endStation");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var parentAl = FindAlignment(civilDoc, tr, parent);
      var styleId = StyleId(civilDoc.Styles.AlignmentStyles, Str(p, "style"));
      var id = st0.HasValue && st1.HasValue
        ? Alignment.CreateOffsetAlignment(UniqueAlignmentName(name), parentAl.ObjectId, offset, styleId, st0.Value, st1.Value)
        : Alignment.CreateOffsetAlignment(UniqueAlignmentName(name), parentAl.ObjectId, offset, styleId);
      return Summary((Alignment)tr.GetObject(id, OpenMode.ForRead));
    });
  }

  public static Task<object?> SetAlignmentDesignAsync(JsonObject? p)
  {
    var name = PluginRuntime.GetRequiredString(p, "alignment");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var al = FindAlignment(civilDoc, tr, name);
      al.UpgradeOpen();
      ApplyAlignmentSettings(al, p);
      return Summary(al);
    });
  }

  public static Task<object?> GetAlignmentGeometryAsync(JsonObject? p)
  {
    var name = PluginRuntime.GetRequiredString(p, "alignment");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var al = FindAlignment(civilDoc, tr, name);
      return new { summary = Summary(al), entities = DescribeEntities(al), designSpeeds = al.DesignSpeeds.Select(d => new { station = d.Station, speed = d.Value }).ToList() };
    });
  }

  // ════════════════════════════ Profiles ════════════════════════════

  public static Task<object?> CreateSurfaceProfileAsync(JsonObject? p)
  {
    var alName = PluginRuntime.GetRequiredString(p, "alignment");
    var surfName = PluginRuntime.GetRequiredString(p, "surface");
    var name = Str(p, "name") ?? $"{alName} - {surfName}";
    var offset = PluginRuntime.GetOptionalDouble(p, "offset");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var al = FindAlignment(civilDoc, tr, alName);
      var surf = FindSurface(civilDoc, tr, surfName);
      var layer = LayerId(db, tr, Str(p, "layer"));
      var style = StyleId(civilDoc.Styles.ProfileStyles, Str(p, "style"));
      var labels = StyleId(civilDoc.Styles.LabelSetStyles.ProfileLabelSetStyles, Str(p, "labelSet"));
      var id = offset.HasValue
        ? Profile.CreateFromSurface(name, al.ObjectId, surf.ObjectId, layer, style, labels, offset.Value, al.StartingStation, al.EndingStation)
        : Profile.CreateFromSurface(name, al.ObjectId, surf.ObjectId, layer, style, labels);
      var pr = (Profile)tr.GetObject(id, OpenMode.ForRead);
      return new { success = true, profile = pr.Name, handle = pr.Handle.ToString(), startStation = pr.StartingStation, endStation = pr.EndingStation, minElevation = pr.ElevationMin, maxElevation = pr.ElevationMax };
    });
  }

  /// pvis: [{station, elevation, curveLength?, k?}] — first/last are the ends; interior PVIs get a symmetric parabola.
  public static Task<object?> CreateLayoutProfileAsync(JsonObject? p)
  {
    var alName = PluginRuntime.GetRequiredString(p, "alignment");
    var name = PluginRuntime.GetRequiredString(p, "name");
    var pvis = p?["pvis"] as JsonArray;
    if (pvis == null || pvis.Count < 2)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "'pvis' needs at least 2 points.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var al = FindAlignment(civilDoc, tr, alName);
      var id = Profile.CreateByLayout(name, al.ObjectId,
        LayerId(db, tr, Str(p, "layer")),
        StyleId(civilDoc.Styles.ProfileStyles, Str(p, "style")),
        StyleId(civilDoc.Styles.LabelSetStyles.ProfileLabelSetStyles, Str(p, "labelSet")));
      var pr = (Profile)tr.GetObject(id, OpenMode.ForWrite);

      var pts = pvis.Select(n => new Point2d(n!["station"]!.GetValue<double>(), n["elevation"]!.GetValue<double>())).ToList();
      for (int i = 0; i < pts.Count - 1; i++)
        pr.Entities.AddFixedTangent(pts[i], pts[i + 1]);

      var curves = new List<object>();
      for (int i = 1; i < pts.Count - 1; i++)
      {
        var n = pvis[i]!;
        var k = n["k"] != null ? n["k"]!.GetValue<double>() : (double?)null;
        var len = n["curveLength"] != null ? n["curveLength"]!.GetValue<double>() : (double?)null;
        if (k == null && len == null) continue;
        var pvi = pr.PVIs.GetPVIAt(pts[i].X, pts[i].Y);
        var c = k != null
          ? pr.Entities.AddFreeSymmetricParabolaByPVIAndK(pvi, k.Value)
          : pr.Entities.AddFreeSymmetricParabolaByPVIAndCurveLength(pvi, len!.Value);
        curves.Add(new { station = pts[i].X, type = c.CurveType.ToString(), length = c.Length, k = c.K });
      }

      return new { success = true, profile = pr.Name, handle = pr.Handle.ToString(), curves, pvis = DescribePVIs(pr) };
    });
  }

  public static Task<object?> GetProfileGeometryAsync(JsonObject? p)
  {
    var alName = PluginRuntime.GetRequiredString(p, "alignment");
    var prName = PluginRuntime.GetRequiredString(p, "profile");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var pr = FindProfile(FindAlignment(civilDoc, tr, alName), tr, prName);
      return new { profile = pr.Name, startStation = pr.StartingStation, endStation = pr.EndingStation, pvis = DescribePVIs(pr) };
    });
  }

  public static Task<object?> CreateProfileViewAsync(JsonObject? p)
  {
    var alName = PluginRuntime.GetRequiredString(p, "alignment");
    var x = PluginRuntime.GetRequiredDouble(p, "x");
    var y = PluginRuntime.GetRequiredDouble(p, "y");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var al = FindAlignment(civilDoc, tr, alName);
      var id = ProfileView.Create(al.ObjectId, new Point3d(x, y, 0), Str(p, "name") ?? $"{alName} PV",
        StyleId(civilDoc.Styles.ProfileViewBandSetStyles, Str(p, "bandSet")),
        StyleId(civilDoc.Styles.ProfileViewStyles, Str(p, "style")));
      var pv = (ProfileView)tr.GetObject(id, OpenMode.ForRead);
      return new { success = true, profileView = pv.Name, handle = pv.Handle.ToString() };
    });
  }

  // ════════════════════════════ Assemblies ════════════════════════════

  /// subassemblies: [{stock:"LaneSuperelevationAOR", side:"Left|Right", name?, params:{Width:11}, attach:"previous|assembly"}]
  public static Task<object?> CreateAssemblyAsync(JsonObject? p)
  {
    var name = PluginRuntime.GetRequiredString(p, "name");
    var x = PluginRuntime.GetOptionalDouble(p, "x") ?? 0;
    var y = PluginRuntime.GetOptionalDouble(p, "y") ?? 0;
    var subs = p?["subassemblies"] as JsonArray;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var loc = new Point3d(x, y, 0);
      var typeName = Str(p, "type");
      var type = typeName != null && Enum.TryParse<AssemblyType>(typeName, true, out var t) ? t : AssemblyType.UndividedCrownedRoad;
      var asmId = civilDoc.AssemblyCollection.Add(name, type, loc,
        StyleId(civilDoc.Styles.AssemblyStyles, Str(p, "style")),
        StyleId(civilDoc.Styles.CodeSetStyles, Str(p, "codeSetStyle")));
      var asm = (Assembly)tr.GetObject(asmId, OpenMode.ForWrite);

      var added = new List<object>();
      var lastBySide = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
      if (subs != null)
      {
        var idx = 0;
        foreach (var s in subs)
        {
          idx++;
          var stock = s!["stock"]?.GetValue<string>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Each subassembly needs 'stock' (e.g. LaneSuperelevationAOR).");
          var className = stock.Contains('.') ? stock : "Subassembly." + stock;
          var side = s["side"]?.GetValue<string>() ?? "Right";
          var subName = s["name"]?.GetValue<string>() ?? $"{name} - {stock} ({side}) {idx}";
          var attach = s["attach"]?.GetValue<string>() ?? "previous";

          var subId = civilDoc.SubassemblyCollection.ImportStockSubassembly(subName, className, loc);
          var sub = (Subassembly)tr.GetObject(subId, OpenMode.ForWrite);
          if (sub.HasSide) sub.Side = side.StartsWith("L", StringComparison.OrdinalIgnoreCase) ? SubassemblySideType.Left : SubassemblySideType.Right;
          var setParams = SetParams(sub, s["params"] as JsonObject);

          string hookedTo = "assembly";
          if (attach.Equals("previous", StringComparison.OrdinalIgnoreCase) && lastBySide.TryGetValue(side, out var prevId))
          {
            var prev = (Subassembly)tr.GetObject(prevId, OpenMode.ForWrite);
            var hook = OuterTopPoint(prev, side);
            if (hook != null) { asm.AddSubassembly(subId, hook); hookedTo = $"{prev.Name} P{hook.Index}"; }
            else asm.AddSubassembly(subId);
          }
          else asm.AddSubassembly(subId);

          lastBySide[side] = subId;
          added.Add(new { name = subName, stock = className, side, hookedTo, paramsSet = setParams });
        }
      }

      return new { success = true, assembly = name, handle = asm.Handle.ToString(), subassemblies = added };
    });
  }

  public static Task<object?> GetAssemblyAsync(JsonObject? p)
  {
    var name = PluginRuntime.GetRequiredString(p, "assembly");
    var withParams = PluginRuntime.GetOptionalBool(p, "includeParams") ?? true;
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var asm = FindAssembly(civilDoc, tr, name);
      var groups = new List<object>();
      foreach (AssemblyGroup g in asm.Groups)
      {
        var subs = new List<object>();
        foreach (ObjectId sid in g.GetSubassemblyIds())
        {
          var sub = (Subassembly)tr.GetObject(sid, OpenMode.ForRead);
          subs.Add(new
          {
            name = sub.Name,
            side = sub.HasSide ? sub.Side.ToString() : "None",
            points = sub.Points.Select(pt => new { index = pt.Index, offset = pt.Offset, elevation = pt.Elevation, codes = CodesOf(pt) }).ToList(),
            parameters = withParams ? DescribeParams(sub) : null,
          });
        }
        groups.Add(new { group = g.Name, subassemblies = subs });
      }
      return new { assembly = asm.Name, groups };
    });
  }

  /// Sets parameters on one subassembly (by name) inside an assembly. Corridors using it update on rebuild.
  public static Task<object?> SetSubassemblyParamsAsync(JsonObject? p)
  {
    var asmName = PluginRuntime.GetRequiredString(p, "assembly");
    var subName = PluginRuntime.GetRequiredString(p, "subassembly");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var asm = FindAssembly(civilDoc, tr, asmName);
      foreach (AssemblyGroup g in asm.Groups)
        foreach (ObjectId sid in g.GetSubassemblyIds())
        {
          var sub = (Subassembly)tr.GetObject(sid, OpenMode.ForRead);
          if (!sub.Name.Equals(subName, StringComparison.OrdinalIgnoreCase)) continue;
          sub.UpgradeOpen();
          var set = SetParams(sub, p?["params"] as JsonObject);
          return new { success = true, subassembly = sub.Name, paramsSet = set, parameters = DescribeParams(sub) };
        }
      throw new JsonRpcDispatchException("CIVIL3D.NOT_FOUND", $"Subassembly '{subName}' not found in assembly '{asmName}'.");
    });
  }

  // ════════════════════════════ Corridors ════════════════════════════

  public static Task<object?> CreateCorridorAsync(JsonObject? p)
  {
    var name = PluginRuntime.GetRequiredString(p, "name");
    var alName = PluginRuntime.GetRequiredString(p, "alignment");
    var prName = PluginRuntime.GetRequiredString(p, "profile");
    var asmName = PluginRuntime.GetRequiredString(p, "assembly");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var al = FindAlignment(civilDoc, tr, alName);
      var pr = FindProfile(al, tr, prName);
      var asm = FindAssembly(civilDoc, tr, asmName);

      var corrId = civilDoc.CorridorCollection.Add(name, Str(p, "baselineName") ?? "Baseline (1)", al.ObjectId, pr.ObjectId, Str(p, "regionName") ?? "Region (1)", asm.ObjectId);
      var corr = (Corridor)tr.GetObject(corrId, OpenMode.ForWrite);
      var styleName = Str(p, "style");
      if (styleName != null) corr.StyleId = StyleId(civilDoc.Styles.CorridorStyles, styleName);

      var region = corr.Baselines[0].BaselineRegions[0];
      var st0 = PluginRuntime.GetOptionalDouble(p, "startStation");
      var st1 = PluginRuntime.GetOptionalDouble(p, "endStation");
      if (st0.HasValue) region.StartStation = st0.Value;
      if (st1.HasValue) region.EndStation = st1.Value;

      var f = region.AppliedAssemblySetting;
      f.FrequencyAlongTangents = PluginRuntime.GetOptionalDouble(p, "frequencyTangents") ?? 25;
      f.FrequencyAlongCurves = PluginRuntime.GetOptionalDouble(p, "frequencyCurves") ?? 10;
      f.FrequencyAlongSpirals = PluginRuntime.GetOptionalDouble(p, "frequencySpirals") ?? 10;
      f.FrequencyAlongProfileCurves = PluginRuntime.GetOptionalDouble(p, "frequencyProfileCurves") ?? 10;
      f.AppliedAtHorizontalGeometryPoints = true;
      f.AppliedAtProfileGeometryPoints = true;
      f.AppliedAtProfileHighLowPoints = true;
      f.AppliedAtSuperelevationCriticalPoints = true;

      var targetsSet = SetSurfaceTargets(civilDoc, tr, region, Str(p, "targetSurface"));
      corr.Rebuild();

      return new
      {
        success = true,
        corridor = corr.Name,
        handle = corr.Handle.ToString(),
        region = new { start = region.StartStation, end = region.EndStation },
        targetsSet,
      };
    });
  }

  public static Task<object?> SetCorridorTargetsAsync(JsonObject? p)
  {
    var name = PluginRuntime.GetRequiredString(p, "corridor");
    var surfName = PluginRuntime.GetRequiredString(p, "targetSurface");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var corr = FindCorridor(civilDoc, tr, name);
      corr.UpgradeOpen();
      var result = new List<object>();
      foreach (Baseline b in corr.Baselines)
        foreach (BaselineRegion r in b.BaselineRegions)
          result.Add(new { baseline = b.Name, region = r.Name, targets = SetSurfaceTargets(civilDoc, tr, r, surfName) });
      corr.Rebuild();
      return new { success = true, corridor = corr.Name, regions = result };
    });
  }

  public static Task<object?> AddCorridorSurfaceAsync(JsonObject? p)
  {
    var name = PluginRuntime.GetRequiredString(p, "corridor");
    var surfName = PluginRuntime.GetRequiredString(p, "name");
    var codes = (p?["linkCodes"] as JsonArray)?.Select(n => n!.GetValue<string>()).ToList() ?? new List<string> { "Top" };
    var flCodes = (p?["featureLineCodes"] as JsonArray)?.Select(n => n!.GetValue<string>()).ToList();
    var extents = PluginRuntime.GetOptionalBool(p, "extentsBoundary") ?? true;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var corr = FindCorridor(civilDoc, tr, name);
      corr.UpgradeOpen();
      var styleName = Str(p, "style");
      var cs = styleName != null
        ? corr.CorridorSurfaces.Add(surfName, StyleId(civilDoc.Styles.SurfaceStyles, styleName))
        : corr.CorridorSurfaces.Add(surfName);
      foreach (var c in codes) cs.AddLinkCode(c, true);
      if (flCodes != null) foreach (var c in flCodes) cs.AddFeatureLineCode(c);
      if (extents) cs.Boundaries.AddCorridorExtentsBoundary("Corridor Extents");
      corr.Rebuild();
      return new { success = true, corridor = corr.Name, surface = cs.Name, surfaceHandle = cs.SurfaceId.Handle.ToString(), linkCodes = cs.LinkCodes() };
    });
  }

  public static Task<object?> GetCorridorInfoAsync(JsonObject? p)
  {
    var name = PluginRuntime.GetRequiredString(p, "corridor");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var corr = FindCorridor(civilDoc, tr, name);
      var baselines = new List<object>();
      foreach (Baseline b in corr.Baselines)
      {
        var regions = new List<object>();
        foreach (BaselineRegion r in b.BaselineRegions)
        {
          var asm = r.AssemblyId.IsNull ? null : ((Assembly)tr.GetObject(r.AssemblyId, OpenMode.ForRead)).Name;
          var targets = new List<object>();
          using (var ti = r.GetTargets())
            foreach (SubassemblyTargetInfo t in ti)
              targets.Add(new { subassembly = t.SubassemblyName, target = t.DisplayName, type = t.TargetType.ToString(), ids = t.TargetIds.Count });
          regions.Add(new { name = r.Name, start = r.StartStation, end = r.EndStation, assembly = asm, targets });
        }
        baselines.Add(new
        {
          name = b.Name,
          alignment = ((Alignment)tr.GetObject(b.AlignmentId, OpenMode.ForRead)).Name,
          profile = b.ProfileId.IsNull ? null : ((Profile)tr.GetObject(b.ProfileId, OpenMode.ForRead)).Name,
          regions,
        });
      }
      var surfaces = new List<object>();
      foreach (CorridorSurface cs in corr.CorridorSurfaces)
        surfaces.Add(new { name = cs.Name, linkCodes = cs.LinkCodes(), featureLineCodes = cs.FeatureLineCodes() });
      return new { corridor = corr.Name, baselines, surfaces };
    });
  }

  // ════════════════════════════ Sample lines / sections ════════════════════════════

  public static Task<object?> CreateSampleLinesAsync(JsonObject? p)
  {
    var alName = PluginRuntime.GetRequiredString(p, "alignment");
    var groupName = Str(p, "groupName") ?? "SL Collection";
    var interval = PluginRuntime.GetOptionalDouble(p, "interval") ?? 50;
    var left = PluginRuntime.GetOptionalDouble(p, "leftWidth") ?? 50;
    var right = PluginRuntime.GetOptionalDouble(p, "rightWidth") ?? 50;
    var includeGeometry = PluginRuntime.GetOptionalBool(p, "includeGeometryPoints") ?? true;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var al = FindAlignment(civilDoc, tr, alName);
      var st0 = PluginRuntime.GetOptionalDouble(p, "startStation") ?? al.StartingStation;
      var st1 = PluginRuntime.GetOptionalDouble(p, "endStation") ?? al.EndingStation;

      var stations = new SortedSet<double>();
      for (var s = Math.Ceiling(st0 / interval) * interval; s <= st1 + 1e-6; s += interval) stations.Add(Math.Round(s, 4));
      stations.Add(st0); stations.Add(st1);
      if (includeGeometry)
        foreach (var gs in al.GetStationSet(StationTypes.GeometryPoint))
          if (gs.RawStation >= st0 && gs.RawStation <= st1) stations.Add(Math.Round(gs.RawStation, 4));

      var groupId = SampleLineGroup.Create(groupName, al.ObjectId);
      var count = 0;
      foreach (var st in stations)
      {
        double lx = 0, ly = 0, rx = 0, ry = 0, cx = 0, cy = 0;
        al.PointLocation(st, -left, ref lx, ref ly);
        al.PointLocation(st, 0, ref cx, ref cy);
        al.PointLocation(st, right, ref rx, ref ry);
        var pts = new Point2dCollection { new Point2d(lx, ly), new Point2d(cx, cy), new Point2d(rx, ry) };
        SampleLine.Create(al.GetStationStringWithEquations(st), groupId, pts);
        count++;
      }

      var group = (SampleLineGroup)tr.GetObject(groupId, OpenMode.ForWrite);
      var sources = new List<string>();
      foreach (SectionSource src in group.GetSectionSources())
      {
        src.IsSampled = true;
        sources.Add($"{src.SourceName} ({src.SourceType})");
      }

      return new { success = true, group = group.Name, sampleLines = count, interval, leftWidth = left, rightWidth = right, sampledSources = sources };
    });
  }

  public static Task<object?> CreateSectionViewsAsync(JsonObject? p)
  {
    var alName = PluginRuntime.GetRequiredString(p, "alignment");
    var x = PluginRuntime.GetRequiredDouble(p, "x");
    var y = PluginRuntime.GetRequiredDouble(p, "y");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var al = FindAlignment(civilDoc, tr, alName);
      var group = FindSampleLineGroup(al, tr, Str(p, "groupName"));
      group.UpgradeOpen();
      var svg = group.SectionViewGroups.Add(new Point3d(x, y, 0));
      return new { success = true, group = group.Name, sectionViews = svg.GetSectionViewIds().Count };
    });
  }

  // ════════════════════════════ Superelevation (experimental) ════════════════════════════

  /// Writes AASHTO-style critical stations on every circular curve:
  /// Lr = laneWidth·e / relativeGradient (one lane rotated about the centerline) unless runoffLength is given;
  /// Lt = normalCrown/e · Lr; a fraction 'runoffOnTangent' (default 2/3) of Lr is placed before PC / after PT.
  public static Task<object?> ApplySuperelevationAsync(JsonObject? p)
  {
    var alName = PluginRuntime.GetRequiredString(p, "alignment");
    var e = PluginRuntime.GetOptionalDouble(p, "e") ?? 0.04;
    var nc = PluginRuntime.GetOptionalDouble(p, "normalCrown") ?? 0.02;
    var lane = PluginRuntime.GetOptionalDouble(p, "laneWidth") ?? 11;
    var relGrad = PluginRuntime.GetOptionalDouble(p, "relativeGradient") ?? 0.0066;
    var fixedLr = PluginRuntime.GetOptionalDouble(p, "runoffLength");
    var onTangent = PluginRuntime.GetOptionalDouble(p, "runoffOnTangent") ?? 2.0 / 3.0;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var al = FindAlignment(civilDoc, tr, alName);
      al.UpgradeOpen();
      var Lr = fixedLr ?? lane * e / relGrad;
      var Lt = nc / e * Lr;
      var report = new List<object>();
      var warnings = new List<string>();

      foreach (AlignmentEntity ent in al.Entities)
      {
        if (ent is not AlignmentArc arc) continue;
        var pc = arc.StartStation; var pt = arc.EndStation;
        var lc1 = pc - onTangent * Lr; var bnc = lc1 - Lt; var bfs = pc + (1 - onTangent) * Lr;
        var efs = pt - (1 - onTangent) * Lr; var lc2 = pt + onTangent * Lr; var enc = lc2 + Lt;
        if (bfs > efs) warnings.Add($"Curve at PI {arc.PIStation:F2}: too short for full superelevation (BFS {bfs:F2} > EFS {efs:F2}).");
        if (bnc < al.StartingStation || enc > al.EndingStation) warnings.Add($"Curve at PI {arc.PIStation:F2}: transition runs past the alignment ends.");

        try
        {
          var sub = arc[0];
          al.SuperelevationCurves.AddUserDefinedCurve(sub, sub);
        }
        catch (Exception ex) { warnings.Add($"AddUserDefinedCurve at PI {arc.PIStation:F2}: {ex.Message}"); }

        // Outside of a clockwise (right-turning) curve is the LEFT side.
        var outsideLeft = arc.Clockwise;
        double o(double v) => v; // slopes as decimals (−0.02 = 2% down from crown)
        var crit = al.SuperelevationCriticalStations;
        var rows = new (double st, SuperelevationCriticalStationType type, SuperelevationAttainmentRegionType reg, double outSlope, double inSlope)[]
        {
          (bnc, SuperelevationCriticalStationType.BeginNormalCrown, SuperelevationAttainmentRegionType.BeginingAttainmentRegion, -nc, -nc),
          (lc1, SuperelevationCriticalStationType.LevelCrown, SuperelevationAttainmentRegionType.BeginingAttainmentRegion, 0, -nc),
          (bfs, SuperelevationCriticalStationType.BeginFullSuper, SuperelevationAttainmentRegionType.BeginingAttainmentRegion, e, -e),
          (efs, SuperelevationCriticalStationType.EndFullSuper, SuperelevationAttainmentRegionType.EndingAttainmentRegion, e, -e),
          (lc2, SuperelevationCriticalStationType.LevelCrown, SuperelevationAttainmentRegionType.EndingAttainmentRegion, 0, -nc),
          (enc, SuperelevationCriticalStationType.EndNormalCrown, SuperelevationAttainmentRegionType.EndingAttainmentRegion, -nc, -nc),
        };
        var written = new List<object>();
        foreach (var r in rows)
        {
          try
          {
            crit.Add(r.st, r.type, r.reg);
            var cs = crit.GetCriticalStationAt(r.st, 0.01);
            if (cs != null)
            {
              var (lOut, rOut) = outsideLeft ? (o(r.outSlope), o(r.inSlope)) : (o(r.inSlope), o(r.outSlope));
              cs.SetSlope(SuperelevationCrossSegmentType.LeftInLaneCrossSlope, lOut);
              cs.SetSlope(SuperelevationCrossSegmentType.LeftOutLaneCrossSlope, lOut);
              cs.SetSlope(SuperelevationCrossSegmentType.RightInLaneCrossSlope, rOut);
              cs.SetSlope(SuperelevationCrossSegmentType.RightOutLaneCrossSlope, rOut);
              written.Add(new { station = Math.Round(r.st, 2), type = r.type.ToString(), left = cs.GetSlope(SuperelevationCrossSegmentType.LeftInLaneCrossSlope), right = cs.GetSlope(SuperelevationCrossSegmentType.RightInLaneCrossSlope) });
            }
          }
          catch (Exception ex) { warnings.Add($"{r.type} @ {r.st:F2}: {ex.Message}"); }
        }
        report.Add(new { piStation = arc.PIStation, radius = arc.Radius, direction = arc.Clockwise ? "Right" : "Left", pc, pt, stations = written });
      }

      return new { success = true, e, normalCrown = nc, runoffLength = Math.Round(Lr, 2), tangentRunout = Math.Round(Lt, 2), curves = report, warnings };
    });
  }

  // ════════════════════════════ Design check ════════════════════════════

  public static Task<object?> CheckRoadDesignAsync(JsonObject? p)
  {
    var alName = PluginRuntime.GetRequiredString(p, "alignment");
    var prName = Str(p, "profile");
    var minR = PluginRuntime.GetOptionalDouble(p, "minRadius");
    var maxG = PluginRuntime.GetOptionalDouble(p, "maxGrade");   // percent
    var minG = PluginRuntime.GetOptionalDouble(p, "minGrade");   // percent
    var kCrest = PluginRuntime.GetOptionalDouble(p, "kCrest");
    var kSag = PluginRuntime.GetOptionalDouble(p, "kSag");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var al = FindAlignment(civilDoc, tr, alName);
      var issues = new List<object>();
      var arcs = new List<object>();
      foreach (AlignmentEntity ent in al.Entities)
      {
        if (ent is AlignmentArc a)
        {
          var ok = !minR.HasValue || a.Radius >= minR.Value - 1e-6;
          arcs.Add(new { piStation = a.PIStation, radius = a.Radius, length = a.Length, ok });
          if (!ok) issues.Add(new { element = "horizontal curve", station = a.PIStation, value = a.Radius, limit = minR, message = $"R = {a.Radius:F1} < Rmin {minR}" });
        }
      }

      object? vertical = null;
      if (prName != null)
      {
        var pr = FindProfile(al, tr, prName);
        var pvis = new List<object>();
        for (int i = 0; i < pr.PVIs.Count; i++)
        {
          var v = pr.PVIs[i];
          var gOut = v.GradeOut * 100;
          if (i < pr.PVIs.Count - 1)
          {
            if (maxG.HasValue && Math.Abs(gOut) > maxG.Value + 1e-9) issues.Add(new { element = "grade", station = v.RawStation, value = gOut, limit = maxG, message = $"|g| = {Math.Abs(gOut):F2}% > {maxG}%" });
            if (minG.HasValue && Math.Abs(gOut) < minG.Value - 1e-9) issues.Add(new { element = "grade", station = v.RawStation, value = gOut, limit = minG, message = $"|g| = {Math.Abs(gOut):F2}% < {minG}% (drainage)" });
          }
          if (v.VerticalCurve is ProfileParabolaSymmetric c)
          {
            var lim = c.CurveType == VerticalCurveType.Crest ? kCrest : kSag;
            if (lim.HasValue && c.K < lim.Value - 1e-6) issues.Add(new { element = $"{c.CurveType} curve", station = v.RawStation, value = c.K, limit = lim, message = $"K = {c.K:F1} < {lim}" });
          }
        }
        vertical = DescribePVIs(pr);
      }

      return new { alignment = al.Name, passed = issues.Count == 0, issues, horizontalCurves = arcs, vertical };
    });
  }

  // ════════════════════════════ Helpers ════════════════════════════

  private static string? Str(JsonObject? p, string k) => PluginRuntime.GetOptionalString(p, k);

  private static List<string> Names(StyleCollectionBase coll, Transaction tr)
  {
    var list = new List<string>();
    foreach (ObjectId id in coll)
      if (tr.GetObject(id, OpenMode.ForRead) is StyleBase sb) list.Add(sb.Name);
    return list;
  }

  private static ObjectId StyleId(StyleCollectionBase coll, string? name)
  {
    if (name != null)
    {
      if (coll.Contains(name)) return coll[name];
      throw new JsonRpcDispatchException("CIVIL3D.NOT_FOUND", $"Style '{name}' not found. Use list_styles to see available names.");
    }
    return coll.Count > 0 ? coll[0] : ObjectId.Null;
  }

  private static ObjectId LayerId(Database db, Transaction tr, string? name)
  {
    if (string.IsNullOrWhiteSpace(name)) return db.Clayer;
    var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
    if (lt.Has(name)) return lt[name];
    lt.UpgradeOpen();
    var ltr = new LayerTableRecord { Name = name };
    var id = lt.Add(ltr);
    tr.AddNewlyCreatedDBObject(ltr, true);
    return id;
  }

  private static ObjectId IdFromHandle(Database db, string h)
  {
    if (!db.TryGetObjectId(new Handle(Convert.ToInt64(h, 16)), out var id) || id.IsErased)
      throw new JsonRpcDispatchException("CIVIL3D.NOT_FOUND", $"No object with handle '{h}'.");
    return id;
  }

  private static string UniqueAlignmentName(string name) => Alignment.GetNextUniqueName(name);

  private static void ApplyAlignmentSettings(Alignment al, JsonObject? p)
  {
    var st = PluginRuntime.GetOptionalDouble(p, "startStation");
    if (st.HasValue) al.ReferencePointStation = st.Value;
    var speed = PluginRuntime.GetOptionalDouble(p, "designSpeed");
    if (speed.HasValue) al.DesignSpeeds.Add(al.StartingStation, speed.Value);
    var crit = Str(p, "criteriaFile");
    if (crit != null) { al.CriteriaFileName = crit; al.UseDesignCriteriaFile = true; }
    var useSpeed = PluginRuntime.GetOptionalBool(p, "useDesignSpeed");
    if (useSpeed.HasValue) al.UseDesignSpeed = useSpeed.Value;
  }

  private static object Summary(Alignment al) => new
  {
    alignment = al.Name,
    handle = al.Handle.ToString(),
    startStation = al.StartingStation,
    endStation = al.EndingStation,
    length = al.Length,
    entityCount = al.Entities.Count,
  };

  private static List<object> DescribeEntities(Alignment al)
  {
    var list = new List<object>();
    foreach (AlignmentEntity ent in al.Entities)
    {
      switch (ent)
      {
        case AlignmentLine l:
          list.Add(new { id = l.EntityId, type = "Line", start = l.StartStation, end = l.EndStation, length = l.Length, bearingRad = l.Direction });
          break;
        case AlignmentArc a:
          list.Add(new { id = a.EntityId, type = "Arc", start = a.StartStation, end = a.EndStation, length = a.Length, radius = a.Radius, direction = a.Clockwise ? "Right" : "Left", piStation = a.PIStation, deltaDeg = a.Delta * 180 / Math.PI });
          break;
        default:
          list.Add(new { id = ent.EntityId, type = ent.EntityType.ToString(), subEntities = ent.SubEntityCount });
          break;
      }
    }
    return list;
  }

  private static List<object> DescribePVIs(Profile pr)
  {
    var list = new List<object>();
    for (int i = 0; i < pr.PVIs.Count; i++)
    {
      var v = pr.PVIs[i];
      double? k = null, len = null; string? type = null;
      if (v.VerticalCurve is ProfileParabolaSymmetric c) { k = c.K; len = c.Length; type = c.CurveType.ToString(); }
      else if (v.VerticalCurve != null) { len = v.VerticalCurve.Length; type = v.VerticalCurve.EntityType.ToString(); }
      list.Add(new { station = v.RawStation, elevation = v.Elevation, gradeInPct = v.GradeIn * 100, gradeOutPct = v.GradeOut * 100, curveType = type, curveLength = len, k });
    }
    return list;
  }

  private static Alignment FindAlignment(CivilDocument cd, Transaction tr, string name)
  {
    foreach (ObjectId id in cd.GetAlignmentIds())
      if (tr.GetObject(id, OpenMode.ForRead) is Alignment a && a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return a;
    throw new JsonRpcDispatchException("CIVIL3D.NOT_FOUND", $"Alignment '{name}' not found.");
  }

  private static Profile FindProfile(Alignment al, Transaction tr, string name)
  {
    foreach (ObjectId id in al.GetProfileIds())
      if (tr.GetObject(id, OpenMode.ForRead) is Profile pr && pr.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return pr;
    throw new JsonRpcDispatchException("CIVIL3D.NOT_FOUND", $"Profile '{name}' not found on alignment '{al.Name}'.");
  }

  private static CivilSurface FindSurface(CivilDocument cd, Transaction tr, string name)
  {
    foreach (ObjectId id in cd.GetSurfaceIds())
      if (tr.GetObject(id, OpenMode.ForRead) is CivilSurface s && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return s;
    throw new JsonRpcDispatchException("CIVIL3D.NOT_FOUND", $"Surface '{name}' not found.");
  }

  private static Assembly FindAssembly(CivilDocument cd, Transaction tr, string name)
  {
    foreach (ObjectId id in cd.AssemblyCollection)
      if (tr.GetObject(id, OpenMode.ForRead) is Assembly a && a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return a;
    throw new JsonRpcDispatchException("CIVIL3D.NOT_FOUND", $"Assembly '{name}' not found.");
  }

  private static Corridor FindCorridor(CivilDocument cd, Transaction tr, string name)
  {
    foreach (ObjectId id in cd.CorridorCollection)
      if (tr.GetObject(id, OpenMode.ForRead) is Corridor c && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return c;
    throw new JsonRpcDispatchException("CIVIL3D.NOT_FOUND", $"Corridor '{name}' not found.");
  }

  private static SampleLineGroup FindSampleLineGroup(Alignment al, Transaction tr, string? name)
  {
    foreach (ObjectId id in al.GetSampleLineGroupIds())
    {
      var g = (SampleLineGroup)tr.GetObject(id, OpenMode.ForRead);
      if (name == null || g.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return g;
    }
    throw new JsonRpcDispatchException("CIVIL3D.NOT_FOUND", $"Sample line group '{name ?? "(any)"}' not found on '{al.Name}'.");
  }

  private static List<object> SetSurfaceTargets(CivilDocument cd, Transaction tr, BaselineRegion region, string? surfaceName)
  {
    var result = new List<object>();
    if (surfaceName == null) return result;
    var surf = FindSurface(cd, tr, surfaceName);
    using var targets = region.GetTargets();
    foreach (SubassemblyTargetInfo t in targets)
    {
      if (t.TargetType != SubassemblyLogicalNameType.Surface) continue;
      t.TargetIds = new ObjectIdCollection { surf.ObjectId };
      result.Add(new { subassembly = t.SubassemblyName, target = t.DisplayName, surface = surf.Name });
    }
    region.SetTargets(targets);
    return result;
  }

  private static List<string> SetParams(Subassembly sub, JsonObject? values)
  {
    var set = new List<string>();
    if (values == null) return set;
    foreach (var kv in values)
    {
      var key = kv.Key; var v = kv.Value;
      bool Match(string k, string dn) => k.Equals(key, StringComparison.OrdinalIgnoreCase) || dn.Equals(key, StringComparison.OrdinalIgnoreCase);
      var done = false;
      foreach (var pd in sub.ParamsDouble) if (!done && Match(pd.Key, pd.DisplayName)) { pd.Value = v!.GetValue<double>(); done = true; }
      foreach (var pl in sub.ParamsLong) if (!done && Match(pl.Key, pl.DisplayName)) { pl.Value = (int)v!.GetValue<double>(); done = true; }
      foreach (var pb in sub.ParamsBool) if (!done && Match(pb.Key, pb.DisplayName)) { pb.Value = v!.GetValue<bool>(); done = true; }
      foreach (var ps in sub.ParamsString) if (!done && Match(ps.Key, ps.DisplayName)) { ps.Value = v!.ToString(); done = true; }
      set.Add(done ? key : key + " (NOT FOUND)");
    }
    return set;
  }

  private static object DescribeParams(Subassembly sub) => new
  {
    doubles = sub.ParamsDouble.Select(x => new { key = x.Key, name = x.DisplayName, value = x.Value }).ToList(),
    longs = sub.ParamsLong.Select(x => new { key = x.Key, name = x.DisplayName, value = x.Value }).ToList(),
    bools = sub.ParamsBool.Select(x => new { key = x.Key, name = x.DisplayName, value = x.Value }).ToList(),
    strings = sub.ParamsString.Select(x => new { key = x.Key, name = x.DisplayName, value = x.Value }).ToList(),
  };

  private static string[] CodesOf(Autodesk.Civil.DatabaseServices.Point pt)
  {
    try { return pt.Codes.Cast<object>().Select(c => c.ToString() ?? "").ToArray(); } catch { return Array.Empty<string>(); }
  }

  /// Hook point for chaining: outermost point on the given side, highest if tied.
  private static Autodesk.Civil.DatabaseServices.Point? OuterTopPoint(Subassembly sub, string side)
  {
    try { sub.Run(); } catch { }
    var left = side.StartsWith("L", StringComparison.OrdinalIgnoreCase);
    return sub.Points
      .OrderByDescending(pt => left ? -pt.Offset : pt.Offset)
      .ThenByDescending(pt => pt.Elevation)
      .FirstOrDefault();
  }
}
