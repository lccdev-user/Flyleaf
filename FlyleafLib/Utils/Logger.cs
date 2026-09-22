using System.Linq;
using System.Windows;

namespace FlyleafLib;

public static class Logger
{
    public static bool CanError => Engine.Config.LogLevel >= LogLevel.Error;
    public static bool CanWarn  => Engine.Config.LogLevel >= LogLevel.Warn;
    public static bool CanInfo  => Engine.Config.LogLevel >= LogLevel.Info;
    public static bool CanDebug => Engine.Config.LogLevel >= LogLevel.Debug;
    public static bool CanTrace => Engine.Config.LogLevel >= LogLevel.Trace;


    public   static Action<string> CustomOutput = DevNullPtr;
    internal static Action<string> Output       = DevNullPtr;

    static ConcurrentQueue<byte[]>
                        fileData = [];
    static bool         fileTaskRunning;
    static FileStream   fileStream;
    static object       lockFileStream = new();
    static Dictionary<LogLevel, string>
                        logLevels = [];

    private static string _logBaseDir;
    private static string _logBaseName;
    private static string _logExtension;
    private static int _currentRollIndex = -1;

    static Logger()
    {
        foreach (LogLevel loglevel in Enum.GetValues<LogLevel>())
            logLevels.Add(loglevel, loglevel.ToString().PadRight(5, ' '));

        // Flush File Data on Application Exit
        Application.Current.Exit += (_, _) => DisposeFileStream();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => DisposeFileStream();
    }

    internal static void SetOutput()
    {
        string output = Engine.Config.LogOutput;

        if (string.IsNullOrEmpty(output))
            Output = DevNullPtr;
        else if (output.StartsWith(':'))
        {
            if (output == ":console")
                Output = Console.WriteLine;
            else if (output == ":debug")
                Output = DebugPtr;
            else if (output == ":custom")
                Output = CustomOutput;
            else
                throw new("Invalid log output");
        }
        else
        {
            lock (lockFileStream)
            {
                if (fileStream != null)
                {   // Flush File Data on Previously Opened File Stream
                    while (fileData.TryDequeue(out byte[] data))
                        fileStream.Write(data, 0, data.Length);
                    fileStream.Dispose();
                }

                string dir = Path.GetDirectoryName(output);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                if (Engine.Config.LogAppend)
                {
                    fileStream = new(output, FileMode.Append, FileAccess.Write, FileShare.Read);
                    Output = FilePtr;
                }

                else if (Engine.Config.LogRollMaxFiles > 0 && Engine.Config.LogRollMaxFileSize > 0)
                {
                    _logBaseDir = string.IsNullOrEmpty(dir) ? "." : dir;
                    _logBaseName = Path.GetFileNameWithoutExtension(output);
                    _logExtension = Path.GetExtension(output);

                    OpenNextRollFile();
                    Output = FileRollPtr;
                }
                else
                {
                    fileStream = new(output, FileMode.Create, FileAccess.Write, FileShare.Read);
                    Output = FilePtr;
                }
            }
        }
    }
    static void DebugPtr(string msg) => System.Diagnostics.Debug.WriteLine(msg);
    static void DevNullPtr(string msg) { }
    static void FilePtr(string msg)
    {
        fileData.Enqueue(Encoding.UTF8.GetBytes($"{msg}\r\n"));

        if (!fileTaskRunning && fileData.Count > Engine.Config.LogCachedLines)
            FlushFileData();
    }

    static void FileRollPtr(string msg)
    {
        fileData.Enqueue(Encoding.UTF8.GetBytes($"{msg}\r\n"));

        if (!fileTaskRunning && fileData.Count > Engine.Config.LogCachedLines)
        {
            if (fileStream.Length >= Engine.Config.LogRollMaxFileSize)
            {
                while (fileTaskRunning) Thread.Sleep(10);

                lock (lockFileStream)
                {
                    while (fileData.TryDequeue(out byte[] data))
                        fileStream.Write(data, 0, data.Length);

                    fileStream.Flush();
                }

                HandleLogFileRolling();
            }
            else
                FlushFileData();
        }
    }

