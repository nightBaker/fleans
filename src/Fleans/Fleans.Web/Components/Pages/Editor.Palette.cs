namespace Fleans.Web.Components.Pages;

public partial class Editor
{
    // Left-rail palette. Keys match window.orbitElementCatalog (js/orbitContextMenu.js).
    private sealed record PaletteTile(string Key, string Label, string Title, string Icon);

    private static readonly IReadOnlyList<PaletteTile> PaletteTiles =
    [
        new("start", "Start", "Start event",
            """<svg viewBox="0 0 24 24" fill="none" style="stroke: var(--o-success)" stroke-width="1.6" aria-hidden="true"><circle cx="12" cy="12" r="8"/></svg>"""),
        new("end", "End", "End event",
            """<svg viewBox="0 0 24 24" fill="none" style="stroke: var(--o-danger)" stroke-width="3" aria-hidden="true"><circle cx="12" cy="12" r="8"/></svg>"""),
        new("user-task", "User task", "User task",
            """<svg viewBox="0 0 26 22" fill="none" style="stroke: var(--o-accentink)" stroke-width="1.6" aria-hidden="true"><rect x="2" y="3" width="22" height="16" rx="4"/><circle cx="13" cy="9.5" r="2.5"/><path d="M8.5 16c.6-2.2 2.4-3.5 4.5-3.5s3.9 1.3 4.5 3.5"/></svg>"""),
        new("service-task", "Service", "Service task",
            """<svg viewBox="0 0 26 22" fill="none" style="stroke: var(--o-accentink)" stroke-width="1.6" aria-hidden="true"><rect x="2" y="3" width="22" height="16" rx="4"/><circle cx="13" cy="11" r="2.2"/><path d="M13 6.5v1.6M13 13.9v1.6M8.5 11h1.6M15.9 11h1.6"/></svg>"""),
        new("script-task", "Script", "Script task",
            """<svg viewBox="0 0 26 22" fill="none" style="stroke: var(--o-accentink)" stroke-width="1.6" aria-hidden="true"><rect x="2" y="3" width="22" height="16" rx="4"/><path d="M8 8.5h10M8 11h7M8 13.5h9"/></svg>"""),
        new("call-activity", "Call", "Call activity",
            """<svg viewBox="0 0 26 22" fill="none" style="stroke: var(--o-accentink)" stroke-width="2.6" aria-hidden="true"><rect x="2" y="3" width="22" height="16" rx="4"/></svg>"""),
        new("exclusive-gateway", "XOR", "Exclusive gateway",
            """<svg viewBox="0 0 24 24" fill="none" style="stroke: var(--o-warn)" stroke-width="1.6" aria-hidden="true"><path d="M12 2 22 12 12 22 2 12Z"/><path d="m9 9 6 6M15 9l-6 6"/></svg>"""),
        new("parallel-gateway", "AND", "Parallel gateway",
            """<svg viewBox="0 0 24 24" fill="none" style="stroke: var(--o-warn)" stroke-width="1.6" aria-hidden="true"><path d="M12 2 22 12 12 22 2 12Z"/><path d="M12 7.5v9M7.5 12h9"/></svg>"""),
        new("timer", "Timer", "Timer intermediate event",
            """<svg viewBox="0 0 24 24" fill="none" style="stroke: var(--o-violet)" stroke-width="1.6" aria-hidden="true"><circle cx="12" cy="12" r="9"/><circle cx="12" cy="12" r="6.5"/><path d="M12 8.5V12l2 1.5"/></svg>"""),
        new("message", "Message", "Message intermediate event",
            """<svg viewBox="0 0 24 24" fill="none" style="stroke: var(--o-violet)" stroke-width="1.6" aria-hidden="true"><circle cx="12" cy="12" r="9"/><rect x="7" y="8.5" width="10" height="7" rx="1"/><path d="m7 9 5 3.5L17 9"/></svg>"""),
        new("subprocess", "Sub-process", "Expanded sub-process",
            """<svg viewBox="0 0 26 22" fill="none" style="stroke: var(--o-muted)" stroke-width="1.6" aria-hidden="true"><rect x="2" y="3" width="22" height="16" rx="3"/><rect x="10" y="13" width="6" height="5" rx="1"/><path d="M13 14.2v2.6M11.7 15.5h2.6"/></svg>"""),
        new("pool", "Pool", "Pool (drawn only: the first pool's process is what gets deployed)",
            """<svg viewBox="0 0 26 22" fill="none" style="stroke: var(--o-muted)" stroke-width="1.6" aria-hidden="true"><rect x="2" y="3" width="22" height="16" rx="1"/><path d="M7 3v16"/></svg>"""),
        new("data-object", "Data", "Data object (documentation only: the engine ignores it)",
            """<svg viewBox="0 0 24 24" fill="none" style="stroke: var(--o-muted)" stroke-width="1.6" aria-hidden="true"><path d="M6 3h8l4 4v14H6Z"/><path d="M14 3v4h4"/></svg>"""),
        new("annotation", "Comment", "Text annotation",
            """<svg viewBox="0 0 24 24" fill="none" style="stroke: var(--o-muted)" stroke-width="1.6" aria-hidden="true"><path d="M9 4H5v16h4"/><path d="M12 9h7M12 13h5"/></svg>""")
    ];
}
