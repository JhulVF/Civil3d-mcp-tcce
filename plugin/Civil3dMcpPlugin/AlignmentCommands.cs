using Autodesk.Civil.ApplicationServices;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// Handles alignment operations: list, get, create, delete,
/// station-to-point and point-to-station conversions.
/// </summary>
public static class AlignmentCommands
{
  public static Task<object?> ListAlignmentsAsync()
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var alignmentIds = civilDoc.GetAlignmentIds();
      var alignments = new List<object>();

      foreach (ObjectId id in alignmentIds)
      {
        var alignment = tr.GetObject(id, OpenMode.ForRead) as Alignment;
        if (alignment == null) continue;

        alignments.Add(new
        {
          name = alignment.Name,
          handle = alignment.Handle.ToString(),
          length = alignment.Length,
          startStation = alignment.StartingStation,
          endStation = alignment.EndingStation,
          layer = alignment.Layer,
        });
      }

      return new { alignments };
    });
  }

  public static Task<object?> GetAlignmentAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var alignment = FindAlignmentByName(civilDoc, tr, name);

      return new
      {
        name = alignment.Name,
        handle = alignment.Handle.ToString(),
        length = alignment.Length,
        startStation = alignment.StartingStation,
        endStation = alignment.EndingStation,
        layer = alignment.Layer,
        style = alignment.StyleName,
        entityCount = alignment.Entities.Count,
      };
    });
  }

  public static Task<object?> CreateAlignmentAsync(JsonObject? parameters)
  {
    // Placeholder — creating alignments requires polyline selection or entity definitions
    var name = PluginRuntime.GetRequiredString(parameters, "name");

    return Task.FromResult<object?>(new
    {
      status = "planned",
      message = $"createAlignment for '{name}' requires polyline selection. Use the Civil 3D UI for now.",
    });
  }

  public static Task<object?> DeleteAlignmentAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var alignment = FindAlignmentByName(civilDoc, tr, name);
      alignment.UpgradeOpen();
      alignment.Erase();

      return new { success = true, deleted = name };
    });
  }

  public static Task<object?> StationToPointAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var station = PluginRuntime.GetRequiredDouble(parameters, "station");
    var offset = PluginRuntime.GetOptionalDouble(parameters, "offset") ?? 0.0;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var alignment = FindAlignmentByName(civilDoc, tr, name);
      double x = 0, y = 0;
      alignment.PointLocation(station, offset, ref x, ref y);

      return new
      {
        alignmentName = name,
        station,
        offset,
        x,
        y,
      };
    });
  }

  public static Task<object?> PointToStationAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var x = PluginRuntime.GetRequiredDouble(parameters, "x");
    var y = PluginRuntime.GetRequiredDouble(parameters, "y");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var alignment = FindAlignmentByName(civilDoc, tr, name);
      double station = 0, offset = 0;
      alignment.StationOffset(x, y, ref station, ref offset);

      return new
      {
        alignmentName = name,
        x,
        y,
        station,
        offset,
      };
    });
  }

  // ── Helpers ──

  internal static Alignment FindAlignmentByName(CivilDocument civilDoc, Transaction tr, string name)
  {
    foreach (ObjectId id in civilDoc.GetAlignmentIds())
    {
      var alignment = tr.GetObject(id, OpenMode.ForRead) as Alignment;
      if (alignment != null && string.Equals(alignment.Name, name, StringComparison.OrdinalIgnoreCase))
      {
        return alignment;
      }
    }

    throw new JsonRpcDispatchException("CIVIL3D.NOT_FOUND", $"Alignment '{name}' not found.");
  }
}
