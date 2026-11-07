using System.Text;
using Fleans.Assistant.Contracts;
using Fleans.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Fleans.Web.Components.Pages;

public partial class AssistantPanel : IDisposable
{
    [Inject]
    private EditorAssistant Assistant { get; set; } = null!;

    [Inject]
    private ILogger<AssistantPanel> Logger { get; set; } = null!;

    [Inject]
    private IJSRuntime JS { get; set; } = null!;

    /// <summary>The active tab's chat; the editor renders the panel with <c>@key</c> = tab id, so a
    /// tab switch disposes this instance and stops its request.</summary>
    [Parameter, EditorRequired]
    public AssistantChat Conversation { get; set; } = null!;

    /// <summary>The active tab's XML, or <c>null</c> for a blank diagram.</summary>
    [Parameter, EditorRequired]
    public Func<Task<string?>> GetCurrentXml { get; set; } = null!;

    /// <summary>The active tab's change history (#1024).</summary>
    [Parameter, EditorRequired]
    public AssistantChangeHistory History { get; set; } = null!;

    /// <summary>The browser rejected the last history write, so a reload would lose the newest entries.</summary>
    [Parameter] public bool HistoryNotSaved { get; set; }

    /// <summary>The panel's switch: a proposal that converts is applied without asking.</summary>
    [Parameter] public bool AutoApply { get; set; } = true;

    [Parameter] public EventCallback<bool> OnAutoApplyChanged { get; set; }

    [Parameter] public EventCallback<AssistantChat.Entry> OnOpenInNewTab { get; set; }

    /// <summary>Loads a reply's proposal into the active tab and records it in <see cref="History"/>.</summary>
    [Parameter] public EventCallback<AssistantChat.Entry> OnApply { get; set; }

    /// <summary>Brings back the revision with this number, recording that as a new one.</summary>
    [Parameter] public EventCallback<int> OnRestore { get; set; }

    [Parameter] public EventCallback OnUndo { get; set; }

    [Parameter] public EventCallback OnClose { get; set; }

    /// <summary>The tab's stored chat changed (#1017): started, switched or deleted; the editor persists the tab.</summary>
    [Parameter] public EventCallback OnChatChanged { get; set; }

    /// <summary>The editor finished booting and is in Modeling mode, so its diagram can be read and replaced.</summary>
    [Parameter] public bool Ready { get; set; }

    /// <summary>The active tab's process key; decides which private plugins the assistant hears about.</summary>
    [Parameter] public string? ProcessKey { get; set; }

    private string? draft;
    private bool busy;
    private readonly StringBuilder streaming = new();
    private CancellationTokenSource? cts;
    private bool historyOpen;

    // Stored chats (#1017): shown only when the backend keeps them.
    private bool storesChats;
    private bool loadingChat;
    private bool chatListOpen;
    private bool confirmDelete;
    private List<AssistantConversationInfo>? chats;
    private string? chatsCursor;
    private bool loadingMoreChats;
    private AssistantError? chatsError;
    private string? renamingChatId;
    private string? renameDraft;
    private readonly HashSet<string> cancellingJobs = new();
    private readonly CancellationTokenSource lifetime = new();

    // The chat whose event feed is being followed, while the panel is open.
    private string? watchedChatId;
    private CancellationTokenSource? watchCts;

    private bool CanSend => Ready && !busy && !loadingChat && !string.IsNullOrWhiteSpace(draft);

    private bool ChatIsEmpty => Conversation.Entries.Count == 0 && Conversation.ConversationId is null;

    // An answer carries the diagram, so it needs what a message needs, but no text.
    private bool CanDecide => Ready && !busy && !loadingChat;

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

    // Here rather than at each place the chat changes: starting, switching, deleting and losing a chat all end in a render.
    protected override void OnAfterRender(bool firstRender)
    {
        if (!storesChats || loadingChat || Conversation.ConversationId == watchedChatId)
            return;
        StopWatching();
        watchedChatId = Conversation.ConversationId;
        if (watchedChatId is not null)
        {
            watchCts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            _ = WatchAsync(watchedChatId, watchCts.Token);
        }
    }

