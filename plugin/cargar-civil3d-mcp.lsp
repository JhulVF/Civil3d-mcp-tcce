;; Carga automatica del plugin Civil 3D MCP (una sola vez por sesion de Civil 3D)
(vl-load-com)
(if (not (vl-bb-ref '*civil3d-mcp-cargado*))
  (progn
    (command "_.NETLOAD"
      "C:/Users/Juliana/dev/civil3d-mcp/plugin/Civil3dMcpPlugin/bin/Debug/net10.0-windows/Civil3dMcpPlugin.dll")
    (vl-bb-set '*civil3d-mcp-cargado* T)
    (princ "\nCivil 3D MCP cargado.")
  )
)
(princ)
