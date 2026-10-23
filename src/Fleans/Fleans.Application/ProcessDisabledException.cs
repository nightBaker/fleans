namespace Fleans.Application;

/// <summary>
/// A start refused because the process is disabled (#863). An <see cref="InvalidOperationException"/>,
/// so the API answers 409; a call activity whose called process is disabled fails with it.
/// </summary>
public sealed class ProcessDisabledException(string processDefinitionKey)
    : InvalidOperationException($"Process '{processDefinitionKey}' is disabled. Enable it before creating new instances.");