    private async Task WatchAsync(string conversationId, CancellationToken ct)
    {
        try
        {
            await Assistant.WatchChatAsync(Conversation, conversationId, () => InvokeAsync(StateHasChanged), ct);
        }
        catch (Exception ex)
        {
            // Nothing awaits this task. The feed only adds updates, so the panel keeps working without it.
            LogWatchFailed(ex);
        }
    }

    private void StopWatching()
    {
        watchCts?.Cancel();
        watchCts?.Dispose();
        watchCts = null;
        watchedChatId = null;
    }

    private async Task ToggleChatListAsync()
    {
        chatListOpen = !chatListOpen;
        confirmDelete = false;
        renamingChatId = null;
        if (!chatListOpen) return;
        chats = null;
        chatsCursor = null;
        chatsError = null;
        try
        {
            var (page, error) = await Assistant.ListChatsAsync(lifetime.Token);
            chats = page?.Items;
            chatsCursor = page?.NextCursor;
            chatsError = error;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task LoadMoreChatsAsync()
    {
        loadingMoreChats = true;
        try
        {
            var (page, error) = await Assistant.ListChatsAsync(lifetime.Token, chatsCursor);
            if (page is not null)
            {
                chats?.AddRange(page.Items);
                chatsCursor = page.NextCursor;
            }
            if (error is not null)
                Conversation.LastError = error; // the chats already listed stay
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            loadingMoreChats = false;
        }
    }

    private void StartRename(AssistantConversationInfo chat)
    {
        renamingChatId = chat.ConversationId;
        renameDraft = chat.Title;
    }

    private async Task SaveRenameAsync()
    {
        if (renamingChatId is not { } id || string.IsNullOrWhiteSpace(renameDraft)) return;
        var title = renameDraft.Trim();
        try
        {
            var error = await Assistant.RenameChatAsync(id, title, lifetime.Token);
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
        var index = chats?.FindIndex(c => c.ConversationId == id) ?? -1;
        if (index >= 0)
            chats![index] = chats[index] with { Title = title };
        renamingChatId = null;
    }

    private async Task CancelJobAsync(string jobId)
    {
        cancellingJobs.Add(jobId);
        try
        {
            var error = await Assistant.CancelJobAsync(jobId, lifetime.Token);
            if (error is not null)
            {
                cancellingJobs.Remove(jobId);
                Conversation.LastError = error;
            }
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
        StopWatching(); // opening empties the chat, so its events are read again from the start
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
        await RunBusyAsync((xml, onText, onFinished, ct) =>
            Assistant.RunTurnAsync(Conversation, text, xml, onText, onFinished, ct, ProcessKey, RedrawAsync));

        // Disposed: the tab was switched, and the proposal belongs to the one left behind.
        if (!lifetime.IsCancellationRequested
            && AssistantChangeHistory.AutoApplyCandidate(Conversation, AutoApply, Ready) is { } reply)
            await OnApply.InvokeAsync(reply);
    }

    /// <summary>Run or Cancel on an approval card. Its answer holds no proposal, so nothing is applied afterwards.</summary>
    private async Task DecideAsync(AssistantApprovalRequest approval, bool approved)
    {
        if (!CanDecide) return;
        await RunBusyAsync((xml, onText, onFinished, ct) =>
            Assistant.DecideAsync(Conversation, approval, approved, xml, onText, onFinished, ct, ProcessKey, RedrawAsync));
    }

    private Task RedrawAsync()
    {
        StateHasChanged();
        return Task.CompletedTask;
    }

    /// <summary>One request to the chat endpoint, with the panel's progress display and its Stop button.</summary>
    private async Task RunBusyAsync(Func<string?, Func<string, Task>, Func<Task>, CancellationToken, Task> run)
    {
        busy = true;
        streaming.Clear();
        cts = new CancellationTokenSource();
        var chatBefore = Conversation.ConversationId;
        try
        {
            var xml = await GetCurrentXml();
            await run(xml,
                delta =>
                {
                    streaming.Append(delta);
                    StateHasChanged();
                    return Task.CompletedTask;
                },
                () =>
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

    private Task RevealMessageAsync(string proposalHash) =>
        JS.InvokeVoidAsync("bpmnEditor.revealAssistantMessage", proposalHash).AsTask();

    private string RevisionTitle(AssistantRevision revision) => revision.Kind switch
    {
        AssistantRevisionKind.Original => L["History.Original"],
        AssistantRevisionKind.Assistant => L["History.Assistant"],
        AssistantRevisionKind.Manual => L["History.Manual"],
        _ => L["History.Restored", revision.RestoredFrom ?? 0],
    };

    private string ErrorTitle(AssistantError error) => error.Code switch
    {
        AssistantErrorCodes.Unauthorized => L["Error.Unauthorized"],
        AssistantErrorCodes.RateLimited => L["Error.RateLimited"],
        AssistantErrorCodes.Unavailable => L["Error.Unavailable"],
        AssistantErrorCodes.Timeout => L["Error.Timeout"],
        AssistantErrorCodes.InvalidProposal => L["Error.InvalidProposal", EditorAssistant.MaxAutomaticFixes],
        AssistantErrorCodes.ConversationNotFound => L["Error.ConversationNotFound"],
        AssistantErrorCodes.ForbiddenAction => L["Error.ForbiddenAction"],
        AssistantErrorCodes.ApprovalExpired => L["Error.ApprovalExpired"],
        AssistantErrorCodes.ApprovalInvalid => L["Error.ApprovalInvalid"],
        AssistantErrorCodes.IntegrationUnavailable => L["Error.IntegrationUnavailable"],
        AssistantErrorCodes.JobFailed => L["Error.JobFailed"],
        AssistantErrorCodes.BudgetExceeded => L["Error.BudgetExceeded"],
        _ => L["Error.Other"],
    };

    private static string ActivityPill(string status) => status switch
    {
        AssistantActivityStatus.Completed => "orbit-pill--success",
        AssistantActivityStatus.Failed => "orbit-pill--danger",
        _ => "orbit-pill--accent",
    };

    private string ActivityStatus(string status) => status switch
    {
        AssistantActivityStatus.Started => L["Activity.Started"],
        AssistantActivityStatus.Completed => L["Activity.Completed"],
        AssistantActivityStatus.Failed => L["Activity.Failed"],
        _ => status,
    };

    private static string JobPill(string status) => status switch
    {
        AssistantJobStatus.Succeeded => "orbit-pill--success",
        AssistantJobStatus.Failed => "orbit-pill--danger",
        AssistantJobStatus.Running => "orbit-pill--accent",
        _ => "orbit-pill--draft",
    };

    private string JobStatus(string status) => status switch
    {
        AssistantJobStatus.Queued => L["Job.Queued"],
        AssistantJobStatus.Running => L["Job.Running"],
        AssistantJobStatus.Succeeded => L["Job.Succeeded"],
        AssistantJobStatus.Failed => L["Job.Failed"],
        AssistantJobStatus.Cancelled => L["Job.Cancelled"],
        _ => status,
    };

    public void Dispose()
    {
        cts?.Cancel();
        StopWatching();
        // Not disposed: a handler still running after the panel closed reads its token and sees it cancelled.
        lifetime.Cancel();
    }

    [LoggerMessage(EventId = 13078, Level = LogLevel.Error, Message = "The assistant turn failed unexpectedly")]
    private partial void LogTurnFailed(Exception ex);

    [LoggerMessage(EventId = 13093, Level = LogLevel.Warning, Message = "Following the assistant chat's events failed")]
    private partial void LogWatchFailed(Exception ex);
}
