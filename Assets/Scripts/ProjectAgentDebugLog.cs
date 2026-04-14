using System;
using System.Collections.Generic;
using System.Globalization;
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

    /// <summary>Escape for embedding in JSON string literals (e.g. debug <c>data</c> blobs built manually).</summary>
    public static string EscapeJsonString(string s) => EscapeJson(s);

    /// <summary>Single workspace debug log under <c>.cursor/</c>: Cursor ingest, orbital mirror lines, and <see cref="AtomFunction.AppendOcoCaseDebugNdjson"/> / session probes all append here as NDJSON.</summary>
    public const string CursorDebugModeIngestNdjsonFileName = "cursor-workspace-debug.ndjson";

    /// <summary>Session id on each ingest line; override in tooling if you partition runs.</summary>
    public const string CursorDebugModeIngestSessionId = "workspace";

    /// <summary>When true (default), <see cref="AppendCursorWorkspaceDebugNdjson"/> appends NDJSON to <see cref="CursorDebugModeIngestNdjsonFileName"/>. Default on for triage; set false for quiet runs.</summary>
    public static bool WriteCursorDebugModeIngestNdjson = true;

    static bool _cursorDebugModeIngestLoggedPathOnce;

    static string BuildCursorWorkspaceNdjsonLine(
        string sessionIdForLine,
        string hypothesisId,
        string location,
        string message,
        string dataJsonObject,
        string runId,
        long timestampMs)
    {
        if (string.IsNullOrEmpty(dataJsonObject)) dataJsonObject = "{}";
        var sb = new StringBuilder(384);
        sb.Append("{\"sessionId\":\"").Append(EscapeJson(sessionIdForLine ?? ""));
        sb.Append("\",\"runId\":\"").Append(EscapeJson(runId ?? "run1"));
        sb.Append("\",\"hypothesisId\":\"").Append(EscapeJson(hypothesisId ?? ""));
        sb.Append("\",\"location\":\"").Append(EscapeJson(location ?? ""));
        sb.Append("\",\"message\":\"").Append(EscapeJson(message));
        sb.Append("\",\"data\":").Append(dataJsonObject);
        sb.Append(",\"timestamp\":").Append(timestampMs).Append("}\n");
        return sb.ToString();
    }

    /// <summary>
    /// Append one NDJSON line to <c>{project}/.cursor/<paramref name="fileName"/></c> for Cursor Debug Mode (session-specific file). Does not mirror to Console.
    /// </summary>
    public static void AppendDebugModeNdjson(
        string fileName,
        string sessionId,
        string hypothesisId,
        string location,
        string message,
        string dataJsonObject = "{}",
        string runId = "pre-fix")
    {
        if (string.IsNullOrEmpty(fileName) || string.IsNullOrEmpty(message)) return;
        long ts = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        string line = BuildCursorWorkspaceNdjsonLine(sessionId ?? "", hypothesisId, location, message, dataJsonObject ?? "{}", runId, ts);
        try
        {
            string root = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrEmpty(root)) return;
            string path = Path.GetFullPath(Path.Combine(root, ".cursor", fileName));
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.AppendAllText(path, line);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[debug-mode-ndjson] append failed: " + e.Message);
        }
    }

    /// <summary>When true, <see cref="AppendCursorDebugSessionC2019eNdjson"/> writes to <c>.cursor/debug-c2019e.log</c>. Default on for triage; set false for quiet runs.</summary>
    public static bool WriteCursorDebugSessionC2019eFile = true;

    static bool _loggedCursorDebugSessionC2019ePathOnce;

    /// <summary>
    /// Cursor Debug Mode: append one NDJSON line to <c>{project}/.cursor/debug-c2019e.log</c> with <c>sessionId=c2019e</c> for HTTP ingest correlation.
    /// </summary>
    public static void AppendCursorDebugSessionC2019eNdjson(
        string hypothesisId,
        string location,
        string message,
        string dataJsonObject = "{}",
        string runId = "pre-fix")
    {
        if (!WriteCursorDebugSessionC2019eFile || string.IsNullOrEmpty(message)) return;
        const string fileName = "debug-c2019e.log";
        const string sessionIdLine = "c2019e";
        long ts = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        string line = BuildCursorWorkspaceNdjsonLine(
            sessionIdLine, hypothesisId, location, message, dataJsonObject ?? "{}", runId, ts);
        try
        {
            string root = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrEmpty(root)) return;
            string path = Path.GetFullPath(Path.Combine(root, ".cursor", fileName));
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.AppendAllText(path, line);
#if UNITY_EDITOR
            if (!_loggedCursorDebugSessionC2019ePathOnce)
            {
                _loggedCursorDebugSessionC2019ePathOnce = true;
                Debug.Log("[debug-c2019e] writing NDJSON to " + path);
            }
#endif
        }
        catch (Exception e)
        {
            Debug.LogWarning("[debug-c2019e] append failed: " + e.Message);
        }
    }

    /// <summary>Format a numeric value for JSON <c>data</c> object literals (invariant, no locale comma).</summary>
    public static string JsonFloatInvariant(float value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// One NDJSON line to <c>{project}/.cursor/</c><see cref="CursorDebugModeIngestNdjsonFileName"/> for Cursor Debug Mode ingest.
    /// Shape: sessionId, runId, hypothesisId, location, message, data, timestamp.
    /// </summary>
    public static void AppendCursorWorkspaceDebugNdjson(
        string hypothesisId,
        string location,
        string message,
        string dataJsonObject = "{}",
        string runId = "run1")
    {
        if (!WriteCursorDebugModeIngestNdjson || string.IsNullOrEmpty(message)) return;
        long ts = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        string lineIngest = BuildCursorWorkspaceNdjsonLine(
            CursorDebugModeIngestSessionId, hypothesisId, location, message, dataJsonObject, runId, ts);
        TryAppendLineToUnifiedCursorWorkspaceFile(lineIngest, logFirstPathAsIngest: true);
    }

    static void TryAppendLineToUnifiedCursorWorkspaceFile(string line, bool logFirstPathAsIngest)
    {
        string root = null;
        try
        {
            root = Directory.GetParent(Application.dataPath)?.FullName;
        }
        catch
        {
            return;
        }
        if (string.IsNullOrEmpty(root)) return;

        string pathIngest = Path.GetFullPath(Path.Combine(root, ".cursor", CursorDebugModeIngestNdjsonFileName));
        try
        {
            string dirIngest = Path.GetDirectoryName(pathIngest);
            if (!string.IsNullOrEmpty(dirIngest) && !Directory.Exists(dirIngest))
                Directory.CreateDirectory(dirIngest);
            File.AppendAllText(pathIngest, line);
#if UNITY_EDITOR
            if (logFirstPathAsIngest && !_cursorDebugModeIngestLoggedPathOnce)
            {
                _cursorDebugModeIngestLoggedPathOnce = true;
                Debug.Log("[cursor-debug-mode-ingest] NDJSON path=" + pathIngest + " sessionId=" + CursorDebugModeIngestSessionId);
            }
#endif
        }
        catch (Exception e)
        {
            Debug.LogWarning("[cursor-debug-mode-ingest] append failed path=" + pathIngest + " err=" + e.Message);
        }
    }

    static readonly HashSet<string> _accessibleDiagPathLoggedOnce = new HashSet<string>();

    /// <summary>Append one text line to <c>project-root/</c><paramref name="fileName"/> (e.g. <c>debug-redist3d-angle.log</c>), then <see cref="Application.persistentDataPath"/> if that fails. Emits <paramref name="firstWriteConsoleTag"/> plus path on first success (tag should start with <c>[…]</c>).</summary>
    public static void AppendAccessibleDiagnosticLine(string fileName, string line, string firstWriteConsoleTag = null)
    {
        if (string.IsNullOrEmpty(fileName) || line == null) return;
        string payload = line + Environment.NewLine;
        string usedPath = null;
        string root = null;
        try
        {
            root = Directory.GetParent(Application.dataPath)?.FullName;
        }
        catch { /* ignored */ }

        if (!string.IsNullOrEmpty(root))
        {
            try
            {
                usedPath = Path.GetFullPath(Path.Combine(root, fileName));
                File.AppendAllText(usedPath, payload);
            }
            catch
            {
                usedPath = null;
            }
        }

        if (usedPath == null)
        {
            try
            {
                usedPath = Path.Combine(Application.persistentDataPath, fileName);
                File.AppendAllText(usedPath, payload);
            }
            catch (Exception e)
            {
                if (!string.IsNullOrEmpty(firstWriteConsoleTag))
                    Debug.LogWarning(firstWriteConsoleTag + " append failed all paths err=" + e.Message);
                return;
            }
        }

        if (!string.IsNullOrEmpty(firstWriteConsoleTag) && !_accessibleDiagPathLoggedOnce.Contains(fileName))
        {
            _accessibleDiagPathLoggedOnce.Add(fileName);
            Debug.Log(firstWriteConsoleTag + " diagnostic path=" + usedPath);
        }
    }

    /// <summary>Obsolete name: orbital mirror now uses <see cref="CursorDebugModeIngestNdjsonFileName"/>.</summary>
    [Obsolete("Use CursorDebugModeIngestNdjsonFileName; orbital mirror is merged into that NDJSON log.")]
    public const string OrbitalRedistMirrorFileName = "cursor-workspace-debug.ndjson";

    /// <summary>When true (default), <see cref="AppendOrbitalRedistMirrorLine"/> appends NDJSON lines to <see cref="CursorDebugModeIngestNdjsonFileName"/> (same file as ingest).</summary>
    public static bool MirrorOrbitalRedistDiagnosticsToProjectFile = true;

    /// <summary>Appends one diagnostics line wrapped as NDJSON (<c>hypothesisId=orbitals-redist-mirror</c>, <c>data.line</c>) to <see cref="CursorDebugModeIngestNdjsonFileName"/> when <see cref="MirrorOrbitalRedistDiagnosticsToProjectFile"/> is true.</summary>
    public static void AppendOrbitalRedistMirrorLine(string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        string trimmed = line.TrimEnd();

        if (!MirrorOrbitalRedistDiagnosticsToProjectFile) return;

        long ts = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        string dataJson = "{\"line\":\"" + EscapeJson(trimmed) + "\"}";
        string ndjsonLine = BuildCursorWorkspaceNdjsonLine(
            CursorDebugModeIngestSessionId, "orbitals-redist-mirror", "AppendOrbitalRedistMirrorLine", "mirror_line", dataJson, "run1", ts);
        TryAppendLineToUnifiedCursorWorkspaceFile(ndjsonLine, logFirstPathAsIngest: false);
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
