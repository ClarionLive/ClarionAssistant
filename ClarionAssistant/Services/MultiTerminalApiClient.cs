using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using ClarionAssistant.Models;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// HTTP client for the MultiTerminal REST API (localhost:5050).
    /// Uses WebRequest (.NET 4.7.2 compatible) and JavaScriptSerializer.
    /// </summary>
    public class MultiTerminalApiClient
    {
        private readonly string _baseUrl;
        private readonly JavaScriptSerializer _json;
        private readonly int _timeoutMs;

        public MultiTerminalApiClient(string baseUrl = "http://localhost:5050", int timeoutMs = 10000)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _timeoutMs = timeoutMs;
            _json = new JavaScriptSerializer { MaxJsonLength = 10 * 1024 * 1024 };
        }

        public string BaseUrl { get { return _baseUrl; } }

        private static string E(string value) { return Uri.EscapeDataString(value ?? ""); }

        // ── Tasks ───────────────────────────────────────────

        /// <summary>List tasks, optionally filtered by status (all, todo, in_progress, done, suggestion).</summary>
        public ApiResult<List<KanbanTaskSummary>> ListTasks(string status = "all")
        {
            return Get<List<KanbanTaskSummary>>(string.Format("/api/tasks?status={0}", E(status)));
        }

        /// <summary>Get full task detail including checklist, plan, continuation notes.</summary>
        public ApiResult<TaskDetail> GetTaskDetail(string taskId)
        {
            return Get<TaskDetail>(string.Format("/api/tasks/{0}/detail", E(taskId)));
        }

        /// <summary>Get the active task for a specific agent.</summary>
        public ApiResult<TaskDetail> GetActiveTask(string agentName)
        {
            return Get<TaskDetail>(string.Format("/api/tasks/active/{0}", E(agentName)));
        }

        /// <summary>Get tasks available for an agent to pick.</summary>
        public ApiResult<List<KanbanTaskSummary>> GetPickableTasks(string agentName)
        {
            return Get<List<KanbanTaskSummary>>(string.Format("/api/tasks/pickable/{0}", E(agentName)));
        }

        /// <summary>Create a new task.</summary>
        public ApiResult<CreateTaskResponse> CreateTask(string title, string description, string createdBy,
            string status = "todo", string priority = "normal", string projectId = null)
        {
            var body = new Dictionary<string, object>
            {
                { "title", title },
                { "description", description },
                { "createdBy", createdBy },
                { "status", status },
                { "priority", priority }
            };
            if (projectId != null) body["projectId"] = projectId;
            return Post<CreateTaskResponse>("/api/tasks", body);
        }

        /// <summary>Update task status (todo, in_progress, done, suggestion).</summary>
        public ApiResult<object> UpdateTaskStatus(string taskId, string status)
        {
            return Patch<object>(string.Format("/api/tasks/{0}/status", E(taskId)),
                new Dictionary<string, object> { { "status", status } });
        }

        /// <summary>Delete a task.</summary>
        public ApiResult<object> DeleteTask(string taskId, string deletedBy)
        {
            return Delete<object>(string.Format("/api/tasks/{0}?deletedBy={1}", E(taskId), E(deletedBy)));
        }

        /// <summary>Claim/assign a task to an agent.</summary>
        public ApiResult<object> ClaimTask(string taskId, string agentName)
        {
            return Post<object>(string.Format("/api/tasks/{0}/assign", E(taskId)),
                new Dictionary<string, object> { { "assignee", agentName } });
        }

        /// <summary>Set a task as the active task (auto-pauses others).</summary>
        public ApiResult<object> SetTaskActive(string taskId, string agentName)
        {
            return Post<object>(string.Format("/api/tasks/{0}/activate", E(taskId)),
                new Dictionary<string, object> { { "agentName", agentName } });
        }

        /// <summary>Add a helper to a task.</summary>
        public ApiResult<object> AddHelper(string taskId, string helperName)
        {
            return Post<object>(string.Format("/api/tasks/{0}/helpers", E(taskId)),
                new Dictionary<string, object> { { "helperName", helperName } });
        }

        /// <summary>Remove a helper from a task.</summary>
        public ApiResult<object> RemoveHelper(string taskId, string helperName)
        {
            return Delete<object>(string.Format("/api/tasks/{0}/helpers/{1}", E(taskId), E(helperName)));
        }

        // ── Checklist ───────────────────────────────────────

        /// <summary>Transition a checklist item (pending→coding→testing→done).</summary>
        public ApiResult<ChecklistTransitionResponse> TransitionChecklistItem(
            string taskId, int itemIndex, string newStatus, string notes, string updatedBy)
        {
            return Post<ChecklistTransitionResponse>(
                string.Format("/api/tasks/{0}/checklist/{1}/transition", E(taskId), itemIndex),
                new Dictionary<string, object>
                {
                    { "newStatus", newStatus },
                    { "notes", notes },
                    { "updatedBy", updatedBy }
                });
        }

        /// <summary>Assign a checklist item to a specific agent.</summary>
        public ApiResult<object> AssignChecklistItem(string taskId, int itemIndex, string agentName)
        {
            return Post<object>(
                string.Format("/api/tasks/{0}/checklist/{1}/assign", E(taskId), itemIndex),
                new Dictionary<string, object> { { "assignedTo", agentName } });
        }

        /// <summary>Replace all checklist items.</summary>
        public ApiResult<object> UpdateChecklist(string taskId, List<Dictionary<string, object>> items)
        {
            return Patch<object>(string.Format("/api/tasks/{0}/checklist", E(taskId)),
                new Dictionary<string, object> { { "items", items } });
        }

        // ── Task Fields ─────────────────────────────────────

        /// <summary>Update the implementation plan.</summary>
        public ApiResult<object> UpdatePlan(string taskId, string plan, string updatedBy)
        {
            return Patch<object>(string.Format("/api/tasks/{0}/plan", E(taskId)),
                new Dictionary<string, object> { { "plan", plan }, { "updatedBy", updatedBy } });
        }

        /// <summary>Update implementation summary and/or test results.</summary>
        public ApiResult<object> UpdateSummary(string taskId, string implementationSummary = null,
            string testResults = null, string updatedBy = null)
        {
            var body = new Dictionary<string, object>();
            if (implementationSummary != null) body["implementationSummary"] = implementationSummary;
            if (testResults != null) body["testResults"] = testResults;
            if (updatedBy != null) body["updatedBy"] = updatedBy;
            return Patch<object>(string.Format("/api/tasks/{0}/summary", E(taskId)), body);
        }

        /// <summary>Update continuation notes for session handoff.</summary>
        public ApiResult<object> UpdateContinuation(string taskId, string continuationNotes, string updatedBy)
        {
            return Patch<object>(string.Format("/api/tasks/{0}/continuation", E(taskId)),
                new Dictionary<string, object> { { "continuationNotes", continuationNotes }, { "updatedBy", updatedBy } });
        }

        // ── Task Relationships ──────────────────────────────

        public ApiResult<object> AddRelationship(string taskId, string relatedTaskId, string type)
        {
            return Post<object>(string.Format("/api/tasks/{0}/relationships", E(taskId)),
                new Dictionary<string, object> { { "relatedTaskId", relatedTaskId }, { "type", type } });
        }

        public ApiResult<object> RemoveRelationship(string taskId, string relatedTaskId)
        {
            return Delete<object>(string.Format("/api/tasks/{0}/relationships/{1}", E(taskId), E(relatedTaskId)));
        }

        public ApiResult<List<TaskRelationship>> GetRelationships(string taskId)
        {
            return Get<List<TaskRelationship>>(string.Format("/api/tasks/{0}/relationships", E(taskId)));
        }

        // ── Task Files ──────────────────────────────────────

        public ApiResult<object> LinkFile(string taskId, string filePath, string addedBy, string description = null)
        {
            var body = new Dictionary<string, object>
            {
                { "filePath", filePath },
                { "addedBy", addedBy }
            };
            if (description != null) body["description"] = description;
            return Post<object>(string.Format("/api/tasks/{0}/files", E(taskId)), body);
        }

        public ApiResult<object> UnlinkFile(string taskId, string filePath)
        {
            return Post<object>(string.Format("/api/tasks/{0}/files/unlink", E(taskId)),
                new Dictionary<string, object> { { "filePath", filePath } });
        }

        public ApiResult<List<TaskFileLink>> GetFileLinks(string taskId)
        {
            return Get<List<TaskFileLink>>(string.Format("/api/tasks/{0}/files", E(taskId)));
        }

        // ── Terminals / Messaging ───────────────────────────

        /// <summary>List all registered terminals.</summary>
        public ApiResult<List<TerminalInfo>> ListTerminals()
        {
            return Get<List<TerminalInfo>>("/api/messaging/terminals");
        }

        /// <summary>Send a message to a specific terminal.</summary>
        public ApiResult<object> SendMessage(string fromTerminalId, string to, string message, string priority = "normal")
        {
            return Post<object>("/api/messaging/send", new Dictionary<string, object>
            {
                { "fromTerminalId", fromTerminalId },
                { "to", to },
                { "message", message },
                { "priority", priority }
            });
        }

        /// <summary>Broadcast a message to all terminals.</summary>
        public ApiResult<object> BroadcastMessage(string fromTerminalId, string message)
        {
            return Post<object>("/api/messaging/broadcast", new Dictionary<string, object>
            {
                { "fromTerminalId", fromTerminalId },
                { "message", message }
            });
        }

        // ── Team ────────────────────────────────────────────

        /// <summary>Get all team member profiles with online status.</summary>
        public ApiResult<List<TeamMemberProfile>> GetTeamProfiles()
        {
            return Get<List<TeamMemberProfile>>("/api/team/profiles");
        }

        /// <summary>Get team roster for a project.</summary>
        public ApiResult<List<TeamMemberProfile>> GetTeamRoster(string projectPath = null)
        {
            string path = "/api/team/roster";
            if (projectPath != null) path += "?projectPath=" + E(projectPath);
            return Get<List<TeamMemberProfile>>(path);
        }

        // ── Health ──────────────────────────────────────────

        /// <summary>Check if the MultiTerminal API is reachable.</summary>
        public bool IsAvailable()
        {
            try
            {
                var req = CreateRequest("/health", "GET");
                req.Timeout = 3000;
                using (var resp = (HttpWebResponse)req.GetResponse())
                    return resp.StatusCode == HttpStatusCode.OK;
            }
            catch
            {
                return false;
            }
        }

        // ── Async wrappers ──────────────────────────────────

        /// <summary>List tasks asynchronously.</summary>
        public Task<ApiResult<List<KanbanTaskSummary>>> ListTasksAsync(string status = "all")
        {
            return Task.Run(() => ListTasks(status));
        }

        /// <summary>Get task detail asynchronously.</summary>
        public Task<ApiResult<TaskDetail>> GetTaskDetailAsync(string taskId)
        {
            return Task.Run(() => GetTaskDetail(taskId));
        }

        /// <summary>Get active task asynchronously.</summary>
        public Task<ApiResult<TaskDetail>> GetActiveTaskAsync(string agentName)
        {
            return Task.Run(() => GetActiveTask(agentName));
        }

        /// <summary>Check availability asynchronously.</summary>
        public Task<bool> IsAvailableAsync()
        {
            return Task.Run(() => IsAvailable());
        }

        // ── HTTP plumbing ───────────────────────────────────

        private ApiResult<T> Get<T>(string path)
        {
            return Execute<T>(path, "GET", null);
        }

        private ApiResult<T> Post<T>(string path, object body)
        {
            return Execute<T>(path, "POST", body);
        }

        private ApiResult<T> Patch<T>(string path, object body)
        {
            return Execute<T>(path, "PATCH", body);
        }

        private ApiResult<T> Delete<T>(string path)
        {
            return Execute<T>(path, "DELETE", null);
        }

        private HttpWebRequest CreateRequest(string path, string method)
        {
            var url = _baseUrl + path;
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.Timeout = _timeoutMs;
            req.ContentType = "application/json";
            req.Accept = "application/json";
            return req;
        }

        private ApiResult<T> Execute<T>(string path, string method, object body)
        {
            try
            {
                var req = CreateRequest(path, method);

                if (body != null)
                {
                    var json = _json.Serialize(body);
                    var bytes = Encoding.UTF8.GetBytes(json);
                    req.ContentLength = bytes.Length;
                    using (var stream = req.GetRequestStream())
                        stream.Write(bytes, 0, bytes.Length);
                }

                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    var responseText = reader.ReadToEnd();
                    return ParseResponse<T>(responseText);
                }
            }
            catch (WebException ex) when (ex.Response is HttpWebResponse errorResp)
            {
                using (var reader = new StreamReader(errorResp.GetResponseStream(), Encoding.UTF8))
                {
                    var errorText = reader.ReadToEnd();
                    return new ApiResult<T>
                    {
                        Success = false,
                        Error = string.Format("{0} {1}: {2}",
                            (int)errorResp.StatusCode, errorResp.StatusDescription, errorText)
                    };
                }
            }
            catch (Exception ex)
            {
                return new ApiResult<T>
                {
                    Success = false,
                    Error = ex.Message
                };
            }
        }

        private ApiResult<T> ParseResponse<T>(string responseText)
        {
            if (string.IsNullOrWhiteSpace(responseText))
                return new ApiResult<T> { Success = true };

            try
            {
                // Try to deserialize as a typed response
                var data = DeserializeAs<T>(responseText);
                return new ApiResult<T> { Success = true, Data = data };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("MultiTerminalApiClient: deserialization failed: " + ex.Message);
                return new ApiResult<T> { Success = false, Error = "Deserialization failed: " + ex.Message };
            }
        }

        /// <summary>
        /// Deserialize JSON to a typed object using JavaScriptSerializer.
        /// JavaScriptSerializer returns Dictionary/ArrayList, so we map manually.
        /// </summary>
        private T DeserializeAs<T>(string json)
        {
            var raw = _json.DeserializeObject(json);
            return (T)MapToType(raw, typeof(T));
        }

        private object MapToType(object raw, Type targetType)
        {
            if (raw == null) return null;

            // Direct primitive types
            if (targetType == typeof(string)) return raw.ToString();
            if (targetType == typeof(int)) { try { return Convert.ToInt32(raw); } catch { return 0; } }
            if (targetType == typeof(long)) { try { return Convert.ToInt64(raw); } catch { return 0L; } }
            if (targetType == typeof(double)) { try { return Convert.ToDouble(raw); } catch { return 0.0; } }
            if (targetType == typeof(float)) { try { return Convert.ToSingle(raw); } catch { return 0f; } }
            if (targetType == typeof(decimal)) { try { return Convert.ToDecimal(raw); } catch { return 0m; } }
            if (targetType == typeof(bool)) return Convert.ToBoolean(raw);
            if (targetType == typeof(object)) return raw;

            // List<T>
            if (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(List<>))
            {
                var elementType = targetType.GetGenericArguments()[0];
                var list = (IList)Activator.CreateInstance(targetType);
                if (raw is ArrayList arrayList)
                {
                    foreach (var item in arrayList)
                        list.Add(MapToType(item, elementType));
                }
                else if (raw is object[] array)
                {
                    foreach (var item in array)
                        list.Add(MapToType(item, elementType));
                }
                return list;
            }

            // Dictionary from JSON object
            if (raw is Dictionary<string, object> dict)
            {
                if (targetType == typeof(object) || targetType == typeof(Dictionary<string, object>))
                    return dict;

                // Map dictionary to a typed object
                var instance = Activator.CreateInstance(targetType);
                foreach (var prop in targetType.GetProperties())
                {
                    // Try camelCase and PascalCase keys
                    string camel = char.ToLowerInvariant(prop.Name[0]) + prop.Name.Substring(1);
                    object value;
                    if (dict.TryGetValue(camel, out value) || dict.TryGetValue(prop.Name, out value))
                    {
                        if (value != null)
                            prop.SetValue(instance, MapToType(value, prop.PropertyType), null);
                    }
                }
                return instance;
            }

            // Fallback: try direct conversion
            try { return Convert.ChangeType(raw, targetType); }
            catch { return null; }
        }
    }
}
