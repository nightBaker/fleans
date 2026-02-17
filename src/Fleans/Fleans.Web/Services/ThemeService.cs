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
