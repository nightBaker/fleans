using System.Text;
using Fleans.Assistant.Contracts;
using Fleans.Web.Services;
using Microsoft.AspNetCore.Components;

namespace Fleans.Web.Components.Pages;

public partial class AssistantPanel : IDisposable
{
    [Inject]
    private EditorAssistant Assistant { get; set; } = null!;

    [Inject]
    private ILogger<AssistantPanel> Logger { get; set; } = null!;

    /// <summary>The active tab's chat; the editor renders the panel with <c>@key</c> = tab id, so a
    /// tab switch disposes this instance and stops its request.</summary>
    [Parameter, EditorRequired]
    public AssistantConversation Conversation { get; set; } = null!;

    /// <summary>The active tab's XML, or <c>null</c> for a blank diagram.</summary>
    [Parameter, EditorRequired]
    public Func<Task<string?>> GetCurrentXml { get; set; } = null!;

    [Parameter] public EventCallback<string> OnOpenInNewTab { get; set; }

    /// <summary>Loads the XML into the active tab (resets its undo history); asked for confirmation first.</summary>
    [Parameter] public EventCallback<string> OnReplaceCurrent { get; set; }

    [Parameter] public EventCallback OnClose { get; set; }

    /// <summary>The editor finished booting and is in Modeling mode, so its diagram can be read and replaced.</summary>
    [Parameter] public bool Ready { get; set; }

    private string? draft;
    private bool busy;
    private readonly StringBuilder streaming = new();
    private CancellationTokenSource? cts;
    private string? confirmReplaceXml;

    private bool CanSend => Ready && !busy && !string.IsNullOrWhiteSpace(draft);

    private async Task SendAsync()
    {
        if (!CanSend) return;
        var text = draft!.Trim();
        draft = null;
        busy = true;
        confirmReplaceXml = null;
        streaming.Clear();
        cts = new CancellationTokenSource();
        try
        {
            var xml = await GetCurrentXml();
            await Assistant.RunTurnAsync(Conversation, text, xml,
                onText: delta =>
                {
                    streaming.Append(delta);
                    StateHasChanged();
                    return Task.CompletedTask;
                },
                onAttemptFinished: () =>
                {
                    streaming.Clear();
                    StateHasChanged();
                    return Task.CompletedTask;
                },
                cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Stopped by the user, or the panel was disposed (tab switch).
        }
        catch (Exception ex)
        {
            LogTurnFailed(ex);
            Conversation.LastError = new AssistantError("unexpected", ex.Message);
        }
        finally
        {
            busy = false;
            streaming.Clear();
            cts?.Dispose();
            cts = null;
        }
    }

    private void Stop() => cts?.Cancel();

    private async Task ConfirmReplaceAsync()
    {
        var xml = confirmReplaceXml;
        confirmReplaceXml = null;
        if (xml != null)
            await OnReplaceCurrent.InvokeAsync(xml);
    }

    private string ErrorTitle(AssistantError error) => error.Code switch
    {
        AssistantErrorCodes.Unauthorized => L["Error.Unauthorized"],
        AssistantErrorCodes.RateLimited => L["Error.RateLimited"],
        AssistantErrorCodes.Unavailable => L["Error.Unavailable"],
        AssistantErrorCodes.Timeout => L["Error.Timeout"],
        AssistantErrorCodes.InvalidProposal => L["Error.InvalidProposal", EditorAssistant.MaxAutomaticFixes],
        _ => L["Error.Other"],
    };

    public void Dispose()
    {
        cts?.Cancel();
    }

    [LoggerMessage(EventId = 13078, Level = LogLevel.Error, Message = "The assistant turn failed unexpectedly")]
    private partial void LogTurnFailed(Exception ex);
}