    static void OpenNextRollFile()
    {
        FileStream fs  = null;
        int        idx = GetNextStartingIndex();

        while (fs == null)
        {
            string candidate = Path.Combine(_logBaseDir, $"{_logBaseName}.{idx}{_logExtension}");

            try
            {
                fs = new(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            }
            catch (IOException)
            {
                // Index already taken (e.g. by another process right now)
                idx++;
            }
        }

        fileStream = fs;
        _currentRollIndex = idx;

        CleanupOldRollFiles();
    }

    static int GetNextStartingIndex()
    {
        try
        {
            int maxIdx = Directory
                .EnumerateFiles(_logBaseDir, $"{_logBaseName}.*{_logExtension}")
                .Select(ParseRollIndex)
                .Where(i => i >= 0)
                .DefaultIfEmpty(-1)
                .Max();

            return maxIdx + 1;
        }
        catch
        {
            return 0;
        }
    }

    static void CleanupOldRollFiles()
    {
        try
        {
            var toDelete = Directory
                .EnumerateFiles(_logBaseDir, $"{_logBaseName}.*{_logExtension}")
                .Select(f => (Path: f, Index: ParseRollIndex(f)))
                .Where(f => f.Index >= 0 && f.Index < _currentRollIndex)
                .OrderByDescending(f => f.Index)
                .Skip((int)Math.Max(Engine.Config.LogRollMaxFiles - 1, 0))
                .ToList();

            foreach (var f in toDelete)
            {
                try { File.Delete(f.Path); }
                catch
                {
                    // best-effort cleanup
                }
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    static int ParseRollIndex(string filePath)
    {
        string fileName = Path.GetFileName(filePath);
        string prefix   = _logBaseName + ".";

        if (fileName.StartsWith(prefix) && fileName.EndsWith(_logExtension) && fileName.Length > prefix.Length + _logExtension.Length)
        {
            string middle = fileName.Substring(prefix.Length, fileName.Length - prefix.Length - _logExtension.Length);
            if (int.TryParse(middle, out int idx))
                return idx;
        }

        return -1;
    }

    static void HandleLogFileRolling()
    {
        fileTaskRunning = true;

        Task.Run(() =>
        {
            lock (lockFileStream)
            {
                fileStream.Dispose();
                OpenNextRollFile();
            }

            fileTaskRunning = false;
        });
    }

    static void FlushFileData()
    {
        fileTaskRunning = true;

        Task.Run(() =>
        {
            lock (lockFileStream)
            {
                while (fileData.TryDequeue(out byte[] data))
                    fileStream.Write(data, 0, data.Length);

                fileStream.Flush();
            }

            fileTaskRunning = false;
        });
    }

    /// <summary>
    /// Forces cached file data to be written to the file
    /// </summary>
    public static void ForceFlush()
    {
        if (!fileTaskRunning && fileStream != null)
            FlushFileData();
    }

    internal static void Log(string msg, LogLevel logLevel)
    {
        if (logLevel <= Engine.Config.LogLevel)
            Output($"{DateTime.Now.ToString(Engine.Config.LogDateTimeFormat)} | {logLevels[logLevel]} | {msg}");
    }

    private static void DisposeFileStream()
    {
        lock (lockFileStream)
        {
            if (fileStream != null)
            {
                while (fileData.TryDequeue(out byte[] data))
                    fileStream.Write(data, 0, data.Length);
                fileStream.Dispose();
            }
        }
    }
}

public class LogHandler(string prefix = "")
{
    public string Prefix = prefix;

    public void Error   (string msg)    => Log($"{Prefix}{msg}", LogLevel.Error);
    public void Info    (string msg)    => Log($"{Prefix}{msg}", LogLevel.Info);
    public void Warn    (string msg)    => Log($"{Prefix}{msg}", LogLevel.Warn);
    public void Debug   (string msg)    => Log($"{Prefix}{msg}", LogLevel.Debug);
    public void Trace   (string msg)    => Log($"{Prefix}{msg}", LogLevel.Trace);
}

public enum LogLevel
{
    Quiet   = 0x00,
    Error   = 0x10,
    Warn    = 0x20,
    Info    = 0x30,
    Debug   = 0x40,
    Trace   = 0x50
}
