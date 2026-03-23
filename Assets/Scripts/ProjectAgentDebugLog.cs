using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// NDJSON debug log for agent sessions: writes to the project <c>.cursor/</c> folder when possible so the
/// workspace can read the same file; falls back to <see cref="Application.persistentDataPath"/>.
/// Each <see cref="Append"/> also mirrors the same line to the Unity Console when <see cref="LogToUnityConsole"/> is true.
/// Use <see cref="TryReadAll"/> / <see cref="TryReadTailLines"/> from tooling or filter Console by <c>[agent-debug]</c>.
/// </summary>
public static class ProjectAgentDebugLog
{
    public const string DefaultFileName = "agent-debug.ndjson";
    public const string SessionId = "agent";

    /// <summary>When true, each <see cref="Append"/> also emits <see cref="Debug.Log"/> (same NDJSON line, prefixed).</summary>
    public static bool LogToUnityConsole = true;

    /// <summary>When true, <see cref="MirrorToProjectDotLog"/> appends lines to <c>{project root}/.log</c> (same folder as <c>Assets</c>).</summary>
    public static bool MirrorDiagnosticsToProjectDotLog = true;

    static string _resolvedPath;
    static bool _loggedPathOnce;

    /// <summary>Absolute path of the log file used for the current run (after first write or explicit resolve).</summary>
    public static string ResolvedPath => _resolvedPath ?? ResolvePathForWrite();

    /// <summary>Append one NDJSON object (must be valid JSON). Thread-safe enough for main-thread Unity usage.</summary>
    public static void Append(string hypothesisId, string location, string message, string dataJsonObject)
    {
        if (string.IsNullOrEmpty(dataJsonObject)) dataJsonObject = "{}";
        long ts = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        var sb = new StringBuilder(256);
        sb.Append("{\"sessionId\":\"").Append(SessionId).Append("\",\"hypothesisId\":\"").Append(EscapeJson(hypothesisId));
        sb.Append("\",\"location\":\"").Append(EscapeJson(location)).Append("\",\"message\":\"").Append(EscapeJson(message));
        sb.Append("\",\"data\":").Append(dataJsonObject).Append(",\"timestamp\":").Append(ts).Append("}\n");
        string line = sb.ToString();
        if (LogToUnityConsole)
            Debug.Log("[agent-debug] " + line.TrimEnd());
        TryAppendFile(line);
    }

    static void TryAppendFile(string line)
    {
        try
        {
            string path = ResolvePathForWrite();
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.AppendAllText(path, line);
#if UNITY_EDITOR
            if (!_loggedPathOnce)
            {
                _loggedPathOnce = true;
                Debug.Log($"[ProjectAgentDebugLog] writing to: {path}");
            }
#endif
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ProjectAgentDebugLog] append failed: {e.Message}");
        }
    }

    static string ResolvePathForWrite()
    {
        if (!string.IsNullOrEmpty(_resolvedPath))
            return _resolvedPath;

        // 1) Project root = parent of Assets/
        try
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (!string.IsNullOrEmpty(projectRoot))
            {
                string p = Path.GetFullPath(Path.Combine(projectRoot, ".cursor", DefaultFileName));
                string d = Path.GetDirectoryName(p);
                if (!string.IsNullOrEmpty(d) && (Directory.Exists(d) || CanCreateDirectory(d)))
                {
                    _resolvedPath = p;
                    return _resolvedPath;
                }
            }
        }
        catch { /* try fallback */ }

        // 2) Persistent (player / restricted editor)
        _resolvedPath = Path.Combine(Application.persistentDataPath, DefaultFileName);
        return _resolvedPath;
    }

    static bool CanCreateDirectory(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            return true;
        }
        catch
        {
            return false;
        }
    }

    static string EscapeJson(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
    }

    /// <summary>Append one line to <c>project-root/.log</c> (used by <c>[vsepr3d]</c> / <c>[bond-break]</c> diagnostics).</summary>
    public static void MirrorToProjectDotLog(string line)
    {
        if (!MirrorDiagnosticsToProjectDotLog || string.IsNullOrEmpty(line)) return;
        try
        {
            string root = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrEmpty(root)) return;
            string p = Path.Combine(root, ".log");
            File.AppendAllText(p, line + Environment.NewLine);
        }
        catch { /* ignore */ }
    }

    /// <summary>Read entire log file if it exists.</summary>
    public static bool TryReadAll(out string content)
    {
        content = null;
        string path = File.Exists(_resolvedPath) ? _resolvedPath : TryFindExistingLogPath();
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return false;
        try
        {
            content = File.ReadAllText(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Read last <paramref name="maxLines"/> non-empty lines (for large files).</summary>
    public static bool TryReadTailLines(int maxLines, out IReadOnlyList<string> lines)
    {
        lines = Array.Empty<string>();
        string path = File.Exists(_resolvedPath) ? _resolvedPath : TryFindExistingLogPath();
        if (string.IsNullOrEmpty(path) || !File.Exists(path) || maxLines <= 0)
            return false;
        try
        {
            var buf = new Queue<string>(maxLines + 1);
            foreach (string line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                buf.Enqueue(line);
                while (buf.Count > maxLines)
                    buf.Dequeue();
            }
            lines = buf.ToArray();
            return buf.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    static string TryFindExistingLogPath()
    {
        string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
        if (!string.IsNullOrEmpty(projectRoot))
        {
            string p = Path.GetFullPath(Path.Combine(projectRoot, ".cursor", DefaultFileName));
            if (File.Exists(p)) return p;
        }
        string pers = Path.Combine(Application.persistentDataPath, DefaultFileName);
        return File.Exists(pers) ? pers : null;
    }

    /// <summary>Truncate or delete the session log (call before a new reproduction run).</summary>
    public static void Clear()
    {
        try
        {
            string path = ResolvePathForWrite();
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ProjectAgentDebugLog] clear failed: {e.Message}");
        }
    }
}
