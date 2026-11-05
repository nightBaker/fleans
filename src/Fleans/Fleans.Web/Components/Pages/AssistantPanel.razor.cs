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
    public AssistantChat Conversation { get; set; } = null!;

    /// <summary>The active tab's XML, or <c>null</c> for a blank diagram.</summary>
    [Parameter, EditorRequired]
    public Func<Task<string?>> GetCurrentXml { get; set; } = null!;

    [Parameter] public EventCallback<string> OnOpenInNewTab { get; set; }

    /// <summary>Loads the XML into the active tab (resets its undo history); asked for confirmation first.</summary>
    [Parameter] public EventCallback<string> OnReplaceCurrent { get; set; }

    [Parameter] public EventCallback OnClose { get; set; }

    /// <summary>The tab's stored chat changed (#1017): started, switched or deleted; the editor persists the tab.</summary>
    [Parameter] public EventCallback OnChatChanged { get; set; }

    /// <summary>The editor finished booting and is in Modeling mode, so its diagram can be read and replaced.</summary>
    [Parameter] public bool Ready { get; set; }

    private string? draft;
    private bool busy;
    private readonly StringBuilder streaming = new();
    private CancellationTokenSource? cts;
    private string? confirmReplaceXml;

    // Stored chats (#1017): shown only when the backend keeps them.
    private bool storesChats;
    private bool loadingChat;
    private bool chatListOpen;
    private bool confirmDelete;
    private List<AssistantConversationInfo>? chats;
    private AssistantError? chatsError;
    private readonly CancellationTokenSource lifetime = new();

    private bool CanSend => Ready && !busy && !loadingChat && !string.IsNullOrWhiteSpace(draft);

    private bool ChatIsEmpty => Conversation.Entries.Count == 0 && Conversation.ConversationId is null;

    protected override async Task OnInitializedAsync()
    {
        try
        {
            storesChats = await Assistant.StoresChatsAsync(lifetime.Token);
            // A tab restored after a reload knows its chat's id, not its turns.
            if (storesChats && Conversation is { ConversationId: { } id, Entries.Count: 0 })
            {
                loadingChat = true;
                StateHasChanged();
                await Assistant.OpenChatAsync(Conversation, id, lifetime.Token);
                if (Conversation.ConversationId is null)
                    await OnChatChanged.InvokeAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // The panel was closed or the tab switched.
        }
        finally
        {
            loadingChat = false;
        }
    }

    private async Task ToggleChatListAsync()
    {
        chatListOpen = !chatListOpen;
        confirmDelete = false;
        if (!chatListOpen) return;
        chats = null;
        chatsError = null;
        try
        {
            var (page, error) = await Assistant.ListChatsAsync(lifetime.Token);
            chats = page?.Items;
            chatsError = error;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task OpenChatAsync(string conversationId)
    {
        chatListOpen = false;
        if (conversationId == Conversation.ConversationId && Conversation.Entries.Count > 0) return;
        loadingChat = true;
        confirmReplaceXml = null;
        try
        {
            await Assistant.OpenChatAsync(Conversation, conversationId, lifetime.Token);
            await OnChatChanged.InvokeAsync();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            loadingChat = false;
        }
    }

    private async Task NewChatAsync()
    {
        chatListOpen = false;
        confirmDelete = false;
        confirmReplaceXml = null;
        Conversation.Reset(null);
        await OnChatChanged.InvokeAsync();
    }

    private async Task ConfirmDeleteAsync()
    {
        confirmDelete = false;
        try
        {
            var error = await Assistant.DeleteChatAsync(Conversation, lifetime.Token);
            if (error is not null)
            {
                Conversation.LastError = error;
                return;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        chats = null;
        await OnChatChanged.InvokeAsync();
    }

    private string ChatTitle(AssistantConversationInfo chat) =>
        string.IsNullOrWhiteSpace(chat.Title) ? L["Chats.Untitled"] : chat.Title;

    private async Task SendAsync()
    {
        if (!CanSend) return;
        var text = draft!.Trim();
        draft = null;
        busy = true;
        confirmReplaceXml = null;
        streaming.Clear();
        cts = new CancellationTokenSource();
        var chatBefore = Conversation.ConversationId;
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
        if (Conversation.ConversationId != chatBefore)
            await OnChatChanged.InvokeAsync();
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
        AssistantErrorCodes.ConversationNotFound => L["Error.ConversationNotFound"],
        _ => L["Error.Other"],
    };

    public void Dispose()
    {
        cts?.Cancel();
        // Not disposed: a handler still running after the panel closed reads its token and sees it cancelled.
        lifetime.Cancel();
    }

    [LoggerMessage(EventId = 13078, Level = LogLevel.Error, Message = "The assistant turn failed unexpectedly")]
    private partial void LogTurnFailed(Exception ex);
}
