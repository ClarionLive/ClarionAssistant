using System.Collections.Generic;

namespace ClarionAssistant.Models
{
    // ── Task ────────────────────────────────────────────────

    public class KanbanTaskSummary
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string Status { get; set; }
        public string Priority { get; set; }
        public string Assignee { get; set; }
        public string CreatedBy { get; set; }
        public string ProjectId { get; set; }
        public List<string> Helpers { get; set; }
    }

    public class TaskDetail
    {
        public KanbanTaskSummary Task { get; set; }
        public ChecklistSummary ChecklistSummary { get; set; }
        public List<ChecklistItem> Checklist { get; set; }
        public string Plan { get; set; }
        public string ContinuationNotes { get; set; }
        public string ImplementationSummary { get; set; }
        public string TestResults { get; set; }
        public List<TaskRelationship> Relationships { get; set; }
        public List<TaskFileLink> FileLinks { get; set; }
    }

    public class ChecklistSummary
    {
        public int Total { get; set; }
        public int Done { get; set; }
        public int Coding { get; set; }
        public int Testing { get; set; }
        public int Pending { get; set; }
    }

    public class ChecklistItem
    {
        public string Item { get; set; }
        public string Status { get; set; }
        public bool Done { get; set; }
        public List<ChecklistNote> Notes { get; set; }
        public string AssignedTo { get; set; }
        public int CycleCount { get; set; }
    }

    public class ChecklistNote
    {
        public string Text { get; set; }
        public string By { get; set; }
        public string At { get; set; }
        public string Transition { get; set; }
    }

    public class TaskRelationship
    {
        public string RelatedTaskId { get; set; }
        public string Type { get; set; }
        public string Direction { get; set; }
    }

    public class TaskFileLink
    {
        public string FilePath { get; set; }
        public string AddedBy { get; set; }
        public string Description { get; set; }
    }

    // ── Terminal ─────────────────────────────────────────────

    public class TerminalInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string RegisteredAt { get; set; }
        public string LastActiveAt { get; set; }
        public bool IsConnected { get; set; }
        public string Color { get; set; }
        public string DocId { get; set; }
        public bool IsReady { get; set; }
    }

    // ── Team ─────────────────────────────────────────────────

    public class TeamMemberProfile
    {
        public string Name { get; set; }
        public string Role { get; set; }
        public string PreferredModel { get; set; }
        public List<string> Skills { get; set; }
        public bool IsOnline { get; set; }
    }

    // ── API Responses ────────────────────────────────────────

    public class ApiResult<T>
    {
        public bool Success { get; set; }
        public T Data { get; set; }
        public string Error { get; set; }
    }

    public class CreateTaskResponse
    {
        public string TaskId { get; set; }
        public KanbanTaskSummary Task { get; set; }
    }

    public class ChecklistTransitionResponse
    {
        public bool Success { get; set; }
        public string ItemName { get; set; }
        public string PreviousStatus { get; set; }
        public string NewStatus { get; set; }
        public int CycleCount { get; set; }
        public bool EscalationTriggered { get; set; }
    }

    // ── Knowledge Injection ───────────────────────────────

    public class KnowledgeInjectionResponse
    {
        public string Markdown { get; set; }
        public int EntryCount { get; set; }
    }

    // ── Session Lineage ───────────────────────────────────

    public class SessionRecap
    {
        public string SessionId { get; set; }
        public string AgentName { get; set; }
        public string Summary { get; set; }
        public string SessionType { get; set; }
        public string StartedAt { get; set; }
        public List<SessionMessage> RecentMessages { get; set; }
    }

    public class SessionMessage
    {
        public string Role { get; set; }
        public string Content { get; set; }
        public string Timestamp { get; set; }
    }
}
