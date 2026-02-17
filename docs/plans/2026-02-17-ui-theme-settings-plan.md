# UI Theme & Settings Page — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add FluentUI theme support (mode + office color) with a Settings page and localStorage persistence.

**Architecture:** `FluentDesignTheme` component in MainLayout handles rendering and auto-persistence via `StorageName`. A scoped `ThemeService` bridges MainLayout ↔ Settings page state. Flash-prevention script in App.razor head.

**Tech Stack:** FluentUI Blazor 4.13.2, `FluentDesignTheme`, `DesignThemeModes`, `OfficeColor`, Blazor Server (InteractiveServer)

---

### Task 1: Create ThemeService

**Files:**
- Create: `src/Fleans/Fleans.Web/Services/ThemeService.cs`

**Step 1: Create the service**

```csharp
using Microsoft.FluentUI.AspNetCore.Components;

namespace Fleans.Web.Services;

public class ThemeService
{
    public DesignThemeModes Mode { get; set; } = DesignThemeModes.System;
    public OfficeColor? OfficeColor { get; set; }

    public event Action? OnChanged;

    public void SetTheme(DesignThemeModes mode, OfficeColor? officeColor)
    {
        Mode = mode;
        OfficeColor = officeColor;
        OnChanged?.Invoke();
    }
}
```

**Step 2: Register in Program.cs**

Modify: `src/Fleans/Fleans.Web/Program.cs`

Add after the `builder.Services.AddFluentUIComponents();` line:

```csharp
builder.Services.AddScoped<ThemeService>();
```

Add using at top:

```csharp
using Fleans.Web.Services;
```

**Step 3: Build to verify**

Run: `dotnet build src/Fleans/Fleans.Web/`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add src/Fleans/Fleans.Web/Services/ThemeService.cs src/Fleans/Fleans.Web/Program.cs
git commit -m "feat: add ThemeService for shared theme state"
```

---

### Task 2: Add FluentDesignTheme to MainLayout

**Files:**
- Modify: `src/Fleans/Fleans.Web/Components/Layout/MainLayout.razor`

**Step 1: Update MainLayout.razor**

Replace the entire file content with:

```razor
@inherits LayoutComponentBase
@inject Fleans.Web.Services.ThemeService ThemeService
@implements IDisposable

<FluentDesignTheme @bind-Mode="@_mode"
                   @bind-OfficeColor="@_officeColor"
                   StorageName="theme" />

<FluentLayout>
    <FluentHeader>
        Fleans.Web
        <FluentSpacer />
    </FluentHeader>

    <FluentStack Orientation="Orientation.Horizontal" Width="100%">
        <NavMenu />

        <FluentBodyContent Style="padding: calc(var(--design-unit) * 1px) 0">
            @Body
        </FluentBodyContent>
    </FluentStack>
</FluentLayout>

<div id="blazor-error-ui" data-nosnippet>
    An unhandled error has occurred.
    <a href="." class="reload">Reload</a>
    <span class="dismiss">🗙</span>
</div>

@code {
    private DesignThemeModes _mode;
    private OfficeColor? _officeColor;

    protected override void OnInitialized()
    {
        _mode = ThemeService.Mode;
        _officeColor = ThemeService.OfficeColor;
        ThemeService.OnChanged += OnThemeChanged;
    }

    private void OnThemeChanged()
    {
        _mode = ThemeService.Mode;
        _officeColor = ThemeService.OfficeColor;
        InvokeAsync(StateHasChanged);
    }

    public void Dispose()
    {
        ThemeService.OnChanged -= OnThemeChanged;
    }
}
```

Key points:
- `@bind-Mode` and `@bind-OfficeColor` two-way bind to local fields
- `StorageName="theme"` auto-persists to localStorage
- Subscribes to `ThemeService.OnChanged` so Settings page changes propagate here
- `IDisposable` to unsubscribe from event

**Step 2: Build to verify**

Run: `dotnet build src/Fleans/Fleans.Web/`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Fleans/Fleans.Web/Components/Layout/MainLayout.razor
git commit -m "feat: add FluentDesignTheme to MainLayout with ThemeService binding"
```

---

### Task 3: Add flash-prevention script to App.razor

**Files:**
- Modify: `src/Fleans/Fleans.Web/Components/App.razor`

**Step 1: Add loading-theme script and element**

In `App.razor`, add these two lines inside `<head>`, after the `<base href="/">` line and before other stylesheets:

```html
<script src="_content/Microsoft.FluentUI.AspNetCore.Components/js/loading-theme.js"></script>
<loading-theme storage-name="theme"></loading-theme>
```

The `storage-name="theme"` must match the `StorageName="theme"` on `FluentDesignTheme`.

**Step 2: Build to verify**

