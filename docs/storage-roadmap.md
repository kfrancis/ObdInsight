# Storage roadmap

## Remembering settings / last adapter

- Implemented using `Microsoft.Maui.Storage.Preferences` via `AdapterAutoConnectService`.
- Stores:
  - last adapter address
  - last adapter name
  - last BLE profile name
  - auto-connect toggle

This is local-only and per-device.

## Recording values over time (SoH, etc.)

A good evolution path is:

1. **Local-first time series store** (SQLite) to enable offline capture and charts.
2. **Sync/backup** to a remote backend later.

If you specifically want **MySQL**:

- Treat MySQL as a *remote* store (or a LAN store) and avoid direct DB connections from the mobile app.
- Prefer a small API (ASP.NET minimal API) between the MAUI app and MySQL.
- Use authentication and TLS; do not embed DB credentials in the app.
- Define a simple schema:
  - `vehicle_sessions` (device id, vin, adapter id/name, start/end timestamps)
  - `metrics` (session id, metric type, numeric value, unit, timestamp)

When ready, we can introduce an interface in Core (e.g., `IVehicleMetricsStore`) with a SQLite implementation and later a remote implementation that calls your API.
