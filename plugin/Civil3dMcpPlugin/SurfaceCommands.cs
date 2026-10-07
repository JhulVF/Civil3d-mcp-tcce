using Autodesk.Civil.ApplicationServices;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// Handles surface operations: list, get, elevation, statistics, create, delete,
/// add points/breaklines/boundaries, contours, and volume computation.
/// </summary>
public static class SurfaceCommands
{
  public static Task<object?> ListSurfacesAsync()
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var surfaceIds = civilDoc.GetSurfaceIds();
      var surfaces = new List<object>();

      foreach (ObjectId id in surfaceIds)
      {
        var surface = tr.GetObject(id, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Surface;
        if (surface == null) continue;

        surfaces.Add(new
        {
          name = surface.Name,
          handle = surface.Handle.ToString(),
          type = surface is TinSurface ? "TIN" : surface is GridSurface ? "Grid" : "Other",
        });
      }

      return new { surfaces };
    });
  }

  public static Task<object?> GetSurfaceAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var surface = FindSurfaceByName(civilDoc, tr, name);

      var result = new
      {
        name = surface.Name,
        handle = surface.Handle.ToString(),
        type = surface is TinSurface ? "TIN" : surface is GridSurface ? "Grid" : "Other",
        layer = surface.Layer,
        style = surface.StyleName,
      };

      if (surface is TinSurface tinSurface)
      {
        return new
        {
          result.name,
          result.handle,
          result.type,
          result.layer,
          result.style,
          statistics = new
          {
            minimumElevation = tinSurface.GetGeneralProperties().MinimumElevation,
            maximumElevation = tinSurface.GetGeneralProperties().MaximumElevation,
            numberOfPoints = tinSurface.GetGeneralProperties().NumberOfPoints,
          },
        };
      }

      return (object)result;
    });
  }

  public static Task<object?> GetSurfaceElevationAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var x = PluginRuntime.GetRequiredDouble(parameters, "x");
    var y = PluginRuntime.GetRequiredDouble(parameters, "y");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var surface = FindSurfaceByName(civilDoc, tr, name);
      var elevation = surface.FindElevationAtXY(x, y);

      return new
      {
        surfaceName = name,
        elevation,
        x,
        y,
      };
    });
  }

  public static Task<object?> GetSurfaceStatisticsAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var surface = FindSurfaceByName(civilDoc, tr, name);
      var props = surface.GetGeneralProperties();

      return new
      {
        surfaceName = name,
        minimumElevation = props.MinimumElevation,
        maximumElevation = props.MaximumElevation,
        numberOfPoints = props.NumberOfPoints,
      };
    });
  }

  public static Task<object?> CreateSurfaceAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var style = PluginRuntime.GetOptionalString(parameters, "style");
    var layer = PluginRuntime.GetOptionalString(parameters, "layer");
    var description = PluginRuntime.GetOptionalString(parameters, "description");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var surfaceId = TinSurface.Create(db, name);
      var surface = tr.GetObject(surfaceId, OpenMode.ForWrite) as TinSurface;

      if (description != null && surface != null)
      {
        surface.Description = description;
      }

      return new
      {
        success = true,
        name,
        handle = surface?.Handle.ToString(),
      };
    });
  }

  public static Task<object?> DeleteSurfaceAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var surface = FindSurfaceByName(civilDoc, tr, name);
      surface.UpgradeOpen();
      surface.Erase();

      return new { success = true, deleted = name };
    });
  }

  public static Task<object?> AddSurfacePointsAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var pointsNode = parameters?["points"] as JsonArray;

    if (pointsNode == null || pointsNode.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Parameter 'points' is required.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var surface = FindSurfaceByName(civilDoc, tr, name) as TinSurface
        ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Surface '{name}' is not a TIN surface.");

      surface.UpgradeOpen();
      var points = new Point3dCollection();

      foreach (var pt in pointsNode)
      {
        var x = pt!["x"]!.GetValue<double>();
        var y = pt["y"]!.GetValue<double>();
        var z = pt["z"]!.GetValue<double>();
        points.Add(new Point3d(x, y, z));
      }

      surface.AddVertices(points);

      return new { success = true, surfaceName = name, pointsAdded = points.Count };
    });
  }

  public static Task<object?> AddSurfaceBreaklineAsync(JsonObject? parameters)
  {
    // Placeholder — requires more complex implementation
    return Task.FromResult<object?>(new { status = "planned", message = "addSurfaceBreakline is not yet fully implemented." });
  }

  public static Task<object?> AddSurfaceBoundaryAsync(JsonObject? parameters)
  {
    // Placeholder — requires more complex implementation
    return Task.FromResult<object?>(new { status = "planned", message = "addSurfaceBoundary is not yet fully implemented." });
  }

  public static Task<object?> ExtractSurfaceContoursAsync(JsonObject? parameters)
  {
    // Placeholder — requires more complex implementation
    return Task.FromResult<object?>(new { status = "planned", message = "extractSurfaceContours is not yet fully implemented." });
  }

  public static Task<object?> ComputeSurfaceVolumeAsync(JsonObject? parameters)
  {
    var baseName = PluginRuntime.GetRequiredString(parameters, "baseSurface");
    var compName = PluginRuntime.GetRequiredString(parameters, "comparisonSurface");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var baseSurface = FindSurfaceByName(civilDoc, tr, baseName) as TinSurface
        ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{baseName}' is not a TIN surface.");
      var compSurface = FindSurfaceByName(civilDoc, tr, compName) as TinSurface
        ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{compName}' is not a TIN surface.");

      var volId = TinVolumeSurface.Create("_mcp_tmp_vol_" + Guid.NewGuid().ToString("N"), baseSurface.ObjectId, compSurface.ObjectId);
      var volSurface = (TinVolumeSurface)tr.GetObject(volId, OpenMode.ForRead);
      var props = volSurface.GetVolumeProperties();

      return new
      {
        baseSurface = baseName,
        comparisonSurface = compName,
        cutVolume = props.UnadjustedCutVolume,
        fillVolume = props.UnadjustedFillVolume,
        netVolume = props.UnadjustedCutVolume - props.UnadjustedFillVolume,
      };
    });
  }

  // ── Batch sampling (TCCE) ──

  /// <summary>
  /// Samples the surface on a regular grid. If outputPath is given, writes a CSV (x,y,z; z empty
  /// outside the surface) and returns only a summary; otherwise returns the grid inline
  /// (row-major from minY, z rounded to 0.01, null outside), limited to 40,000 cells.
  /// </summary>
  public static Task<object?> SampleSurfaceGridAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var minX = PluginRuntime.GetRequiredDouble(parameters, "minX");
    var minY = PluginRuntime.GetRequiredDouble(parameters, "minY");
    var maxX = PluginRuntime.GetRequiredDouble(parameters, "maxX");
    var maxY = PluginRuntime.GetRequiredDouble(parameters, "maxY");
    var spacing = PluginRuntime.GetRequiredDouble(parameters, "spacing");
    var outputPath = PluginRuntime.GetOptionalString(parameters, "outputPath");

    if (spacing <= 0 || maxX <= minX || maxY <= minY)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Invalid grid extents or spacing.");

    var nx = (int)Math.Floor((maxX - minX) / spacing) + 1;
    var ny = (int)Math.Floor((maxY - minY) / spacing) + 1;
    long cells = (long)nx * ny;
    if (cells > 4_000_000)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Grid too large ({cells} cells, max 4,000,000).");
    if (outputPath == null && cells > 40_000)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Grid has {cells} cells; pass outputPath (max 40,000 inline).");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var surface = FindSurfaceByName(civilDoc, tr, name);
      var inv = System.Globalization.CultureInfo.InvariantCulture;
      int outside = 0;
      double zMin = double.MaxValue, zMax = double.MinValue;

      if (outputPath != null)
      {
        var dir = System.IO.Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
        using var w = new System.IO.StreamWriter(outputPath, false);
        w.WriteLine("x,y,z");
        for (int j = 0; j < ny; j++)
        {
          var y = minY + j * spacing;
          for (int i = 0; i < nx; i++)
          {
            var x = minX + i * spacing;
            var z = TryElevation(surface, x, y);
            if (z == null) { outside++; w.WriteLine(string.Format(inv, "{0:0.##},{1:0.##},", x, y)); continue; }
            zMin = Math.Min(zMin, z.Value); zMax = Math.Max(zMax, z.Value);
            w.WriteLine(string.Format(inv, "{0:0.##},{1:0.##},{2:0.##}", x, y, z.Value));
          }
        }
        return new { surfaceName = name, outputPath, minX, minY, spacing, nx, ny, cells, outside,
          zMin = outside == cells ? (double?)null : zMin, zMax = outside == cells ? (double?)null : zMax };
      }

      var zs = new double?[cells];
      for (int j = 0; j < ny; j++)
        for (int i = 0; i < nx; i++)
        {
          var z = TryElevation(surface, minX + i * spacing, minY + j * spacing);
          if (z == null) outside++;
          zs[j * nx + i] = z == null ? null : Math.Round(z.Value, 2);
        }
      return new { surfaceName = name, minX, minY, spacing, nx, ny, cells, outside, order = "row-major from minY", z = zs };
    });
  }

  /// <summary>Elevations at a list of XY points (null outside the surface).</summary>
  public static Task<object?> GetSurfaceElevationsAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var pts = parameters?["points"] as JsonArray;
    if (pts == null || pts.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Parameter 'points' is required.");
    if (pts.Count > 20_000)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Max 20,000 points per call.");

    var xy = new List<(double x, double y)>(pts.Count);
    foreach (var p in pts)
      xy.Add((p!["x"]!.GetValue<double>(), p["y"]!.GetValue<double>()));

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var surface = FindSurfaceByName(civilDoc, tr, name);
      var z = new double?[xy.Count];
      int outside = 0;
      for (int k = 0; k < xy.Count; k++)
      {
        var e = TryElevation(surface, xy[k].x, xy[k].y);
        if (e == null) outside++;
        z[k] = e == null ? null : Math.Round(e.Value, 3);
      }
      return new { surfaceName = name, count = xy.Count, outside, z };
    });
  }

  private static double? TryElevation(Autodesk.Civil.DatabaseServices.Surface surface, double x, double y)
  {
    try { return surface.FindElevationAtXY(x, y); }
    catch { return null; }
  }

  // ── Helpers ──

  private static Autodesk.Civil.DatabaseServices.Surface FindSurfaceByName(
    CivilDocument civilDoc, Transaction tr, string name)
  {
    foreach (ObjectId id in civilDoc.GetSurfaceIds())
    {
      var surface = tr.GetObject(id, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Surface;
      if (surface != null && string.Equals(surface.Name, name, StringComparison.OrdinalIgnoreCase))
      {
        return surface;
      }
    }

    throw new JsonRpcDispatchException("CIVIL3D.NOT_FOUND", $"Surface '{name}' not found.");
  }
}
