using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using App = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3DMcpPlugin;

/// <summary>
/// Handles drawing-level operations: info, settings, save, undo, redo,
/// listing object types, and getting selected object info.
/// </summary>
public static class DrawingCommands
{
  public static Task<object?> GetCivil3DHealthAsync()
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, db, tr) =>
    {
      return new
      {
        connected = true,
        drawingName = doc.Name,
        civil3dVersion = CivilApplication.ActiveProduct.ToString(),
      };
    });
  }

  public static Task<object?> GetDrawingInfoAsync()
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, db, tr) =>
    {
      return new
      {
        drawingName = doc.Name,
        drawingPath = doc.Database.Filename,
        coordinateSystem = civilDoc.Settings.DrawingSettings.UnitZoneSettings.CoordinateSystemCode,
        units = civilDoc.Settings.DrawingSettings.UnitZoneSettings.DrawingUnits.ToString(),
      };
    });
  }

  public static Task<object?> GetDrawingSettingsAsync()
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var settings = civilDoc.Settings.DrawingSettings;
      return new
      {
        coordinateSystem = settings.UnitZoneSettings.CoordinateSystemCode,
        drawingUnits = settings.UnitZoneSettings.DrawingUnits.ToString(),
        angularUnits = settings.UnitZoneSettings.AngularUnits.ToString(),
        scaleObjects = settings.ObjectLayerSettings.ToString(),
      };
    });
  }

  public static Task<object?> SaveDrawingAsync(JsonObject? parameters)
  {
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, db, tr) =>
    {
      db.SaveAs(db.Filename, DwgVersion.Current);
      return new { success = true, path = db.Filename };
    });
  }

  public static Task<object?> NewDrawingAsync(JsonObject? parameters)
  {
    return Task.FromResult<object?>(new
    {
      error = "newDrawing requires user interaction and is not fully supported via MCP."
    });
  }

  public static Task<object?> UndoDrawingAsync(JsonObject? parameters)
  {
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, db, tr) =>
    {
      doc.SendStringToExecute("UNDO 1\n", true, false, true);
      return new { success = true, action = "undo" };
    });
  }

  public static Task<object?> RedoDrawingAsync(JsonObject? parameters)
  {
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, db, tr) =>
    {
      doc.SendStringToExecute("REDO\n", true, false, true);
      return new { success = true, action = "redo" };
    });
  }

  public static Task<object?> ListCivilObjectTypesAsync()
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var types = new List<string>();

      if (civilDoc.GetSurfaceIds().Count > 0) types.Add("Surface");
      if (civilDoc.GetAlignmentIds().Count > 0) types.Add("Alignment");
      if (civilDoc.CorridorCollection.Count > 0) types.Add("Corridor");
      if (civilDoc.GetSiteIds().Count > 0) types.Add("Site/Parcel");

      // Always show available types even if none exist
      types.AddRange(new[]
      {
        "CogoPoint", "PointGroup", "Profile", "ProfileView",
        "PipeNetwork", "Structure", "Pipe",
        "SampleLine", "Section", "SectionView",
        "Assembly", "Subassembly", "FeatureLine", "Grading"
      });

      return types.Distinct().OrderBy(t => t).ToList();
    });
  }

  public static Task<object?> GetSelectedCivilObjectsInfoAsync(JsonObject? parameters)
  {
    var limit = PluginRuntime.GetOptionalInt(parameters, "limit") ?? 100;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var editor = doc.Editor;
      var selection = editor.SelectImplied();

      if (selection.Status != Autodesk.AutoCAD.EditorInput.PromptStatus.OK)
      {
        return new { objects = Array.Empty<object>(), message = "No objects selected." };
      }

      var objects = new List<object>();
      var count = 0;

      foreach (var id in selection.Value.GetObjectIds())
      {
        if (count >= limit) break;

        var obj = tr.GetObject(id, OpenMode.ForRead);
        objects.Add(new
        {
          handle = obj.Handle.ToString(),
          objectType = obj.GetType().Name,
          layer = (obj as Autodesk.AutoCAD.DatabaseServices.Entity)?.Layer ?? "Unknown",
        });
        count++;
      }

      return new { objects, total = selection.Value.Count };
    });
  }
}
