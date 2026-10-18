using Aictiq.SharedKernel.Domain;

namespace Aictiq.Modules.Automation.Domain;

/// <summary>The rules of a person's private project chat with the project's default agent.</summary>
public static class ProjectChat
{
    /// <summary>The built-in playbook every project-chat run follows. Not a project playbook: nobody edits or picks it.</summary>
    public const string PlaybookName = "Project chat";

    /// <summary>How many question/answer pairs a chat keeps; asking past it drops the oldest.</summary>
    public const int MaxPairs = 10;

    public const int MaxQuestionLength = 4000;

    /// <summary>The answer is the run's outcome summary, so it shares that limit.</summary>
    public const int MaxAnswerLength = Run.MaxOutcomeSummaryLength;

    public const int MaxErrorLength = 500;

    /// <summary>A question is answered in minutes; anything longer is a run gone astray.</summary>
    public const int MaxMinutes = 20;

    /// <summary>The harness when the project has no default playbook to borrow one from.</summary>
    public const string DefaultHarness = "claude";
}

/// <summary>Where one question stands. Only <see cref="Pending"/> has a live run.</summary>
public enum ProjectChatMessageStatus : short
{
    Pending = 0,
    Answered = 1,
    Failed = 2,
    Cancelled = 3,
    TimedOut = 4,
}

/// <summary>
/// One question a person asked in their project chat and, once its run ends, the answer or
/// what went wrong. A chat is the person's alone: every read and write is keyed on
/// (project, user), and nobody else - another member, an admin - is ever shown it.
/// </summary>
public sealed class ProjectChatMessage : TenantEntity
{
    public Guid ProjectId { get; init; }

    public required string UserId { get; init; }

    public required string Question { get; init; }

    public string? Answer { get; set; }

    public ProjectChatMessageStatus Status { get; set; }

    /// <summary>Why there is no answer, in words for the panel; null unless the run failed, was cancelled or timed out.</summary>
    public string? Error { get; set; }

    /// <summary>The run answering it. No FK: the run list and the chat are pruned on their own schedules.</summary>
    public Guid? RunId { get; set; }

    public DateTimeOffset AskedAt { get; init; }

    public DateTimeOffset? AnsweredAt { get; set; }

    public uint Version { get; set; }
}
