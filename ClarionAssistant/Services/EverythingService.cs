using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// P/Invoke wrapper for Everything SDK — provides instant file search via the Everything service.
    /// Requires Everything (voidtools.com) to be installed and running.
    /// </summary>
    public static class EverythingService
    {
        #region Constants

        public const int EVERYTHING_OK = 0;
        public const int EVERYTHING_ERROR_MEMORY = 1;
        public const int EVERYTHING_ERROR_IPC = 2;
        public const int EVERYTHING_ERROR_REGISTERCLASSEX = 3;
        public const int EVERYTHING_ERROR_CREATEWINDOW = 4;
        public const int EVERYTHING_ERROR_CREATETHREAD = 5;
        public const int EVERYTHING_ERROR_INVALIDINDEX = 6;
        public const int EVERYTHING_ERROR_INVALIDCALL = 7;
        public const int EVERYTHING_ERROR_INVALIDREQUEST = 8;
        public const int EVERYTHING_ERROR_INVALIDPARAMETER = 9;

        public const int EVERYTHING_SORT_NAME_ASCENDING = 1;
        public const int EVERYTHING_SORT_NAME_DESCENDING = 2;
        public const int EVERYTHING_SORT_PATH_ASCENDING = 3;
        public const int EVERYTHING_SORT_PATH_DESCENDING = 4;
        public const int EVERYTHING_SORT_SIZE_ASCENDING = 5;
        public const int EVERYTHING_SORT_SIZE_DESCENDING = 6;
        public const int EVERYTHING_SORT_DATE_MODIFIED_ASCENDING = 13;
        public const int EVERYTHING_SORT_DATE_MODIFIED_DESCENDING = 14;

        public const uint EVERYTHING_REQUEST_FILE_NAME = 0x00000001;
        public const uint EVERYTHING_REQUEST_PATH = 0x00000002;
        public const uint EVERYTHING_REQUEST_FULL_PATH_AND_FILE_NAME = 0x00000004;
        public const uint EVERYTHING_REQUEST_SIZE = 0x00000010;
        public const uint EVERYTHING_REQUEST_DATE_MODIFIED = 0x00000040;

        #endregion

        #region P/Invoke

        [DllImport("Everything32.dll", CharSet = CharSet.Unicode)]
        private static extern void Everything_SetSearchW(string search);

        [DllImport("Everything32.dll")]
        private static extern void Everything_SetMatchCase(bool matchCase);

        [DllImport("Everything32.dll")]
        private static extern void Everything_SetMatchWholeWord(bool matchWholeWord);

        [DllImport("Everything32.dll")]
        private static extern void Everything_SetRegex(bool regex);

        [DllImport("Everything32.dll")]
        private static extern void Everything_SetMax(uint max);

        [DllImport("Everything32.dll")]
        private static extern void Everything_SetOffset(uint offset);

        [DllImport("Everything32.dll")]
        private static extern void Everything_SetSort(uint sort);

        [DllImport("Everything32.dll")]
        private static extern void Everything_SetRequestFlags(uint flags);

        [DllImport("Everything32.dll")]
        private static extern bool Everything_QueryW(bool wait);

        [DllImport("Everything32.dll")]
        private static extern uint Everything_GetLastError();

        [DllImport("Everything32.dll")]
        private static extern uint Everything_GetNumResults();

        [DllImport("Everything32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr Everything_GetResultFileNameW(uint index);

        [DllImport("Everything32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr Everything_GetResultPathW(uint index);

        [DllImport("Everything32.dll")]
        private static extern bool Everything_IsFileResult(uint index);

        [DllImport("Everything32.dll")]
        private static extern bool Everything_IsFolderResult(uint index);

        #endregion

        private static readonly object _lock = new object();

        /// <summary>
        /// Check if Everything service is available.
        /// </summary>
        public static bool IsAvailable()
        {
            try
            {
                lock (_lock)
                {
                    Everything_SetSearchW("__everything_ping_test__");
                    Everything_SetMax(1);
                    Everything_QueryW(true);
                    uint err = Everything_GetLastError();
                    return err != EVERYTHING_ERROR_IPC;
                }
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Execute an Everything search and return results.
        /// </summary>
        public static SearchResult Search(string query, SearchOptions options = null)
        {
            if (options == null) options = new SearchOptions();

            lock (_lock)
            {
                try
                {
                    Everything_SetSearchW(query);
                    Everything_SetMatchCase(options.MatchCase);
                    Everything_SetMatchWholeWord(options.MatchWholeWord);
                    Everything_SetRegex(options.Regex);
                    Everything_SetMax((uint)options.MaxResults);
                    Everything_SetOffset(0);
                    Everything_SetRequestFlags(EVERYTHING_REQUEST_FILE_NAME | EVERYTHING_REQUEST_PATH | EVERYTHING_REQUEST_FULL_PATH_AND_FILE_NAME);
                    Everything_SetSort((uint)GetSortOrder(options.SortBy));

                    bool success = Everything_QueryW(true);

                    if (!success)
                    {
                        uint errorCode = Everything_GetLastError();
                        return new SearchResult { Error = GetErrorMessage(errorCode) };
                    }

                    uint numResults = Everything_GetNumResults();
                    var results = new List<SearchResultItem>((int)numResults);

                    for (uint i = 0; i < numResults; i++)
                    {
                        IntPtr fileNamePtr = Everything_GetResultFileNameW(i);
                        IntPtr pathPtr = Everything_GetResultPathW(i);
                        bool isFile = Everything_IsFileResult(i);
                        bool isFolder = Everything_IsFolderResult(i);

                        string fileName = fileNamePtr != IntPtr.Zero ? Marshal.PtrToStringUni(fileNamePtr) : "";
                        string filePath = pathPtr != IntPtr.Zero ? Marshal.PtrToStringUni(pathPtr) : "";

                        string fullPath;
                        if (!string.IsNullOrEmpty(filePath) && !string.IsNullOrEmpty(fileName))
                            fullPath = filePath.EndsWith("\\") ? filePath + fileName : filePath + "\\" + fileName;
                        else
                            fullPath = fileName;

                        results.Add(new SearchResultItem
                        {
                            FullPath = fullPath,
                            FileName = fileName,
                            Directory = filePath,
                            IsFile = isFile,
                            IsFolder = isFolder
                        });
                    }

                    return new SearchResult { Items = results, TotalResults = (int)numResults };
                }
                catch (DllNotFoundException)
                {
                    return new SearchResult { Error = "Everything32.dll not found. Ensure Everything SDK DLL is deployed alongside the addin." };
                }
                catch (Exception ex)
                {
                    return new SearchResult { Error = "Everything search failed: " + ex.Message };
                }
            }
        }

        private static int GetSortOrder(string sortBy)
        {
            if (string.IsNullOrEmpty(sortBy)) return EVERYTHING_SORT_NAME_ASCENDING;
            switch (sortBy.ToLower())
            {
                case "name_desc": return EVERYTHING_SORT_NAME_DESCENDING;
                case "path_asc": return EVERYTHING_SORT_PATH_ASCENDING;
                case "path_desc": return EVERYTHING_SORT_PATH_DESCENDING;
                case "size_asc": return EVERYTHING_SORT_SIZE_ASCENDING;
                case "size_desc": return EVERYTHING_SORT_SIZE_DESCENDING;
                case "date_asc": return EVERYTHING_SORT_DATE_MODIFIED_ASCENDING;
                case "date_desc": return EVERYTHING_SORT_DATE_MODIFIED_DESCENDING;
                default: return EVERYTHING_SORT_NAME_ASCENDING;
            }
        }

        private static string GetErrorMessage(uint errorCode)
        {
            switch (errorCode)
            {
                case EVERYTHING_ERROR_MEMORY: return "Out of memory";
                case EVERYTHING_ERROR_IPC: return "Everything search client is not running. Please start Everything (voidtools.com).";
                case EVERYTHING_ERROR_REGISTERCLASSEX: return "Unable to register window class";
                case EVERYTHING_ERROR_CREATEWINDOW: return "Unable to create listening window";
                case EVERYTHING_ERROR_CREATETHREAD: return "Unable to create listening thread";
                case EVERYTHING_ERROR_INVALIDINDEX: return "Invalid index";
                case EVERYTHING_ERROR_INVALIDCALL: return "Invalid call";
                case EVERYTHING_ERROR_INVALIDREQUEST: return "Invalid request data";
                case EVERYTHING_ERROR_INVALIDPARAMETER: return "Bad parameter";
                default: return "Everything query failed with error code: " + errorCode;
            }
        }
    }

    public class SearchOptions
    {
        public int MaxResults { get; set; } = 100;
        public bool MatchCase { get; set; }
        public bool MatchWholeWord { get; set; }
        public bool Regex { get; set; }
        public string SortBy { get; set; }
    }

    public class SearchResult
    {
        public List<SearchResultItem> Items { get; set; } = new List<SearchResultItem>();
        public int TotalResults { get; set; }
        public string Error { get; set; }
        public bool HasError { get { return !string.IsNullOrEmpty(Error); } }
    }

    public class SearchResultItem
    {
        public string FullPath { get; set; }
        public string FileName { get; set; }
        public string Directory { get; set; }
        public bool IsFile { get; set; }
        public bool IsFolder { get; set; }
        public string Type { get { return IsFile ? "FILE" : IsFolder ? "FOLDER" : "UNKNOWN"; } }
    }
}
