// <copyright file="ConsoleLog.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace PluginUpdater
{
    public enum LogLevel
    {
        Info,
        Success,
        Warning,
        Error,
    }

    public sealed record LogEntry(DateTime Timestamp, string Message, LogLevel Level);

    /// <summary>Keeps a bounded, thread-safe console for background Git and build tasks.</summary>
    public sealed class ConsoleLog
    {
        private readonly Queue<LogEntry> entries = new();

        public IReadOnlyList<LogEntry> GetEntries()
        {
            lock (this.entries)
            {
                return this.entries.ToArray();
            }
        }

        public void Clear()
        {
            lock (this.entries)
            {
                this.entries.Clear();
            }
        }

        public void Write(string message, LogLevel level = LogLevel.Info)
        {
            lock (this.entries)
            {
                this.entries.Enqueue(new LogEntry(DateTime.Now, message, level));
                while (this.entries.Count > 1000)
                {
                    this.entries.Dequeue();
                }
            }

            System.Console.WriteLine($"[PluginUpdater] {message}");
        }

        public void LogInfo(string message) => this.Write(message);

        public void LogSuccess(string message) => this.Write(message, LogLevel.Success);

        public void LogWarning(string message) => this.Write(message, LogLevel.Warning);

        public void LogError(string message) => this.Write(message, LogLevel.Error);
    }
}
