using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using DayManager.App.Services;
using DayManager.Core.Entities;

namespace DayManager.App.ViewModels;

/// <summary>对话页里的一条气泡。区分用户 / AI / 系统三类，系统消息是故障说明、不进气泡框。</summary>
public partial class ChatBubble : ObservableObject
{
    private ChatBubble()
    {
        Text = string.Empty;
    }

    public bool IsUser { get; private init; }
    public bool IsSystem { get; private init; }
    public string Text { get; private init; }

    /// <summary>这一轮 AI 实际改动的条目。没改数据就为空。</summary>
    public List<ActionOutcomeDto> Outcomes { get; private init; } = new();

    public Guid? BatchId { get; private init; }

    /// <summary>库里那条记录的 id。还没落库时是 Guid.Empty。删除按 id 删，不靠文本找。</summary>
    public Guid Id { get; private set; }

    /// <summary>这句话什么时候说的。库里存 UTC，显示前要 ToLocalTime。</summary>
    [ObservableProperty]
    public partial DateTimeOffset? CreatedAt { get; set; }

    /// <summary>系统提示是这一次运行的状态，不显示时间。</summary>
    public bool HasTime => !IsSystem && CreatedAt.HasValue;

    public string TimeText => CreatedAt?.ToLocalTime().ToString("HH:mm") ?? string.Empty;

    /// <summary>这一条上面要不要顶一根日期条（每天只有最旧的一条顶）。</summary>
    [ObservableProperty]
    public partial bool ShowDayDivider { get; set; }

    [ObservableProperty]
    public partial string DayDividerText { get; set; } = string.Empty;

    public void SetDayDivider(bool show, string text)
    {
        ShowDayDivider = show;
        DayDividerText = text;
    }

    partial void OnCreatedAtChanged(DateTimeOffset? value)
    {
        OnPropertyChanged(nameof(HasTime));
        OnPropertyChanged(nameof(TimeText));
    }

    /// <summary>落库之后把 id 和时间补回来（先显示后落库，避免干等二三十秒）。</summary>
    public void AttachRecord(ChatMessage record)
    {
        Id = record.Id;
        CreatedAt = record.CreatedAt;
    }

    [ObservableProperty]
    public partial bool IsUndone { get; set; }

    public bool HasOutcomes => Outcomes.Count > 0;

    /// <summary>对齐方式：你说的靠右，AI 靠左，系统提示居中且不装进气泡。</summary>
    public LayoutOptions Align =>
        IsUser ? LayoutOptions.End
        : IsSystem ? LayoutOptions.Center
        : LayoutOptions.Start;

    public bool ShowAsBubble => !IsSystem;

    public bool ShowAvatar => !IsUser && !IsSystem;

    public bool ShowMyAvatar => IsUser;

    /// <summary>气泡底色与文字色成对从 AppColors 取，主题一变两个一起变。</summary>
    public Color BubbleColor => IsUser ? AppColors.BubbleSelf : AppColors.BubbleAi;

    public Color BubbleTextColor => AppColors.OnBubble;

    /// <summary>没被撤回、且确实有改动时才显示撤回按钮。</summary>
    public bool CanUndo => BatchId is not null && !IsUndone;

    public string UndoText => IsUndone ? "已撤回" : $"撤回这 {Outcomes.Count} 条改动";

    public void MarkUndone() => IsUndone = true;

    /// <summary>主题换过后叫一次，让绑定重新来取 BubbleColor / BubbleTextColor。</summary>
    public void RefreshTheme()
    {
        OnPropertyChanged(nameof(BubbleColor));
        OnPropertyChanged(nameof(BubbleTextColor));
    }

    public static ChatBubble User(string text) =>
        new() { IsUser = true, Text = text, CreatedAt = DateTimeOffset.Now };

    public static ChatBubble System(string text) =>
        new() { IsSystem = true, Text = text };

    public static ChatBubble Ai(AgentTurn turn) =>
        new()
        {
            IsUser = false,
            Text = turn.Reply,
            Outcomes = turn.Outcomes,
            BatchId = turn.BatchId,
            CreatedAt = DateTimeOffset.Now
        };

    /// <summary>从库里的一条记录还原成气泡。明细 JSON 坏了就当没有，不影响整页显示。</summary>
    public static ChatBubble FromRecord(ChatMessage record)
    {
        var outcomes = new List<ActionOutcomeDto>();

        if (!string.IsNullOrWhiteSpace(record.OutcomesJson))
        {
            try
            {
                outcomes = JsonSerializer.Deserialize<List<ActionOutcomeDto>>(record.OutcomesJson)
                           ?? new List<ActionOutcomeDto>();
            }
            catch (JsonException)
            {
                // 明细坏了就当没有，气泡照常显示。
            }
        }

        return new ChatBubble
        {
            Id = record.Id,
            IsUser = record.Role == ChatMessage.RoleUser,
            Text = record.Text,
            Outcomes = outcomes,
            BatchId = record.BatchId,
            IsUndone = record.IsUndone,
            CreatedAt = record.CreatedAt
        };
    }
}
