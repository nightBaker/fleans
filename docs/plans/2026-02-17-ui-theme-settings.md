# UI Theme & Settings Page

## Goal

Add theme support using FluentUI Blazor's built-in `FluentDesignTheme` component and a new Settings page where users can choose their preferred theme mode and accent color.

## Design

### FluentDesignTheme in MainLayout

Place `<FluentDesignTheme>` in `MainLayout.razor` with `@bind-Mode`, `@bind-OfficeColor`, and `StorageName="theme"` for automatic localStorage persistence. Add the official `loading-theme.js` script and `<loading-theme>` element in `App.razor` `<head>` to prevent flash of unstyled content on page load.

### ThemeService

A scoped service (`Fleans.Web/Services/ThemeService.cs`) that holds `Mode` and `OfficeColor` state with an `OnChanged` event. MainLayout subscribes to `OnChanged` and calls `StateHasChanged()`. MainLayout also updates the service when bindings change (e.g. on initial load from localStorage). The Settings page reads from and writes to this service.

Properties:
- `DesignThemeModes Mode` (default: `System`)
- `OfficeColor? OfficeColor` (default: `null`)
- `event Action? OnChanged`
- `void SetTheme(DesignThemeModes mode, OfficeColor? officeColor)`

Registered in `Program.cs` as `builder.Services.AddScoped<ThemeService>()`.

### Settings Page

New page at `Fleans.Web/Components/Pages/Settings.razor`, route `/settings`.

- Uses `PageHeader` with title "Settings"
- Appearance section with two `FluentSelect` controls:
  - **Mode**: System, Light, Dark
  - **Office Color**: Default (null) + all 24 OfficeColor enum values
- On change, calls `ThemeService.SetTheme(...)` which triggers MainLayout update via `OnChanged` event

### Navigation

Add a Settings `FluentAppBarItem` to `NavMenu.razor` with `Icons.Regular.Size24.Settings` / `Icons.Filled.Size24.Settings`.

## Files Changed

- `Fleans.Web/Components/App.razor` — add loading-theme script
- `Fleans.Web/Components/Layout/MainLayout.razor` — add FluentDesignTheme, bind to ThemeService
- `Fleans.Web/Components/Layout/NavMenu.razor` — add Settings nav item
- `Fleans.Web/Components/Pages/Settings.razor` — new settings page
- `Fleans.Web/Services/ThemeService.cs` — new theme service
- `Fleans.Web/Program.cs` — register ThemeService
