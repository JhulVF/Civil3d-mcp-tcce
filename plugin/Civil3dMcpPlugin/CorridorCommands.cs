using Autodesk.Civil.ApplicationServices;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DMcpPlugin;

public static class CorridorCommands
{
  public static Task<object?> ListCorridorsAsync()
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var corridorIds = civilDoc.CorridorCollection;
      var corridors = new List<object>();
      foreach (ObjectId id in corridorIds)
      {
        var c = tr.GetObject(id, OpenMode.ForRead) as Corridor;
        if (c != null) corridors.Add(new { name = c.Name, handle = c.Handle.ToString(), baselineCount = c.Baselines.Count, layer = c.Layer });
      }
      return new { corridors };
    });
  }

  public static Task<object?> GetCorridorAsync(JsonObject? p)
  {
    var name = PluginRuntime.GetRequiredString(p, "name");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var c = FindByName(civilDoc, tr, name);
      return new { name = c.Name, handle = c.Handle.ToString(), layer = c.Layer, baselineCount = c.Baselines.Count };
    });
  }

  public static Task<object?> RebuildCorridorAsync(JsonObject? p)
  {
    var name = PluginRuntime.GetRequiredString(p, "name");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, db, tr) => { var c = FindByName(civilDoc, tr, name); c.UpgradeOpen(); c.Rebuild(); return new { success = true, rebuilt = name }; });
  }

  public static Task<object?> GetCorridorSurfacesAsync(JsonObject? p) => Task.FromResult<object?>(new { status = "planned" });
  public static Task<object?> GetCorridorFeatureLinesAsync(JsonObject? p) => Task.FromResult<object?>(new { status = "planned" });
  public static Task<object?> ComputeCorridorVolumesAsync(JsonObject? p) => Task.FromResult<object?>(new { status = "planned" });

  private static Corridor FindByName(CivilDocument cd, Transaction tr, string name)
  {
    foreach (ObjectId id in cd.CorridorCollection)
    {
      var c = tr.GetObject(id, OpenMode.ForRead) as Corridor;
      if (c != null && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return c;
    }
    throw new JsonRpcDispatchException("CIVIL3D.NOT_FOUND", $"Corridor '{name}' not found.");
  }
}
