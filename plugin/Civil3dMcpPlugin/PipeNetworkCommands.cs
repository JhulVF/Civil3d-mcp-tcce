using Autodesk.Civil.ApplicationServices;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DMcpPlugin;

public static class PipeNetworkCommands
{
  public static Task<object?> ListPipeNetworksAsync()
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var networks = new List<object>();
      foreach (ObjectId id in civilDoc.GetPipeNetworkIds())
      {
        var n = tr.GetObject(id, OpenMode.ForRead) as Network;
        if (n != null) networks.Add(new { name = n.Name, handle = n.Handle.ToString(), pipeCount = n.GetPipeIds().Count, structureCount = n.GetStructureIds().Count });
      }
      return new { networks };
    });
  }

  public static Task<object?> GetPipeNetworkAsync(JsonObject? p)
  {
    var name = PluginRuntime.GetRequiredString(p, "networkName");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var n = FindByName(civilDoc, tr, name);
      return new { name = n.Name, handle = n.Handle.ToString(), pipeCount = n.GetPipeIds().Count, structureCount = n.GetStructureIds().Count };
    });
  }

  public static Task<object?> GetPipeAsync(JsonObject? p) => Task.FromResult<object?>(new { status = "planned" });
  public static Task<object?> GetStructureAsync(JsonObject? p) => Task.FromResult<object?>(new { status = "planned" });
  public static Task<object?> CreatePipeNetworkAsync(JsonObject? p) => Task.FromResult<object?>(new { status = "planned" });
  public static Task<object?> AddPipeToNetworkAsync(JsonObject? p) => Task.FromResult<object?>(new { status = "planned" });
  public static Task<object?> AddStructureToNetworkAsync(JsonObject? p) => Task.FromResult<object?>(new { status = "planned" });
  public static Task<object?> CheckPipeNetworkInterferenceAsync(JsonObject? p) => Task.FromResult<object?>(new { status = "planned" });

  private static Network FindByName(CivilDocument cd, Transaction tr, string name)
  {
    foreach (ObjectId id in cd.GetPipeNetworkIds())
    {
      var n = tr.GetObject(id, OpenMode.ForRead) as Network;
      if (n != null && string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)) return n;
    }
    throw new JsonRpcDispatchException("CIVIL3D.NOT_FOUND", $"Pipe network '{name}' not found.");
  }
}