Run: `dotnet build src/Fleans/Fleans.Web/`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Fleans/Fleans.Web/Components/App.razor
git commit -m "feat: add theme flash-prevention script to App.razor"
```

---

### Task 4: Add Settings nav item

**Files:**
- Modify: `src/Fleans/Fleans.Web/Components/Layout/NavMenu.razor`

**Step 1: Add Settings FluentAppBarItem**

Add a new `FluentAppBarItem` after the Editor item in `NavMenu.razor`:

```razor
<FluentAppBarItem Href="/settings"
                  Text="Settings"
                  IconRest="@(new Microsoft.FluentUI.AspNetCore.Components.Icons.Regular.Size24.Settings())"
                  IconActive="@(new Microsoft.FluentUI.AspNetCore.Components.Icons.Filled.Size24.Settings())" />
```

**Step 2: Build to verify**

Run: `dotnet build src/Fleans/Fleans.Web/`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Fleans/Fleans.Web/Components/Layout/NavMenu.razor
git commit -m "feat: add Settings nav item to NavMenu"
```

---

### Task 5: Create Settings page

**Files:**
- Create: `src/Fleans/Fleans.Web/Components/Pages/Settings.razor`

**Step 1: Create the Settings page**

```razor
@page "/settings"
@rendermode InteractiveServer
@inject Fleans.Web.Services.ThemeService ThemeService

<PageTitle>Settings</PageTitle>

<FluentStack Orientation="Orientation.Vertical" Gap="20px">
    <PageHeader Title="Settings" />

    <FluentStack Orientation="Orientation.Vertical" Gap="16px" Style="max-width: 400px; padding: 0 16px;">
        <FluentLabel Typo="Typography.Subject">Appearance</FluentLabel>

        <FluentSelect TOption="DesignThemeModes"
                      Label="Theme Mode"
                      Items="@_modeOptions"
                      OptionValue="@(m => m.ToString())"
                      OptionText="@(m => m.ToString())"
                      @bind-SelectedOption="@_selectedMode" />

        <FluentSelect TOption="OfficeColorOption"
                      Label="Accent Color"
                      Items="@_colorOptions"
                      OptionValue="@(o => o.Label)"
                      OptionText="@(o => o.Label)"
                      @bind-SelectedOption="@_selectedColor" />
    </FluentStack>
</FluentStack>

@code {
    private record OfficeColorOption(string Label, OfficeColor? Value);

    private static readonly List<DesignThemeModes> _modeOptions =
    [
        DesignThemeModes.System,
        DesignThemeModes.Light,
        DesignThemeModes.Dark
    ];

    private static readonly List<OfficeColorOption> _colorOptions = BuildColorOptions();

    private DesignThemeModes _selectedMode;
    private OfficeColorOption _selectedColor = null!;

    private DesignThemeModes SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (_selectedMode == value) return;
            _selectedMode = value;
            ThemeService.SetTheme(_selectedMode, _selectedColor.Value);
        }
    }

    private OfficeColorOption SelectedColor
    {
        get => _selectedColor;
        set
        {
            if (_selectedColor == value) return;
            _selectedColor = value;
            ThemeService.SetTheme(_selectedMode, _selectedColor.Value);
        }
    }

    protected override void OnInitialized()
    {
        _selectedMode = ThemeService.Mode;
        _selectedColor = _colorOptions.First(o => o.Value == ThemeService.OfficeColor);
    }

    private static List<OfficeColorOption> BuildColorOptions()
    {
        var options = new List<OfficeColorOption> { new("Default", null) };
        foreach (var color in Enum.GetValues<OfficeColor>())
        {
            options.Add(new(color.ToString(), color));
        }
        return options;
    }
}
```

Key points:
- Uses `@bind-SelectedOption` with backing properties that call `ThemeService.SetTheme()` on change
- `OfficeColorOption` record wraps nullable `OfficeColor?` so FluentSelect has a "Default" option
- Reads initial state from `ThemeService` on init

**Step 2: Build to verify**

Run: `dotnet build src/Fleans/Fleans.Web/`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Fleans/Fleans.Web/Components/Pages/Settings.razor
git commit -m "feat: add Settings page with theme mode and accent color controls"
```

---

### Task 6: Manual verification

**Step 1: Run the app**

Run: `dotnet run --project src/Fleans/Fleans.Aspire/`

**Step 2: Verify these behaviors**

1. Navigate to `/settings` — page loads with Mode=System, Color=Default
2. Change Mode to Dark — layout immediately switches to dark theme
3. Change Mode to Light — layout switches to light theme
4. Change accent color to Word (blue) — accent color updates across the app
5. Refresh the page — theme persists (localStorage via `StorageName`)
6. No white flash on page load (loading-theme script works)
7. Settings icon appears in the nav bar and highlights when on `/settings`
