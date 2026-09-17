# Window geometry contract

PetGPT is Per-Monitor V2 aware. Geometry code distinguishes physical desktop
pixels from WPF device-independent pixels (DIPs):

- `ScreenPointPx` and `ScreenRectPx` are physical virtual-screen coordinates;
- `SizeDip` is a WPF layout size;
- `MonitorInfo` exposes physical monitor bounds/work area plus effective DPI;
- `WindowPlacement` stores a monitor identity and work-area-relative DIP
  offsets.

Monitor enumeration, work areas, cursor positions, and native window rectangles
enter `WindowPositionService` as physical pixels. Persisted offsets and WPF
sizes cross the boundary through `DipToPx`/`PxToDip` exactly once. Native
`SetWindowPos` applies physical rectangles; physical cursor deltas are never
added to WPF `Window.Left`/`Top` values.

## Restore and persistence

Restore first looks for the saved monitor identity. If it is missing, an old
global T2 coordinate is associated with the nearest monitor; otherwise the
primary monitor is the final fallback. Size and position are clamped to the
current physical work area with an 8-DIP safety margin. This supports negative
virtual-screen coordinates and taskbars on any edge, keeps the pet usable, and
keeps the chat title strip reachable. Oversized chat dimensions are reduced to
the available work area.

After placement, PetGPT persists the selected monitor ID and X/Y offsets from
that monitor's work-area origin in DIPs. Legacy/T2 settings with no monitor ID
are treated as best-effort global coordinates and immediately normalized after
a successful restore. Historical mixed-DPI intent cannot be reconstructed
exactly; safe nearest-monitor selection and clamping are the defined fallback.

## Chat modes

`FollowPet` remains the default and current effective mode. The bubble uses the
pet's current monitor, prefers above when it fits, otherwise tries below, and
then clamps to the work area.

`Free` restores and persists the chat window's own monitor-relative placement,
independently of the pet. The compatibility value `Remembered` is accepted as
the same free-placement behavior. T3 adds no settings UI for changing modes.

## Runtime changes and testing

WPF handles its Per-Monitor V2 DPI suggestion. PetGPT then performs only a
deferred clamp/persistence pass, avoiding double-scaling. Display configuration
and work-area changes are handled through Windows/WPF events; there is no
polling loop. During a drag, current pointer and window movement remain in
physical pixels; final placement is clamped, persisted, and flushed when the
drag or mouse capture ends.

Synthetic tests cover 100%, 150%, and 200% DPI, mixed monitor layouts, negative
coordinates, taskbars on each edge, missing monitors, oversized windows,
FollowPet/Free behavior, explicit conversions, drag deltas, and legacy/T2
recovery. These tests do not constitute real multi-monitor hardware validation.
