namespace Aictiq.Modules.WorkItems.Domain;

/// <summary>
/// The parent/child type matrix. The database trigger <c>work.check_item_parent_type()</c>
/// is the guarantee; this is the same table for friendly validation, the CSV importer and
/// type changes, so the three cannot drift apart.
/// </summary>
public static class ItemHierarchy
{
    /// <summary>Whether a <paramref name="child"/> may sit directly beneath a <paramref name="parent"/>.</summary>
    public static bool Allows(WorkItemType parent, WorkItemType child) => child switch
    {
        WorkItemType.Feature => parent == WorkItemType.Epic,
        // A backlog item and a bug may hang off an Epic directly; a Feature layer is optional.
        WorkItemType.Story => parent is WorkItemType.Epic or WorkItemType.Feature,
        WorkItemType.Bug => parent is WorkItemType.Epic or WorkItemType.Feature or WorkItemType.Story,
        WorkItemType.Task => parent is WorkItemType.Story or WorkItemType.Bug,
        _ => false,
    };

    /// <summary>Stories and Bugs may stand alone; Features and Tasks need a parent; Epics never have one.</summary>
    public static bool RequiresParent(WorkItemType type) => type is WorkItemType.Feature or WorkItemType.Task;
}
