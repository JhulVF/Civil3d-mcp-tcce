using System.Collections;
using System.Reflection;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace Civil3DMcpPlugin;

/// <summary>
/// Style editing (TCCE): object styles, display components, label styles and their components,
/// label sets, create/copy/rename/delete, import from a template DWG/DWT and assignment to objects.
/// Collections are addressed by their path under CivilDocument.Styles, e.g. "AlignmentStyles",
/// "SurfaceStyles", "LabelStyles.AlignmentLabelStyles.MajorStationLabelStyles",
/// "LabelSetStyles.AlignmentLabelSetStyles". Properties are addressed by reflection paths,
/// e.g. "ContourStyle.MajorInterval" or, for label components, "Text.Height".
/// </summary>
public static class StyleCommands
{
  // ════════════════════════════ Discovery ════════════════════════════

  public static Task<object?> ListCollectionsAsync(JsonObject? p)
  {
    var filter = PluginRuntime.GetOptionalString(p, "filter");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var found = new List<CollectionInfo>();
      Walk(civilDoc.Styles, "", 0, found, filter);
      return new { collections = found, count = found.Count };
    });
  }

  public static Task<object?> ListStylesAsync(JsonObject? p)
  {
    var path = PluginRuntime.GetRequiredString(p, "collection");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var coll = ResolveCollection(civilDoc, path);
      var styles = new List<object>();
      foreach (ObjectId id in coll)
        if (tr.GetObject(id, OpenMode.ForRead) is StyleBase sb)
          styles.Add(new { name = sb.Name, type = sb.GetType().Name, modified = SafeStr(() => sb.DateModified), modifiedBy = SafeStr(() => sb.ModifiedBy) });
      return new { collection = path, styles, count = styles.Count };
    });
  }

  public static Task<object?> GetStyleAsync(JsonObject? p)
  {
    var path = PluginRuntime.GetRequiredString(p, "collection");
    var name = PluginRuntime.GetRequiredString(p, "name");
    var depth = PluginRuntime.GetOptionalInt(p, "depth") ?? 2;
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var style = FindStyle(civilDoc, tr, path, name, OpenMode.ForRead);
      object? components = null, labelSet = null, labelProps = null;
      if (style is LabelStyle ls)
      {
        components = DescribeComponents(ls, tr, depth);
        labelProps = Inspect(ls.Properties, depth);
      }
      if (style is BaseLabelSetStyle set) labelSet = DescribeLabelSet(set, tr);
      return new
      {
        collection = path,
        name = style.Name,
        type = style.GetType().Name,
        properties = Inspect(style, depth),
        displayStyles = DescribeDisplayStyles(style),
        labelProperties = labelProps,
        components,
        labelSetItems = labelSet,
      };
    });
  }

  // ════════════════════════════ Create / copy / rename / delete ════════════════════════════

  public static Task<object?> CreateStyleAsync(JsonObject? p)
  {
    var path = PluginRuntime.GetRequiredString(p, "collection");
    var name = PluginRuntime.GetRequiredString(p, "name");
    var copyFrom = PluginRuntime.GetOptionalString(p, "copyFrom");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var coll = ResolveCollection(civilDoc, path);
      if (coll.Contains(name)) throw new JsonRpcDispatchException("CIVIL3D.ALREADY_EXISTS", $"Style '{name}' already exists in {path}.");
      ObjectId id;
      if (copyFrom != null)
      {
        var src = FindStyle(civilDoc, tr, path, copyFrom, OpenMode.ForWrite);
        id = src.CopyAsSibling(name);
      }
      else id = coll.Add(name);
      var st = (StyleBase)tr.GetObject(id, OpenMode.ForRead);
      return new { success = true, collection = path, name = st.Name, copiedFrom = copyFrom };
    });
  }

  public static Task<object?> RenameStyleAsync(JsonObject? p)
  {
    var path = PluginRuntime.GetRequiredString(p, "collection");
    var name = PluginRuntime.GetRequiredString(p, "name");
    var newName = PluginRuntime.GetRequiredString(p, "newName");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var st = FindStyle(civilDoc, tr, path, name, OpenMode.ForWrite);
      st.Name = newName;
      return new { success = true, collection = path, oldName = name, newName };
    });
  }

  public static Task<object?> DeleteStyleAsync(JsonObject? p)
  {
    var path = PluginRuntime.GetRequiredString(p, "collection");
    var name = PluginRuntime.GetRequiredString(p, "name");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var coll = ResolveCollection(civilDoc, path);
      if (!coll.Contains(name)) throw new JsonRpcDispatchException("CIVIL3D.NOT_FOUND", $"Style '{name}' not found in {path}.");
      coll.Remove(name);
      return new { success = true, collection = path, deleted = name };
    });
  }

  // ════════════════════════════ Edit properties ════════════════════════════

  /// changes: [{path, value}] applied to the style object (or to label-level Properties with path "Properties.…").
  public static Task<object?> SetStylePropertiesAsync(JsonObject? p)
  {
    var path = PluginRuntime.GetRequiredString(p, "collection");
    var name = PluginRuntime.GetRequiredString(p, "name");
    var changes = Changes(p);
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var st = FindStyle(civilDoc, tr, path, name, OpenMode.ForWrite);
      var results = changes.Select(c => ApplyChange(st, c.path, c.value)).ToList();
      return new { success = results.All(r => r.ok), style = st.Name, results };
    });
  }

  /// Edits display components (layer, color, linetype, lineweight, visibility, linetype scale).
  /// view: plan | model | profile | section | all ; components: names from get_style (or ["*"]).
  public static Task<object?> SetDisplayStyleAsync(JsonObject? p)
  {
    var path = PluginRuntime.GetRequiredString(p, "collection");
    var name = PluginRuntime.GetRequiredString(p, "name");
    var view = (PluginRuntime.GetOptionalString(p, "view") ?? "plan").ToLowerInvariant();
    var comps = (p?["components"] as JsonArray)?.Select(n => n!.GetValue<string>()).ToList() ?? new List<string> { "*" };
    var fields = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
    foreach (var k in new[] { "visible", "layer", "color", "linetype", "lineweight", "linetypeScale", "plotStyle" })
      if (p?[k] != null) fields[k] = p[k];
    if (fields.Count == 0) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give at least one of visible, layer, color, linetype, lineweight, linetypeScale, plotStyle.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var st = FindStyle(civilDoc, tr, path, name, OpenMode.ForWrite);
      var changed = new List<object>();
      foreach (var (viewName, comp, ds) in EnumerateDisplayStyles(st))
      {
        if (view != "all" && !viewName.Equals(view, StringComparison.OrdinalIgnoreCase)) continue;
        if (!comps.Contains("*") && !comps.Any(c => c.Equals(comp, StringComparison.OrdinalIgnoreCase))) continue;
        var errs = new List<string>();
        foreach (var f in fields)
        {
          try
          {
            switch (f.Key.ToLowerInvariant())
            {
              case "visible": ds.Visible = f.Value!.GetValue<bool>(); break;
              case "layer": EnsureLayer(db, tr, f.Value!.GetValue<string>()); ds.Layer = f.Value!.GetValue<string>(); break;
              case "color": ds.Color = ParseColor(f.Value!); break;
              case "linetype": ds.Linetype = f.Value!.GetValue<string>(); break;
              case "lineweight": ds.Lineweight = ParseLineWeight(f.Value!); break;
              case "linetypescale": ds.LinetypeScale = f.Value!.GetValue<double>(); break;
              case "plotstyle": ds.PlotStyle = f.Value!.GetValue<string>(); break;
            }
          }
          catch (Exception ex) { errs.Add($"{f.Key}: {ex.Message}"); }
        }
        changed.Add(new { view = viewName, component = comp, after = DescribeDisplay(ds), errors = errs });
      }
      if (changed.Count == 0) throw new JsonRpcDispatchException("CIVIL3D.NOT_FOUND", "No display component matched. Use get_style to see view/component names.");
      return new { success = true, style = st.Name, changed };
    });
  }

  // ════════════════════════════ Label style components ════════════════════════════

  /// component: component name (e.g. "Station Value"); changes: [{path:"Text.Height", value:0.1}, {path:"Text.Contents", value:"..."}, {path:"General.Visible", value:false}]
  public static Task<object?> SetLabelComponentAsync(JsonObject? p)
  {
    var path = PluginRuntime.GetRequiredString(p, "collection");
    var name = PluginRuntime.GetRequiredString(p, "name");
    var compName = PluginRuntime.GetRequiredString(p, "component");
    var changes = Changes(p);
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var ls = FindStyle(civilDoc, tr, path, name, OpenMode.ForWrite) as LabelStyle
        ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{name}' is not a label style.");
      var comp = FindComponent(ls, tr, compName, OpenMode.ForWrite);
      var results = changes.Select(c => ApplyChange(comp, c.path, c.value)).ToList();
      return new { success = results.All(r => r.ok), style = ls.Name, component = comp.Name, results, after = Inspect(comp, 2) };
    });
  }

  public static Task<object?> AddLabelComponentAsync(JsonObject? p)
  {
    var path = PluginRuntime.GetRequiredString(p, "collection");
    var name = PluginRuntime.GetRequiredString(p, "name");
    var compName = PluginRuntime.GetRequiredString(p, "component");
    var typeName = PluginRuntime.GetOptionalString(p, "componentType") ?? "Text";
    var changes = p?["changes"] != null ? Changes(p) : new List<(string path, JsonNode? value)>();
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var ls = FindStyle(civilDoc, tr, path, name, OpenMode.ForWrite) as LabelStyle
        ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{name}' is not a label style.");
      if (!Enum.TryParse<LabelStyleComponentType>(typeName, true, out var type))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"componentType must be one of {string.Join(", ", Enum.GetNames<LabelStyleComponentType>())}.");
      if (!ls.IsSupportedComponent(type))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{type} components are not supported by this label style.");
      var id = ls.AddComponent(compName, type);
      var comp = (LabelStyleComponent)tr.GetObject(id, OpenMode.ForWrite);
      var results = changes.Select(c => ApplyChange(comp, c.path, c.value)).ToList();
      return new { success = true, style = ls.Name, component = comp.Name, type = type.ToString(), results, after = Inspect(comp, 2) };
    });
  }

  public static Task<object?> RemoveLabelComponentAsync(JsonObject? p)
  {
    var path = PluginRuntime.GetRequiredString(p, "collection");
    var name = PluginRuntime.GetRequiredString(p, "name");
    var compName = PluginRuntime.GetRequiredString(p, "component");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var ls = FindStyle(civilDoc, tr, path, name, OpenMode.ForWrite) as LabelStyle
        ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{name}' is not a label style.");
      var comp = FindComponent(ls, tr, compName, OpenMode.ForRead);
      ls.RemoveComponent(comp.Name);
      return new { success = true, style = ls.Name, removed = comp.Name };
    });
  }

  // ════════════════════════════ Label sets ════════════════════════════

  /// Adds a label style to a label set. labelStyle is searched in labelStyleCollection (path) or anywhere under LabelStyles.
  public static Task<object?> LabelSetAddAsync(JsonObject? p)
  {
    var path = PluginRuntime.GetRequiredString(p, "collection");
    var name = PluginRuntime.GetRequiredString(p, "name");
    var labelStyle = PluginRuntime.GetRequiredString(p, "labelStyle");
    var lsColl = PluginRuntime.GetOptionalString(p, "labelStyleCollection");
    var inc = PluginRuntime.GetOptionalDouble(p, "increment");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var set = FindStyle(civilDoc, tr, path, name, OpenMode.ForWrite) as BaseLabelSetStyle
        ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{name}' is not a label set.");
      var lsId = FindLabelStyleId(civilDoc, tr, labelStyle, lsColl);
      set.Add(lsId);
      var items = Items(set);
      if (inc.HasValue && items.Count > 0) SetIncrement(items[^1], inc.Value);
      return new { success = true, labelSet = set.Name, items = DescribeLabelSet(set, tr) };
    });
  }

  public static Task<object?> LabelSetRemoveAsync(JsonObject? p)
  {
    var path = PluginRuntime.GetRequiredString(p, "collection");
    var name = PluginRuntime.GetRequiredString(p, "name");
    var index = PluginRuntime.GetRequiredInt(p, "index");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var set = FindStyle(civilDoc, tr, path, name, OpenMode.ForWrite) as BaseLabelSetStyle
        ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{name}' is not a label set.");
      if (index < 0 || index >= set.Count) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"index must be 0..{set.Count - 1}.");
      set.RemoveAt(index);
      return new { success = true, labelSet = set.Name, items = DescribeLabelSet(set, tr) };
    });
  }

  /// Edits one item: increment and/or swaps its label style (labelStyle name).
  public static Task<object?> LabelSetEditAsync(JsonObject? p)
  {
    var path = PluginRuntime.GetRequiredString(p, "collection");
    var name = PluginRuntime.GetRequiredString(p, "name");
    var index = PluginRuntime.GetRequiredInt(p, "index");
    var inc = PluginRuntime.GetOptionalDouble(p, "increment");
    var labelStyle = PluginRuntime.GetOptionalString(p, "labelStyle");
    var lsColl = PluginRuntime.GetOptionalString(p, "labelStyleCollection");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var set = FindStyle(civilDoc, tr, path, name, OpenMode.ForWrite) as BaseLabelSetStyle
        ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{name}' is not a label set.");
      var items = Items(set);
      if (index < 0 || index >= items.Count) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"index must be 0..{items.Count - 1}.");
      if (inc.HasValue) SetIncrement(items[index], inc.Value);
      if (labelStyle != null) items[index].LabelStyleId = FindLabelStyleId(civilDoc, tr, labelStyle, lsColl);
      return new { success = true, labelSet = set.Name, items = DescribeLabelSet(set, tr) };
    });
  }

  // ════════════════════════════ Import / assign ════════════════════════════

  /// Copies styles from a template .dwt/.dwg. names omitted → whole collection. conflict: Override (default) | Rename | Ignore.
  public static Task<object?> ImportStylesAsync(JsonObject? p)
  {
    var file = PluginRuntime.GetRequiredString(p, "filePath");
    var path = PluginRuntime.GetRequiredString(p, "collection");
    var names = (p?["names"] as JsonArray)?.Select(n => n!.GetValue<string>()).ToList();
    var conflictName = PluginRuntime.GetOptionalString(p, "conflict") ?? "Override";
    if (!File.Exists(file)) throw new JsonRpcDispatchException("CIVIL3D.NOT_FOUND", $"File not found: {file}");
    if (!Enum.TryParse<StyleConflictResolverType>(conflictName, true, out var conflict)) conflict = StyleConflictResolverType.Override;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, db, tr) =>
    {
      using var src = new Database(false, true);
      src.ReadDwgFile(file, FileOpenMode.OpenForReadAndAllShare, true, "");
      src.CloseInput(true);
      var srcCivil = CivilDocument.GetCivilDocument(src);
      var ids = new ObjectIdCollection();
      var imported = new List<string>();
      var missing = new List<string>();
      using (var str = src.TransactionManager.StartTransaction())
      {
        var coll = ResolveCollection(srcCivil, path);
        foreach (ObjectId id in coll)
        {
          var sb = (StyleBase)str.GetObject(id, OpenMode.ForRead);
          if (names == null || names.Any(n => n.Equals(sb.Name, StringComparison.OrdinalIgnoreCase))) { ids.Add(id); imported.Add(sb.Name); }
        }
        if (names != null) missing = names.Where(n => !imported.Any(i => i.Equals(n, StringComparison.OrdinalIgnoreCase))).ToList();
        if (ids.Count > 0) StyleBase.ExportTo(ids, db, conflict);
        str.Commit();
      }
      return new { success = ids.Count > 0, source = file, collection = path, imported, missing, conflict = conflict.ToString() };
    });
  }

  /// Sets StyleId on objects. target by handle(s) or by objectType + objectName (alignment, profile, surface, corridor, assembly, profileView, sampleLineGroup).
  public static Task<object?> AssignStyleAsync(JsonObject? p)
  {
    var styleName = PluginRuntime.GetRequiredString(p, "style");
    var collPath = PluginRuntime.GetOptionalString(p, "collection");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var results = new List<object>();
      foreach (var obj in ResolveTargets(civilDoc, db, tr, p))
      {
        var path = collPath ?? DefaultStyleCollection(obj);
        var coll = ResolveCollection(civilDoc, path);
        if (!coll.Contains(styleName)) throw new JsonRpcDispatchException("CIVIL3D.NOT_FOUND", $"Style '{styleName}' not found in {path}.");
        var prop = obj.GetType().GetProperty("StyleId", BindingFlags.Public | BindingFlags.Instance)
          ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{obj.GetType().Name} has no StyleId.");
        obj.UpgradeOpen();
        prop.SetValue(obj, coll[styleName]);
        results.Add(new { handle = obj.Handle.ToString(), type = obj.GetType().Name, name = NameOf(obj), style = styleName, collection = path });
      }
      return new { success = results.Count > 0, assigned = results };
    });
  }

  /// Applies a label set to alignments (replaces their labels with the set).
  public static Task<object?> AssignLabelSetAsync(JsonObject? p)
  {
    var setName = PluginRuntime.GetRequiredString(p, "labelSet");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, db, tr) =>
    {
      var coll = civilDoc.Styles.LabelSetStyles.AlignmentLabelSetStyles;
      if (!coll.Contains(setName)) throw new JsonRpcDispatchException("CIVIL3D.NOT_FOUND", $"Alignment label set '{setName}' not found.");
      var results = new List<string>();
      foreach (var obj in ResolveTargets(civilDoc, db, tr, p, defaultType: "alignment"))
      {
        if (obj is not Alignment al) continue;
        al.UpgradeOpen();
        al.ImportLabelSet(coll[setName]);
        results.Add(al.Name);
      }
      return new { success = results.Count > 0, labelSet = setName, alignments = results };
    });
  }

  // ════════════════════════════ Reflection engine ════════════════════════════

  private static readonly HashSet<string> SkipProps = new(StringComparer.Ordinal)
  {
    "UnmanagedObject", "AutoDelete", "IsDisposed", "Handle", "ObjectId", "OwnerId", "Database", "Id", "ClassID",
    "ExtensionDictionary", "XData", "AcadObject", "ObjectBirthVersion", "IsAProxy", "IsTransactionResident",
    "HasSaveVersionOverride", "IsModifiedGraphics", "IsModifiedXData", "IsUndoing", "IsNewObject", "IsNotifyEnabled",
    "IsWriteEnabled", "IsReadEnabled", "IsNotifying", "IsErased", "IsEraseStatusToggled", "IsReallyClosing", "IsCancelling",
    "IsModified", "Drawable", "DrawableType", "HasFields", "MergeStyle", "PaperOrientation", "Bounds", "Annotative",
  };

  private static object? Inspect(object? obj, int depth)
  {
    if (obj == null) return null;
    var dict = new SortedDictionary<string, object?>();
    foreach (var pi in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
    {
      if (pi.GetIndexParameters().Length > 0 || SkipProps.Contains(pi.Name)) continue;
      object? v;
      try { v = pi.GetValue(obj); } catch { continue; }
      var shown = Show(v, depth);
      if (shown != Skip) dict[pi.Name] = shown;
    }
    return dict;
  }

  private static readonly object Skip = new();

  private static object? Show(object? v, int depth)
  {
    switch (v)
    {
      case null: return null;
      case string or bool or int or long or short or uint or ushort or byte: return v;
      case double d: return Math.Round(d, 6);
      case Enum e: return e.ToString();
      case Color c: return ColorText(c);
      case ObjectId oid: return oid.IsNull ? null : oid.Handle.ToString();
      case Autodesk.AutoCAD.Geometry.Point3d p3: return new { p3.X, p3.Y, p3.Z };
      case Autodesk.AutoCAD.Geometry.Point2d p2: return new { p2.X, p2.Y };
    }
    var t = v.GetType();
    var valueProp = t.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
    if (valueProp != null && t.Namespace?.StartsWith("Autodesk.Civil") == true && valueProp.GetIndexParameters().Length == 0)
    {
      try { return Show(valueProp.GetValue(v), 0); } catch { return Skip; }
    }
    if (v is IEnumerable) return Skip;
    if (depth <= 0 || t.Namespace?.StartsWith("Autodesk.Civil") != true) return Skip;
    return Inspect(v, depth - 1);
  }

  private record ChangeResult(string path, bool ok, string? error, object? after);

  private static ChangeResult ApplyChange(object root, string path, JsonNode? value)
  {
    try
    {
      var parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
      object target = root;
      for (int i = 0; i < parts.Length - 1; i++)
      {
        var pi = Prop(target, parts[i]);
        target = pi.GetValue(target) ?? throw new InvalidOperationException($"'{parts[i]}' is null");
      }
      var last = Prop(target, parts[^1]);
      var current = last.GetValue(target);
      var valueProp = current?.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
      if (current != null && valueProp != null && valueProp.CanWrite && current.GetType().Namespace?.StartsWith("Autodesk.Civil") == true)
      {
        valueProp.SetValue(current, Convert(value, valueProp.PropertyType));
        return new ChangeResult(path, true, null, Show(valueProp.GetValue(current), 0));
      }
      if (!last.CanWrite) throw new InvalidOperationException($"'{parts[^1]}' is read-only");
      last.SetValue(target, Convert(value, last.PropertyType));
      return new ChangeResult(path, true, null, Show(last.GetValue(target), 0));
    }
    catch (Exception ex)
    {
      var msg = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException.Message : ex.Message;
      return new ChangeResult(path, false, msg, null);
    }
  }

  private static PropertyInfo Prop(object o, string name) =>
    o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
      .FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && p.GetIndexParameters().Length == 0)
    ?? throw new InvalidOperationException($"{o.GetType().Name} has no property '{name}'");

  private static object? Convert(JsonNode? v, Type t)
  {
    if (v == null) return null;
    var u = Nullable.GetUnderlyingType(t) ?? t;
    if (u == typeof(string)) return v.ToString().Trim('"');
    if (u == typeof(bool)) return v.GetValueKind() == System.Text.Json.JsonValueKind.String ? bool.Parse(v.GetValue<string>()) : v.GetValue<bool>();
    if (u == typeof(double)) return v.GetValueKind() == System.Text.Json.JsonValueKind.String ? double.Parse(v.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture) : v.GetValue<double>();
    if (u == typeof(int)) return (int)v.GetValue<double>();
    if (u == typeof(Color)) return ParseColor(v);
    if (u == typeof(LineWeight)) return ParseLineWeight(v);
    if (u.IsEnum)
    {
      if (v.GetValueKind() == System.Text.Json.JsonValueKind.Number) return Enum.ToObject(u, (int)v.GetValue<double>());
      return Enum.Parse(u, v.GetValue<string>(), true);
    }
    return System.Convert.ChangeType(v.ToString(), u, System.Globalization.CultureInfo.InvariantCulture);
  }

  private static List<(string path, JsonNode? value)> Changes(JsonObject? p)
  {
    var arr = p?["changes"] as JsonArray;
    if (arr != null && arr.Count > 0)
      return arr.Select(n => (n!["path"]!.GetValue<string>(), n["value"]?.DeepClone())).ToList();
    var single = PluginRuntime.GetOptionalString(p, "path");
    if (single != null) return new() { (single, p!["value"]?.DeepClone()) };
    throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give 'changes': [{path, value}] or 'path' + 'value'.");
  }

  // ── display styles via reflection: GetDisplayStylePlan/Model/Profile/Section(enum?) ──

  private static IEnumerable<(string view, string component, DisplayStyle ds)> EnumerateDisplayStyles(object style)
  {
    foreach (var m in style.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
    {
      if (!m.Name.StartsWith("GetDisplayStyle") || m.ReturnType != typeof(DisplayStyle)) continue;
      var view = m.Name.Substring("GetDisplayStyle".Length).ToLowerInvariant();
      var ps = m.GetParameters();
      if (ps.Length == 0)
      {
        DisplayStyle? ds = null;
        try { ds = (DisplayStyle?)m.Invoke(style, null); } catch { }
        if (ds != null) yield return (view, "(default)", ds);
      }
      else if (ps.Length == 1 && ps[0].ParameterType.IsEnum)
      {
        foreach (var ev in Enum.GetValues(ps[0].ParameterType))
        {
          DisplayStyle? ds = null;
          try { ds = (DisplayStyle?)m.Invoke(style, new[] { ev }); } catch { }
          if (ds != null) yield return (view, ev.ToString()!, ds);
        }
      }
    }
  }

  private static object? DescribeDisplayStyles(object style)
  {
    var list = EnumerateDisplayStyles(style).Select(x => new { x.view, x.component, display = DescribeDisplay(x.ds) }).ToList();
    return list.Count == 0 ? null : list;
  }

  private static object DescribeDisplay(DisplayStyle ds) => new
  {
    visible = SafeGet(() => ds.Visible),
    layer = SafeStr(() => ds.Layer),
    color = SafeStr(() => ColorText(ds.Color)),
    linetype = SafeStr(() => ds.Linetype),
    lineweight = SafeStr(() => ds.Lineweight.ToString()),
    linetypeScale = SafeGet(() => ds.LinetypeScale),
  };

  // ── label components ──

  private static List<object> DescribeComponents(LabelStyle ls, Transaction tr, int depth)
  {
    var list = new List<object>();
    foreach (LabelStyleComponentType type in Enum.GetValues<LabelStyleComponentType>())
    {
      ObjectIdCollection ids;
      try { ids = ls.GetComponents(type); } catch { continue; }
      foreach (ObjectId id in ids)
        if (tr.GetObject(id, OpenMode.ForRead) is LabelStyleComponent c)
          list.Add(new { name = c.Name, type = type.ToString(), settings = Inspect(c, depth) });
    }
    return list;
  }

  private static LabelStyleComponent FindComponent(LabelStyle ls, Transaction tr, string name, OpenMode mode)
  {
    foreach (LabelStyleComponentType type in Enum.GetValues<LabelStyleComponentType>())
    {
      ObjectIdCollection ids;
      try { ids = ls.GetComponents(type); } catch { continue; }
      foreach (ObjectId id in ids)
        if (tr.GetObject(id, mode) is LabelStyleComponent c && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return c;
    }
    throw new JsonRpcDispatchException("CIVIL3D.NOT_FOUND", $"Component '{name}' not found in label style '{ls.Name}'.");
  }

  // ── label sets ──

  private static List<BaseLabelSetItem> Items(BaseLabelSetStyle set)
  {
    var list = new List<BaseLabelSetItem>();
    var ge = set.GetType().GetMethod("GetEnumerator", Type.EmptyTypes);
    if (ge?.Invoke(set, null) is IEnumerator en)
      while (en.MoveNext()) if (en.Current is BaseLabelSetItem it) list.Add(it);
    return list;
  }

  private static void SetIncrement(BaseLabelSetItem item, double inc)
  {
    var pi = item.GetType().GetProperty("Increment") ?? throw new InvalidOperationException($"{item.GetType().Name} has no Increment.");
    pi.SetValue(item, inc);
  }

  private static List<object> DescribeLabelSet(BaseLabelSetStyle set, Transaction tr)
  {
    var list = new List<object>();
    var i = 0;
    foreach (var it in Items(set))
    {
      var incProp = it.GetType().GetProperty("Increment");
      list.Add(new
      {
        index = i++,
        labelStyle = SafeStr(() => it.LabelStyleName),
        labelType = SafeStr(() => it.LabelStyleType.ToString()),
        increment = incProp != null ? SafeGet(() => (double)incProp.GetValue(it)!) : null,
      });
    }
    return list;
  }

  private static ObjectId FindLabelStyleId(CivilDocument cd, Transaction tr, string name, string? collPath)
  {
    if (collPath != null)
    {
      var coll = ResolveCollection(cd, collPath);
      if (coll.Contains(name)) return coll[name];
      throw new JsonRpcDispatchException("CIVIL3D.NOT_FOUND", $"Label style '{name}' not found in {collPath}.");
    }
    var all = new List<CollectionInfo>();
    Walk(cd.Styles.LabelStyles, "LabelStyles", 1, all, null);
    var matches = new List<(string path, ObjectId id)>();
    foreach (var entry in all)
    {
      var coll = ResolveCollection(cd, entry.path);
      if (coll.Contains(name)) matches.Add((entry.path, coll[name]));
    }
    if (matches.Count == 1) return matches[0].id;
    if (matches.Count == 0) throw new JsonRpcDispatchException("CIVIL3D.NOT_FOUND", $"Label style '{name}' not found.");
    throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Label style '{name}' exists in several collections ({string.Join(", ", matches.Select(m => m.path))}); pass labelStyleCollection.");
  }

  // ── collections ──

  public record CollectionInfo(string path, int count);

  private static void Walk(object node, string prefix, int depth, List<CollectionInfo> found, string? filter)
  {
    if (depth > 5) return;
    foreach (var pi in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
    {
      if (pi.GetIndexParameters().Length > 0) continue;
      var pt = pi.PropertyType;
      var isColl = typeof(StyleCollectionBase).IsAssignableFrom(pt);
      var isNode = !isColl && pt.Namespace == "Autodesk.Civil.DatabaseServices.Styles" && (pt.Name.EndsWith("Root") || pt.Name.EndsWith("Node"));
      if (!isColl && !isNode) continue;
      object? v;
      try { v = pi.GetValue(node); } catch { continue; }
      if (v == null) continue;
      var path = string.IsNullOrEmpty(prefix) ? pi.Name : prefix + "." + pi.Name;
      if (isColl)
      {
        if (filter == null || path.Contains(filter, StringComparison.OrdinalIgnoreCase))
        {
          int count = 0; try { count = ((StyleCollectionBase)v).Count; } catch { }
          found.Add(new CollectionInfo(path, count));
        }
      }
      else Walk(v, path, depth + 1, found, filter);
    }
  }

  private static StyleCollectionBase ResolveCollection(CivilDocument cd, string path)
  {
    object cur = cd.Styles;
    foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
    {
      var pi = cur.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .FirstOrDefault(p => p.Name.Equals(part, StringComparison.OrdinalIgnoreCase) && p.GetIndexParameters().Length == 0)
        ?? throw new JsonRpcDispatchException("CIVIL3D.NOT_FOUND", $"Unknown style collection path '{path}' (at '{part}'). Use list_collections.");
      cur = pi.GetValue(cur) ?? throw new JsonRpcDispatchException("CIVIL3D.NOT_FOUND", $"'{part}' is empty.");
    }
    return cur as StyleCollectionBase ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{path}' is a group, not a style collection. Use list_collections.");
  }

  private static StyleBase FindStyle(CivilDocument cd, Transaction tr, string path, string name, OpenMode mode)
  {
    var coll = ResolveCollection(cd, path);
    if (!coll.Contains(name)) throw new JsonRpcDispatchException("CIVIL3D.NOT_FOUND", $"Style '{name}' not found in {path}. Use list_styles.");
    return (StyleBase)tr.GetObject(coll[name], mode);
  }

  // ── targets for assignment ──

  private static List<Autodesk.AutoCAD.DatabaseServices.DBObject> ResolveTargets(CivilDocument cd, Database db, Transaction tr, JsonObject? p, string? defaultType = null)
  {
    var list = new List<Autodesk.AutoCAD.DatabaseServices.DBObject>();
    if (p?["handles"] is JsonArray hs)
    {
      foreach (var h in hs)
      {
        var s = h!.GetValue<string>();
        if (!db.TryGetObjectId(new Handle(System.Convert.ToInt64(s, 16)), out var id) || id.IsErased)
          throw new JsonRpcDispatchException("CIVIL3D.NOT_FOUND", $"No object with handle '{s}'.");
        list.Add(tr.GetObject(id, OpenMode.ForRead));
      }
      return list;
    }
    var type = (PluginRuntime.GetOptionalString(p, "objectType") ?? defaultType ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give 'handles' or 'objectType' + 'objectName'.")).ToLowerInvariant();
    var names = (p?["objectNames"] as JsonArray)?.Select(n => n!.GetValue<string>()).ToList()
      ?? (PluginRuntime.GetOptionalString(p, "objectName") is string one ? new List<string> { one } : null);
    bool Wanted(string n) => names == null || names.Contains("*") || names.Any(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));

    IEnumerable<ObjectId> ids = type switch
    {
      "alignment" => cd.GetAlignmentIds().Cast<ObjectId>(),
      "surface" => cd.GetSurfaceIds().Cast<ObjectId>(),
      "corridor" => cd.CorridorCollection,
      "assembly" => cd.AssemblyCollection,
      "profile" => cd.GetAlignmentIds().Cast<ObjectId>().SelectMany(a => ((Alignment)tr.GetObject(a, OpenMode.ForRead)).GetProfileIds().Cast<ObjectId>()),
      "profileview" => cd.GetAlignmentIds().Cast<ObjectId>().SelectMany(a => ((Alignment)tr.GetObject(a, OpenMode.ForRead)).GetProfileViewIds().Cast<ObjectId>()),
      "samplelinegroup" => cd.GetAlignmentIds().Cast<ObjectId>().SelectMany(a => ((Alignment)tr.GetObject(a, OpenMode.ForRead)).GetSampleLineGroupIds().Cast<ObjectId>()),
      _ => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "objectType must be alignment, profile, profileView, surface, corridor, assembly or sampleLineGroup (or use handles)."),
    };
    foreach (var id in ids)
    {
      var o = tr.GetObject(id, OpenMode.ForRead);
      if (Wanted(NameOf(o))) list.Add(o);
    }
    if (list.Count == 0) throw new JsonRpcDispatchException("CIVIL3D.NOT_FOUND", $"No {type} matched {string.Join(", ", names ?? new List<string> { "*" })}.");
    return list;
  }

  private static string DefaultStyleCollection(object o) => o switch
  {
    Alignment => "AlignmentStyles",
    Profile => "ProfileStyles",
    ProfileView => "ProfileViewStyles",
    CivilSurface => "SurfaceStyles",
    Corridor => "CorridorStyles",
    Autodesk.Civil.DatabaseServices.Assembly => "AssemblyStyles",
    SampleLineGroup => "SampleLineStyles",
    SectionView => "SectionViewStyles",
    CogoPoint => "PointStyles",
    _ => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"No default style collection for {o.GetType().Name}; pass 'collection'."),
  };

  private static string NameOf(object o) => o.GetType().GetProperty("Name")?.GetValue(o) as string ?? "";

  // ── value helpers ──

  private static Color ParseColor(JsonNode v)
  {
    if (v.GetValueKind() == System.Text.Json.JsonValueKind.Number) return Color.FromColorIndex(ColorMethod.ByAci, (short)v.GetValue<double>());
    var s = v.GetValue<string>().Trim();
    if (s.Equals("bylayer", StringComparison.OrdinalIgnoreCase)) return Color.FromColorIndex(ColorMethod.ByLayer, 256);
    if (s.Equals("byblock", StringComparison.OrdinalIgnoreCase)) return Color.FromColorIndex(ColorMethod.ByBlock, 0);
    var parts = s.Split(',');
    if (parts.Length == 3) return Color.FromRgb(byte.Parse(parts[0]), byte.Parse(parts[1]), byte.Parse(parts[2]));
    if (short.TryParse(s, out var aci)) return Color.FromColorIndex(ColorMethod.ByAci, aci);
    var named = new Dictionary<string, short>(StringComparer.OrdinalIgnoreCase)
    { ["red"] = 1, ["yellow"] = 2, ["green"] = 3, ["cyan"] = 4, ["blue"] = 5, ["magenta"] = 6, ["white"] = 7, ["gray"] = 8, ["grey"] = 8, ["lightgray"] = 9 };
    if (named.TryGetValue(s, out var n)) return Color.FromColorIndex(ColorMethod.ByAci, n);
    throw new InvalidOperationException($"Color '{s}' not understood (use ACI number, 'r,g,b', ByLayer, ByBlock or a basic color name).");
  }

  private static LineWeight ParseLineWeight(JsonNode v)
  {
    if (v.GetValueKind() == System.Text.Json.JsonValueKind.Number)
    {
      var mm100 = (int)Math.Round(v.GetValue<double>() < 3 ? v.GetValue<double>() * 100 : v.GetValue<double>()); // 0.30 mm or 30
      return Enum.Parse<LineWeight>("LineWeight" + mm100.ToString("000"));
    }
    var s = v.GetValue<string>();
    if (Enum.TryParse<LineWeight>(s, true, out var lw)) return lw;
    if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
      return Enum.Parse<LineWeight>("LineWeight" + ((int)Math.Round(d < 3 ? d * 100 : d)).ToString("000"));
    throw new InvalidOperationException($"Lineweight '{s}' not understood (use ByLayer, ByBlock, 0.30 or LineWeight030).");
  }

  private static string ColorText(Color c)
  {
    if (c.IsByLayer) return "ByLayer";
    if (c.IsByBlock) return "ByBlock";
    if (c.ColorMethod == ColorMethod.ByAci) return c.ColorIndex.ToString();
    return $"{c.Red},{c.Green},{c.Blue}";
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

  private static string? SafeStr(Func<string?> f) { try { return f(); } catch { return null; } }
  private static T? SafeGet<T>(Func<T> f) where T : struct { try { return f(); } catch { return null; } }
}
