// <copyright file="PluginCatalog.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace PluginUpdater
{
    /// <summary>The catalog format used by the upstream PluginUpdater browser.</summary>
    public sealed class PluginRepositoryData
    {
        public List<PluginDescription> PluginDescriptions { get; set; } = new();
    }

    public sealed class PluginDescription
    {
        public string Name { get; set; } = string.Empty;
        public string OriginalAuthor { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string EndorsedAuthor { get; set; } = string.Empty;
        public List<Fork> Forks { get; set; } = new();
    }

    public sealed class Fork
    {
        public string Author { get; set; } = string.Empty;
        public string Location { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Branch { get; set; } = string.Empty;
        public string ProjectPath { get; set; } = string.Empty;
        public LatestCommit? LatestCommit { get; set; }
    }

    public sealed class LatestCommit
    {
        public string Message { get; set; } = string.Empty;
        public string Hash { get; set; } = string.Empty;
        public string Author { get; set; } = string.Empty;
        public string Date { get; set; } = string.Empty;
    }
}
