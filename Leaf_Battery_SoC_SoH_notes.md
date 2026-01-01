# Nissan Leaf Battery SoC / SoH notes (derived from provided DBCs)

This file summarizes the most relevant frames/signals for battery State-of-Charge (SoC) and State-of-Health (SoH)
found in the uploaded DBCs:

- EV-can_AZE0.dbc (EV-CAN): SoC-related signals
- CAR-can_AZE0.dbc (Car-CAN): SoH + stored energy (GIDs)

## SoC (State of Charge)

### EV-CAN 0x1DB — `LB_Usable_SOC` (dash SoC)
- Message: 0x1DB (EV-CAN)
- Signal: `LB_Usable_SOC` (7 bits)
- DBC comment: “Contains SOC for dash… 1% resolution”
- Suggested decode:
  - `soc_percent = raw`  (clamp 0..100)

Use this for what the cluster/driver expects.

### EV-CAN 0x55B — `LB_SOC` (higher-resolution SoC, but verify scaling)
- Message: 0x55B (EV-CAN)
- Signal: `LB_SOC` (10 bits)
- DBC comment indicates **0.1% resolution**; however the DBC factor is 1.0 and the unit string is `%+1`,
  so treat this as “needs verification”.
- Suggested decode (most likely):
  - `soc_percent_fine = raw * 0.1`
  - clamp 0..100

If you log raw values at a known SoC (e.g., 50% displayed), you can confirm whether it needs *0.1*, *0.5*, or an offset.

## SoH (State of Health) + stored energy

### CAR-CAN 0x5B3 — `BatteryStateOfHealth` (SoH %)
- Message: 0x5B3 (Car-CAN)
- Signal: `BatteryStateOfHealth` (7 bits)
- DBC comment: “Confirmed to contain State-Of-Health (SOH)”
- Suggested decode:
  - `soh_percent = raw` (clamp 0..100)

### CAR-CAN 0x5B3 — `BatteryGIDS` (stored energy)
- Message: 0x5B3 (Car-CAN)
- Signal: `BatteryGIDS` (10 bits, unit “GIDs”)
- Common decoding convention:
  - `energy_Wh = gids * 80`
  - `energy_kWh = energy_Wh / 1000`

This is extremely useful for “remaining energy” displays and for range estimation.

## Useful derived values

### Remaining usable energy
- `remaining_kWh = gids * 80 / 1000`

### Estimated full usable capacity (from GIDs)
If you capture `gids_full` at (or very near) 100% charge:
- `full_kWh_est = gids_full * 80 / 1000`

### Capacity estimate from SoH (requires nominal pack size)
If you configure a nominal usable capacity for the pack (e.g., 24/30/40/62 kWh class), you can estimate:
- `capacity_kWh_est = nominal_kWh * (soh_percent / 100.0)`

In practice, **GIDs-based capacity** (above) tends to be more directly tied to what the car considers “stored energy”.

## Practical recommendation for ObdInsight UI
- Show **SoC (dash)** from `LB_Usable_SOC`.
- Show **SoH** from `BatteryStateOfHealth` (0x5B3, Car-CAN).
- Show **Remaining kWh** from `BatteryGIDS`.
- Optionally show **Fine SoC** from `LB_SOC` once you confirm scaling empirically.
