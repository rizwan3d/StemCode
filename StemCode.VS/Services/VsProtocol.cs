namespace StemCode.VS.Services
{
    internal static class VsProtocol
    {
        public const string Ready = "ready";
        public const string SessionInfo = "session_info";
        public const string MessageChunk = "message_chunk";
        public const string UserMessageChunk = "user_message_chunk";
        public const string ReasoningChunk = "reasoning_chunk";
        public const string ToolCallStart = "tool_call_start";
        public const string ToolCallEnd = "tool_call_end";
        public const string PlanUpdate = "plan_update";
        public const string FileEditsSummary = "file_edits_summary";
        public const string RequestPermission = "request_permission";
        public const string RequestText = "request_text";
        public const string AvailableCommands = "available_commands";
        public const string HistoryReset = "history_reset";
    }
}
