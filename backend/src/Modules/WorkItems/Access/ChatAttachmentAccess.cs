using Aictiq.Modules.WorkItems.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;

namespace Aictiq.Modules.WorkItems.Access;

/// <summary>
/// Who may read a file attached to a project-chat question: its uploader, whose private chat
/// it is, and the live project-chat run answering that same person in that same project. No
/// project role widens it - not an admin, not another member, not another run's agent.
/// </summary>
public static class ChatAttachmentAccess
{
    public static async Task<bool> CanReadAsync(Attachment attachment, ICurrentUser user, IProjectChatGrants grants, CancellationToken ct)
    {
        if (attachment.ChatMessageId is null) return true;
        if (attachment.UploadedBy == user.UserId) return true;
        if (!user.Scopes.Contains(Scopes.ProjectChat, StringComparer.Ordinal) || user.TokenId is not { } tokenId) return false;
        var grant = await grants.FindAsync(tokenId, ct);
        return grant is not null && grant.ProjectId == attachment.ProjectId && grant.RequestedBy == attachment.UploadedBy;
    }
}
