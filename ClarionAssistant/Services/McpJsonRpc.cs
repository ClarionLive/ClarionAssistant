using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// JSON-RPC 2.0 message types for MCP protocol.
    /// Uses JavaScriptSerializer (.NET 4.0 built-in via System.Web).
    /// </summary>
    public static class McpJsonRpc
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer
        {
            MaxJsonLength = int.MaxValue
        };

        #region Deserialization

        /// <summary>
        /// Parse a JSON-RPC request from a JSON string.
        /// </summary>
        public static JsonRpcRequest ParseRequest(string json)
        {
            var dict = Serializer.Deserialize<Dictionary<string, object>>(json);
            var request = new JsonRpcRequest();

            if (dict.ContainsKey("jsonrpc")) request.JsonRpc = dict["jsonrpc"] as string;
            if (dict.ContainsKey("id")) request.Id = dict["id"];
            if (dict.ContainsKey("method")) request.Method = dict["method"] as string;
            if (dict.ContainsKey("params")) request.Params = dict["params"] as Dictionary<string, object>;

            return request;
        }

        #endregion

        #region Serialization

        /// <summary>
        /// Serialize a JSON-RPC success response.
        /// </summary>
        public static string SerializeResponse(object id, object result)
        {
            var response = new Dictionary<string, object>
            {
                { "jsonrpc", "2.0" },
                { "id", id },
                { "result", result }
            };
            return Serializer.Serialize(response);
        }

        /// <summary>
        /// Serialize a JSON-RPC error response.
        /// </summary>
        public static string SerializeError(object id, int code, string message, object data = null)
        {
            var error = new Dictionary<string, object>
            {
                { "code", code },
                { "message", message }
            };
            if (data != null) error["data"] = data;

            var response = new Dictionary<string, object>
            {
                { "jsonrpc", "2.0" },
                { "id", id },
                { "error", error }
            };
            return Serializer.Serialize(response);
        }

        /// <summary>
        /// Serialize any object to JSON.
        /// </summary>
        public static string Serialize(object obj)
        {
            return Serializer.Serialize(obj);
        }

        /// <summary>
        /// Deserialize JSON to a dictionary.
        /// </summary>
        public static Dictionary<string, object> Deserialize(string json)
        {
            return Serializer.Deserialize<Dictionary<string, object>>(json);
        }

        /// <summary>
        /// Safely get a string value from a params dictionary.
        /// </summary>
        public static string GetString(Dictionary<string, object> dict, string key, string defaultValue = null)
        {
            if (dict != null && dict.ContainsKey(key) && dict[key] != null)
                return dict[key].ToString();
            return defaultValue;
        }

        /// <summary>
        /// Safely get an int value from a params dictionary.
        /// </summary>
        public static int GetInt(Dictionary<string, object> dict, string key, int defaultValue = 0)
        {
            if (dict != null && dict.ContainsKey(key) && dict[key] != null)
            {
                int result;
                if (int.TryParse(dict[key].ToString(), out result))
                    return result;
            }
            return defaultValue;
        }

        #endregion

        #region MCP Protocol Helpers

        /// <summary>
        /// Build an MCP initialize result with server info and capabilities.
        /// </summary>
        public static object BuildInitializeResult(string serverName, string serverVersion)
        {
            return new Dictionary<string, object>
            {
                { "protocolVersion", "2024-11-05" },
                { "capabilities", new Dictionary<string, object>
                    {
                        { "tools", new Dictionary<string, object>() }
                    }
                },
                { "serverInfo", new Dictionary<string, object>
                    {
                        { "name", serverName },
                        { "version", serverVersion }
                    }
                }
            };
        }

        /// <summary>
        /// Build a tool definition for the tools/list response.
        /// </summary>
        public static Dictionary<string, object> BuildToolDefinition(
            string name, string description, Dictionary<string, object> inputSchema)
        {
            return new Dictionary<string, object>
            {
                { "name", name },
                { "description", description },
                { "inputSchema", inputSchema ?? new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>() }
                    }
                }
            };
        }

        /// <summary>
        /// Build a tool call result (success).
        /// </summary>
        public static object BuildToolResult(string text, bool isError = false)
        {
            return new Dictionary<string, object>
            {
                { "content", new object[]
                    {
                        new Dictionary<string, object>
                        {
                            { "type", "text" },
                            { "text", text }
                        }
                    }
                },
                { "isError", isError }
            };
        }

        /// <summary>
        /// Build a JSON schema for tool input with string properties.
        /// </summary>
        public static Dictionary<string, object> BuildSchema(
            Dictionary<string, string> properties,
            string[] required = null)
        {
            var props = new Dictionary<string, object>();
            var requiredList = required != null ? new List<string>(required) : new List<string>();

            foreach (var kv in properties)
            {
                string key = kv.Key;
                if (key.EndsWith("?"))
                {
                    // Trailing '?' marks the property as optional — strip it from the key
                    key = key.Substring(0, key.Length - 1);
                }
                else if (required == null)
                {
                    // No explicit required array: keys without '?' are required
                    requiredList.Add(key);
                }

                props[key] = new Dictionary<string, object>
                {
                    { "type", "string" },
                    { "description", kv.Value }
                };
            }

            var schema = new Dictionary<string, object>
            {
                { "type", "object" },
                { "properties", props }
            };

            if (requiredList.Count > 0)
                schema["required"] = requiredList.ToArray();

            return schema;
        }

        #endregion
    }

    /// <summary>
    /// Represents a JSON-RPC 2.0 request message.
    /// </summary>
    public class JsonRpcRequest
    {
        public string JsonRpc { get; set; }
        public object Id { get; set; }
        public string Method { get; set; }
        public Dictionary<string, object> Params { get; set; }

        public bool IsNotification { get { return Id == null; } }
    }
}
