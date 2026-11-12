# RTX Lens for Xeneon Edge

Import rtxlens.icuewidget into iCUE and assign RTX Lens to a Xeneon Edge slot. The service must run locally on port 59273. Configure backend plugins and hardware in the RTX Lens desktop app; the desktop app does not need to remain open.

L horizontal (1688x696) shows two independent panels, XL horizontal (2536x696) three. Expansion requires the larger slot assigned in iCUE. Small slots show the configured starting panel; header arrows and optional automatic paging navigate hidden panels. There is no widget footer or comparison caption below the chart. The comparison connector remains selectable in settings and is included in the chart's accessible description.

All settings fit two scrollable categories. Panels and telemetry contains connection-independent display settings and three numbered panel sections. Widget Personalization is second and exposes iCUE's Custom Style toggle. Enable that toggle to apply the widget colours. Theme selects Dark, Light or Custom colours; the visual style is always Neon, with configurable glow and optional HUD scan lines.

The font-family option applies to every element and defaults to Consolas at weight 700. Default sizes are panel titles 110%, metric values 120%, units 115%, pin labels/values 130%, axis 140%, headroom/PSU comparison 140%. Metric labels default to 130% and icons to 140%. Role size sliders remain available; weight and letter spacing are fixed. Current spread defaults to #22edff, headroom to #6bffbb and PSU comparison to #00ffce. Bar rounding defaults to 15% of bar width, spacing to 50%, width to 70%. Connected status is green. Missing or stale readings use a regular dash.

Devices are selected from the reporting sensors automatically. WireView/Astral comparison overlays each select MSI Connector 1 or 2 independently. Status rows use a fixed order. Custom titles render as text, never HTML.

All settings apply live and affect presentation only. Headroom uses the backend/device protection limit, never the chart maximum. Current uses a fixed display range; voltage spans 11.4-12.6 V and fills upwards. Per-pin power uses one sixth of detected cable capability, or the configured display fallback when capability is unknown. A fallback is not a detected cable rating. Temperature warnings are display-only.

Decimal places is a dropdown at the top of Panels and telemetry: 0, 1 (default), or 2. It enforces the selected digit count, including trailing zeros: 0.4 A becomes 0.40 A when 2 is selected. Existing numeric preferences are retained. This controls measurement values, including power and temperatures; clocks, RPM and percentages remain whole numbers, and axis ticks retain the precision needed to label the scale accurately.

Bars update immediately with telemetry. Bar fill edges are square and clipped by the rounded track, so only edges touching its corners follow the configured radius.

MSI telemetry is limited to the service's published pin currents, temperature and fan readings. Ai1600TS identification is supported; Ai1300TS hardware verification alone does not prove Ai1600TS hardware behaviour. No unverified PSU voltage, power, efficiency or protection feature is implied. Logical P1-P6 numbering does not prove physical conductor orientation or cable loss.

Touch A/W/V buttons temporarily change the local metric until the next iCUE property update. Use the settings to persist the choice. Peak reset is available beside those buttons only when peak hold is enabled. Peak history is local, bounded and cleared on reload.

Optional backgrounds are local PNG/JPEG/GIF/WebP/SVG/BMP images, with iCUE crop, scale, position and rotation. Video formats are rejected. Missing images fall back to the configured colour without blocking telemetry. Font files are not bundled; select a locally installed family.

The package targets Windows iCUE/Xeneon Edge and service schema 1. The package ID differs from the original WireView/Astral widgets. Native iCUE/device import testing remains separate from browser verification.

Astral/WireView pin charts contain no summary tiles. GPU status shows connector Power, Total current and Voltage from the active backend source profile. Configure Automatic or WireView priorities in the desktop app under Configuration > Source selections; the widget remains read-only. Automatic falls back independently for each metric and returns to higher-priority sources when they recover. MSI is a current-only source; Nvidia voltage can use its connector power/current ratio when direct voltage is unavailable. GPU board power is never used. Older backend versions retain the legacy source behavior. GPU usage is a badge beside the GPU status title; GPU board power is omitted. MSI summaries retain their fixed defaults. Primary metric and per-panel accent overrides are removed. Headroom and comparison values use small rectangular badges, numeric units are vertically centred, and chart controls/current spread use more space. Display-only warning defaults are 81 C for GPU core and 85 C for memory.


Units use static CSS layout with no glyph measurement or repositioning. Numeric updates reuse text nodes and unit elements. Pin values default to 130%; the larger spread row reads Max spread, Pn High and Pn Low. Astral comparison values map PSU pins P6-P1 beneath chart positions P1-P6. WireView comparisons and standalone MSI charts retain P1-P6 order. This mapping affects presentation only.

Summary tiles use equal widths and one value/unit size, with larger labels, more left padding and the same border/surface as status rows. There is no hero metric or hero-size control. Summary connector power is labelled Power and PSU connector current is labelled Current. The enlarged GPU usage badge sits immediately before Connected. Spread reads value followed by A Max Spread; its suffix matches High/Low typography, and its outline/separators match the summary tiles without moving or resizing the spread row.

Double tap the widget outside buttons to view backend configuration. Mouse double click or Enter when the widget is focused also opens the page. The page fetches the current configuration, displays plugin sections separated by horizontal rules, and scrolls within the allocated slot. Available WireView Pro II device settings are also read. It is read-only; the red notice at the bottom directs changes to the desktop app. Close or Escape returns to live metrics. Unavailable service responses show an error and Retry button.

Under Panels and telemetry, Warning flyout preview selects simulated individual warnings or combined electrical/temperature examples. Previews are labelled SIMULATED and do not change real sensor data or service settings. Select Off to close the preview, or change the selection to show another example. Active live warnings are separately labelled during previews. Live warning flyouts can be dismissed; they reopen on a new incident or after recovery and recurrence. Show service alerts controls automatic live flyouts and the banner.

GPU usage has reserved width through 100%. Clocks and fan RPM show no thousands separator. Hidden panels and disabled chart/status features are not mounted; dashboard updates pause behind a flyout while telemetry stays connected.


## Software updates

Automatically check for updates defaults to enabled, with Daily scheduling. Weekly and Monthly are also available. When enabled, the widget checks on startup and while running at the selected interval. Disabling the setting stops automatic checks; manual checks remain available by double tapping the widget and choosing Check for software updates.

An update badge opens a separate update page with installed/available versions, View release, Remind me later (24 hours) and Skip this version. These notices do not use the hardware warning policy. Schedule state and dismissals are saved per widget instance when local storage is available.

View release opens the system browser using the required iCUE Link Provider. Download and import the new widget manually. Update checks resolve the latest published stable GitHub release and read version.json from its tag; they do not poll development branches. Offline checks never block telemetry.
